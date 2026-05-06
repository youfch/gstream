using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using gStream.Core.Capture;
using gStream.Core.Encoding;
using gStream.Core.Input;
using gStream.Core.Streaming;

namespace gStream.Core.NativeExports;

/// <summary>
/// Flat C API exported from gStream.Core native DLL.
/// Called by UE5 C++ plugin via LoadLibrary + GetProcAddress.
/// All strings are UTF-8 passed as byte pointers.
/// </summary>
public static unsafe class GStreamNativeApi
{
    // ── Session lifecycle ──

    /// <summary>
    /// Initialize a streaming session. Returns session handle (>0) or 0 on failure.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "gstream_session_create")]
    public static nint SessionCreate(
        int width, int height, int fps, int bitrateKbps,
        int codec,           // VideoCodec enum value
        int preset,          // EncoderPreset enum value
        byte* signalingUrl,  // UTF-8 null-terminated
        byte* bindAddress,   // UTF-8 null-terminated (can be null)
        int receiveRemoteVideo)
    {
        try
        {
            var videoCodec = (VideoCodec)codec;
            var encoderPreset = (EncoderPreset)preset;
            var url = Marshal.PtrToStringUTF8((nint)signalingUrl) ?? "";
            var bind = bindAddress != null ? Marshal.PtrToStringUTF8((nint)bindAddress) : null;

            var session = new NativeSession(width, height, fps, bitrateKbps, videoCodec, encoderPreset, url, bind, receiveRemoteVideo != 0);
            var handle = session.Handle;
            _sessions[handle] = session;
            return handle;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[gStream] SessionCreate FAILED: {ex}");
            return 0;
        }
    }

    /// <summary>Destroy a streaming session.</summary>
    [UnmanagedCallersOnly(EntryPoint = "gstream_session_destroy")]
    public static void SessionDestroy(nint sessionHandle)
    {
        if (_sessions.TryRemove(sessionHandle, out var session))
        {
            session.Dispose();
        }
    }

    // ── Frame submission ──

    /// <summary>
    /// Push a captured BGRA32 frame to the encoder.
    /// The frame data is COPIED — caller can free the buffer after this returns.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "gstream_push_frame")]
    public static void PushFrame(nint sessionHandle, int width, int height, int stride, byte* data)
    {
        if (!_sessions.TryGetValue(sessionHandle, out var session)) return;
        if (data == null) return;

        var size = stride * height;
        var source = new ReadOnlySpan<byte>(data, size);
        var frame = CapturedFrame.CopyFrom(source, width, height, stride, Stopwatch.GetTimestamp());
        session.PushFrame(frame);
    }

    /// <summary>
    /// Push a captured BGRA32 frame WITHOUT copying. The caller MUST ensure the data pointer
    /// remains valid until this function returns (synchronous encoding).
    /// This is the zero-copy alternative to gstream_push_frame — avoids 8MB memcpy per frame.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "gstream_push_frame_direct")]
    public static void PushFrameDirect(nint sessionHandle, int width, int height, int stride, byte* data)
    {
        if (!_sessions.TryGetValue(sessionHandle, out var session)) return;
        if (data == null) return;
        session.PushFrameDirect(data, width, height, stride);
    }

    /// <summary>Force a keyframe on the next encoded frame.</summary>
    [UnmanagedCallersOnly(EntryPoint = "gstream_force_keyframe")]
    public static void ForceKeyframe(nint sessionHandle)
    {
        if (!_sessions.TryGetValue(sessionHandle, out var session)) return;
        session.ForceKeyframe();
    }

    // ── Audio ──

    /// <summary>
    /// Push interleaved float32 PCM samples for Opus encoding.
    /// </summary>
    [UnmanagedCallersOnly(EntryPoint = "gstream_push_audio")]
    public static void PushAudio(nint sessionHandle, float* samples, int sampleCount)
    {
        if (!_sessions.TryGetValue(sessionHandle, out var session)) return;
        if (samples == null || sampleCount <= 0) return;

        var span = new ReadOnlySpan<float>(samples, sampleCount);
        session.PushAudio(span);
    }

    // ── Status ──

    /// <summary>Returns 1 if the WebRTC connection is established, 0 otherwise.</summary>
    [UnmanagedCallersOnly(EntryPoint = "gstream_is_connected")]
    public static int IsConnected(nint sessionHandle)
    {
        if (!_sessions.TryGetValue(sessionHandle, out var session)) return 0;
        return session.IsConnected ? 1 : 0;
    }

    /// <summary>Returns the active encoder name as UTF-8 (caller must free with gstream_free).</summary>
    [UnmanagedCallersOnly(EntryPoint = "gstream_get_encoder_name")]
    public static byte* GetEncoderName(nint sessionHandle)
    {
        if (!_sessions.TryGetValue(sessionHandle, out var session)) return null;
        var name = session.EncoderName ?? "";
        var ptr = Marshal.StringToCoTaskMemUTF8(name);
        return (byte*)ptr;
    }

    /// <summary>Free memory returned by gstream_get_encoder_name.</summary>
    [UnmanagedCallersOnly(EntryPoint = "gstream_free")]
    public static void FreeMemory(nint ptr)
    {
        if (ptr != 0)
        {
            Marshal.FreeCoTaskMem(ptr);
        }
    }

    // ── Internal state ──

    private static readonly ConcurrentDictionary<nint, NativeSession> _sessions = new();
}

/// <summary>
/// Holds all resources for a single streaming session.
/// </summary>
internal sealed unsafe class NativeSession : IDisposable
{
    private readonly nint _handle;
    private readonly IVideoEncoder _videoEncoder;
    private readonly OpusAudioEncoder _audioEncoder;
    private readonly WebRtcStreamer _streamer;
    private readonly SignalingClient _signalingClient;
    private readonly InputRelay _inputRelay;
    private readonly string _encoderName;
    private readonly VideoCodec _codec;
    private bool _disposed;

    public nint Handle => _handle;
    public bool IsConnected => _streamer.IsConnected;
    public string? EncoderName => _encoderName;

    public NativeSession(
        int width, int height, int fps, int bitrateKbps,
        VideoCodec codec, EncoderPreset preset,
        string signalingUrl, string? bindAddress,
        bool receiveRemoteVideo)
    {
        _codec = codec;
        _handle = (nint)Interlocked.Increment(ref _nextHandle);
        _inputRelay = new InputRelay();

        // Create encoder based on codec family
        _videoEncoder = CreateEncoder(codec, preset);
        _encoderName = GetEncoderDisplayName(_videoEncoder);

        // Resolve SDP parameters for the streamer
        var (codecName, fmtp) = ResolveSdpParams(codec);
        var declareBoth = codec == VideoCodec.Auto;

        // Parse bind address
        IPAddress? bindIp = null;
        if (!string.IsNullOrEmpty(bindAddress))
        {
            IPAddress.TryParse(bindAddress, out bindIp);
        }

        _streamer = new WebRtcStreamer(
            iceServers: null, // uses default STUN
            fps: fps,
            codecName: codecName,
            fmtp: fmtp,
            declareBothCodecs: declareBoth,
            bindAddress: bindIp);

        _streamer.ReceiveRemoteVideo = receiveRemoteVideo;

        // Wire encoder output → streamer send
        _videoEncoder.OnEncodedNALU += (data, length, isKeyframe) =>
        {
            if (IsAV1Family(codec))
                _streamer.SendAv1Obu(data, length, isKeyframe != 0);
            else if (IsVP9Family(codec))
                _streamer.SendVp9Frame(data, length, isKeyframe != 0);
            else
                _streamer.SendH264Nalu(data, length, isKeyframe != 0);
        };

        // Wire streamer keyframe request → encoder
        _streamer.OnKeyframeRequested += () =>
        {
            _videoEncoder.ForceKeyframe();
        };

        // Wire streamer input events → input relay
        _streamer.OnInputEvent += (data) =>
        {
            _inputRelay.OnDataChannelMessage(data);
        };

        // Configure encoder
        _videoEncoder.Configure(width, height, fps, bitrateKbps);

        // Create audio encoder
        _audioEncoder = new OpusAudioEncoder();
        _audioEncoder.Configure(48000, 2, 128); // 48kHz stereo, 128kbps

        // Wire audio encoder output → streamer
        _audioEncoder.OnEncodedFrame += (data, length) =>
        {
            _streamer.SendAudio(data, length);
        };

        // Connect signaling
        _signalingClient = new SignalingClient(signalingUrl);
        WireSignaling();
        _ = _signalingClient.ConnectAsync();
    }

    private void WireSignaling()
    {
        _signalingClient.OnOfferReceived += (sender, args) =>
        {
            try
            {
                _streamer.SetRemoteOffer(args.Sdp);
                _streamer.AddVideoTrack();
                _streamer.AddAudioTrack();
                var answer = _streamer.CreateAnswer();
                _signalingClient.SendAnswer(answer, args.FromConnectionId);
            }
            catch { /* swallow — signaling thread */ }
        };

        _signalingClient.OnAnswerReceived += (sender, args) =>
        {
            try
            {
                _streamer.SetRemoteAnswer(args.Sdp);
            }
            catch { /* swallow */ }
        };

        _signalingClient.OnCandidateReceived += (sender, args) =>
        {
            try
            {
                _streamer.AddIceCandidate(args.Candidate, args.SdpMLineIndex, args.SdpMid);
            }
            catch { /* swallow */ }
        };

        _streamer.OnIceCandidate += (candidate) =>
        {
            try
            {
                _signalingClient.SendCandidate(candidate.candidate, candidate.sdpMLineIndex, candidate.sdpMid);
            }
            catch { /* swallow */ }
        };

        _signalingClient.OnShouldCreateOffer += (sender, args) =>
        {
            try
            {
                var offer = _streamer.CreateOffer();
                _signalingClient.SendOffer(offer);
            }
            catch { /* swallow */ }
        };
    }

    public void PushFrame(CapturedFrame frame)
    {
        if (_disposed) { frame.Dispose(); return; }
        _videoEncoder.Encode(frame);
    }

    public void PushFrameDirect(byte* data, int width, int height, int stride)
    {
        if (_disposed) return;
        // Wrap the raw pointer directly — NO copy, NO GCHandle
        // The caller guarantees the pointer stays valid until Encode() returns
        var frame = new CapturedFrame(data, width, height, stride, Stopwatch.GetTimestamp(), 0);
        _videoEncoder.Encode(frame);
    }

    public void ForceKeyframe()
    {
        if (_disposed) return;
        _videoEncoder.ForceKeyframe();
    }

    public void PushAudio(ReadOnlySpan<float> samples)
    {
        if (_disposed) return;
        _audioEncoder.Encode(samples);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try { _videoEncoder.Dispose(); } catch { }
        try { _audioEncoder.Dispose(); } catch { }
        try { _streamer.Dispose(); } catch { }
        try { _signalingClient.DisposeAsync().AsTask().Wait(TimeSpan.FromSeconds(3)); } catch { }
        try { _inputRelay.Dispose(); } catch { }
    }

    // ── Helpers ──

    private static IVideoEncoder CreateEncoder(VideoCodec codec, EncoderPreset preset) => codec switch
    {
        VideoCodec.AV1_Main_L5 => new AV1HardwareEncoder(preset),
        VideoCodec.VP9_Profile0 or VideoCodec.VP9_Profile2 => new VP9HardwareEncoder(preset),
        _ => new H264HardwareEncoder(preset, codec) // covers H264 variants, H265, and Auto
    };

    private static (string codecName, string fmtp) ResolveSdpParams(VideoCodec codec)
    {
        if (codec == VideoCodec.Auto)
            return ("H264", "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=64001f");

        return codec.ToSdp();
    }

    private static string? GetEncoderDisplayName(IVideoEncoder encoder) => encoder switch
    {
        H264HardwareEncoder h => h.ActiveEncoderName,
        AV1HardwareEncoder a => a.ActiveEncoderName,
        VP9HardwareEncoder v => v.ActiveEncoderName,
        _ => encoder.GetType().Name
    };

    private static bool IsAV1Family(VideoCodec codec) =>
        codec == VideoCodec.AV1_Main_L5;

    private static bool IsVP9Family(VideoCodec codec) =>
        codec == VideoCodec.VP9_Profile0 || codec == VideoCodec.VP9_Profile2;

    private static long _nextHandle;
}
