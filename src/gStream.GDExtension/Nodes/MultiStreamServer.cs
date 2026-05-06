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
using Godot.Bridge;
using Godot.Collections;
using gStream.Core.Capture;
using gStream.Core.Encoding;
using gStream.Core.Input;
using gStream.Core.Streaming;
using gStream.GDExtension.Capture;
using gStream.GDExtension.Input;
using SIPSorcery.Net;

namespace gStream.GDExtension.Nodes;

/// <summary>
/// Multi-client streaming server for multiplay mode.
/// GDExtension version with manual BindMembers registration.
/// </summary>
public sealed partial class MultiStreamServer : Node
{
    #region Properties (replacing [Export] — bound via BindMembers)

    public SubViewport? SourceViewport { get; set; }

    public bool CaptureMainWindow { get; set; } = true;

    public int TargetFps { get; set; } = 60;

    public int BitrateKbps { get; set; } = 8000;

    public float MaxRateMultiplier { get; set; } = 2.0f;

    public EncoderPreset Preset { get; set; } = EncoderPreset.UltraLowLatency;

    public VideoCodec Codec { get; set; } = VideoCodec.Auto;

    public bool EnableAudio { get; set; } = true;

    public int AudioBitrateKbps { get; set; } = 128;

    public string SignalingUrl { get; set; } = "ws://localhost:80";

    public string ConnectionId { get; set; } = Guid.NewGuid().ToString();

    public string[] IceServers { get; set; } = { "stun:stun.l.google.com:19302" };

    public string BindAddress { get; set; } = "";

    public string[] AllowedIcePrefixes { get; set; } = Array.Empty<string>();

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
    private string? _negotiatedCodecName;

    private Channel<CapturedFrame>? _frameChannel;
    private CancellationTokenSource? _encodeCts;
    private Task? _encodeTask;
    private int _encodeLoopFrameCount;
    private int _framesDroppedPool;
    private int _framesDroppedChannel;

    private int _framesEncoded;
    private long _bytesEncoded;
    private double _encodeMsAccum;
    private double _captureMsAccum;
    private int _statFrameCount;
    private double _statTimer;

    #endregion

    #region Godot Lifecycle

    protected override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        InstallDebugListenerFilter();

        GD.Print("[MultiStreamServer] Ready — auto-starting stream");
        StartStream();
    }

    protected override void _Process(double delta)
    {
        if (!_isRunning) return;

        if (!_captureStarted && _encoder != null && _clients.Values.Any(c => c.IsConnected))
        {
            _capture.Start();
            _captureStarted = true;
            GD.Print("[MultiStreamServer] Capture started (encoder ready, peer(s) connected)");
            _audioCapture?.Start();
        }

        DrainAllInputs();
        DrainMultiplayMessages();

        if (_audioCapture != null && _audioEncoder != null && _isRunning && _captureStarted)
        {
            while (_audioCapture.TryGetSamples(out var samples))
                _audioEncoder.Encode(samples);
        }

        _statFrameCount++;
        _statTimer += delta;
        if (_statTimer >= 1.0)
        {
            var fps = _statFrameCount;
            var avgEncodeMs = _statFrameCount > 0 ? _encodeMsAccum / _statFrameCount : 0;
            var avgCaptureMs = _statFrameCount > 0 ? _captureMsAccum / _statFrameCount : 0;
            var bitrateKbps = (int)(_bytesEncoded * 8 / 1000.0 / _statTimer);
            var droppedFrames = _framesDroppedPool + _framesDroppedChannel;

            EmitSignal("stats_updated", fps, bitrateKbps, droppedFrames, avgEncodeMs, avgCaptureMs);

            if (droppedFrames > 0)
                GD.PrintErr($"[MultiStreamServer] Frame drops in last 1s: pool={_framesDroppedPool}, channel={_framesDroppedChannel}");

            _statFrameCount = 0;
            _statTimer = 0;
            _encodeMsAccum = 0;
            _captureMsAccum = 0;
            _bytesEncoded = 0;
            _framesDroppedPool = 0;
            _framesDroppedChannel = 0;
        }
    }

    protected override void _Notification(int what)
    {
        base._Notification(what);

        // NOTIFICATION_PREDELETE = 1 — fired before node destruction.
        if (what == 1) // Node.NotificationPredelete
        {
            StopStream();
        }
    }

    protected override void _ExitTree()
    {
        StopStream();
        base._ExitTree();
    }

    #endregion

    #region Debug Filter

    private static bool _debugFilterInstalled;
    private static void InstallDebugListenerFilter()
    {
        if (_debugFilterInstalled) return;
        _debugFilterInstalled = true;

        Trace.Listeners.Clear();
        Trace.Listeners.Add(new DefaultTraceListener());

        var mainThreadId = Thread.CurrentThread.ManagedThreadId;
        var originalOut = Console.Out;
        Console.SetOut(new StreamServer.ThreadSafeTextWriter(originalOut, mainThreadId));
    }

    #endregion

    #region Start / Stop

    public async void StartStream()
    {
        if (_isRunning)
        {
            GD.PrintErr("[MultiStreamServer] Already streaming");
            return;
        }

        try
        {
            GD.Print("[MultiStreamServer] Step 1: Initializing capture...");
            if (SourceViewport != null)
                _capture.Initialize(SourceViewport);
            else if (CaptureMainWindow)
            {
                _capture.Initialize(GetViewport());
                GD.Print("[MultiStreamServer] Capturing current window");
            }
            else
            {
                GD.PrintErr("[MultiStreamServer] No capture source!");
                return;
            }

            _capture.OnFrame += OnFrameCaptured;
            _capture.OnResolutionChanged += OnResolutionChanged;
            var (w, h) = _capture.Resolution;

            // Clamp to encoder minimum — NVENC requires >= 48x16, AV1/VP9 similar.
            // Use 64x64 as a safe floor, ensuring even dimensions for NV12.
            const int MinDim = 64;
            if (w < MinDim || h < MinDim)
            {
                w = Math.Max(w, MinDim);
                h = Math.Max(h, MinDim);
                w = (w + 1) & ~1;
                h = (h + 1) & ~1;
                GD.Print($"[MultiStreamServer] Resolution clamped to {w}x{h} (encoder minimum)");
            }

            var viewport = SourceViewport ?? GetViewport();
            _inputInjector = new GodotInputInjector(viewport!);

            var preset = Preset;
            var codecPref = Codec;

            if (codecPref == VideoCodec.Auto)
            {
                GD.Print("[MultiStreamServer] Auto codec — deferring encoder");
                _encoder = null;
            }
            else
            {
                GD.Print($"[MultiStreamServer] Initializing encoder ({w}x{h} @ {TargetFps}fps)...");
                CreateEncoder(codecPref, preset, w, h);
            }

            if (EnableAudio)
            {
                try
                {
                    _audioCapture = new AudioCapture();
                    _audioEncoder = new OpusAudioEncoder();
                    _audioEncoder.Configure(48000, 2, AudioBitrateKbps);
                    _audioEncoder.OnEncodedFrame += OnEncodedAudioFrame;
                }
                catch (Exception ex)
                {
                    GD.PrintErr($"[MultiStreamServer] Audio init failed: {ex.Message}");
                    _audioEncoder?.Dispose();
                    _audioEncoder = null;
                    _audioCapture = null;
                }
            }

            GD.Print($"[MultiStreamServer] Connecting signaling to {SignalingUrl}...");
            _signaling = new SignalingClient(SignalingUrl, ConnectionId);
            _signaling.OnOfferReceived += OnSignalingOffer;
            _signaling.OnAnswerReceived += OnSignalingAnswer;
            _signaling.OnCandidateReceived += OnSignalingCandidate;
            _signaling.OnConnected += (s, e) => GD.Print($"[MultiStreamServer] Signaling connected as {e.ConnectionId}");
            _signaling.OnDisconnected += OnSignalingDisconnected;
            _signaling.OnShouldCreateOffer += (s, e) => GD.Print("[MultiStreamServer] Impolite peer — waiting...");

            await _signaling.ConnectAsync();

            StartEncodePipeline();
            _isRunning = true;

            EmitSignal("stream_started", w, h);
            GD.Print($"[MultiStreamServer] Streaming started: {w}x{h} @ {TargetFps}fps, max {MaxClients} clients");
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MultiStreamServer] Start failed: {ex.GetType().Name}: {ex.Message}");
            StopStream();
        }
    }

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

        _audioCapture?.Stop();
        _audioCapture?.Dispose();
        _audioCapture = null;
        _audioEncoder?.Dispose();
        _audioEncoder = null;

        StopEncodePipeline();
        _encoder?.Dispose();
        _encoder = null;

        foreach (var kvp in _clients)
            try { kvp.Value.Dispose(); } catch { }
        _clients.Clear();

        // Fire-and-forget async dispose — avoid blocking the main thread
        // during node destruction (which causes "Children name does not match
        // parent name in hashtable" crash in Godot's ~Node destructor).
        var signaling = _signaling;
        _signaling = null;
        if (signaling != null)
        {
            _ = signaling.DisposeAsync().AsTask().ContinueWith(static t =>
            {
                if (t.IsFaulted && t.Exception != null)
                {
                    GD.PrintErr($"[MultiStreamServer] Signaling dispose error: {t.Exception.InnerException?.Message ?? t.Exception.Message}");
                }
            }, TaskScheduler.Default);
        }
        _capture.Dispose();
        _inputInjector = null;
        _negotiatedCodecName = null;

        _framesEncoded = 0; _bytesEncoded = 0;
        _encodeMsAccum = 0; _captureMsAccum = 0;
        _statFrameCount = 0; _statTimer = 0;
        _framesDroppedPool = 0; _framesDroppedChannel = 0;

        EmitSignal("stream_stopped");
        GD.Print("[MultiStreamServer] Streaming stopped");
    }

    #endregion

    #region Encoder Management

    private void CreateEncoder(VideoCodec codecPref, EncoderPreset preset, int w, int h)
    {
        if (codecPref.IsAV1Family())
        {
            var av1 = new AV1HardwareEncoder(preset);
            av1.Configure(w, h, TargetFps, BitrateKbps, MaxRateMultiplier);
            av1.OnEncodedNALU += OnEncodedAv1Obu;
            _encoder = av1;
        }
        else if (codecPref.IsVP9Family())
        {
            var vp9 = new VP9HardwareEncoder(preset);
            vp9.Configure(w, h, TargetFps, BitrateKbps, MaxRateMultiplier);
            vp9.OnEncodedNALU += OnEncodedVp9Frame;
            _encoder = vp9;
        }
        else
        {
            _encoder = new H264HardwareEncoder(preset, codecPref);
            _encoder.Configure(w, h, TargetFps, BitrateKbps, MaxRateMultiplier);
            _encoder.OnEncodedNALU += OnEncodedNalu;
        }
    }

    private void CreateEncoderFromNegotiatedCodec(string negotiatedCodec)
    {
        try
        {
            var (w, h) = _capture.Resolution;

            // Clamp to encoder minimum
            const int MinDim = 64;
            if (w < MinDim || h < MinDim)
            {
                w = Math.Max(w, MinDim);
                h = Math.Max(h, MinDim);
                w = (w + 1) & ~1;
                h = (h + 1) & ~1;
            }

            if (negotiatedCodec == "AV1")
            {
                var av1 = new AV1HardwareEncoder(Preset);
                av1.Configure(w, h, TargetFps, BitrateKbps, MaxRateMultiplier);
                av1.OnEncodedNALU += OnEncodedAv1Obu;
                _encoder = av1;
            }
            else if (negotiatedCodec == "VP9")
            {
                var vp9 = new VP9HardwareEncoder(Preset);
                vp9.Configure(w, h, TargetFps, BitrateKbps, MaxRateMultiplier);
                vp9.OnEncodedNALU += OnEncodedVp9Frame;
                _encoder = vp9;
            }
            else
            {
                var resolved = negotiatedCodec == "H265" ? VideoCodec.H265_Main_L41 : VideoCodec.H264_High_L31;
                _encoder = new H264HardwareEncoder(Preset, resolved);
                _encoder.Configure(w, h, TargetFps, BitrateKbps, MaxRateMultiplier);
                _encoder.OnEncodedNALU += OnEncodedNalu;
            }

            _negotiatedCodecName = negotiatedCodec;
            GD.Print($"[MultiStreamServer] Encoder lazy-initialized, codec={negotiatedCodec}");

            if (_clients.Values.Any(c => c.IsConnected))
                _encoder.ForceKeyframe();
        }
        catch (Exception ex)
        {
            GD.PrintErr($"[MultiStreamServer] Failed to lazy-init encoder: {ex.Message}");
        }
    }

    #endregion

    #region Client Management

    private ClientConnection? CreateClient(string browserConnectionId)
    {
        if (_clients.Count >= MaxClients)
        {
            GD.PrintErr($"[MultiStreamServer] Max clients ({MaxClients}) reached — rejecting {browserConnectionId}");
            return null;
        }

        var iceServersList = IceServers.Select(url => new RTCIceServer { urls = url }).ToList();

        IPAddress? bindAddr = null;
        if (!string.IsNullOrEmpty(BindAddress))
        {
            try { bindAddr = IPAddress.Parse(BindAddress); }
            catch { GD.PrintErr($"[MultiStreamServer] Invalid BindAddress: {BindAddress}"); }
        }

        string[]? icePrefixes = AllowedIcePrefixes?.Length > 0 ? AllowedIcePrefixes : null;

        WebRtcStreamer streamer;

        if (_negotiatedCodecName == null && Codec == VideoCodec.Auto)
        {
            streamer = new WebRtcStreamer(iceServersList, TargetFps, "H264",
                declareBothCodecs: true, bindAddress: bindAddr, allowedIcePrefixes: icePrefixes);

            streamer.OnVideoFormatNegotiated += (negotiatedCodec) =>
            {
                if (_encoder == null)
                    CreateEncoderFromNegotiatedCodec(negotiatedCodec);
            };
        }
        else
        {
            var codecName = _negotiatedCodecName;
            string? fmtp = null;

            if (codecName != null)
            {
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
                var (sdpCodecName, sdpFmtp) = Codec.ToSdp();
                codecName = sdpCodecName;
                fmtp = sdpFmtp;
            }

            streamer = new WebRtcStreamer(iceServersList, TargetFps, codecName,
                fmtp, bindAddress: bindAddr, allowedIcePrefixes: icePrefixes);
        }

        var connection = new ClientConnection(browserConnectionId, streamer);

        streamer.OnInputEvent += (data) => connection.InputRelay.OnDataChannelMessage(data);
        streamer.OnStateChanged += (state) => OnClientStateChanged(browserConnectionId, state);
        streamer.OnIceCandidate += (candidate) =>
        {
            _signaling?.SendCandidate(candidate.candidate, (int)candidate.sdpMLineIndex,
                candidate.sdpMid ?? "0", browserConnectionId);
        };
        streamer.OnKeyframeRequested += () => { };

        _clients[browserConnectionId] = connection;

        var existingClient = _clients.Values.FirstOrDefault(c => c.IsConnected && c != connection);
        if (existingClient != null)
        {
            var kfData = existingClient.Streamer.TryGetPendingKeyframe(out var kfLen);
            if (kfData != null)
                connection.Streamer.CopyPendingKeyframe(kfData, kfLen);
        }

        return connection;
    }

    private void RemoveClient(string connectionId)
    {
        if (_clients.TryRemove(connectionId, out var client))
        {
            try { client.Dispose(); } catch { }
            EmitSignal("client_disconnected", connectionId);
        }
    }

    #endregion

    #region Pipeline Callbacks

    private async Task EncodeLoopAsync(CancellationToken ct)
    {
        while (true)
        {
            CapturedFrame frame;
            try { frame = await _frameChannel!.Reader.ReadAsync(ct); }
            catch (OperationCanceledException) { break; }

            if (_encoder == null) { frame.Dispose(); continue; }

            var encodeStart = Godot.Time.Singleton.GetTicksUsec();
            _encoder.Encode(frame);
            var elapsed = (Godot.Time.Singleton.GetTicksUsec() - encodeStart) / 1000.0;

            Interlocked.Increment(ref _encodeLoopFrameCount);
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
            try { _encodeTask.Wait(TimeSpan.FromSeconds(2)); } catch { }
        if (_frameChannel != null)
            while (_frameChannel.Reader.TryRead(out var frame))
                frame.Dispose();
        _frameChannel = null;
        _encodeCts = null;
        _encodeTask = null;
    }

    private void OnFrameCaptured(CapturedFrame frame)
    {
        _captureMsAccum += _capture.LastCaptureUs / 1000.0;
        if (_capture.PoolExhaustionCount > 0)
        {
            _framesDroppedPool += _capture.PoolExhaustionCount;
            _capture.PoolExhaustionCount = 0;
        }
        if (!_isRunning || _encoder == null || _frameChannel == null)
        { frame.Dispose(); return; }
        if (!_frameChannel.Writer.TryWrite(frame))
        { _framesDroppedChannel++; frame.Dispose(); }
    }

    private void OnResolutionChanged(int newWidth, int newHeight)
    {
        if (_encoder != null)
        {
            try
            {
                _encoder.Configure(newWidth, newHeight, TargetFps, BitrateKbps, MaxRateMultiplier);
                _encoder.ForceKeyframe();
            }
            catch (Exception ex)
            {
                GD.PrintErr($"[MultiStreamServer] Failed to reconfigure encoder: {ex.Message}");
            }
        }
        _inputInjector?.UpdateViewportSize(newWidth, newHeight);
    }

    private void OnEncodedNalu(byte[] naluData, int length, int isKeyframe)
    {
        foreach (var client in _clients.Values)
            if (client.IsConnected) client.Streamer.SendH264Nalu(naluData, length, isKeyframe != 0);
        _bytesEncoded += length;
    }

    private void OnEncodedAv1Obu(byte[] obuData, int length, int isKeyframe)
    {
        foreach (var client in _clients.Values)
            if (client.IsConnected) client.Streamer.SendAv1Obu(obuData, length, isKeyframe != 0);
        _bytesEncoded += length;
    }

    private int _vp9FrameCount;
    private void OnEncodedVp9Frame(byte[] frameData, int length, int isKeyframe)
    {
        foreach (var client in _clients.Values)
            if (client.IsConnected) client.Streamer.SendVp9Frame(frameData, length, isKeyframe != 0);
        _bytesEncoded += length;
    }

    private void OnEncodedAudioFrame(byte[] data, int length)
    {
        foreach (var client in _clients.Values)
            if (client.IsConnected) client.Streamer.SendAudio(data, length);
    }

    #endregion

    #region Signaling Callbacks

    private void OnSignalingOffer(object? sender, OfferReceivedEventArgs e)
    {
        var browserId = e.FromConnectionId;
        if (!_clients.TryGetValue(browserId, out var client))
        {
            client = CreateClient(browserId);
            if (client == null) return;
        }

        if (client.Step2Done) return;

        if (!client.Step1Done)
        {
            client.Streamer.SetRemoteOffer(e.Sdp);
            var answerSdp = client.Streamer.CreateAnswer();
            client.Step1Done = true;
            _signaling!.SendAnswer(answerSdp, browserId);
            EmitSignal("client_connected", browserId, client.Label);
        }
    }

    private void OnSignalingAnswer(object? sender, AnswerReceivedEventArgs e)
    {
        var browserId = e.FromConnectionId;
        if (!_clients.TryGetValue(browserId, out var client)) return;

        var patchedSdp = PatchH265LevelId(e.Sdp);
        client.Streamer.SetRemoteAnswer(patchedSdp);
        client.Streamer.ForceKeyframeSend();

        if (_encoder != null)
            _encoder.ForceKeyframe();
    }

    private void OnSignalingCandidate(object? sender, CandidateReceivedEventArgs e)
    {
        if (_clients.TryGetValue(e.FromConnectionId, out var client))
            client.Streamer.AddIceCandidate(e.Candidate, e.SdpMLineIndex, e.SdpMid);
    }

    private void OnSignalingDisconnected(object? sender, ConnectionEventArgs e)
    {
        if (e.ConnectionId == ConnectionId) return;
        if (_clients.ContainsKey(e.ConnectionId))
            RemoveClient(e.ConnectionId);
    }

    private void OnClientStateChanged(string connectionId, RTCPeerConnectionState state)
    {
        if (!_clients.TryGetValue(connectionId, out var client)) return;

        if (state == RTCPeerConnectionState.connected)
        {
            if (client.Step1Done && !client.Step2Done)
            {
                _ = Task.Run(() =>
                {
                    try
                    {
                        client.Streamer.AddVideoTrack();
                        var videoOfferSdp = client.Streamer.CreateOffer();
                        client.Step2Done = true;
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
            RemoveClient(connectionId);
        }
    }

    private static readonly Regex H265LevelIdRegex = new(
        @"(level-id=)93(;)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string PatchH265LevelId(string sdp)
        => H265LevelIdRegex.Replace(sdp, "${1}123${2}");

    #endregion

    #region Input & Multiplay

    private void DrainAllInputs()
    {
        foreach (var kvp in _clients)
        {
            var client = kvp.Value;
            while (client.InputRelay.TryDequeue(out var evt))
                _inputInjector?.InjectEvent(evt);
        }
    }

    private void DrainMultiplayMessages()
    {
        foreach (var kvp in _clients)
        {
            var senderId = kvp.Key;
            var sender = kvp.Value;

            while (sender.MultiplayMessages.TryDequeue(out var message))
            {
                EmitSignal("multiplay_message_received", senderId, message);

                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(message);
                    var root = doc.RootElement;
                    if (root.TryGetProperty("type", out var typeEl) && typeEl.GetInt32() == 0)
                    {
                        if (root.TryGetProperty("argument", out var argEl))
                        {
                            sender.Label = argEl.GetString() ?? sender.Label;
                        }
                    }
                }
                catch { }
            }
        }
    }

    #endregion

    #region BindMembers — GDExtension manual registration

    internal static void BindMembers(ClassRegistrationContext context)
    {
        context.BindConstructor(() => new MultiStreamServer());

        // ── Capture ──
        context.AddPropertyGroup("Capture");
        context.BindProperty(
            new PropertyDefinition(new StringName("capture_source_viewport"), VariantType.Object)
            {
                ClassName = new StringName("SubViewport"),
                Usage = PropertyUsageFlags.Default
            },
            static (MultiStreamServer inst) => inst.SourceViewport,
            static (MultiStreamServer inst, Node? val) => inst.SourceViewport = val as SubViewport);

        context.BindProperty(
            new PropertyDefinition(new StringName("capture_main_window"), VariantType.Bool)
            { Usage = PropertyUsageFlags.Default },
            static (MultiStreamServer inst) => inst.CaptureMainWindow,
            static (MultiStreamServer inst, bool val) => inst.CaptureMainWindow = val);

        // ── Encoding ──
        context.AddPropertyGroup("Encoding");
        context.BindProperty(
            new PropertyDefinition(new StringName("encoding_target_fps"), VariantType.Int)
            {
                Hint = PropertyHint.Range,
                HintString = "1,120,1",
                Usage = PropertyUsageFlags.Default
            },
            static (MultiStreamServer inst) => inst.TargetFps,
            static (MultiStreamServer inst, int val) => inst.TargetFps = val);

        context.BindProperty(
            new PropertyDefinition(new StringName("encoding_bitrate_kbps"), VariantType.Int)
            {
                Hint = PropertyHint.Range,
                HintString = "500,50000,100,suffix:kbps",
                Usage = PropertyUsageFlags.Default
            },
            static (MultiStreamServer inst) => inst.BitrateKbps,
            static (MultiStreamServer inst, int val) => inst.BitrateKbps = val);

        context.BindProperty(
            new PropertyDefinition(new StringName("encoding_max_rate_multiplier"), VariantType.Float)
            {
                Hint = PropertyHint.Range,
                HintString = "1.0,4.0,0.1",
                Usage = PropertyUsageFlags.Default
            },
            static (MultiStreamServer inst) => inst.MaxRateMultiplier,
            static (MultiStreamServer inst, float val) => inst.MaxRateMultiplier = val);

        context.BindProperty(
            new PropertyDefinition(new StringName("encoding_preset"), VariantType.Int)
            {
                Hint = PropertyHint.Enum,
                HintString = "Ultra Low Latency:0,Low Latency:1,Balanced:2,High Quality:3",
                Usage = PropertyUsageFlags.Default
            },
            static (MultiStreamServer inst) => (int)inst.Preset,
            static (MultiStreamServer inst, int val) => inst.Preset = (EncoderPreset)val);

        context.BindProperty(
            new PropertyDefinition(new StringName("encoding_codec"), VariantType.Int)
            {
                Hint = PropertyHint.Enum,
                HintString = "Auto:0,H264 High L31:1,H264 Main L31:2,H264 CBaseline L31:3,H264 Baseline L31:4,H265 Main L41:10,AV1 Main L5:20,VP9 Profile0:30,VP9 Profile2:31",
                Usage = PropertyUsageFlags.Default
            },
            static (MultiStreamServer inst) => (int)inst.Codec,
            static (MultiStreamServer inst, int val) => inst.Codec = (VideoCodec)val);

        // ── Audio ──
        context.AddPropertyGroup("Audio");
        context.BindProperty(
            new PropertyDefinition(new StringName("audio_enable"), VariantType.Bool)
            { Usage = PropertyUsageFlags.Default },
            static (MultiStreamServer inst) => inst.EnableAudio,
            static (MultiStreamServer inst, bool val) => inst.EnableAudio = val);

        context.BindProperty(
            new PropertyDefinition(new StringName("audio_bitrate_kbps"), VariantType.Int)
            {
                Hint = PropertyHint.Range,
                HintString = "16,512,8,suffix:kbps",
                Usage = PropertyUsageFlags.Default
            },
            static (MultiStreamServer inst) => inst.AudioBitrateKbps,
            static (MultiStreamServer inst, int val) => inst.AudioBitrateKbps = val);

        // ── Signaling ──
        context.AddPropertyGroup("Signaling");
        context.BindProperty(
            new PropertyDefinition(new StringName("signaling_url"), VariantType.String)
            { Usage = PropertyUsageFlags.Default },
            static (MultiStreamServer inst) => inst.SignalingUrl,
            static (MultiStreamServer inst, string val) => inst.SignalingUrl = val);

        context.BindProperty(
            new PropertyDefinition(new StringName("signaling_connection_id"), VariantType.String)
            { Usage = PropertyUsageFlags.Default },
            static (MultiStreamServer inst) => inst.ConnectionId,
            static (MultiStreamServer inst, string val) => inst.ConnectionId = val);

        // ── STUN/TURN ──
        context.AddPropertyGroup("STUN/TURN");
        context.BindProperty(
            new PropertyDefinition(new StringName("ice_servers"), VariantType.PackedStringArray)
            { Usage = PropertyUsageFlags.Default },
            static (MultiStreamServer inst) => new PackedStringArray(inst.IceServers),
            static (MultiStreamServer inst, PackedStringArray val) => inst.IceServers = val.ToArray());

        // ── Network ──
        context.AddPropertyGroup("Network");
        context.BindProperty(
            new PropertyDefinition(new StringName("network_bind_address"), VariantType.String)
            { Usage = PropertyUsageFlags.Default },
            static (MultiStreamServer inst) => inst.BindAddress,
            static (MultiStreamServer inst, string val) => inst.BindAddress = val);

        context.BindProperty(
            new PropertyDefinition(new StringName("network_allowed_ice_prefixes"), VariantType.PackedStringArray)
            { Usage = PropertyUsageFlags.Default },
            static (MultiStreamServer inst) => new PackedStringArray(inst.AllowedIcePrefixes),
            static (MultiStreamServer inst, PackedStringArray val) => inst.AllowedIcePrefixes = val.ToArray());

        // ── Multiplay ──
        context.AddPropertyGroup("Multiplay");
        context.BindProperty(
            new PropertyDefinition(new StringName("multiplay_max_clients"), VariantType.Int)
            {
                Hint = PropertyHint.Range,
                HintString = "1,16,1",
                Usage = PropertyUsageFlags.Default
            },
            static (MultiStreamServer inst) => inst.MaxClients,
            static (MultiStreamServer inst, int val) => inst.MaxClients = val);

        // Signals
        context.BindSignal(new SignalDefinition(new StringName("stream_started"))
        {
            Parameters =
            {
                new ParameterDefinition(new StringName("width"), VariantType.Int),
                new ParameterDefinition(new StringName("height"), VariantType.Int),
            }
        });

        context.BindSignal(new SignalDefinition(new StringName("stream_stopped")));

        context.BindSignal(new SignalDefinition(new StringName("client_connected"))
        {
            Parameters =
            {
                new ParameterDefinition(new StringName("connection_id"), VariantType.String),
                new ParameterDefinition(new StringName("label"), VariantType.String),
            }
        });

        context.BindSignal(new SignalDefinition(new StringName("client_disconnected"))
        {
            Parameters =
            {
                new ParameterDefinition(new StringName("connection_id"), VariantType.String),
            }
        });

        context.BindSignal(new SignalDefinition(new StringName("multiplay_message_received"))
        {
            Parameters =
            {
                new ParameterDefinition(new StringName("connection_id"), VariantType.String),
                new ParameterDefinition(new StringName("message"), VariantType.String),
            }
        });

        context.BindSignal(new SignalDefinition(new StringName("stats_updated"))
        {
            Parameters =
            {
                new ParameterDefinition(new StringName("fps"), VariantType.Int),
                new ParameterDefinition(new StringName("bitrate_kbps"), VariantType.Int),
                new ParameterDefinition(new StringName("pending_frames"), VariantType.Int),
                new ParameterDefinition(new StringName("encode_ms"), VariantType.Float),
                new ParameterDefinition(new StringName("capture_ms"), VariantType.Float),
            }
        });

        // Methods
        context.BindMethod(new StringName("start_stream"),
            static (MultiStreamServer inst) => { inst.StartStream(); });

        context.BindMethod(new StringName("stop_stream"),
            static (MultiStreamServer inst) => { inst.StopStream(); });
    }

    #endregion
}
