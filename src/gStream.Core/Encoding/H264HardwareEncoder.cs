using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using gStream.Core.Capture;
using gStream.Core.Interop;

namespace gStream.Core.Encoding;

/// <summary>
/// High-performance hardware encoder using FFmpeg.AutoGen.
/// Supports H.264 and HEVC (H.265) with automatic hardware detection.
///
/// GPU direct path (when available):
///   BGRA → sws_scale(NV12, CPU) → av_hwframe_transfer_data(memcpy to GPU) → encoder from GPU memory
///
/// CPU fallback path (when GPU upload unavailable):
///   BGRA → sws_scale(YUV420P, CPU) → encoder uploads internally
///
/// Thread-safe: Encode can be called from any thread.
/// </summary>
public unsafe sealed class H264HardwareEncoder : IVideoEncoder
{
    private const AVPixelFormat TargetPixelFormat = AVPixelFormat.AV_PIX_FMT_YUV420P;

    // ── Encoder priority lists per codec preference ──

    private static readonly string[] HEVCEncoderPriority =
    {
        "hevc_nvenc",        // NVIDIA NVENC HEVC
        "hevc_amf",          // AMD AMF HEVC (Windows)
        "hevc_videotoolbox", // Apple VideoToolbox HEVC (macOS)
        "hevc_qsv",          // Intel Quick Sync HEVC (Windows/Linux)
        "hevc_vaapi",        // Linux VAAPI HEVC (AMD/Intel fallback)
        "libx265"            // Software HEVC fallback
    };

    private static readonly string[] H264EncoderPriority =
    {
        "h264_nvenc",        // NVIDIA NVENC
        "h264_amf",          // AMD AMF (Windows)
        "h264_videotoolbox", // Apple VideoToolbox (macOS)
        "h264_qsv",          // Intel Quick Sync Video (Windows/Linux)
        "h264_vaapi",        // Linux VAAPI (AMD/Intel fallback)
        "libx264"            // Software fallback
    };

    private static readonly string[] AutoEncoderPriority =
    {
        "h264_nvenc",        // NVIDIA NVENC H.264 (best compatibility)
        "h264_amf",          // AMD AMF H.264
        "h264_videotoolbox", // Apple VideoToolbox H.264
        "h264_qsv",          // Intel Quick Sync H.264
        "h264_vaapi",        // Linux VAAPI H.264
        "libx264",           // Software H.264 fallback
        "hevc_nvenc",        // NVIDIA NVENC HEVC (use VideoCodec.H265 to select)
        "hevc_amf",          // AMD AMF HEVC
        "hevc_videotoolbox", // Apple VideoToolbox HEVC
        "hevc_qsv",          // Intel Quick Sync HEVC
        "hevc_vaapi",        // Linux VAAPI HEVC
        "libx265"            // Software HEVC fallback
    };

    /// <summary>
    /// Maps hardware encoder name to the FFmpeg AVHWDeviceType for GPU upload.
    /// Covers both H.264 and HEVC encoders.
    /// </summary>
    private static readonly Dictionary<string, AVHWDeviceType> EncoderDeviceTypeMap = new()
    {
        // H.264
        { "h264_nvenc", AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA },
        { "h264_amf", AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA },
        { "h264_qsv", AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA },
        { "h264_videotoolbox", AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX },
        { "h264_vaapi", AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI },
        // HEVC
        { "hevc_nvenc", AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA },
        { "hevc_amf", AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA },
        { "hevc_qsv", AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA },
        { "hevc_videotoolbox", AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX },
        { "hevc_vaapi", AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI },
    };

    /// <summary>
    /// Input pixel format from capture source.
    /// Godot texture_get_data_async returns FORMAT_RGBA8 which in memory is RGBA byte order.
    /// </summary>
    private const AVPixelFormat InputPixelFormat = AVPixelFormat.AV_PIX_FMT_RGBA;

    private readonly object _lock = new();
    private readonly EncoderPreset _preset;
    private readonly VideoCodec _codecPreference;

    private readonly FFmpegResourceManager _resources = new();
    private bool _useHardwareUpload;

    private bool _configured;
    private bool _disposed;
    private int _width;
    private int _height;
    private int _fps;
    private int _bitrateKbps;
    private float _maxRateMultiplier = 2.0f;
    private readonly int _gpuFramePoolSize;

    private long _frameCount;
    private long _lastKeyframePts;
    private volatile bool _forceKeyframeRequested;

    /// <summary>
    /// Fired when an encoded H.264 NAL unit is ready.
    /// </summary>
    public event Action<byte[], int, int>? OnEncodedNALU;

    /// <summary>
    /// Gets the name of the encoder currently in use.
    /// </summary>
    public string? ActiveEncoderName { get; private set; }

    /// <summary>
    /// Gets whether the encoder is using hardware acceleration.
    /// </summary>
    public bool IsHardwareAccelerated
    {
        get
        {
            if (ActiveEncoderName == null) return false;
            return !ActiveEncoderName.StartsWith("libx264") && !ActiveEncoderName.StartsWith("libx265");
        }
    }

    /// <summary>
    /// Gets whether the active encoder is outputting HEVC (H.265).
    /// </summary>
    public bool IsHEVC => ActiveEncoderName?.StartsWith("hevc_") == true || ActiveEncoderName == "libx265";

    /// <summary>
    /// Gets the SDP codec name for the active encoder ("H264" or "H265").
    /// </summary>
    public string SdpCodecName => IsHEVC ? "H265" : "H264";

    /// <summary>
    /// Gets whether GPU direct upload is active (encoder reads from GPU memory).
    /// </summary>
    public bool IsGpuDirectUpload => _useHardwareUpload;

    /// <summary>
    /// Creates a new encoder with the specified preset and codec preference.
    /// </summary>
    public H264HardwareEncoder(EncoderPreset preset = EncoderPreset.UltraLowLatency, VideoCodec codec = VideoCodec.Auto, int gpuFramePoolSize = 0)
    {
        _preset = preset;
        _codecPreference = codec;
        _gpuFramePoolSize = gpuFramePoolSize;
    }

    /// <summary>
    /// Configures the encoder with video parameters.
    /// Must be called before any Encode calls.
    /// </summary>
    public void Configure(int width, int height, int fps, int bitrateKbps, float maxRateMultiplier = 2.0f)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(H264HardwareEncoder));

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

    // ═══════════════════════════════════════════════════════════════
    // Initialization
    // ═══════════════════════════════════════════════════════════════

    private void InitializeEncoder()
    {
        FFmpegLibraryLoader.Configure();

        // ── Step 1: Select encoder priority list based on codec preference ──
        var encoderList = _codecPreference switch
        {
            var c when c.IsH265Family() => HEVCEncoderPriority,
            var c when c.IsH264Family() => H264EncoderPriority,
            _ => AutoEncoderPriority // Auto: try H.264 first, then HEVC
        };

        // ── Step 2: Find best available encoder ──
        string? selectedEncoder = null;
        AVCodec* codec = null;

        foreach (var encoderName in encoderList)
        {
            codec = ffmpeg.avcodec_find_encoder_by_name(encoderName);
            if (codec != null)
            {
                selectedEncoder = encoderName;
                break;
            }
        }

        if (codec == null)
            throw new InvalidOperationException("No video encoder found. Please ensure FFmpeg libraries are available.");

        ActiveEncoderName = selectedEncoder;

        // ── Step 2: Create codec context ──
        _resources.CodecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (_resources.CodecContext == null)
            throw new InvalidOperationException("Failed to allocate codec context.");

        _resources.CodecContext->width = _width;
        _resources.CodecContext->height = _height;
        _resources.CodecContext->time_base = new AVRational { num = 1, den = _fps };
        _resources.CodecContext->framerate = new AVRational { num = _fps, den = 1 };
        _resources.CodecContext->bit_rate = _bitrateKbps * 1000L;
        _resources.CodecContext->gop_size = int.MaxValue;
        _resources.CodecContext->max_b_frames = 0;

        if (selectedEncoder is "libx264" or "libx265")
            _resources.CodecContext->thread_count = Math.Max(1, Environment.ProcessorCount);

        // ── Step 3: Build encoder options and set codec context fields ──
            var options = EncoderOptionsBuilder.BuildOptions(selectedEncoder, _preset, _bitrateKbps, _maxRateMultiplier);
            _resources.CodecContext->max_b_frames = 0;
            _resources.CodecContext->gop_size = selectedEncoder is "libvpx-vp9" ? 120 : int.MaxValue;
            if (selectedEncoder is "libx264" or "libx265")
                _resources.CodecContext->thread_type = ffmpeg.FF_THREAD_FRAME;

        // ── Step 4: Try GPU direct upload path ──
        _useHardwareUpload = TryInitializeHardwareUpload(selectedEncoder);

        if (!_useHardwareUpload)
        {
            // CPU fallback: YUV420P
            _resources.CodecContext->pix_fmt = TargetPixelFormat;
        }

        // ── Step 5: Open codec (options are consumed and freed by avcodec_open2) ──
        var ret = ffmpeg.avcodec_open2(_resources.CodecContext, codec, &options);
        ffmpeg.av_dict_free(&options);
        if (ret < 0)
        {
            var errorBuf = stackalloc byte[256];
            ffmpeg.av_strerror(ret, errorBuf, 256);
            throw new InvalidOperationException($"Failed to open codec: {Marshal.PtrToStringAnsi((nint)errorBuf)}");
        }

        // ── Step 6: Allocate resources ──
        _resources.Packet = ffmpeg.av_packet_alloc();
        if (_resources.Packet == null)
            throw new InvalidOperationException("Failed to allocate packet.");

        if (_useHardwareUpload)
            _resources.AllocateHardwareResources(_width, _height, InputPixelFormat);
        else
            _resources.AllocateSoftwareResources(_width, _height, InputPixelFormat, TargetPixelFormat);

        _frameCount = 0;
        _lastKeyframePts = 0;
    }

    // ═══════════════════════════════════════════════════════════════
    // GPU Hardware Upload Setup
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Attempts to create GPU device + frames context for hardware upload.
    /// Path: sws_scale(BGRA→NV12 on CPU) → av_hwframe_transfer_data(memcpy to GPU) → NVENC.
    /// Falls back to CPU path on any failure.
    /// </summary>
    private bool TryInitializeHardwareUpload(string? encoderName)
    {
        if (encoderName == null) return false;
        if (!EncoderDeviceTypeMap.TryGetValue(encoderName, out var deviceType)) return false;

        // ── Create hardware device context ──
        AVBufferRef* deviceCtx = null;
        int ret = ffmpeg.av_hwdevice_ctx_create(&deviceCtx, deviceType, null, null, 0);
        if (ret < 0 || deviceCtx == null)
        {
            return false;
        }

        // ── Create hardware frames context (GPU NV12 frame pool) ──
        AVBufferRef* framesRef = ffmpeg.av_hwframe_ctx_alloc(deviceCtx);
        if (framesRef == null)
        {
            ffmpeg.av_buffer_unref(&deviceCtx);
            return false;
        }

        var hwFrames = (AVHWFramesContext*)framesRef->data;
        hwFrames->format = GetHwPixelFormat(deviceType);
        hwFrames->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        hwFrames->width = _width;
        hwFrames->height = _height;
        hwFrames->initial_pool_size = _gpuFramePoolSize > 0
            ? _gpuFramePoolSize
            : ((_width * _height > 3840 * 2160) ? 8 : 6);

        ret = ffmpeg.av_hwframe_ctx_init(framesRef);
        if (ret < 0)
        {
            ffmpeg.av_buffer_unref(&framesRef);
            ffmpeg.av_buffer_unref(&deviceCtx);
            return false;
        }

        // ── Set hw_frames_ctx on codec (before avcodec_open2) ──
        _resources.CodecContext->hw_frames_ctx = ffmpeg.av_buffer_ref(framesRef);
        _resources.CodecContext->pix_fmt = GetHwPixelFormat(deviceType);

        _resources.HwDeviceCtx = deviceCtx;
        _resources.HwFramesCtx = framesRef;

        return true;
    }

    private static AVPixelFormat GetHwPixelFormat(AVHWDeviceType deviceType) => deviceType switch
    {
        AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA => AVPixelFormat.AV_PIX_FMT_CUDA,
        AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA => AVPixelFormat.AV_PIX_FMT_D3D11,
        AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI => AVPixelFormat.AV_PIX_FMT_VAAPI,
        AVHWDeviceType.AV_HWDEVICE_TYPE_QSV => AVPixelFormat.AV_PIX_FMT_QSV,
        AVHWDeviceType.AV_HWDEVICE_TYPE_VIDEOTOOLBOX => AVPixelFormat.AV_PIX_FMT_VIDEOTOOLBOX,
        _ => AVPixelFormat.AV_PIX_FMT_NONE
    };

    // ═══════════════════════════════════════════════════════════════
    // Encoding
    // ═══════════════════════════════════════════════════════════════

    public void Encode(CapturedFrame frame)
    {
        lock (_lock)
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(H264HardwareEncoder));

            if (!_configured)
                throw new InvalidOperationException("Encoder not configured. Call Configure() first.");

            try
            {
                if (_useHardwareUpload)
                    EncodeGpu(frame);
                else
                    EncodeCpu(frame);
            }
            finally
            {
                frame.Dispose();
            }
        }
    }

    /// <summary>
    /// GPU direct encoding path:
    ///   1. Wrap captured BGRA data (zero-copy)
    ///   2. sws_scale: BGRA → NV12 (CPU, NV12 is 2 planes — faster than YUV420P's 3)
    ///   3. av_hwframe_transfer_data: NV12 CPU → NV12 GPU (pure memcpy)
    ///   4. NVENC encodes from GPU memory — no PCIe reads during encoding
    /// </summary>
    private void EncodeGpu(CapturedFrame frame)
    {
        _resources.RgbaWrapper->data[0] = frame.Data;
        _resources.RgbaWrapper->linesize[0] = frame.Stride;

        int ret = ffmpeg.sws_scale(
            _resources.SwsUploadCtx,
            _resources.RgbaWrapper->data, _resources.RgbaWrapper->linesize,
            0, frame.Height,
            _resources.Nv12CpuFrame->data, _resources.Nv12CpuFrame->linesize);

        if (ret < 0)
        {
            return;
        }

        ffmpeg.av_frame_unref(_resources.HwFrame);
        ret = ffmpeg.av_hwframe_get_buffer(_resources.HwFramesCtx, _resources.HwFrame, 0);
        if (ret < 0)
        {
            return;
        }

        ret = ffmpeg.av_hwframe_transfer_data(_resources.HwFrame, _resources.Nv12CpuFrame, 0);
        if (ret < 0)
        {
            ffmpeg.av_frame_unref(_resources.HwFrame);
            return;
        }

        // Force keyframe on next frame if requested
        if (_forceKeyframeRequested)
        {
            _resources.HwFrame->flags |= ffmpeg.AV_FRAME_FLAG_KEY;
            _resources.HwFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
            _forceKeyframeRequested = false;
        }

        _resources.HwFrame->pts = _frameCount++;

        ret = ffmpeg.avcodec_send_frame(_resources.CodecContext, _resources.HwFrame);
        ffmpeg.av_frame_unref(_resources.HwFrame);

        if (ret < 0)
        {
            return;
        }

        DrainEncodedPackets();
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

        // Force keyframe on next frame if requested
        if (_forceKeyframeRequested)
        {
            _resources.YuvFrame->flags |= ffmpeg.AV_FRAME_FLAG_KEY;
            _resources.YuvFrame->pict_type = AVPictureType.AV_PICTURE_TYPE_I;
            _forceKeyframeRequested = false;
        }

        _resources.YuvFrame->pts = _frameCount++;

        ret = ffmpeg.avcodec_send_frame(_resources.CodecContext, _resources.YuvFrame);
        if (ret < 0)
        {
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
            {
                _lastKeyframePts = _resources.Packet->pts;

                // Reset gop_size after keyframe to prevent encoder from
                // continuously producing keyframes (wastes bandwidth).
                if (_resources.CodecContext->gop_size == 1)
                    _resources.CodecContext->gop_size = int.MaxValue;
            }

            // Allocate exact-sized array — SIPSorcery's SendVideo copies synchronously,
            // so this short-lived Gen0 allocation is cheaper than ArrayPool + ToArray overhead.
            var naluData = new byte[_resources.Packet->size];
            Marshal.Copy((nint)_resources.Packet->data, naluData, 0, _resources.Packet->size);
            OnEncodedNALU?.Invoke(naluData, _resources.Packet->size, isKeyframe);

            ffmpeg.av_packet_unref(_resources.Packet);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Keyframe / Stats
    // ═══════════════════════════════════════════════════════════════

    public void ForceKeyframe()
    {
        lock (_lock)
        {
            if (_disposed || !_configured || _resources.CodecContext == null)
                return;

            // Set the flag so the next Encode() call forces a keyframe via
            // AV_FRAME_FLAG_KEY + pict_type=I. This works reliably across both
            // hardware (NVENC) and software (libx264) encoders.
            _forceKeyframeRequested = true;
        }
    }

    public (long TotalFrames, long LastKeyframePts) GetStats()
    {
        lock (_lock)
        {
            return (_frameCount, _lastKeyframePts);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // Cleanup
    // ═══════════════════════════════════════════════════════════════

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
