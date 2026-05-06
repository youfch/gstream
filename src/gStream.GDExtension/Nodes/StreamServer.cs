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
using Godot.Bridge;
using Godot.Collections;
using gStream.Core.Capture;
using gStream.Core.Encoding;
using gStream.Core.Input;
using gStream.Core.Streaming;
using gStream.GDExtension.Capture;
using gStream.GDExtension.Input;
using gStream.GDExtension.Render;
using SIPSorcery.Net;

namespace gStream.GDExtension.Nodes;

/// <summary>
/// Main Godot node that wires together: Capture → Encode → Stream pipeline.
/// GDExtension version with manual BindMembers registration.
/// </summary>
public sealed partial class StreamServer : Node
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

    public string DataChannelLabel { get; set; } = "input";

    public string InputProtocol { get; set; } = "urs";

    public string BindAddress { get; set; } = "";

    public string[] AllowedIcePrefixes { get; set; } = Array.Empty<string>();

    public bool BidirectionalMode { get; set; } = false;

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

    private int _framesEncoded;
    private long _bytesEncoded;
    private double _encodeMsAccum;
    private double _captureMsAccum;
    private int _statFrameCount;
    private double _statTimer;

    private bool _isRunning;
    private bool _captureStarted;

    private Channel<CapturedFrame>? _frameChannel;
    private CancellationTokenSource? _encodeCts;
    private Task? _encodeTask;
    private int _framesDroppedPool;
    private int _framesDroppedChannel;

    #endregion

    #region Godot Lifecycle

    protected override void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;

        InstallDebugListenerFilter();

        GD.Print("[StreamServer] Ready — auto-starting stream");
        StartStream();
    }

    protected override void _Process(double delta)
    {
        if (!_isRunning) return;

        if (!_captureStarted && _encoder != null && _streamer?.IsConnected == true)
        {
            _capture.Start();
            _captureStarted = true;
            GD.Print("[StreamServer] Capture started (encoder ready, peer connected)");
            _audioCapture?.Start();
        }

        DrainInput();

        if (_audioCapture != null && _audioEncoder != null && _isRunning && _captureStarted)
        {
            while (_audioCapture.TryGetSamples(out var samples))
            {
                _audioEncoder.Encode(samples);
            }
        }

        _remoteRenderer?.Process();

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

    protected override void _Notification(int what)
    {
        base._Notification(what);

        // NOTIFICATION_PREDELETE = 1 — fired before node destruction.
        // Handles cleanup when the node is freed without going through _ExitTree.
        // Also ensures StopStream runs even if _ExitTree was already called,
        // since StopStream is idempotent.
        if (what == 1) // Node.NotificationPredelete
        {
            StopStream();
            _inputParser?.Dispose();
        }
    }

    protected override void _ExitTree()
    {
        StopStream();
        _inputParser?.Dispose();
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
        Console.SetOut(new ThreadSafeTextWriter(originalOut, mainThreadId));
    }

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
            base.Dispose(disposing);
        }
    }

    #endregion

    #region Start / Stop

    public async void StartStream()
    {
        if (_isRunning)
        {
            GD.PrintErr("[StreamServer] Already streaming");
            return;
        }

        try
        {
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
                GD.PrintErr("[StreamServer] No capture source! Set SourceViewport or enable CaptureMainWindow.");
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
                // Ensure even dimensions (required by NV12 pixel format)
                w = (w + 1) & ~1;
                h = (h + 1) & ~1;
                GD.Print($"[StreamServer] Resolution clamped to {w}x{h} (encoder minimum)");
            }

            var viewport = SourceViewport ?? GetViewport();
            _inputInjector = new GodotInputInjector(viewport!);

            var preset = Preset;
            var codecPref = Codec;
            var iceServers = IceServers
                .Select(url => new RTCIceServer { urls = url })
                .ToList();

            IPAddress? bindAddr = null;
            if (!string.IsNullOrEmpty(BindAddress))
            {
                try { bindAddr = IPAddress.Parse(BindAddress); }
                catch { GD.PrintErr($"[StreamServer] Invalid BindAddress: {BindAddress}"); }
            }

            string[]? icePrefixes = AllowedIcePrefixes?.Length > 0 ? AllowedIcePrefixes : null;

            if (codecPref == VideoCodec.Auto)
            {
                GD.Print($"[StreamServer] Step 2: Default codec mode — deferring encoder until SDP negotiation");
                _encoder = null;

                GD.Print("[StreamServer] Step 3: Initializing WebRTC streamer (quad H264+H265+AV1+VP9)...");
                _streamer = new WebRtcStreamer(iceServers, TargetFps, "H264", declareBothCodecs: true, bindAddress: bindAddr, allowedIcePrefixes: icePrefixes);
                _streamer.OnVideoFormatNegotiated += (negotiatedCodec) =>
                {
                    try
                    {
                        GD.Print($"[StreamServer] SDP negotiated codec: {negotiatedCodec} — creating encoder");

                        var (encW, encH) = ClampToEncoderMin(_capture.Resolution.Width, _capture.Resolution.Height);

                        if (negotiatedCodec == "AV1")
                        {
                            var av1Encoder = new AV1HardwareEncoder(preset);
                            av1Encoder.Configure(encW, encH, TargetFps, BitrateKbps, MaxRateMultiplier);
                            av1Encoder.OnEncodedNALU += OnEncodedAv1Obu;
                            _encoder = av1Encoder;
                        }
                        else if (negotiatedCodec == "VP9")
                        {
                            var vp9Encoder = new VP9HardwareEncoder(preset);
                            vp9Encoder.Configure(encW, encH, TargetFps, BitrateKbps, MaxRateMultiplier);
                            vp9Encoder.OnEncodedNALU += OnEncodedVp9Frame;
                            _encoder = vp9Encoder;
                        }
                        else
                        {
                            var resolvedCodec = negotiatedCodec == "H265" ? VideoCodec.H265_Main_L41 : VideoCodec.H264_High_L31;
                            _encoder = new H264HardwareEncoder(preset, resolvedCodec);
                            _encoder.Configure(encW, encH, TargetFps, BitrateKbps, MaxRateMultiplier);
                            _encoder.OnEncodedNALU += OnEncodedNalu;
                        }

                        GD.Print($"[StreamServer] Encoder lazy-initialized, codec={negotiatedCodec}");

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
                GD.Print($"[StreamServer] Step 2: Initializing encoder ({w}x{h} @ {TargetFps}fps, {BitrateKbps}kbps)...");

                if (codecPref.IsAV1Family())
                {
                    var av1Encoder = new AV1HardwareEncoder(preset);
                    av1Encoder.Configure(w, h, TargetFps, BitrateKbps, MaxRateMultiplier);
                    av1Encoder.OnEncodedNALU += OnEncodedAv1Obu;
                    _encoder = av1Encoder;
                }
                else if (codecPref.IsVP9Family())
                {
                    var vp9Encoder = new VP9HardwareEncoder(preset);
                    vp9Encoder.Configure(w, h, TargetFps, BitrateKbps, MaxRateMultiplier);
                    vp9Encoder.OnEncodedNALU += OnEncodedVp9Frame;
                    _encoder = vp9Encoder;
                }
                else
                {
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

            if (BidirectionalMode)
            {
                _streamer.ReceiveRemoteVideo = true;
                _remoteRenderer = new VideoTrackRenderer();
                _remoteRenderer.OnFirstFrameReceived += () =>
                {
                    var tex = _remoteRenderer.Texture;
                    if (tex != null && RemoteVideoDisplay != null)
                    {
                        RemoteVideoDisplay.Texture = tex;
                        GD.Print("[StreamServer] Remote video texture assigned to RemoteVideoDisplay");
                    }
                    EmitSignal("remote_video_ready", _remoteRenderer.Texture?.GetWidth() ?? 0, _remoteRenderer.Texture?.GetHeight() ?? 0);
                };
                _streamer.OnRemoteVideoFrame += OnRemoteVideoFrame;
                GD.Print("[StreamServer] Bidirectional mode enabled — remote video reception active");
            }

            _streamer.DataChannelLabel = DataChannelLabel;

            _inputParser?.Dispose();
            _inputParser = InputProtocol == "videoplayer"
                ? new VideoplayerInputParser()
                : new InputRelay();
            GD.Print($"[StreamServer] Input protocol: {InputProtocol}, DataChannel label: {DataChannelLabel}");

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

            GD.Print($"[StreamServer] Step 4: Connecting signaling to {SignalingUrl}...");
            _signaling = new SignalingClient(SignalingUrl, ConnectionId);
            _signaling.OnOfferReceived += OnSignalingOffer;
            _signaling.OnAnswerReceived += OnSignalingAnswer;
            _signaling.OnCandidateReceived += OnSignalingCandidate;
            _signaling.OnConnected += (s, e) => EmitSignal("client_connected", e.ConnectionId);
            _signaling.OnDisconnected += (s, e) => EmitSignal("client_disconnected", e.ConnectionId);
            _signaling.OnShouldCreateOffer += OnShouldCreateOffer;

            await _signaling.ConnectAsync();

            GD.Print("[StreamServer] Step 5: Starting encode pipeline...");
            StartEncodePipeline();

            _isRunning = true;
            EmitSignal("stream_started", w, h);
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
                    GD.PrintErr($"[StreamServer] Signaling dispose error: {t.Exception.InnerException?.Message ?? t.Exception.Message}");
                }
            }, TaskScheduler.Default);
        }
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

        EmitSignal("stream_stopped");
        GD.Print("[StreamServer] Streaming stopped");
    }

    #endregion

    #region Pipeline Callbacks

    private int _encodeLoopFrameCount;
    private async Task EncodeLoopAsync(CancellationToken ct)
    {
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
        {
            try { _encodeTask.Wait(TimeSpan.FromSeconds(2)); }
            catch (AggregateException) { }
        }

        if (_frameChannel != null)
        {
            while (_frameChannel.Reader.TryRead(out var frame))
                frame.Dispose();
        }

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
        {
            frame.Dispose();
            return;
        }

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
    }

    private int _remoteFrameCount;
    private void OnRemoteVideoFrame(byte[] frameData, SIPSorceryMedia.Abstractions.VideoFormat format)
    {
        _ = Interlocked.Increment(ref _remoteFrameCount);
    }

    private void DrainInput()
    {
        while (_inputParser != null && _inputParser.TryDequeue(out var evt))
        {
            EmitSignal("input_received",
                (int)evt.Type, evt.X, evt.Y, evt.Button, (int)evt.KeyCode);

            _inputInjector?.InjectEvent(evt);
        }
    }

    #endregion

    #region Signaling Callbacks

    private string? _remoteConnectionId;
    private volatile bool _step1Done;
    private volatile bool _step2Done;
    private volatile bool _connected;

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

        var patchedSdp = PatchH265LevelId(e.Sdp);
        if (patchedSdp != e.Sdp)
            GD.Print($"[StreamServer] Patched H265 level-id in browser answer: 93 → 123");

        _streamer!.SetRemoteAnswer(patchedSdp);
        GD.Print("[StreamServer] Step 2 answer set successfully — video should be active");

        if (_encoder != null)
        {
            GD.Print("[StreamServer] Forcing keyframe after answer received");
            _encoder.ForceKeyframe();
        }
        else
        {
            GD.Print("[StreamServer] Encoder not yet initialized (Auto mode) — keyframe deferred");
        }
    }

    private void OnSignalingCandidate(object? sender, CandidateReceivedEventArgs e)
    {
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

            if (_step1Done && !_step2Done)
            {
                _ = Task.Run(() =>
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

    private void OnKeyframeRequested() { }

    private void OnShouldCreateOffer(object? sender, EventArgs e)
    {
        GD.Print("[StreamServer] Impolite peer — waiting for browser's offer...");
    }

    private static readonly Regex H265LevelIdRegex = new(
        @"(level-id=)93(;)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static string PatchH265LevelId(string sdp)
    {
        return H265LevelIdRegex.Replace(sdp, "${1}123${2}");
    }

    #endregion

    #region BindMembers — GDExtension manual registration

    internal static void BindMembers(ClassRegistrationContext context)
    {
        context.BindConstructor(() => new StreamServer());

        // ── Capture ──
        context.AddPropertyGroup("Capture");
        context.BindProperty(
            new PropertyDefinition(new StringName("capture_source_viewport"), VariantType.Object)
            {
                ClassName = new StringName("SubViewport"),
                Usage = PropertyUsageFlags.Default
            },
            static (StreamServer inst) => inst.SourceViewport,
            static (StreamServer inst, Node? val) => inst.SourceViewport = val as SubViewport);

        context.BindProperty(
            new PropertyDefinition(new StringName("capture_main_window"), VariantType.Bool)
            {
                Usage = PropertyUsageFlags.Default
            },
            static (StreamServer inst) => inst.CaptureMainWindow,
            static (StreamServer inst, bool val) => inst.CaptureMainWindow = val);

        // ── Encoding ──
        context.AddPropertyGroup("Encoding");
        context.BindProperty(
            new PropertyDefinition(new StringName("encoding_target_fps"), VariantType.Int)
            {
                Hint = PropertyHint.Range,
                HintString = "1,120,1",
                Usage = PropertyUsageFlags.Default
            },
            static (StreamServer inst) => inst.TargetFps,
            static (StreamServer inst, int val) => inst.TargetFps = val);

        context.BindProperty(
            new PropertyDefinition(new StringName("encoding_bitrate_kbps"), VariantType.Int)
            {
                Hint = PropertyHint.Range,
                HintString = "500,50000,100,suffix:kbps",
                Usage = PropertyUsageFlags.Default
            },
            static (StreamServer inst) => inst.BitrateKbps,
            static (StreamServer inst, int val) => inst.BitrateKbps = val);

        context.BindProperty(
            new PropertyDefinition(new StringName("encoding_max_rate_multiplier"), VariantType.Float)
            {
                Hint = PropertyHint.Range,
                HintString = "1.0,4.0,0.1",
                Usage = PropertyUsageFlags.Default
            },
            static (StreamServer inst) => inst.MaxRateMultiplier,
            static (StreamServer inst, float val) => inst.MaxRateMultiplier = val);

        context.BindProperty(
            new PropertyDefinition(new StringName("encoding_preset"), VariantType.Int)
            {
                Hint = PropertyHint.Enum,
                HintString = "Ultra Low Latency:0,Low Latency:1,Balanced:2,High Quality:3",
                Usage = PropertyUsageFlags.Default
            },
            static (StreamServer inst) => (int)inst.Preset,
            static (StreamServer inst, int val) => inst.Preset = (EncoderPreset)val);

        context.BindProperty(
            new PropertyDefinition(new StringName("encoding_codec"), VariantType.Int)
            {
                Hint = PropertyHint.Enum,
                HintString = "Auto:0,H264 High L31:1,H264 Main L31:2,H264 CBaseline L31:3,H264 Baseline L31:4,H265 Main L41:10,AV1 Main L5:20,VP9 Profile0:30,VP9 Profile2:31",
                Usage = PropertyUsageFlags.Default
            },
            static (StreamServer inst) => (int)inst.Codec,
            static (StreamServer inst, int val) => inst.Codec = (VideoCodec)val);

        // ── Audio ──
        context.AddPropertyGroup("Audio");
        context.BindProperty(
            new PropertyDefinition(new StringName("audio_enable"), VariantType.Bool)
            {
                Usage = PropertyUsageFlags.Default
            },
            static (StreamServer inst) => inst.EnableAudio,
            static (StreamServer inst, bool val) => inst.EnableAudio = val);

        context.BindProperty(
            new PropertyDefinition(new StringName("audio_bitrate_kbps"), VariantType.Int)
            {
                Hint = PropertyHint.Range,
                HintString = "16,512,8,suffix:kbps",
                Usage = PropertyUsageFlags.Default
            },
            static (StreamServer inst) => inst.AudioBitrateKbps,
            static (StreamServer inst, int val) => inst.AudioBitrateKbps = val);

        // ── Signaling ──
        context.AddPropertyGroup("Signaling");
        context.BindProperty(
            new PropertyDefinition(new StringName("signaling_url"), VariantType.String)
            {
                Usage = PropertyUsageFlags.Default
            },
            static (StreamServer inst) => inst.SignalingUrl,
            static (StreamServer inst, string val) => inst.SignalingUrl = val);

        context.BindProperty(
            new PropertyDefinition(new StringName("signaling_connection_id"), VariantType.String)
            {
                Usage = PropertyUsageFlags.Default
            },
            static (StreamServer inst) => inst.ConnectionId,
            static (StreamServer inst, string val) => inst.ConnectionId = val);

        // ── STUN/TURN ──
        context.AddPropertyGroup("STUN/TURN");
        context.BindProperty(
            new PropertyDefinition(new StringName("ice_servers"), VariantType.PackedStringArray)
            {
                Usage = PropertyUsageFlags.Default
            },
            static (StreamServer inst) => new PackedStringArray(inst.IceServers),
            static (StreamServer inst, PackedStringArray val) => inst.IceServers = val.ToArray());

        // ── Input ──
        context.AddPropertyGroup("Input");
        context.BindProperty(
            new PropertyDefinition(new StringName("input_data_channel_label"), VariantType.String)
            {
                Usage = PropertyUsageFlags.Default
            },
            static (StreamServer inst) => inst.DataChannelLabel,
            static (StreamServer inst, string val) => inst.DataChannelLabel = val);

        context.BindProperty(
            new PropertyDefinition(new StringName("input_protocol"), VariantType.String)
            {
                Hint = PropertyHint.Enum,
                HintString = "urs,videoplayer",
                Usage = PropertyUsageFlags.Default
            },
            static (StreamServer inst) => inst.InputProtocol,
            static (StreamServer inst, string val) => inst.InputProtocol = val);

        // ── Network ──
        context.AddPropertyGroup("Network");
        context.BindProperty(
            new PropertyDefinition(new StringName("network_bind_address"), VariantType.String)
            {
                Usage = PropertyUsageFlags.Default
            },
            static (StreamServer inst) => inst.BindAddress,
            static (StreamServer inst, string val) => inst.BindAddress = val);

        context.BindProperty(
            new PropertyDefinition(new StringName("network_allowed_ice_prefixes"), VariantType.PackedStringArray)
            {
                Usage = PropertyUsageFlags.Default
            },
            static (StreamServer inst) => new PackedStringArray(inst.AllowedIcePrefixes),
            static (StreamServer inst, PackedStringArray val) => inst.AllowedIcePrefixes = val.ToArray());

        // ── Bidirectional ──
        context.AddPropertyGroup("Bidirectional");
        context.BindProperty(
            new PropertyDefinition(new StringName("bidirectional_mode"), VariantType.Bool)
            {
                Usage = PropertyUsageFlags.Default
            },
            static (StreamServer inst) => inst.BidirectionalMode,
            static (StreamServer inst, bool val) => inst.BidirectionalMode = val);

        context.BindProperty(
            new PropertyDefinition(new StringName("bidirectional_remote_video_display"), VariantType.Object)
            {
                ClassName = new StringName("TextureRect"),
                Usage = PropertyUsageFlags.Default
            },
            static (StreamServer inst) => inst.RemoteVideoDisplay,
            static (StreamServer inst, Node? val) => inst.RemoteVideoDisplay = val as TextureRect);

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
            }
        });

        context.BindSignal(new SignalDefinition(new StringName("client_disconnected"))
        {
            Parameters =
            {
                new ParameterDefinition(new StringName("connection_id"), VariantType.String),
            }
        });

        context.BindSignal(new SignalDefinition(new StringName("input_received"))
        {
            Parameters =
            {
                new ParameterDefinition(new StringName("event_type"), VariantType.Int),
                new ParameterDefinition(new StringName("x"), VariantType.Float),
                new ParameterDefinition(new StringName("y"), VariantType.Float),
                new ParameterDefinition(new StringName("button"), VariantType.Int),
                new ParameterDefinition(new StringName("key_code"), VariantType.Int),
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

        context.BindSignal(new SignalDefinition(new StringName("remote_video_ready"))
        {
            Parameters =
            {
                new ParameterDefinition(new StringName("width"), VariantType.Int),
                new ParameterDefinition(new StringName("height"), VariantType.Int),
            }
        });

        // Methods
        context.BindMethod(new StringName("start_stream"),
            static (StreamServer inst) => { inst.StartStream(); });

        context.BindMethod(new StringName("stop_stream"),
            static (StreamServer inst) => { inst.StopStream(); });
    }

    #endregion

    /// <summary>
    /// Clamp resolution to encoder minimum (NVENC >= 48x16, safe floor 64x64, even dimensions for NV12).
    /// </summary>
    private static (int w, int h) ClampToEncoderMin(int w, int h)
    {
        const int MinDim = 64;
        if (w >= MinDim && h >= MinDim) return (w, h);
        w = Math.Max(w, MinDim);
        h = Math.Max(h, MinDim);
        w = (w + 1) & ~1;
        h = (h + 1) & ~1;
        return (w, h);
    }
}
