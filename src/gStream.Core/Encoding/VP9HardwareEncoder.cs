using System;
using System.Buffers;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using gStream.Core.Capture;
using gStream.Core.Interop;

namespace gStream.Core.Encoding;

public unsafe sealed class VP9HardwareEncoder : IVideoEncoder
{
    private const AVPixelFormat TargetPixelFormat = AVPixelFormat.AV_PIX_FMT_YUV420P;
    private const AVPixelFormat InputPixelFormat = AVPixelFormat.AV_PIX_FMT_RGBA;

    private static readonly string[] VP9EncoderPriority =
    {
        "libvpx-vp9",
    };

    private readonly object _lock = new();
    private readonly EncoderPreset _preset;

    private readonly FFmpegResourceManager _resources = new();
    private bool _configured;
    private bool _disposed;
    private int _width;
    private int _height;
    private int _fps;
    private int _bitrateKbps;
    private float _maxRateMultiplier = 2.0f;

    private long _frameCount;
    private long _lastKeyframePts;
    private volatile bool _forceNextKeyframe;

    public event Action<byte[], int, int>? OnEncodedNALU;

    public string? ActiveEncoderName { get; private set; }

    public bool IsHardwareAccelerated => false;

    public bool IsVP9 => true;

    public string SdpCodecName => "VP9";

    public bool IsGpuDirectUpload => false;

    public VP9HardwareEncoder(EncoderPreset preset = EncoderPreset.UltraLowLatency)
    {
        _preset = preset;
    }

    public void Configure(int width, int height, int fps, int bitrateKbps, float maxRateMultiplier = 2.0f)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VP9HardwareEncoder));

            if (_configured)
            {
                _resources.Cleanup();
                _configured = false;
            }

            _width = width;
            _height = height;
            _fps = fps;
            _bitrateKbps = bitrateKbps;
            _maxRateMultiplier = maxRateMultiplier;

            InitializeEncoder();
            _configured = true;
        }
    }

    private void InitializeEncoder()
    {
        FFmpegLibraryLoader.Configure();

        string? selectedEncoder = null;
        AVCodec* codec = null;

        foreach (var encoderName in VP9EncoderPriority)
        {
            codec = ffmpeg.avcodec_find_encoder_by_name(encoderName);
            if (codec != null)
            {
                selectedEncoder = encoderName;
                break;
            }
        }

        if (codec == null)
            throw new InvalidOperationException("No VP9 encoder found. Please ensure FFmpeg libraries with libvpx-vp9 are available.");

        ActiveEncoderName = selectedEncoder;

        _resources.CodecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (_resources.CodecContext == null)
            throw new InvalidOperationException("Failed to allocate codec context.");

        _resources.CodecContext->width = _width;
        _resources.CodecContext->height = _height;
        _resources.CodecContext->time_base = new AVRational { num = 1, den = _fps };
        _resources.CodecContext->framerate = new AVRational { num = _fps, den = 1 };
        _resources.CodecContext->bit_rate = _bitrateKbps * 1000L;
        _resources.CodecContext->gop_size = 120;
        _resources.CodecContext->max_b_frames = 0;
        _resources.CodecContext->pix_fmt = TargetPixelFormat;
        _resources.CodecContext->thread_count = Math.Min(4, Environment.ProcessorCount);
        // libvpx-vp9 in realtime MUST use slice threads, NOT frame threads.
        // FF_THREAD_FRAME causes internal frame buffering that starves avcodec_receive_packet.
        _resources.CodecContext->thread_type = ffmpeg.FF_THREAD_SLICE;

        var options = EncoderOptionsBuilder.BuildOptions(selectedEncoder, _preset, _bitrateKbps, _maxRateMultiplier);

        var ret = ffmpeg.avcodec_open2(_resources.CodecContext, codec, &options);
        ffmpeg.av_dict_free(&options);
        if (ret < 0)
        {
            var errorBuf = stackalloc byte[256];
            ffmpeg.av_strerror(ret, errorBuf, 256);
            throw new InvalidOperationException($"Failed to open codec: {Marshal.PtrToStringAnsi((nint)errorBuf)}");
        }

        _resources.Packet = ffmpeg.av_packet_alloc();
        if (_resources.Packet == null)
            throw new InvalidOperationException("Failed to allocate packet.");

        _resources.AllocateSoftwareResources(_width, _height, InputPixelFormat, TargetPixelFormat);

        _frameCount = 0;
        _lastKeyframePts = 0;
    }

    public void Encode(CapturedFrame frame)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(VP9HardwareEncoder));

            if (!_configured)
                throw new InvalidOperationException("Encoder not configured. Call Configure() first.");

            try
            {
                EncodeCpu(frame);
            }
            finally
            {
                frame.Dispose();
            }
        }
    }

    private void EncodeCpu(CapturedFrame frame)
    {
        _resources.RgbaFrame->data[0] = frame.Data;
        _resources.RgbaFrame->linesize[0] = frame.Stride;

        int ret = ffmpeg.sws_scale(
            _resources.SwsContext,
            _resources.RgbaFrame->data, _resources.RgbaFrame->linesize,
            0, frame.Height,
            _resources.YuvFrame->data, _resources.YuvFrame->linesize);

        if (ret < 0)
        {
            return;
        }

        _resources.YuvFrame->pts = _frameCount++;

        if (_forceNextKeyframe)
        {
            _resources.YuvFrame->pict_type = (FFmpeg.AutoGen.AVPictureType)1; // AV_PICTURE_TYPE_I
            _forceNextKeyframe = false;
        }

        ret = ffmpeg.avcodec_send_frame(_resources.CodecContext, _resources.YuvFrame);
        if (ret < 0)
        {
            // Log specific errors 鈥?EAGAIN is normal (encoder full), others are problems
            if (ret != ffmpeg.AVERROR(ffmpeg.EAGAIN))
            {
            }
            return;
        }

        DrainEncodedPackets();
    }

    private void DrainEncodedPackets()
    {
        while (true)
        {
            int ret = ffmpeg.avcodec_receive_packet(_resources.CodecContext, _resources.Packet);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                break;

            if (ret < 0)
            {
                break;
            }

            int isKeyframe = (_resources.Packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0 ? 1 : 0;

            if (isKeyframe != 0)
                _lastKeyframePts = _resources.Packet->pts;

            var naluData = ArrayPool<byte>.Shared.Rent(_resources.Packet->size);
            try
            {
                Marshal.Copy((nint)_resources.Packet->data, naluData, 0, _resources.Packet->size);
                OnEncodedNALU?.Invoke(naluData, _resources.Packet->size, isKeyframe);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(naluData);
            }

            ffmpeg.av_packet_unref(_resources.Packet);
        }
    }

    public void ForceKeyframe()
    {
        _forceNextKeyframe = true;
    }

    public (long TotalFrames, long LastKeyframePts) GetStats()
    {
        lock (_lock)
        {
            return (_frameCount, _lastKeyframePts);
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;

            _resources.Dispose();
            _disposed = true;
        }
    }
}
