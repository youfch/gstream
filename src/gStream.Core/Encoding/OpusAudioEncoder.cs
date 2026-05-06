using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using gStream.Core.Interop;

namespace gStream.Core.Encoding;

/// <summary>
/// Opus audio encoder using FFmpeg's libopus encoder.
/// Encodes interleaved float32 PCM samples into Opus frames for WebRTC streaming.
/// Thread-safe: Encode can be called from any thread.
/// </summary>
public unsafe sealed class OpusAudioEncoder : IAudioEncoder
{
    private readonly object _lock = new();

    private AVCodecContext* _codecContext;
    private AVPacket* _packet;
    private AVFrame* _frame;

    private bool _configured;
    private bool _disposed;
    private int _sampleRate;
    private int _channels;
    private int _frameSize; // samples per channel per frame (960 for 20ms @ 48kHz)
    private long _sampleCount; // total samples per channel encoded (for pts)

    // Sample buffering — accumulates incoming float samples until a complete
    // Opus frame's worth is available (frameSize * channels floats).
    private float[] _sampleBuffer;
    private int _sampleBufferCount;

    /// <summary>
    /// Fired when an encoded Opus frame is ready.
    /// </summary>
    public event Action<byte[], int>? OnEncodedFrame;

    public OpusAudioEncoder()
    {
        _sampleBuffer = new float[4096]; // ~42ms @ 48kHz stereo
    }

    /// <summary>
    /// Configures the Opus encoder with audio parameters.
    /// Must be called before any Encode calls.
    /// </summary>
    public void Configure(int sampleRate, int channels, int bitrateKbps)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(OpusAudioEncoder));

            if (_configured)
            {
                Cleanup();
                _configured = false;
            }

            _sampleRate = sampleRate;
            _channels = channels;
            _sampleCount = 0;
            _sampleBufferCount = 0;

            InitializeEncoder(sampleRate, channels, bitrateKbps);
            _configured = true;
        }
    }

    private void InitializeEncoder(int sampleRate, int channels, int bitrateKbps)
    {
        FFmpegLibraryLoader.Configure();

        // ── Step 1: Find libopus encoder ──
        var codec = ffmpeg.avcodec_find_encoder_by_name("libopus");
        if (codec == null)
            throw new InvalidOperationException(
                "libopus encoder not found. Ensure FFmpeg is built with libopus support.");

        // ── Step 2: Create codec context ──
        _codecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (_codecContext == null)
            throw new InvalidOperationException("Failed to allocate audio codec context.");

        _codecContext->bit_rate = bitrateKbps * 1000L;
        _codecContext->sample_rate = sampleRate;
        _codecContext->time_base = new AVRational { num = 1, den = sampleRate };
        _codecContext->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLT;

        // Set channel layout (stereo: 2 channels)
        ffmpeg.av_channel_layout_default(&_codecContext->ch_layout, channels);

        // ── Step 3: Open codec with libopus-specific options ──
        var options = (AVDictionary*)IntPtr.Zero;
        try
        {
            ffmpeg.av_dict_set(&options, "application", "audio", 0);
            ffmpeg.av_dict_set(&options, "frame_duration", "20.0", 0);
            ffmpeg.av_dict_set(&options, "vbr", "on", 0);

            int ret = ffmpeg.avcodec_open2(_codecContext, codec, &options);
            if (ret < 0)
            {
                var errorBuf = stackalloc byte[256];
                ffmpeg.av_strerror(ret, errorBuf, 256);
                throw new InvalidOperationException(
                    $"Failed to open libopus codec: {Marshal.PtrToStringAnsi((nint)errorBuf)}");
            }
        }
        finally
        {
            ffmpeg.av_dict_free(&options);
        }

        // ── Step 4: Determine frame size ──
        // libopus with 20ms @ 48kHz = 960 samples per channel
        _frameSize = _codecContext->frame_size;
        if (_frameSize <= 0)
            _frameSize = sampleRate / 50; // 20ms fallback

        // ── Step 5: Allocate packet ──
        _packet = ffmpeg.av_packet_alloc();
        if (_packet == null)
            throw new InvalidOperationException("Failed to allocate audio packet.");

        // ── Step 6: Allocate frame for sending samples to encoder ──
        _frame = ffmpeg.av_frame_alloc();
        if (_frame == null)
            throw new InvalidOperationException("Failed to allocate audio frame.");

        _frame->format = (int)AVSampleFormat.AV_SAMPLE_FMT_FLT;
        _frame->nb_samples = _frameSize;
        ffmpeg.av_channel_layout_default(&_frame->ch_layout, channels);

        int frameRet = ffmpeg.av_frame_get_buffer(_frame, 0);
        if (frameRet < 0)
            throw new InvalidOperationException("Failed to allocate audio frame buffer.");
    }

    // ═══════════════════════════════════════════════════════════════
    // Encoding
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Encodes a block of interleaved float32 PCM samples.
    /// Buffers internally and emits complete Opus frames via OnEncodedFrame.
    /// </summary>
    public void Encode(ReadOnlySpan<float> samples)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(OpusAudioEncoder));

            if (!_configured)
                throw new InvalidOperationException("Encoder not configured. Call Configure() first.");

            if (samples.IsEmpty)
                return;

            // Ensure buffer is large enough
            int required = _sampleBufferCount + samples.Length;
            if (required > _sampleBuffer.Length)
            {
                int newSize = Math.Max(_sampleBuffer.Length * 2, required);
                Array.Resize(ref _sampleBuffer, newSize);
            }

            // Append samples to buffer
            samples.CopyTo(_sampleBuffer.AsSpan(_sampleBufferCount));
            _sampleBufferCount += samples.Length;

            // Encode complete frames
            int floatsPerFrame = _frameSize * _channels;
            while (_sampleBufferCount >= floatsPerFrame)
            {
                EncodeOneFrame(floatsPerFrame);
            }
        }
    }

    private void EncodeOneFrame(int floatsPerFrame)
    {
        // Copy buffered samples to AVFrame (interleaved float → data[0])
        Marshal.Copy(_sampleBuffer, 0, (nint)_frame->data[0], floatsPerFrame);

        // Set presentation timestamp (monotonically increasing)
        _frame->pts = _sampleCount;
        _sampleCount += _frameSize;

        // Send frame to encoder
        int ret = ffmpeg.avcodec_send_frame(_codecContext, _frame);
        if (ret < 0)
        {
            // Still consume the samples to prevent infinite loop
            ShiftSampleBuffer(floatsPerFrame);
            return;
        }

        // Drain encoded packets
        DrainEncodedPackets();

        // Shift remaining samples in buffer
        ShiftSampleBuffer(floatsPerFrame);
    }

    private void ShiftSampleBuffer(int consumed)
    {
        int remaining = _sampleBufferCount - consumed;
        if (remaining > 0)
            Array.Copy(_sampleBuffer, consumed, _sampleBuffer, 0, remaining);
        _sampleBufferCount = remaining;
    }

    private void DrainEncodedPackets()
    {
        while (true)
        {
            int ret = ffmpeg.avcodec_receive_packet(_codecContext, _packet);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                break;

            if (ret < 0)
            {
                break;
            }

            var frameData = new byte[_packet->size];
            Marshal.Copy((nint)_packet->data, frameData, 0, _packet->size);
            OnEncodedFrame?.Invoke(frameData, _packet->size);

            ffmpeg.av_packet_unref(_packet);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Keyframe (no-op for audio)
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Not applicable for audio. No-op for interface symmetry.
    /// </summary>
    public void ForceKeyframe()
    {
        // Audio encoding doesn't use keyframes
    }

    // ═══════════════════════════════════════════════════════════════
    // Cleanup
    // ═══════════════════════════════════════════════════════════════

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;

            Cleanup();
            _disposed = true;
        }
    }

    private void Cleanup()
    {
        // Free in reverse allocation order
        if (_frame != null)
        {
            var f = _frame;
            ffmpeg.av_frame_free(&f);
            _frame = null;
        }

        if (_packet != null)
        {
            var pkt = _packet;
            ffmpeg.av_packet_free(&pkt);
            _packet = null;
        }

        if (_codecContext != null)
        {
            // Flush encoder: send NULL frame, drain remaining packets
            ffmpeg.avcodec_send_frame(_codecContext, null);

            AVPacket* tempPacket = ffmpeg.av_packet_alloc();
            if (tempPacket != null)
            {
                while (true)
                {
                    var ret = ffmpeg.avcodec_receive_packet(_codecContext, tempPacket);
                    if (ret != 0) break;
                    ffmpeg.av_packet_unref(tempPacket);
                }
                ffmpeg.av_packet_free(&tempPacket);
            }

            var ctx = _codecContext;
            ffmpeg.avcodec_free_context(&ctx);
            _codecContext = null;
        }

        _sampleBufferCount = 0;
    }
}
