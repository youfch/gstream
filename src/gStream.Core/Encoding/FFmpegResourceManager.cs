using FFmpeg.AutoGen;

namespace gStream.Core.Encoding;

internal sealed unsafe class FFmpegResourceManager : IDisposable
{
    private bool _disposed;

    public AVCodecContext* CodecContext { get; set; }
    public AVPacket* Packet { get; set; }

    public AVFrame* YuvFrame { get; set; }
    public AVFrame* RgbaFrame { get; set; }
    public SwsContext* SwsContext { get; set; }

    public AVBufferRef* HwDeviceCtx { get; set; }
    public AVBufferRef* HwFramesCtx { get; set; }
    public AVFrame* HwFrame { get; set; }
    public AVFrame* Nv12CpuFrame { get; set; }
    public AVFrame* RgbaWrapper { get; set; }
    public SwsContext* SwsUploadCtx { get; set; }

    public void AllocateSoftwareResources(int width, int height, AVPixelFormat inputFormat, AVPixelFormat targetFormat)
    {
        YuvFrame = ffmpeg.av_frame_alloc();
        RgbaFrame = ffmpeg.av_frame_alloc();

        if (YuvFrame == null || RgbaFrame == null)
            throw new InvalidOperationException("Failed to allocate frames.");

        YuvFrame->format = (int)targetFormat;
        YuvFrame->width = width;
        YuvFrame->height = height;

        var ret = ffmpeg.av_frame_get_buffer(YuvFrame, 32);
        if (ret < 0)
            throw new InvalidOperationException("Failed to allocate YUV frame buffer.");

        RgbaFrame->format = (int)inputFormat;
        RgbaFrame->width = width;
        RgbaFrame->height = height;

        SwsContext = ffmpeg.sws_getContext(
            width, height, inputFormat,
            width, height, targetFormat,
            1, null, null, null);  // SWS_FAST_BILINEAR=1 (was SWS_BILINEAR=2)

        if (SwsContext == null)
            throw new InvalidOperationException("Failed to create SwsContext.");
    }

    public void AllocateHardwareResources(int width, int height, AVPixelFormat inputFormat)
    {
        HwFrame = ffmpeg.av_frame_alloc();

        Nv12CpuFrame = ffmpeg.av_frame_alloc();
        if (Nv12CpuFrame == null)
            throw new InvalidOperationException("Failed to allocate NV12 CPU frame.");

        Nv12CpuFrame->format = (int)AVPixelFormat.AV_PIX_FMT_NV12;
        Nv12CpuFrame->width = width;
        Nv12CpuFrame->height = height;

        var ret = ffmpeg.av_frame_get_buffer(Nv12CpuFrame, 32);
        if (ret < 0)
            throw new InvalidOperationException("Failed to allocate NV12 CPU frame buffer.");

        RgbaWrapper = ffmpeg.av_frame_alloc();
        if (RgbaWrapper == null)
            throw new InvalidOperationException("Failed to allocate RGBA wrapper.");
        RgbaWrapper->format = (int)inputFormat;
        RgbaWrapper->width = width;
        RgbaWrapper->height = height;

        SwsUploadCtx = ffmpeg.sws_getContext(
            width, height, inputFormat,
            width, height, AVPixelFormat.AV_PIX_FMT_NV12,
            1, null, null, null);  // SWS_FAST_BILINEAR=1 (was SWS_BILINEAR=2)

        if (SwsUploadCtx == null)
            throw new InvalidOperationException("Failed to create upload SwsContext.");
    }

    public void Cleanup()
    {
        if (SwsUploadCtx != null)
        {
            ffmpeg.sws_freeContext(SwsUploadCtx);
            SwsUploadCtx = null;
        }

        if (HwFrame != null)
        {
            var f = HwFrame;
            ffmpeg.av_frame_free(&f);
            HwFrame = null;
        }

        if (Nv12CpuFrame != null)
        {
            var f = Nv12CpuFrame;
            ffmpeg.av_frame_free(&f);
            Nv12CpuFrame = null;
        }

        if (RgbaWrapper != null)
        {
            var f = RgbaWrapper;
            ffmpeg.av_frame_free(&f);
            RgbaWrapper = null;
        }

        if (HwFramesCtx != null)
        {
            var ctx = HwFramesCtx;
            ffmpeg.av_buffer_unref(&ctx);
            HwFramesCtx = null;
        }

        if (HwDeviceCtx != null)
        {
            var ctx = HwDeviceCtx;
            ffmpeg.av_buffer_unref(&ctx);
            HwDeviceCtx = null;
        }

        if (SwsContext != null)
        {
            ffmpeg.sws_freeContext(SwsContext);
            SwsContext = null;
        }

        if (YuvFrame != null)
        {
            var frame = YuvFrame;
            ffmpeg.av_frame_free(&frame);
            YuvFrame = null;
        }

        if (RgbaFrame != null)
        {
            var frame = RgbaFrame;
            ffmpeg.av_frame_free(&frame);
            RgbaFrame = null;
        }

        if (Packet != null)
        {
            var pkt = Packet;
            ffmpeg.av_packet_free(&pkt);
            Packet = null;
        }

        if (CodecContext != null)
        {
            ffmpeg.avcodec_send_frame(CodecContext, null);

            AVPacket* tempPacket = ffmpeg.av_packet_alloc();
            if (tempPacket != null)
            {
                while (true)
                {
                    var ret = ffmpeg.avcodec_receive_packet(CodecContext, tempPacket);
                    if (ret != 0) break;
                    ffmpeg.av_packet_unref(tempPacket);
                }
                ffmpeg.av_packet_free(&tempPacket);
            }

            var ctx = CodecContext;
            ffmpeg.avcodec_free_context(&ctx);
            CodecContext = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        Cleanup();
        _disposed = true;
    }
}
