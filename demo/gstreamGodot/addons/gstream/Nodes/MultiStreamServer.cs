using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Godot;
using gStream.Core.Capture;
using gStream.Core.Encoding;
using gStream.Core.Input;
using gStream.Core.Streaming;
using gStream.Godot;
using gStream.Godot.Capture;
using SIPSorcery.Net;

namespace gStream.Godot.Nodes;

/// <summary>
/// Multi-client streaming server for multiplay mode.
/// Manages multiple simultaneous WebRTC connections (one per browser client),
/// sharing a single encoder instance with fan-out to all connected streamers.
/// <para>
/// Architecture: single encoder → fan-out to N <see cref="WebRtcStreamer"/> instances.
/// Each browser client gets its own peer connection for independent SDP negotiation,
/// while the capture and encode pipeline runs only once.
/// </para>
/// </summary>
[GlobalClass]
public sealed partial class MultiStreamServer : Node
{
    #region Signals

    [Signal]
    public delegate void StreamStartedEventHandler(int width, int height);

    [Signal]
    public delegate void StreamStoppedEventHandler();

    [Signal]
    public delegate void ClientConnectedEventHandler(string connectionId, string label);

    [Signal]
    public delegate void ClientDisconnectedEventHandler(string connectionId);

    [Signal]
    public delegate void MultiplayMessageReceivedEventHandler(string connectionId, string message);

    // NOTE: InputReceived signal removed. Godot 4.6's EmitSignal C#→GDScript marshaling
    // triggers "Unexpected NUL character" UTF-8 errors at high frequency (every mouse move),
    // even with only int/float parameters. Input events are now injected directly into
    // Godot's input system via GodotInputInjector, and GDScript uses _unhandled_input
    // to handle them — same pattern as receiver.gd / StreamServer.

    [Signal]
    public delegate void StatsUpdatedEventHandler(int fps, int bitrateKbps, int pendingFrames, double encodeMs, double captureMs);

    #endregion

    #region Exports

    [ExportGroup("Capture")]
    [Export]
    public SubViewport? SourceViewport { get; set; }

    /// <summary>
    /// If true and <see cref="SourceViewport"/> is null, captures the current running window directly.
    /// </summary>
    [Export]
    public bool CaptureMainWindow { get; set; } = true;

    [ExportGroup("Encoding")]
    [Export]
    public int TargetFps { get; set; } = 60;

    [Export]
    public int BitrateKbps { get; set; } = 8000;

    [Export(PropertyHint.Range, "1.0,4.0,0.1")]
    public float MaxRateMultiplier { get; set; } = 2.0f;

    [Export]
    public EncoderPreset Preset { get; set; } = EncoderPreset.UltraLowLatency;

    [Export]
    public VideoCodec Codec { get; set; } = VideoCodec.Auto;

    [ExportGroup("Audio")]
    [Export]
    public bool EnableAudio { get; set; } = true;

    [Export]
    public int AudioBitrateKbps { get; set; } = 128;

    [ExportGroup("Signaling")]
    [Export]
    public string SignalingUrl { get; set; } = "ws://localhost:80";

    [Export]
    public string ConnectionId { get; set; } = Guid.NewGuid().ToString();

    [ExportGroup("STUN/TURN")]
    [Export]
    public string[] IceServers { get; set; } = { "stun:stun.l.google.com:19302" };

    /// <summary>
    /// Optional local IP to bind RTP/ICE sockets to (e.g. "192.168.1.100").
    /// When set, only this interface is used for ICE host candidates.
    /// </summary>
    [Export]
    public string BindAddress { get; set; } = "";

    /// <summary>
    /// Optional IP prefix whitelist for ICE candidate filtering.
    /// E.g. { "192.168.", "10." } allows only those subnets.
    /// </summary>
    [Export]
    public string[] AllowedIcePrefixes { get; set; } = Array.Empty<string>();

    [ExportGroup("Multiplay")]
    [Export]
    public int MaxClients { get; set; } = 4;

    #endregion

    #region State

    private readonly ConcurrentDictionary<string, ClientConnection> _clients = new();
    private ViewportCapture _capture = new();
    private IVideoEncoder? _encoder;
    private SignalingClient? _signaling;
    private GodotInputInjector? _inputInjector;
    private AudioCapture? _audioCapture;
    private OpusAudioEncoder? _audioEncoder;

    private bool _isRunning;
    private bool _captureStarted;

    /// <summary>
    /// After the first client negotiates a codec in Auto mode, this stores the
    /// result so subsequent clients are created with the fixed codec.
    /// </summary>
    private string? _negotiatedCodecName;

    // Pipeline parallelism: capture → channel → background encode
    private Channel<CapturedFrame>? _frameChannel;
    private CancellationTokenSource? _encodeCts;
    private Task? _encodeTask;
    private int _encodeLoopFrameCount;
    private int _framesDroppedPool;
    private int _framesDroppedChannel;

    // Stats
    private int _framesEncoded;
    private long _bytesEncoded;
    private double _encodeMsAccum;
    private double _captureMsAccum;
    private int _statFrameCount;
    private double _statTimer;

    #endregion

    public override void _Ready()
    {
        // Must run even when scene is paused so DrainInput() continues processing.
        ProcessMode = ProcessModeEnum.Always;

        // Replace Godot's Debug TraceListener with a thread-safe one.
        // See StreamServer._Ready for detailed explanation.
        InstallDebugListenerFilter();

        GD.Print("[MultiStreamServer] Ready — auto-starting stream");
        StartStream();
    }

    /// <summary>
    /// Suppresses Debug.WriteLine from background threads. See StreamServer for details.
    /// Clears Godot's native TraceListener (which bypasses Console.Out) and wraps
    /// Console.Out to filter non-main-thread output.
    /// </summary>
    private static bool _debugFilterInstalled;
    private static void InstallDebugListenerFilter()
    {
        if (_debugFilterInstalled) return;
        _debugFilterInstalled = true;

        // Remove Godot's native TraceListener — see StreamServer.InstallDebugListenerFilter
        // for detailed explanation.
        Trace.Listeners.Clear();
        Trace.Listeners.Add(new DefaultTraceListener());

        var mainThreadId = Thread.CurrentThread.ManagedThreadId;
        var originalOut = Console.Out;
        Console.SetOut(new StreamServer.ThreadSafeTextWriter(originalOut, mainThreadId));
    }

    public override void _Process(double delta)
    {
        if (!_isRunning) return;

        // Defer capture until at least one client is fully connected.
        // This avoids encoder allocations when no browser is connected.
        if (!_captureStarted && _encoder != null && _clients.Values.Any(c => c.IsConnected))
        {
            _capture.Start();
            _captureStarted = true;
            GD.Print("[MultiStreamServer] Capture started (encoder ready, peer(s) connected)");

            _audioCapture?.Start();
        }

        // Drain input and multiplay events from all clients
        DrainAllInputs();
        DrainMultiplayMessages();

        // Audio capture & encode (lightweight — done inline)
        if (_audioCapture != null && _audioEncoder != null && _isRunning && _captureStarted)
        {
            while (_audioCapture.TryGetSamples(out var samples))
            {
                _audioEncoder.Encode(samples);
            }
        }

        // Stats
        _statFrameCount++;
        _statTimer += delta;
        if (_statTimer >= 1.0)
        {
            var fps = _statFrameCount;
            var avgEncodeMs = _statFrameCount > 0 ? _encodeMsAccum / _statFrameCount : 0;
            var avgCaptureMs = _statFrameCount > 0 ? _captureMsAccum / _statFrameCount : 0;
            var bitrateKbps = (int)(_bytesEncoded * 8 / 1000.0 / _statTimer);
            var droppedFrames = _framesDroppedPool + _framesDroppedChannel;

            EmitSignal(SignalName.StatsUpdated, fps, bitrateKbps, droppedFrames, avgEncodeMs, avgCaptureMs);

            if (droppedFrames > 0)
            {
                GD.PrintErr($"[MultiStreamServer] Frame drops in last 1s: pool={_framesDroppedPool}, channel={_framesDroppedChannel}");
            }

            _statFrameCount = 0;
            _statTimer = 0;
            _encodeMsAccum = 0;
            _captureMsAccum = 0;
            _bytesEncoded = 0;
            _framesDroppedPool = 0;
            _framesDroppedChannel = 0;
        }
    }

    #region Start / Stop

    /// <summary>Start the capture → encode → multi-stream pipeline.</summary>
    public async void StartStream()
    {
        if (_isRunning)
        {
            GD.PushWarning("[MultiStreamServer] Already streaming");
            return;
        }

        try
        {
            // 1. Initialize capture
            GD.Print("[MultiStreamServer] Step 1: Initializing capture...");
            if (SourceViewport != null)
            {
                _capture.Initialize(SourceViewport);
            }
            else if (CaptureMainWindow)
            {
                _capture.Initialize(GetViewport());
                GD.Print("[MultiStreamServer] Capturing current window");
            }
            else
            {
                GD.PushError("[MultiStreamServer] No capture source! Set SourceViewport or enable CaptureMainWindow.");
                return;
            }

            _capture.OnFrame += OnFrameCaptured;
            _capture.OnResolutionChanged += OnResolutionChanged;

            var (w, h) = _capture.Resolution;

            var viewport = SourceViewport ?? GetViewport();
            _inputInjector = new GodotInputInjector(viewport!);

            // 2. Encoder — deferred in Auto mode until first client negotiates codec
            var preset = Preset;
            var codecPref = Codec;

            if (codecPref == VideoCodec.Auto)
            {
                GD.Print("[MultiStreamServer] Step 2: Auto codec — deferring encoder until first client SDP negotiation");
                _encoder = null;
            }
            else
            {
                GD.Print($"[MultiStreamServer] Step 2: Initializing encoder ({w}x{h} @ {TargetFps}fps, {BitrateKbps}kbps)...");
                CreateEncoder(codecPref, preset, w, h);
            }

            // 3. Initialize audio pipeline (if enabled)
            if (EnableAudio)
            {
                try
                {
                    GD.Print("[MultiStreamServer] Initializing audio pipeline...");

                    _audioCapture = new AudioCapture();
                    _audioEncoder = new OpusAudioEncoder();
                    _audioEncoder.Configure(48000, 2, AudioBitrateKbps);
                    _audioEncoder.OnEncodedFrame += OnEncodedAudioFrame;

                    GD.Print($"[MultiStreamServer] Audio encoder initialized: 48kHz stereo {AudioBitrateKbps}kbps");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[MultiStreamServer] Audio init failed (non-fatal): {ex.Message}");
                    _audioEncoder?.Dispose();
                    _audioEncoder = null;
                    _audioCapture = null;
                }
            }

            // 4. Connect signaling (single shared connection for all clients)
            GD.Print($"[MultiStreamServer] Step 4: Connecting signaling to {SignalingUrl}...");
            _signaling = new SignalingClient(SignalingUrl, ConnectionId);
            _signaling.OnOfferReceived += OnSignalingOffer;
            _signaling.OnAnswerReceived += OnSignalingAnswer;
            _signaling.OnCandidateReceived += OnSignalingCandidate;
            _signaling.OnConnected += (s, e) =>
                GD.Print($"[MultiStreamServer] Signaling connected as {e.ConnectionId}");
            _signaling.OnDisconnected += OnSignalingDisconnected;
            _signaling.OnShouldCreateOffer += (s, e) =>
                GD.Print("[MultiStreamServer] Impolite peer — waiting for browser offers...");

            await _signaling.ConnectAsync();

            // 5. Start encode pipeline (async channel + background thread)
            GD.Print("[MultiStreamServer] Step 5: Starting encode pipeline...");
            StartEncodePipeline();

            _isRunning = true;

            EmitSignal(SignalName.StreamStarted, w, h);
            GD.Print($"[MultiStreamServer] Streaming started: {w}x{h} @ {TargetFps}fps, max {MaxClients} clients");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MultiStreamServer] Start failed: {ex.GetType().Name}: {ex.Message}");
            GD.PrintErr($"[MultiStreamServer] Stack: {ex.StackTrace}");
            if (ex.InnerException != null)
                GD.PrintErr($"[MultiStreamServer] Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            StopStream();
        }
    }

    /// <summary>Stop the pipeline and release all resources.</summary>
    public void StopStream()
    {
        if (!_isRunning && _encoder == null && _signaling == null && _clients.IsEmpty)
            return;

        GD.Print("[MultiStreamServer] Stopping stream...");

        _isRunning = false;
        _captureStarted = false;

        _capture.Stop();
        _capture.OnFrame -= OnFrameCaptured;
        _capture.OnResolutionChanged -= OnResolutionChanged;

        if (_audioCapture != null)
        {
            _audioCapture.Stop();
            _audioCapture.Dispose();
            _audioCapture = null;
        }
        _audioEncoder?.Dispose();
        _audioEncoder = null;

        StopEncodePipeline();

        _encoder?.Dispose();
        _encoder = null;

        // Dispose all client connections
        foreach (var kvp in _clients)
        {
            try { kvp.Value.Dispose(); } catch { }
        }
        _clients.Clear();

        _signaling?.DisposeAsync().AsTask().Wait(1000);
        _signaling = null;

        _capture.Dispose();

        _inputInjector = null;
        _negotiatedCodecName = null;

        _framesEncoded = 0;
        _bytesEncoded = 0;
        _encodeMsAccum = 0;
        _captureMsAccum = 0;
        _statFrameCount = 0;
        _statTimer = 0;
        _framesDroppedPool = 0;
        _framesDroppedChannel = 0;
        _encodeLoopFrameCount = 0;

        EmitSignal(SignalName.StreamStopped);
        GD.Print("[MultiStreamServer] Streaming stopped");
    }

    public override void _ExitTree()
    {
        StopStream();
        base._ExitTree();
    }

    #endregion

    #region Encoder Management

    /// <summary>
    /// Creates the video encoder for a fixed codec selection.
    /// </summary>
    private void CreateEncoder(VideoCodec codecPref, EncoderPreset preset, int w, int h)
    {
        if (codecPref.IsAV1Family())
        {
            var av1Encoder = new AV1HardwareEncoder(preset);
            av1Encoder.Configure(w, h, TargetFps, BitrateKbps, MaxRateMultiplier);
            av1Encoder.OnEncodedNALU += OnEncodedAv1Obu;
            _encoder = av1Encoder;
            GD.Print($"[MultiStreamServer] AV1 Encoder initialized");
        }
        else if (codecPref.IsVP9Family())
        {
            var vp9Encoder = new VP9HardwareEncoder(preset);
            vp9Encoder.Configure(w, h, TargetFps, BitrateKbps, MaxRateMultiplier);
            vp9Encoder.OnEncodedNALU += OnEncodedVp9Frame;
            _encoder = vp9Encoder;
            GD.Print($"[MultiStreamServer] VP9 Encoder initialized");
        }
        else
        {
            // H264 or H265 family — H264HardwareEncoder handles both
            _encoder = new H264HardwareEncoder(preset, codecPref);
            _encoder.Configure(w, h, TargetFps, BitrateKbps, MaxRateMultiplier);
            _encoder.OnEncodedNALU += OnEncodedNalu;
            var enc = _encoder as H264HardwareEncoder;
            GD.Print($"[MultiStreamServer] Encoder initialized: {enc?.ActiveEncoderName ?? "unknown"}, codec={enc?.SdpCodecName ?? "?"}");
        }
    }

    /// <summary>
    /// Creates encoder lazily when the first client's SDP negotiation picks a codec (Auto mode).
    /// Stores the negotiated codec name so subsequent clients use the fixed codec.
    /// </summary>
    private void CreateEncoderFromNegotiatedCodec(string negotiatedCodec)
    {
        try
        {
            GD.Print($"[MultiStreamServer] SDP negotiated codec: {negotiatedCodec} — creating encoder");

            var (w, h) = _capture.Resolution;

            if (negotiatedCodec == "AV1")
            {
                var av1Encoder = new AV1HardwareEncoder(Preset);
                av1Encoder.Configure(w, h, TargetFps, BitrateKbps, MaxRateMultiplier);
                av1Encoder.OnEncodedNALU += OnEncodedAv1Obu;
                _encoder = av1Encoder;
            }
            else if (negotiatedCodec == "VP9")
            {
                var vp9Encoder = new VP9HardwareEncoder(Preset);
                vp9Encoder.Configure(w, h, TargetFps, BitrateKbps, MaxRateMultiplier);
                vp9Encoder.OnEncodedNALU += OnEncodedVp9Frame;
                _encoder = vp9Encoder;
            }
            else
            {
                // H264 or H265 — use H265 variant if negotiated as HEVC
                var resolvedCodec = negotiatedCodec == "H265" ? VideoCodec.H265_Main_L41 : VideoCodec.H264_High_L31;
                _encoder = new H264HardwareEncoder(Preset, resolvedCodec);
                _encoder.Configure(w, h, TargetFps, BitrateKbps, MaxRateMultiplier);
                _encoder.OnEncodedNALU += OnEncodedNalu;
            }

            _negotiatedCodecName = negotiatedCodec;

            var enc = _encoder as H264HardwareEncoder;
            var av1Enc = _encoder as AV1HardwareEncoder;
            var vp9Enc = _encoder as VP9HardwareEncoder;
            GD.Print($"[MultiStreamServer] Encoder lazy-initialized: {enc?.ActiveEncoderName ?? av1Enc?.ToString() ?? vp9Enc?.ActiveEncoderName ?? "unknown"}, codec={negotiatedCodec}");

            // If any client is already connected (SCTP established before encoder was ready),
            // force a keyframe so the browser decoder can initialize immediately.
            if (_clients.Values.Any(c => c.IsConnected))
            {
                GD.Print("[MultiStreamServer] Encoder initialized with existing peer(s) — forcing keyframe");
                _encoder.ForceKeyframe();
            }
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MultiStreamServer] Failed to lazy-init encoder: {ex.Message}");
        }
    }

    #endregion

    #region Client Management

    /// <summary>
    /// Creates a new <see cref="ClientConnection"/> with its own <see cref="WebRtcStreamer"/>.
    /// Called when a new browser client sends an SDP offer through the signaling server.
    /// </summary>
    /// <returns>The new connection, or null if <see cref="MaxClients"/> was reached.</returns>
    private ClientConnection? CreateClient(string browserConnectionId)
    {
        if (_clients.Count >= MaxClients)
        {
            GD.PrintErr($"[MultiStreamServer] Max clients ({MaxClients}) reached — rejecting {browserConnectionId}");
            return null;
        }

        var iceServersList = IceServers
            .Select(url => new RTCIceServer { urls = url })
            .ToList();

        IPAddress? bindAddr = null;
        if (!string.IsNullOrEmpty(BindAddress))
        {
            try { bindAddr = IPAddress.Parse(BindAddress); }
            catch { GD.PushError($"[MultiStreamServer] Invalid BindAddress: {BindAddress}"); }
        }

        string[]? icePrefixes = AllowedIcePrefixes?.Length > 0 ? AllowedIcePrefixes : null;

        WebRtcStreamer streamer;

        if (_negotiatedCodecName == null && Codec == VideoCodec.Auto)
        {
            // First client in Auto mode — declare all codecs for SDP negotiation.
            // Once the codec is negotiated, OnVideoFormatNegotiated fires and creates the encoder.
            GD.Print($"[MultiStreamServer] Creating streamer for {browserConnectionId} (Auto codec mode — quad H264+H265+AV1+VP9)");
            streamer = new WebRtcStreamer(iceServersList, TargetFps, "H264",
                declareBothCodecs: true, bindAddress: bindAddr, allowedIcePrefixes: icePrefixes);

            streamer.OnVideoFormatNegotiated += (negotiatedCodec) =>
            {
                if (_encoder == null)
                {
                    CreateEncoderFromNegotiatedCodec(negotiatedCodec);
                }
            };
        }
        else
        {
            // Fixed codec or subsequent client — use the known codec.
            var codecName = _negotiatedCodecName;
            string? fmtp = null;

            if (codecName != null)
            {
                // Use the codec negotiated by the first client
                fmtp = codecName switch
                {
                    "H265" => "level-id=123;profile-id=1;tier-flag=0;tx-mode=SRST",
                    "AV1" => "level-idx=5;profile=0;tier=0",
                    "VP9" => "profile-id=0",
                    _ => "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=64001f"
                };
            }
            else
            {
                // Fixed codec mode
                var (sdpCodecName, sdpFmtp) = Codec.ToSdp();
                codecName = sdpCodecName;
                fmtp = sdpFmtp;
            }

            GD.Print($"[MultiStreamServer] Creating streamer for {browserConnectionId} (codec={codecName})");
            streamer = new WebRtcStreamer(iceServersList, TargetFps, codecName,
                fmtp, bindAddress: bindAddr, allowedIcePrefixes: icePrefixes);
        }

        // Create connection BEFORE hooking events so we can capture the reference.
        var connection = new ClientConnection(browserConnectionId, streamer);

        // ── Hook per-client streamer events ──

        // Input: route DataChannel messages to this client's InputRelay
        streamer.OnInputEvent += (data) =>
        {
            connection.InputRelay.OnDataChannelMessage(data);
        };

        // Connection state: trigger step 2 renegotiation or cleanup
        streamer.OnStateChanged += (state) =>
            OnClientStateChanged(browserConnectionId, state);

        // ICE candidates: send via shared signaling, targeting this browser
        streamer.OnIceCandidate += (candidate) =>
        {
            GD.Print($"[MultiStreamServer] Sending ICE candidate to {browserConnectionId}: {candidate.candidate}");
            _signaling?.SendCandidate(
                candidate.candidate,
                (int)candidate.sdpMLineIndex,
                candidate.sdpMid ?? "0",
                browserConnectionId);
        };

        // Keyframe requests: forward to the shared encoder
        streamer.OnKeyframeRequested += () =>
        {
            // Don't force keyframe here — handled after answer received
            // in OnSignalingAnswer.
        };

        // Store in dictionary
        _clients[browserConnectionId] = connection;

        // Seed the new streamer's cached keyframe from an existing connected client.
        // This ensures the new client can immediately serve a keyframe to the browser
        // decoder without waiting for the encoder's next forced keyframe cycle.
        var existingClient = _clients.Values.FirstOrDefault(c => c.IsConnected && c != connection);
        if (existingClient != null)
        {
            var kfData = existingClient.Streamer.TryGetPendingKeyframe(out var kfLen);
            if (kfData != null)
            {
                connection.Streamer.CopyPendingKeyframe(kfData, kfLen);
                GD.Print($"[MultiStreamServer] Seeded keyframe ({kfLen} bytes) for new client {browserConnectionId}");
            }
        }

        GD.Print($"[MultiStreamServer] Client created: {browserConnectionId} (total: {_clients.Count})");
        return connection;
    }

    /// <summary>
    /// Removes a client connection, disposes its resources, and emits <see cref="SignalName.ClientDisconnected"/>.
    /// </summary>
    private void RemoveClient(string connectionId)
    {
        if (_clients.TryRemove(connectionId, out var client))
        {
            try { client.Dispose(); } catch { }
            GD.Print($"[MultiStreamServer] Client removed: {connectionId} (remaining: {_clients.Count})");
            EmitSignal(SignalName.ClientDisconnected, connectionId);
        }
    }

    #endregion

    #region Pipeline Callbacks

    /// <summary>
    /// Background encoding loop: reads frames from channel and encodes them.
    /// Encoded output is fanned out to all connected clients in the OnEncoded* callbacks.
    /// </summary>
    private async Task EncodeLoopAsync(CancellationToken ct)
    {
        // NOTE: This runs on a thread pool thread. Do NOT call GD.Print or any
        // Godot native API here — Godot's string marshaling is not thread-safe.

        while (true)
        {
            CapturedFrame frame;
            try
            {
                frame = await _frameChannel!.Reader.ReadAsync(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            if (_encoder == null)
            {
                frame.Dispose();
                continue;
            }

            var encodeStart = Time.GetTicksUsec();
            _encoder.Encode(frame);
            var elapsed = (Time.GetTicksUsec() - encodeStart) / 1000.0;

            var count = Interlocked.Increment(ref _encodeLoopFrameCount);

            Interlocked.Add(ref _framesEncoded, 1);
            _encodeMsAccum += elapsed;
        }
    }

    private void StartEncodePipeline()
    {
        _frameChannel = Channel.CreateBounded<CapturedFrame>(new BoundedChannelOptions(2)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleWriter = true,
            SingleReader = true
        });

        _encodeCts = new CancellationTokenSource();
        _encodeTask = Task.Run(() => EncodeLoopAsync(_encodeCts.Token));
    }

    private void StopEncodePipeline()
    {
        _frameChannel?.Writer.TryComplete();
        _encodeCts?.Cancel();

        if (_encodeTask != null)
        {
            try
            {
                _encodeTask.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // Task may throw on cancellation — safe to ignore
            }
        }

        // Dispose any remaining frames in the channel
        if (_frameChannel != null)
        {
            while (_frameChannel.Reader.TryRead(out var frame))
            {
                frame.Dispose();
            }
        }

        _frameChannel = null;
        _encodeCts = null;
        _encodeTask = null;
    }

    private void OnFrameCaptured(CapturedFrame frame)
    {
        // Accumulate capture timing from ViewportCapture
        _captureMsAccum += _capture.LastCaptureUs / 1000.0;

        // Accumulate pool exhaustion drops from ViewportCapture
        if (_capture.PoolExhaustionCount > 0)
        {
            _framesDroppedPool += _capture.PoolExhaustionCount;
            _capture.PoolExhaustionCount = 0;
        }

        if (!_isRunning || _encoder == null || _frameChannel == null)
        {
            frame.Dispose();
            return;
        }

        // Non-blocking write to channel. If channel is full (encoder lagging),
        // DropOldest ensures we always encode the latest frame.
        if (!_frameChannel.Writer.TryWrite(frame))
        {
            _framesDroppedChannel++;
            frame.Dispose();
        }
    }

    private void OnResolutionChanged(int newWidth, int newHeight)
    {
        GD.Print($"[MultiStreamServer] Resolution changed to {newWidth}x{newHeight} — reconfiguring encoder");

        if (_encoder != null)
        {
            try
            {
                _encoder.Configure(newWidth, newHeight, TargetFps, BitrateKbps, MaxRateMultiplier);
                _encoder.ForceKeyframe();
                GD.Print($"[MultiStreamServer] Encoder reconfigured for {newWidth}x{newHeight}");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[MultiStreamServer] Failed to reconfigure encoder: {ex.Message}");
            }
        }

        _inputInjector?.UpdateViewportSize(newWidth, newHeight);
    }

    // ── Fan-out: send encoded data to ALL connected clients ──

    /// <summary>
    /// Fan-out: send encoded H264/H265 NALU to ALL connected clients.
    /// </summary>
    private void OnEncodedNalu(byte[] naluData, int length, int isKeyframe)
    {
        foreach (var client in _clients.Values)
        {
            if (client.IsConnected && client.Step2Done)
                client.Streamer.SendH264Nalu(naluData, length, isKeyframe != 0);
        }
        _bytesEncoded += length;
    }

    /// <summary>
    /// Fan-out: send encoded AV1 OBU to ALL connected clients.
    /// </summary>
    private void OnEncodedAv1Obu(byte[] obuData, int length, int isKeyframe)
    {
        foreach (var client in _clients.Values)
        {
            if (client.IsConnected && client.Step2Done)
                client.Streamer.SendAv1Obu(obuData, length, isKeyframe != 0);
        }
        _bytesEncoded += length;
    }

    private int _vp9FrameCount;

    /// <summary>
    /// Fan-out: send encoded VP9 frame to ALL connected clients.
    /// </summary>
    private void OnEncodedVp9Frame(byte[] frameData, int length, int isKeyframe)
    {
        foreach (var client in _clients.Values)
        {
            if (client.IsConnected && client.Step2Done)
                client.Streamer.SendVp9Frame(frameData, length, isKeyframe != 0);
        }
        _bytesEncoded += length;
        _ = Interlocked.Increment(ref _vp9FrameCount);
    }

    /// <summary>
    /// Fan-out: send encoded audio frame to ALL connected clients.
    /// </summary>
    private void OnEncodedAudioFrame(byte[] data, int length)
    {
        foreach (var client in _clients.Values)
        {
            if (client.IsConnected && client.Step2Done)
                client.Streamer.SendAudio(data, length);
        }
        // Don't add to _bytesEncoded — it's video-only stats
    }

    #endregion

    #region Signaling Callbacks

    /// <summary>
    /// Two-step negotiation per client (mirrors StreamServer's approach):
    ///   Step 1: Answer browser's data-only offer with data-only answer → SCTP/DataChannel established
    ///   Step 2: After ICE connects, send renegotiation offer with video+data → video starts
    /// </summary>
    private void OnSignalingOffer(object? sender, OfferReceivedEventArgs e)
    {
        var browserId = e.FromConnectionId;
        GD.Print($"[MultiStreamServer] Received offer from {browserId} ({e.Sdp.Length} bytes)");

        // Get or create client connection for this browser
        if (!_clients.TryGetValue(browserId, out var client))
        {
            client = CreateClient(browserId);
            if (client == null)
                return; // MaxClients reached
        }

        if (!client.Step1Done)
        {
            // Step 1: data-only answer
            client.Streamer.SetRemoteOffer(e.Sdp);
            var answerSdp = client.Streamer.CreateAnswer();
            client.Step1Done = true;

            GD.Print($"[MultiStreamServer] Step 1: Sending data-only answer to {browserId} ({answerSdp.Length} bytes)");
            _signaling!.SendAnswer(answerSdp, browserId);

            EmitSignal(SignalName.ClientConnected, browserId, client.Label);
            return;
        }

        // Subsequent offers from browser (e.g. onnegotiationneeded after Step 2).
        // Must answer to keep the signaling state machine consistent. Ignoring
        // would leave the browser stuck in have-local-offer.
        client.Streamer.SetRemoteOffer(e.Sdp);
        var subsequentAnswerSdp = client.Streamer.CreateAnswer();
        _signaling!.SendAnswer(subsequentAnswerSdp, browserId);
        GD.Print($"[MultiStreamServer] Answered subsequent offer from {browserId} ({e.Sdp.Length} bytes)");
    }

    private void OnSignalingAnswer(object? sender, AnswerReceivedEventArgs e)
    {
        var browserId = e.FromConnectionId;
        GD.Print($"[MultiStreamServer] Received answer from {browserId} ({e.Sdp.Length} bytes)");

        if (!_clients.TryGetValue(browserId, out var client))
        {
            GD.PrintErr($"[MultiStreamServer] Received answer from unknown client {browserId}");
            return;
        }

        // Patch browser's H265 level-id from 93 back to 123 (same as StreamServer).
        var patchedSdp = PatchH265LevelId(e.Sdp);
        if (patchedSdp != e.Sdp)
        {
            GD.Print($"[MultiStreamServer] Patched H265 level-id in answer from {browserId}: 93 → 123");
        }

        client.Streamer.SetRemoteAnswer(patchedSdp);

        // Mark Step2Done now that the answer is set — encoder callbacks can start sending.
        // Must be set before ForceKeyframeSend so the keyframe actually gets transmitted.
        client.Step2Done = true;
        GD.Print($"[MultiStreamServer] Step 2 answer set for {browserId} — video should be active");

        // Immediately send the cached keyframe to this client's decoder.
        // The streamer should have a cached keyframe either from:
        //   (a) the keyframe seeded in CreateClient from an existing client, or
        //   (b) keyframes produced by the encoder since the client was created.
        // ForceKeyframeSend also resets _sentKeyframeThisSession so the next
        // fan-out frame will re-send the cached keyframe as a safety net.
        client.Streamer.ForceKeyframeSend();

        // Also request a fresh keyframe from the encoder for good measure.
        if (_encoder != null)
        {
            GD.Print($"[MultiStreamServer] Forcing keyframe after answer from {browserId}");
            _encoder.ForceKeyframe();
        }
        else
        {
            GD.Print($"[MultiStreamServer] Encoder not yet initialized (Auto mode) — keyframe deferred");
        }
    }

    private void OnSignalingCandidate(object? sender, CandidateReceivedEventArgs e)
    {
        var browserId = e.FromConnectionId;

        if (!_clients.TryGetValue(browserId, out var client))
        {
            GD.Print($"[MultiStreamServer] ICE candidate for unknown client {browserId} — ignoring");
            return;
        }

        client.Streamer.AddIceCandidate(e.Candidate, e.SdpMLineIndex, e.SdpMid);
    }

    /// <summary>
    /// Handles remote peer disconnection notifications from the signaling server.
    /// Only cleans up if the disconnected ID belongs to one of our clients.
    /// </summary>
    private void OnSignalingDisconnected(object? sender, ConnectionEventArgs e)
    {
        if (e.ConnectionId == ConnectionId)
            return; // Our own signaling connection — handled by reconnect logic

        if (_clients.ContainsKey(e.ConnectionId))
        {
            GD.Print($"[MultiStreamServer] Client {e.ConnectionId} disconnected via signaling");
            RemoveClient(e.ConnectionId);
        }
    }

    /// <summary>
    /// Per-client WebRTC state change handler.
    /// Triggers step 2 (video renegotiation) when ICE connects after step 1.
    /// Cleans up client on connection failure.
    /// </summary>
    private void OnClientStateChanged(string connectionId, RTCPeerConnectionState state)
    {
        GD.Print($"[MultiStreamServer] Client {connectionId} WebRTC state: {state}");

        if (!_clients.TryGetValue(connectionId, out var client))
            return;

        if (state == RTCPeerConnectionState.connected)
        {
            // Step 2: ICE connected after step 1 → send video renegotiation offer.
            if (client.Step1Done && !client.Step2Done)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        client.Streamer.AddVideoTrack();
                        var videoOfferSdp = client.Streamer.CreateOffer();
                        // Step2Done is set in OnSignalingAnswer when the browser accepts the video track.
                        // Do NOT set it here — video data must not flow until the answer is applied.

                        GD.Print($"[MultiStreamServer] Step 2: Sending video renegotiation offer to {connectionId} ({videoOfferSdp.Length} bytes)");
                        _signaling?.SendOffer(videoOfferSdp, connectionId);
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"[MultiStreamServer] Step 2 failed for {connectionId}: {ex.Message}");
                    }
                });
            }
        }
        else if (state == RTCPeerConnectionState.failed ||
                 state == RTCPeerConnectionState.disconnected ||
                 state == RTCPeerConnectionState.closed)
        {
            GD.Print($"[MultiStreamServer] Client {connectionId} connection lost — removing");
            RemoveClient(connectionId);
        }
    }

    /// <summary>
    /// Patches the H265 level-id in a browser SDP answer back to the value we offered.
    /// Chromium's experimental H265 WebRTC downgrades level-id=123 (Level 4.1) to
    /// level-id=93 (Level 3.1) in its answer.
    /// </summary>
    private static readonly Regex H265LevelIdRegex = new(
        @"(level-id=)93(;)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string PatchH265LevelId(string sdp)
    {
        return H265LevelIdRegex.Replace(sdp, "${1}123${2}");
    }

    #endregion

    #region Input & Multiplay

    /// <summary>
    /// Drain input events from ALL clients' input relays.
    /// Injects events via <see cref="GodotInputInjector"/> into Godot's input system.
    /// GDScript handles them via _unhandled_input (same pattern as receiver.gd).
    /// </summary>
    private void DrainAllInputs()
    {
        foreach (var kvp in _clients)
        {
            var client = kvp.Value;

            while (client.InputRelay.TryDequeue(out var evt))
            {
                _inputInjector?.InjectEvent(evt);
            }
        }
    }

    /// <summary>
    /// Drain multiplay messages from ALL clients' queues.
    /// Broadcasts each message to all OTHER connected clients and emits
    /// <see cref="SignalName.MultiplayMessageReceived"/>.
    /// <para>
    /// The multiplay DataChannel receives JSON messages:
    /// <c>{ type: 0 (ChangeLabel), argument: "randomNumber" }</c>
    /// </para>
    /// <para>
    /// Note: Full multiplay DataChannel send support requires <see cref="WebRtcStreamer"/>
    /// to be extended with a <c>SendOnDataChannel(string label, byte[] data)</c> method.
    /// The broadcast infrastructure is in place here; once that extension is added,
    /// the send path will be:
    /// <c>otherClient.Streamer.SendOnDataChannel("multiplay", Encoding.UTF8.GetBytes(message));</c>
    /// </para>
    /// </summary>
    private void DrainMultiplayMessages()
    {
        foreach (var kvp in _clients)
        {
            var senderId = kvp.Key;
            var sender = kvp.Value;

            while (sender.MultiplayMessages.TryDequeue(out var message))
            {
                // Forward to GDScript via signal
                EmitSignal(SignalName.MultiplayMessageReceived, senderId, message);

                // Broadcast to all OTHER connected clients
                foreach (var otherKvp in _clients)
                {
                    if (otherKvp.Key != senderId && otherKvp.Value.IsConnected)
                    {
                        // TODO: Send on "multiplay" DataChannel once WebRtcStreamer
                        // exposes a SendOnDataChannel method. For now, the message is
                        // forwarded to GDScript via the MultiplayMessageReceived signal.
                        // otherKvp.Value.Streamer.SendOnDataChannel("multiplay",
                        //     System.Text.Encoding.UTF8.GetBytes(message));
                    }
                }

                // Handle label change locally
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(message);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var typeEl) && typeEl.GetInt32() == 0)
                    {
                        if (root.TryGetProperty("argument", out var argEl))
                        {
                            sender.Label = argEl.GetString() ?? sender.Label;
                            GD.Print($"[MultiStreamServer] Client {senderId} changed label to: {sender.Label}");
                        }
                    }
                }
                catch
                {
                    // Ignore malformed multiplay messages
                }
            }
        }
    }

    #endregion
}
