using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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
using gStream.Godot.Render;
using SIPSorcery.Net;

namespace gStream.Godot.Nodes;

/// <summary>
/// Main Godot node that wires together: Capture → Encode → Stream pipeline.
/// Add as a child node to any scene, assign a SubViewport, configure, and start streaming.
/// </summary>
[GlobalClass]
public sealed partial class StreamServer : Node
{
    [Signal]
    public delegate void StreamStartedEventHandler(int width, int height);

    [Signal]
    public delegate void StreamStoppedEventHandler();

    [Signal]
    public delegate void ClientConnectedEventHandler(string connectionId);

    [Signal]
    public delegate void ClientDisconnectedEventHandler(string connectionId);

    [Signal]
    public delegate void InputReceivedEventHandler(int eventType, float x, float y, int button, int keyCode);

    [Signal]
    public delegate void StatsUpdatedEventHandler(int fps, int bitrateKbps, int pendingFrames, double encodeMs, double captureMs);

    [Signal]
    public delegate void RemoteVideoReadyEventHandler(int width, int height);

    #region Exports

    [ExportGroup("Capture")]
    [Export]
    public SubViewport? SourceViewport { get; set; }

    /// <summary>
    /// If true and SourceViewport is null, captures the current running window directly.
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

    [ExportGroup("Input")]
    /// <summary>
    /// DataChannel label to use for input events. Defaults to "input" (URS protocol).
    /// Set to "data" for videoplayer mode.
    /// </summary>
    [Export]
    public string DataChannelLabel { get; set; } = "input";

    /// <summary>
    /// Input protocol parser to use. Options: "urs" (default) or "videoplayer".
    /// </summary>
    [Export(PropertyHint.Enum, "urs,videoplayer")]
    public string InputProtocol { get; set; } = "urs";

    [ExportGroup("Network")]
    /// <summary>
    /// Optional local IP to bind RTP/ICE sockets to (e.g. "192.168.1.100").
    /// When set, only this interface is used for ICE host candidates, preventing
    /// virtual adapters (Hyper-V, WSL, VPN) from producing unreachable IPs.
    /// Leave empty to auto-select (suitable for public/TURN deployment).
    /// </summary>
    [Export]
    public string BindAddress { get; set; } = "";

    /// <summary>
    /// Optional IP prefix whitelist for ICE candidate filtering.
    /// When set, only candidates whose IP matches one of these prefixes are forwarded.
    /// E.g. { "192.168.", "10." } allows only those subnets.
    /// Leave empty to allow all candidates (suitable for public/TURN deployment).
    /// </summary>
    [Export]
    public string[] AllowedIcePrefixes { get; set; } = Array.Empty<string>();

    [ExportGroup("Bidirectional")]
    /// <summary>
    /// When true, enables bidirectional mode where the browser sends its camera video
    /// to Godot. The video track direction is set to SendRecv instead of SendOnly.
    /// Remote video frames are rendered to <see cref="RemoteVideoDisplay"/>.
    /// </summary>
    [Export]
    public bool BidirectionalMode { get; set; } = false;

    /// <summary>
    /// Target TextureRect for displaying remote video (browser's camera).
    /// Only used when <see cref="BidirectionalMode"/> is true.
    /// Assign in the Godot Inspector.
    /// </summary>
    [Export]
    public TextureRect? RemoteVideoDisplay { get; set; }

    #endregion

    #region State

    private ViewportCapture _capture = new();
    private IVideoEncoder? _encoder;
    private WebRtcStreamer? _streamer;
    private SignalingClient? _signaling;
    private IInputParser? _inputParser;
    private GodotInputInjector? _inputInjector;
    private AudioCapture? _audioCapture;
    private OpusAudioEncoder? _audioEncoder;
    private VideoTrackRenderer? _remoteRenderer;

    // Stats
    private int _framesEncoded;
    private long _bytesEncoded;
    private double _encodeMsAccum;
    private double _captureMsAccum;
    private int _statFrameCount;
    private double _statTimer;

    private bool _isRunning;
    private bool _captureStarted;

    // Pipeline parallelism: capture → channel → background encode
    private Channel<CapturedFrame>? _frameChannel;
    private CancellationTokenSource? _encodeCts;
    private Task? _encodeTask;
    private int _framesDroppedPool;       // Pool exhaustion drops (read from ViewportCapture)
    private int _framesDroppedChannel;    // Channel full drops

    #endregion

    public override void _Ready()
    {
        // Must run even when scene is paused (e.g. DemoPage pauses the tree)
        // so that DrainInput() continues processing WebRTC input events.
        ProcessMode = ProcessModeEnum.Always;

        // Replace Godot's Debug TraceListener with a thread-safe one that
        // suppresses output from background threads. SIPSorcery (and other
        // libraries) call Debug.WriteLine on SCTP/encode threads; Godot's
        // built-in listener marshals those strings via String::parse_utf8()
        // which is not thread-safe and causes "Unexpected NUL character" errors
        // when binary data (mouse coordinates, etc.) is formatted.
        InstallDebugListenerFilter();

        GD.Print("[StreamServer] Ready — auto-starting stream");
        StartStream();
    }

    /// <summary>
    /// Suppresses Debug.WriteLine output from background threads.
    /// Godot's .NET host installs a native TraceListener that intercepts
    /// Debug.WriteLine and marshals it through String::parse_utf8(), which is
    /// not thread-safe from non-main threads. Binary data (e.g. SIPSorcery SCTP
    /// thread logging mouse coordinates) contains NUL bytes that cause
    /// "Unexpected NUL character" errors.
    /// <para>
    /// Two-pronged fix:
    /// 1. Clear Godot's native TraceListener from Debug.Listeners, keeping only
    ///    DefaultTraceListener (which routes through Console.Out).
    /// 2. Wrap Console.Out with ThreadSafeTextWriter that drops output from
    ///    non-main threads.
    /// This ensures Debug.WriteLine from background threads goes through:
    ///   Debug.Listeners → DefaultTraceListener → Console.Out → ThreadSafeTextWriter → dropped
    /// </para>
    /// </summary>
    private static bool _debugFilterInstalled;
    private static void InstallDebugListenerFilter()
    {
        if (_debugFilterInstalled) return;
        _debugFilterInstalled = true;

        // 1. Remove Godot's native TraceListener from Trace.Listeners.
        //    Godot's listener calls String::parse_utf8() directly on whatever thread
        //    fires Debug.WriteLine — including SIPSorcery's SCTP thread — causing
        //    the NUL character error. Replace with DefaultTraceListener which routes
        //    through Console.Out (which we wrap in step 2).
        Trace.Listeners.Clear();
        Trace.Listeners.Add(new DefaultTraceListener());

        // 2. Wrap Console.Out to drop output from non-main threads.
        //    DefaultTraceListener writes to Console.Out when no debugger is attached,
        //    so background-thread Debug.WriteLine now goes through our filter.
        var mainThreadId = Thread.CurrentThread.ManagedThreadId;
        var originalOut = Console.Out;
        Console.SetOut(new ThreadSafeTextWriter(originalOut, mainThreadId));
    }

    /// <summary>
    /// A TextWriter that only forwards output from the main Godot thread.
    /// Background thread output (e.g. SIPSorcery Debug.WriteLine) is silently dropped.
    /// GD.Print is NOT affected — it calls Godot's native API directly.
    /// </summary>
    internal sealed class ThreadSafeTextWriter : TextWriter
    {
        private readonly TextWriter _inner;
        private readonly int _mainThreadId;

        public ThreadSafeTextWriter(TextWriter inner, int mainThreadId)
        {
            _inner = inner;
            _mainThreadId = mainThreadId;
        }

        public override System.Text.Encoding Encoding => _inner.Encoding;

        public override void Write(char value)
        {
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
                _inner.Write(value);
        }

        public override void Write(string? value)
        {
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
                _inner.Write(value);
        }

        public override void Write(char[] buffer, int index, int count)
        {
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
                _inner.Write(buffer, index, count);
        }

        public override void WriteLine(string? value)
        {
            if (Thread.CurrentThread.ManagedThreadId == _mainThreadId)
                _inner.WriteLine(value);
        }

        protected override void Dispose(bool disposing)
        {
            // Do NOT dispose _inner — Console.Out lifetime is managed by the runtime.
            base.Dispose(disposing);
        }
    }

    public override void _Process(double delta)
    {
        if (!_isRunning) return;

        // Defer capture until WebRTC is fully connected.
        // This avoids ALL Godot readback allocations (~8MB/frame) and encoder
        // work when no browser is connected yet.
        // In Auto mode: _encoder and _streamer are null until browser offer arrives.
        // In Fixed codec mode: both exist but IsConnected stays false until SDP completes.
        if (!_captureStarted && _encoder != null && _streamer?.IsConnected == true)
        {
            _capture.Start();
            _captureStarted = true;
            GD.Print("[StreamServer] Capture started (encoder ready, peer connected)");

            _audioCapture?.Start();
        }

        // Capture is driven by RenderingServer.FramePostDraw signal in ViewportCapture.
        // No explicit CaptureFrame() call needed.

        // Drain input events
        DrainInput();

        // Audio capture & encode (lightweight — done inline)
        if (_audioCapture != null && _audioEncoder != null && _isRunning && _captureStarted)
        {
            while (_audioCapture.TryGetSamples(out var samples))
            {
                _audioEncoder.Encode(samples);
            }
        }

        // Remote video rendering (bidirectional mode)
        _remoteRenderer?.Process();

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
                GD.PrintErr($"[StreamServer] Frame drops in last 1s: pool={_framesDroppedPool}, channel={_framesDroppedChannel}");
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

    /// <summary>Start the capture → encode → stream pipeline.</summary>
    public async void StartStream()
    {
        if (_isRunning)
        {
            GD.PushWarning("[StreamServer] Already streaming");
            return;
        }

        try
        {
            // 1. Initialize capture — use SourceViewport or current window
            GD.Print("[StreamServer] Step 1: Initializing capture...");
            if (SourceViewport != null)
            {
                _capture.Initialize(SourceViewport);
            }
            else if (CaptureMainWindow)
            {
                _capture.Initialize(GetViewport());
                GD.Print("[StreamServer] Capturing current window");
            }
            else
            {
                GD.PushError("[StreamServer] No capture source! Set SourceViewport or enable CaptureMainWindow.");
                return;
            }

            _capture.OnFrame += OnFrameCaptured;
            _capture.OnResolutionChanged += OnResolutionChanged;

            var (w, h) = _capture.Resolution;

            var viewport = SourceViewport ?? GetViewport();
            _inputInjector = new GodotInputInjector(viewport!);

            // 2. Initialize encoder & WebRTC streamer
            var preset = Preset;
            var codecPref = Codec;
            var iceServers = IceServers
                .Select(url => new RTCIceServer { urls = url })
                .ToList();

            IPAddress? bindAddr = null;
            if (!string.IsNullOrEmpty(BindAddress))
            {
                try { bindAddr = IPAddress.Parse(BindAddress); }
                catch { GD.PushError($"[StreamServer] Invalid BindAddress: {BindAddress}"); }
            }

            string[]? icePrefixes = AllowedIcePrefixes?.Length > 0 ? AllowedIcePrefixes : null;

            if (codecPref == VideoCodec.Auto)
            {
                // Default mode: delay encoder creation until SDP negotiation picks the codec
                GD.Print($"[StreamServer] Step 2: Default codec mode — deferring encoder until SDP negotiation");
                _encoder = null;

                GD.Print("[StreamServer] Step 3: Initializing WebRTC streamer (quad H264+H265+AV1+VP9)...");
                _streamer = new WebRtcStreamer(iceServers, TargetFps, "H264", declareBothCodecs: true, bindAddress: bindAddr, allowedIcePrefixes: icePrefixes);
                _streamer.OnVideoFormatNegotiated += (negotiatedCodec) =>
                {
                    try
                    {
                        GD.Print($"[StreamServer] SDP negotiated codec: {negotiatedCodec} — creating encoder");

                        if (negotiatedCodec == "AV1")
                        {
                            var av1Encoder = new AV1HardwareEncoder(preset);
                            av1Encoder.Configure(_capture.Resolution.Width, _capture.Resolution.Height, TargetFps, BitrateKbps, MaxRateMultiplier);                            av1Encoder.OnEncodedNALU += OnEncodedAv1Obu;
                            _encoder = av1Encoder;
                        }
                        else if (negotiatedCodec == "VP9")
                        {
                            var vp9Encoder = new VP9HardwareEncoder(preset);
                            vp9Encoder.Configure(_capture.Resolution.Width, _capture.Resolution.Height, TargetFps, BitrateKbps, MaxRateMultiplier);                            vp9Encoder.OnEncodedNALU += OnEncodedVp9Frame;
                            _encoder = vp9Encoder;
                        }
                        else
                        {
                            // H264 or H265 — use H265 variant if negotiated as HEVC
                            var resolvedCodec = negotiatedCodec == "H265" ? VideoCodec.H265_Main_L41 : VideoCodec.H264_High_L31;
                            _encoder = new H264HardwareEncoder(preset, resolvedCodec);
                            _encoder.Configure(_capture.Resolution.Width, _capture.Resolution.Height, TargetFps, BitrateKbps, MaxRateMultiplier);
                            _encoder.OnEncodedNALU += OnEncodedNalu;
                        }

                        var enc = _encoder as H264HardwareEncoder;
                        var av1Enc = _encoder as AV1HardwareEncoder;
                        var vp9Enc = _encoder as VP9HardwareEncoder;
                        GD.Print($"[StreamServer] Encoder lazy-initialized: {enc?.ActiveEncoderName ?? av1Enc?.ToString() ?? vp9Enc?.ActiveEncoderName ?? "unknown"}, codec={negotiatedCodec}");

                        // If the peer connection is already connected (answer received and
                        // processing raced ahead of encoder init), force a keyframe now
                        // so the browser decoder can initialize immediately.
                        if (_streamer?.IsConnected == true)
                        {
                            GD.Print("[StreamServer] Encoder initialized after peer connected — forcing keyframe");
                            _encoder.ForceKeyframe();
                        }
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"[StreamServer] Failed to lazy-init encoder: {ex.Message}");
                    }
                };
            }
            else
            {
                // Fixed codec mode: create encoder immediately
                GD.Print($"[StreamServer] Step 2: Initializing encoder ({w}x{h} @ {TargetFps}fps, {BitrateKbps}kbps)...");
                
                if (codecPref.IsAV1Family())
                {
                    var av1Encoder = new AV1HardwareEncoder(preset);
                    av1Encoder.Configure(w, h, TargetFps, BitrateKbps, MaxRateMultiplier);
                    av1Encoder.OnEncodedNALU += OnEncodedAv1Obu;
                    _encoder = av1Encoder;
                    GD.Print($"[StreamServer] AV1 Encoder initialized");
                }
                else if (codecPref.IsVP9Family())
                {
                    var vp9Encoder = new VP9HardwareEncoder(preset);
                    vp9Encoder.Configure(w, h, TargetFps, BitrateKbps, MaxRateMultiplier);
                    vp9Encoder.OnEncodedNALU += OnEncodedVp9Frame;
                    _encoder = vp9Encoder;
                    GD.Print($"[StreamServer] VP9 Encoder initialized");
                }
                else
                {
                    // H264 or H265 family — H264HardwareEncoder handles both
                    _encoder = new H264HardwareEncoder(preset, codecPref);
                    _encoder.Configure(w, h, TargetFps, BitrateKbps, MaxRateMultiplier);
                    _encoder.OnEncodedNALU += OnEncodedNalu;
                    var enc = _encoder as H264HardwareEncoder;
                    GD.Print($"[StreamServer] Encoder initialized: {enc?.ActiveEncoderName ?? "unknown"}, codec={enc?.SdpCodecName ?? "?"}");
                }

                GD.Print("[StreamServer] Step 3: Initializing WebRTC streamer...");
                var (sdpCodecName, sdpFmtp) = codecPref.ToSdp();
                _streamer = new WebRtcStreamer(iceServers, TargetFps, sdpCodecName, sdpFmtp, bindAddress: bindAddr, allowedIcePrefixes: icePrefixes);
            }
            _streamer.OnInputEvent += OnInputFromBrowser;
            _streamer.OnStateChanged += OnStreamerStateChanged;
            _streamer.OnIceCandidate += OnLocalIceCandidate;
            _streamer.OnKeyframeRequested += OnKeyframeRequested;

            // Bidirectional mode: enable remote video reception
            if (BidirectionalMode)
            {
                _streamer.ReceiveRemoteVideo = true;

                _remoteRenderer = new VideoTrackRenderer();
                _remoteRenderer.OnFirstFrameReceived += () =>
                {
                    var tex = _remoteRenderer.Texture;
                    if (tex != null && RemoteVideoDisplay != null)
                    {
                        // Must set texture on main thread — OnFirstFrameReceived fires during Process
                        RemoteVideoDisplay.Texture = tex;
                        GD.Print("[StreamServer] Remote video texture assigned to RemoteVideoDisplay");
                    }
                    EmitSignal(SignalName.RemoteVideoReady, _remoteRenderer.Texture?.GetWidth() ?? 0, _remoteRenderer.Texture?.GetHeight() ?? 0);
                };

                _streamer.OnRemoteVideoFrame += OnRemoteVideoFrame;
                GD.Print("[StreamServer] Bidirectional mode enabled — remote video reception active");
            }
            _streamer.DataChannelLabel = DataChannelLabel;

            // Initialize input parser based on selected protocol
            _inputParser?.Dispose();
            _inputParser = InputProtocol == "videoplayer"
                ? new VideoplayerInputParser()
                : new InputRelay();
            GD.Print($"[StreamServer] Input protocol: {InputProtocol}, DataChannel label: {DataChannelLabel}");

            // 3.5. Initialize audio pipeline (if enabled)
            if (EnableAudio)
            {
                try
                {
                    GD.Print("[StreamServer] Initializing audio pipeline...");

                    _audioCapture = new AudioCapture();
                    _audioEncoder = new OpusAudioEncoder();
                    _audioEncoder.Configure(48000, 2, AudioBitrateKbps);
                    _audioEncoder.OnEncodedFrame += OnEncodedAudioFrame;

                    GD.Print($"[StreamServer] Audio encoder initialized: 48kHz stereo {AudioBitrateKbps}kbps");
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[StreamServer] Audio init failed (non-fatal): {ex.Message}");
                    _audioEncoder?.Dispose();
                    _audioEncoder = null;
                    _audioCapture = null;
                }
            }

            // 4. Connect signaling
            GD.Print($"[StreamServer] Step 4: Connecting signaling to {SignalingUrl}...");
            _signaling = new SignalingClient(SignalingUrl, ConnectionId);
            _signaling.OnOfferReceived += OnSignalingOffer;
            _signaling.OnAnswerReceived += OnSignalingAnswer;
            _signaling.OnCandidateReceived += OnSignalingCandidate;
            _signaling.OnConnected += (s, e) => EmitSignal(SignalName.ClientConnected, e.ConnectionId);
            _signaling.OnDisconnected += (s, e) => EmitSignal(SignalName.ClientDisconnected, e.ConnectionId);
            _signaling.OnShouldCreateOffer += OnShouldCreateOffer;  // URS: impolite peer initiates

            await _signaling.ConnectAsync();

            // 5. Start encode pipeline (async channel + background thread)
            GD.Print("[StreamServer] Step 5: Starting encode pipeline...");
            StartEncodePipeline();

            // 6. Capture deferred — started in _Process once encoder is ready
            _isRunning = true;

            EmitSignal(SignalName.StreamStarted, w, h);
            GD.Print($"[StreamServer] Streaming started: {w}x{h} @ {TargetFps}fps, {BitrateKbps}kbps");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[StreamServer] Start failed: {ex.GetType().Name}: {ex.Message}");
            GD.PrintErr($"[StreamServer] Stack: {ex.StackTrace}");
            if (ex.InnerException != null)
                GD.PrintErr($"[StreamServer] Inner: {ex.InnerException.GetType().Name}: {ex.InnerException.Message}");
            StopStream();
        }
    }

    /// <summary>Stop the pipeline and release all resources.</summary>
    public void StopStream()
    {
        if (!_isRunning && _encoder == null && _streamer == null && _signaling == null)
            return;

        GD.Print("[StreamServer] Stopping stream...");

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

        _remoteRenderer?.Dispose();
        _remoteRenderer = null;

        _streamer?.Dispose();
        _streamer = null;

        _signaling?.DisposeAsync().AsTask().Wait(1000);
        _signaling = null;

        _capture.Dispose();

        _inputInjector = null;

        _remoteConnectionId = null;
        _step1Done = false;
        _step2Done = false;
        _connected = false;

        _framesEncoded = 0;
        _bytesEncoded = 0;
        _encodeMsAccum = 0;
        _captureMsAccum = 0;
        _statFrameCount = 0;
        _statTimer = 0;
        _framesDroppedPool = 0;
        _framesDroppedChannel = 0;
        _remoteFrameCount = 0;

        EmitSignal(SignalName.StreamStopped);
        GD.Print("[StreamServer] Streaming stopped");
    }

    public override void _ExitTree()
    {
        StopStream();
        _inputParser?.Dispose();
        base._ExitTree();
    }

    #region Pipeline Callbacks

    /// <summary>
    /// Background encoding loop: reads frames from channel and encodes them.
    /// Runs on a thread pool thread, decoupled from the Godot main thread.
    /// </summary>
    private int _encodeLoopFrameCount;
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

    /// <summary>
    /// Starts the async encode pipeline: bounded channel + background consumer task.
    /// </summary>
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

    /// <summary>
    /// Stops the async encode pipeline and waits for the background task to finish.
    /// </summary>
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

    // ── Existing pipeline callbacks below ──

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
        GD.Print($"[StreamServer] Resolution changed to {newWidth}x{newHeight} — reconfiguring encoder");

        if (_encoder != null)
        {
            try
            {
                _encoder.Configure(newWidth, newHeight, TargetFps, BitrateKbps, MaxRateMultiplier);
                _encoder.ForceKeyframe();
                GD.Print($"[StreamServer] Encoder reconfigured for {newWidth}x{newHeight}");
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[StreamServer] Failed to reconfigure encoder: {ex.Message}");
            }
        }

        // Update input injector viewport reference if needed
        _inputInjector?.UpdateViewportSize(newWidth, newHeight);
    }

    private void OnEncodedNalu(byte[] naluData, int length, int isKeyframe)
    {
        _streamer?.SendH264Nalu(naluData, length, isKeyframe != 0);
        _bytesEncoded += length;
    }

    private void OnEncodedAv1Obu(byte[] obuData, int length, int isKeyframe)
    {
        _streamer?.SendAv1Obu(obuData, length, isKeyframe != 0);
        _bytesEncoded += length;
    }

    private int _vp9FrameCount;
    private void OnEncodedVp9Frame(byte[] frameData, int length, int isKeyframe)
    {
        _streamer?.SendVp9Frame(frameData, length, isKeyframe != 0);
        _bytesEncoded += length;
        _ = Interlocked.Increment(ref _vp9FrameCount);
    }

    private void OnInputFromBrowser(byte[] data)
    {
        _inputParser?.OnDataChannelMessage(data);
    }

    private void OnEncodedAudioFrame(byte[] data, int length)
    {
        _streamer?.SendAudio(data, length);
        // Don't add to _bytesEncoded — it's video-only stats
    }

    /// <summary>
    /// Handles received remote video frames from the browser (bidirectional mode).
    /// The frame data is encoded (H.264/VP8/VP9) — needs decoding before rendering.
    /// For the initial implementation, we expose the raw encoded frames.
    /// A future task will add FFmpeg-based decoding to produce BGRA pixel data.
    /// </summary>
    private int _remoteFrameCount;
    private void OnRemoteVideoFrame(byte[] frameData, SIPSorceryMedia.Abstractions.VideoFormat format)
    {
        _ = Interlocked.Increment(ref _remoteFrameCount);

        // TODO: Add FFmpeg H.264/VP8 decoder here to convert frameData → BGRA pixels
        // then call _remoteRenderer.EnqueueFrame(bgraPixels, width, height)
        //
        // For now, remote frames are received and counted but not yet decoded for display.
        // The full decode pipeline will be added in a subsequent task.
    }

    private void DrainInput()
    {
        while (_inputParser != null && _inputParser.TryDequeue(out var evt))
        {
            EmitSignal(SignalName.InputReceived,
                (int)evt.Type, evt.X, evt.Y, evt.Button, (int)evt.KeyCode);
            
            _inputInjector?.InjectEvent(evt);
        }
    }
    
    #endregion

    private string? _remoteConnectionId;  // Browser's connectionId — used as sender ID for all outbound messages
    private volatile bool _step1Done;      // True after data-only answer sent (SCTP established)
    private volatile bool _step2Done;      // True after video renegotiation offer sent
    private volatile bool _connected;      // True after WebRTC connection is fully established

    #region Signaling Callbacks

    /// <summary>
    /// Two-step negotiation:
    ///   Step 1: Answer browser's data-only offer with data-only answer → SCTP/DataChannel established
    ///   Step 2: After ICE connects, send renegotiation offer with video+data → video starts
    /// 
    /// Why two steps: Chrome's setRemoteDescription(answer) requires the answer to have the
    /// same number of m= sections as the offer. The browser's offer has 1 section (application).
    /// We cannot add video in the answer. Instead, we answer data-only, then renegotiate.
    /// </summary>
    private void OnSignalingOffer(object? sender, OfferReceivedEventArgs e)
    {
        GD.Print($"[StreamServer] Received offer from {e.FromConnectionId} ({e.Sdp.Length} bytes)");

        if (_step2Done)
        {
            GD.Print("[StreamServer] Already negotiating — ignoring duplicate offer");
            return;
        }

        if (string.IsNullOrEmpty(_remoteConnectionId))
        {
            _remoteConnectionId = e.FromConnectionId;
            GD.Print($"[StreamServer] Using browser connectionId: {_remoteConnectionId}");
        }

        if (!_step1Done)
        {
            _streamer!.SetRemoteOffer(e.Sdp);
            var answerSdp = _streamer.CreateAnswer();
            _step1Done = true;

            GD.Print($"[StreamServer] Step 1: Sending data-only answer ({answerSdp.Length} bytes)");
            _signaling!.SendAnswer(answerSdp, _remoteConnectionId!);
            return;
        }

        GD.Print("[StreamServer] Ignoring duplicate data-only offer");
    }

    private void OnSignalingAnswer(object? sender, AnswerReceivedEventArgs e)
    {
        GD.Print($"[StreamServer] Received answer from {e.FromConnectionId} ({e.Sdp.Length} bytes)");
        _remoteConnectionId = e.FromConnectionId;

        // Patch browser's H265 level-id from 93 back to 123.
        // The browser's experimental H265 WebRTC downgrades our level-id=123 offer
        // to level-id=93 in its answer. Since SIPSorcery replaces our LocalTrack
        // capabilities with the remote's, this causes downstream issues.
        // 1152x648@60fps requires ~177K mac/sec which needs level 4.1 (123).
        var patchedSdp = PatchH265LevelId(e.Sdp);
        if (patchedSdp != e.Sdp)
        {
            GD.Print($"[StreamServer] Patched H265 level-id in browser answer: 93 → 123");
        }

        _streamer!.SetRemoteAnswer(patchedSdp);
        GD.Print("[StreamServer] Step 2 answer set successfully — video should be active");

        // Now that the browser has accepted our video offer, force a keyframe
        // so the decoder can initialize properly.
        // In Auto codec mode, the encoder is lazy-initialized in OnVideoFormatNegotiated
        // which may fire asynchronously — check again in the encoder init callback.
        if (_encoder != null)
        {
            GD.Print("[StreamServer] Forcing keyframe after answer received");
            _encoder.ForceKeyframe();
        }
        else
        {
            GD.Print("[StreamServer] Encoder not yet initialized (Auto mode) — keyframe will be forced after encoder ready");
        }
    }

    private void OnSignalingCandidate(object? sender, CandidateReceivedEventArgs e)
    {
        // Pass ICE candidates through as-is. In BUNDLE mode all sections share
        // one ICE transport. No remapping needed.
        _streamer!.AddIceCandidate(e.Candidate, e.SdpMLineIndex, e.SdpMid);
    }

    private void OnLocalIceCandidate(RTCIceCandidate candidate)
    {
        if (string.IsNullOrEmpty(_remoteConnectionId))
        {
            GD.Print("[StreamServer] Skipping ICE candidate — no remote connectionId yet");
            return;
        }

        GD.Print($"[StreamServer] Sending ICE candidate as {_remoteConnectionId}: {candidate.candidate}");
        _signaling?.SendCandidate(
            candidate.candidate,
            (int)candidate.sdpMLineIndex,
            candidate.sdpMid ?? "0",
            _remoteConnectionId);
    }

    private void OnStreamerStateChanged(RTCPeerConnectionState state)
    {
        GD.Print($"[StreamServer] WebRTC state: {state}");

        if (state == RTCPeerConnectionState.connected)
        {
            _connected = true;

            // Step 2: ICE connected after step 1 → send video renegotiation offer.
            // We wait for "connected" to ensure step 1 is fully established before
            // starting renegotiation. This avoids SDP processing race conditions.
            if (_step1Done && !_step2Done)
            {
                _ = System.Threading.Tasks.Task.Run(() =>
                {
                    try
                    {
                        _streamer!.AddVideoTrack();
                        var videoOfferSdp = _streamer.CreateOffer();
                        _step2Done = true;

                        GD.Print($"[StreamServer] Step 2: Sending video renegotiation offer ({videoOfferSdp.Length} bytes)");
                        _signaling!.SendOffer(videoOfferSdp, _remoteConnectionId!);
                    }
                    catch (Exception ex)
                    {
                        GD.PrintErr($"[StreamServer] Step 2 failed: {ex.Message}");
                    }
                });
            }
        }
        else if (state == RTCPeerConnectionState.failed ||
                 state == RTCPeerConnectionState.disconnected ||
                 state == RTCPeerConnectionState.closed)
        {
            _connected = false;
        }
    }

    private void OnKeyframeRequested()
    {
        // Do NOT force keyframe here — the encoder may not have a video track
        // registered yet. Keyframe will be forced after the browser's answer
        // is received in OnSignalingAnswer.
    }

    /// <summary>
    /// Called when server assigns impolite (private mode, first connector).
    /// In public mode we get polite=true so this won't fire; we create our offer
    /// in OnSignalingOffer instead, using the browser's connectionId.
    /// </summary>
    private void OnShouldCreateOffer(object? sender, EventArgs e)
    {
        GD.Print("[StreamServer] Impolite peer — waiting for browser's offer to learn its connectionId...");
    }

    /// <summary>
    /// Patches the H265 level-id in a browser SDP answer back to the value we offered.
    /// Chromium's experimental H265 WebRTC implementation downgrades level-id=123
    /// (Level 4.1) to level-id=93 (Level 3.1) in its answer, which is insufficient
    /// for 1152x648@60fps (~177K mac/sec requires Level 4.1+).
    /// This causes the browser decoder to malfunction (Decoder: undefined, black flower screen).
    /// </summary>
    private static readonly Regex H265LevelIdRegex = new(
        @"(level-id=)93(;)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string PatchH265LevelId(string sdp)
    {
        return H265LevelIdRegex.Replace(sdp, "${1}123${2}");
    }

    #endregion
}