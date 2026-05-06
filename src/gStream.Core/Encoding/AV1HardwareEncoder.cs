using System;
using System.Buffers;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;
using gStream.Core.Capture;
using gStream.Core.Interop;

namespace gStream.Core.Encoding;

/// <summary>
/// AV1 hardware encoder using FFmpeg with support for NVENC, AMF, QSV, VAAPI,
/// and software encoders (SVT-AV1, libaom-av1).
/// </summary>
public unsafe sealed class AV1HardwareEncoder : IVideoEncoder
{
    private static readonly string[] AV1EncoderPriority =
    {
        "av1_nvenc",        // NVIDIA RTX 40 series+
        "av1_qsv",          // Intel Arc / 11th Gen+
        "av1_amf",          // AMD (limited support)
        "av1_vaapi",        // Linux VAAPI
        "svt_av1",          // Intel SVT-AV1 (software, good performance)
        "libaom-av1",       // AOM reference encoder (slow)
    };

    private static readonly Dictionary<string, AVHWDeviceType> EncoderDeviceTypeMap = new()
    {
        { "av1_nvenc", AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA },
        { "av1_qsv", AVHWDeviceType.AV_HWDEVICE_TYPE_QSV },
        { "av1_amf", AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA },
        { "av1_vaapi", AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI },
    };

    private const AVPixelFormat InputPixelFormat = AVPixelFormat.AV_PIX_FMT_RGBA;

    private readonly object _lock = new();
    private readonly EncoderPreset _preset;

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

    private readonly string _selectedEncoder;
    private readonly bool _isHardwareEncoder;
    private readonly AVPixelFormat _swFormat;
    private readonly AVPixelFormat _hwFormat;
    private readonly AVHWDeviceType _hwDeviceType;

    private long _frameCounter;
    private bool _forceKeyframeRestoreGop;

    private byte[] _obuBuffer = Array.Empty<byte>();
    private byte[] _scratchBuffer = Array.Empty<byte>();

    public event Action<byte[], int, int>? OnEncodedNALU;

    public string ActiveEncoderName => _selectedEncoder;

    public AV1HardwareEncoder(EncoderPreset preset = EncoderPreset.UltraLowLatency, int gpuFramePoolSize = 0)
    {
        _preset = preset;
        _gpuFramePoolSize = gpuFramePoolSize;
        _selectedEncoder = SelectEncoder();
        _isHardwareEncoder = EncoderDeviceTypeMap.ContainsKey(_selectedEncoder);
        _hwDeviceType = _isHardwareEncoder ? EncoderDeviceTypeMap[_selectedEncoder] : AVHWDeviceType.AV_HWDEVICE_TYPE_NONE;

        _swFormat = _isHardwareEncoder ? AVPixelFormat.AV_PIX_FMT_NV12 : AVPixelFormat.AV_PIX_FMT_YUV420P;
        _hwFormat = _hwDeviceType switch
        {
            AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA => AVPixelFormat.AV_PIX_FMT_CUDA,
            AVHWDeviceType.AV_HWDEVICE_TYPE_QSV => AVPixelFormat.AV_PIX_FMT_QSV,
            AVHWDeviceType.AV_HWDEVICE_TYPE_D3D11VA => AVPixelFormat.AV_PIX_FMT_D3D11,
            AVHWDeviceType.AV_HWDEVICE_TYPE_VAAPI => AVPixelFormat.AV_PIX_FMT_VAAPI,
            _ => AVPixelFormat.AV_PIX_FMT_NONE,
        };

        _useHardwareUpload = false;
    }

    public void Configure(int width, int height, int fps, int bitrateKbps, float maxRateMultiplier = 2.0f)
    {
        lock (_lock)
        {
            if (_configured)
                throw new InvalidOperationException("Encoder already configured.");

            _width = width;
            _height = height;
            _fps = fps;
            _bitrateKbps = bitrateKbps;
            _maxRateMultiplier = maxRateMultiplier;

            InitializeEncoder();
            _configured = true;
        }
    }

    public void Encode(CapturedFrame frame)
    {
        if (frame == null) throw new ArgumentNullException(nameof(frame));

        lock (_lock)
        {
            if (!_configured)
                throw new InvalidOperationException("Encoder not configured. Call Configure() first.");

            try
            {
                EncodeInternal(frame);
            }
            finally
            {
                frame.Dispose();
            }
        }
    }

    public void ForceKeyframe()
    {
        // AV1 NVENC does not support forced keyframes via pict_type or gop_size=1
        // (both produce corrupted bitstream). Instead, we reduce the GOP interval
        // so the next natural keyframe arrives quickly.
        // Reset to 1 second GOP — the normal GOP (120) is restored after the next keyframe.
        if (_resources.CodecContext != null && _resources.CodecContext->gop_size > _fps)
        {
            _resources.CodecContext->gop_size = _fps; // 1 second worth of frames
            _forceKeyframeRestoreGop = true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _resources.Dispose();
    }

    private string SelectEncoder()
    {
        foreach (var encoderName in AV1EncoderPriority)
        {
            var codec = ffmpeg.avcodec_find_encoder_by_name(encoderName);
            if (codec != null)
            {
                return encoderName;
            }
        }

        throw new InvalidOperationException(
            "No AV1 encoder found. Install FFmpeg with AV1 support (av1_nvenc, svt_av1, or libaom-av1).");
    }

    private static string AvErr2Str(int errNum)
    {
        var buf = new byte[ffmpeg.AV_ERROR_MAX_STRING_SIZE];
        fixed (byte* bufPtr = buf)
        {
            ffmpeg.av_strerror(errNum, bufPtr, (ulong)ffmpeg.AV_ERROR_MAX_STRING_SIZE);
            return Marshal.PtrToStringAnsi((IntPtr)bufPtr) ?? $"Unknown error {errNum}";
        }
    }

    private void InitializeEncoder()
    {
        var codec = ffmpeg.avcodec_find_encoder_by_name(_selectedEncoder);
        if (codec == null)
            throw new InvalidOperationException($"AV1 encoder '{_selectedEncoder}' not found.");

        _resources.CodecContext = ffmpeg.avcodec_alloc_context3(codec);
        if (_resources.CodecContext == null)
            throw new InvalidOperationException("Failed to allocate AV1 codec context.");

        _resources.Packet = ffmpeg.av_packet_alloc();
        if (_resources.Packet == null)
            throw new InvalidOperationException("Failed to allocate packet.");

        _resources.CodecContext->width = _width;
        _resources.CodecContext->height = _height;
        _resources.CodecContext->time_base = new AVRational { num = 1, den = _fps };
        _resources.CodecContext->framerate = new AVRational { num = _fps, den = 1 };
        _resources.CodecContext->bit_rate = _bitrateKbps * 1000L;
        _resources.CodecContext->pix_fmt = _swFormat;
        _resources.CodecContext->max_b_frames = 0;
        _resources.CodecContext->gop_size = 120; // ~2s at 60fps — AV1 NVENC doesn't support forced keyframes

        var options = EncoderOptionsBuilder.BuildOptions(_selectedEncoder, _preset, _bitrateKbps, _maxRateMultiplier);

        if (_isHardwareEncoder)
        {
            _useHardwareUpload = TryInitializeHardwareUpload();
        }

        if (!_useHardwareUpload)
        {
            _resources.AllocateSoftwareResources(_width, _height, InputPixelFormat, _swFormat);
        }

        int ret = ffmpeg.avcodec_open2(_resources.CodecContext, codec, &options);
        ffmpeg.av_dict_free(&options);
        if (ret < 0)
            throw new InvalidOperationException($"Failed to open AV1 encoder: {AvErr2Str(ret)}");
    }

    private bool TryInitializeHardwareUpload()
    {
        if (!_isHardwareEncoder)
            return false;

        AVBufferRef* deviceCtx = null;
        int ret = ffmpeg.av_hwdevice_ctx_create(&deviceCtx, _hwDeviceType, null, null, 0);
        if (ret < 0 || deviceCtx == null)
        {
            return false;
        }

        AVBufferRef* framesRef = ffmpeg.av_hwframe_ctx_alloc(deviceCtx);
        if (framesRef == null)
        {
            ffmpeg.av_buffer_unref(&deviceCtx);
            return false;
        }

        var hwFrames = (AVHWFramesContext*)framesRef->data;
        hwFrames->format = _hwFormat;
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

        _resources.CodecContext->hw_frames_ctx = ffmpeg.av_buffer_ref(framesRef);
        _resources.CodecContext->pix_fmt = _hwFormat;

        _resources.HwDeviceCtx = deviceCtx;
        _resources.HwFramesCtx = framesRef;

        _resources.AllocateHardwareResources(_width, _height, InputPixelFormat);

        return true;
    }

    private void EncodeInternal(CapturedFrame frame)
    {
        if (_useHardwareUpload)
        {
            EncodeWithHardwareUpload(frame);
        }
        else
        {
            EncodeWithSoftwarePath(frame);
        }
    }

    private void EncodeWithHardwareUpload(CapturedFrame frame)
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

        _resources.HwFrame->pts = _frameCounter++;

        ret = ffmpeg.avcodec_send_frame(_resources.CodecContext, _resources.HwFrame);
        ffmpeg.av_frame_unref(_resources.HwFrame);

        if (ret < 0)
        {
            return;
        }

        ReceivePackets();
    }

    private void EncodeWithSoftwarePath(CapturedFrame frame)
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

        _resources.YuvFrame->pts = _frameCounter++;

        ret = ffmpeg.avcodec_send_frame(_resources.CodecContext, _resources.YuvFrame);
        if (ret < 0)
        {
            return;
        }

        ReceivePackets();
    }

    private void ReceivePackets()
    {
        var packet = _resources.Packet;

        while (true)
        {
            int ret = ffmpeg.avcodec_receive_packet(_resources.CodecContext, packet);
            if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                break;
            if (ret < 0)
            {
                break;
            }

            int isKeyframe = (packet->flags & ffmpeg.AV_PKT_FLAG_KEY) != 0 ? 1 : 0;
            int length = (int)packet->size;

            // Restore normal GOP after a forced-keyframe-induced short GOP produced a keyframe
            if (isKeyframe != 0 && _forceKeyframeRestoreGop)
            {
                _resources.CodecContext->gop_size = 120;
                _forceKeyframeRestoreGop = false;
            }

            if (_obuBuffer.Length < length)
            {
                Array.Resize(ref _obuBuffer, Math.Max(_obuBuffer.Length * 2, length));
            }

            Marshal.Copy((IntPtr)packet->data, _obuBuffer, 0, length);
            ffmpeg.av_packet_unref(packet);

            // FFmpeg av1_nvenc outputs raw OBUs (has_size_field=0, low-overhead bitstream format)
            // SIPSorcery's AV1Packetiser.ParseObus() expects Annex G format:
            //   - Intermediate OBUs: has_size_field=1 + LEB128-encoded size
            //   - Last OBU: has_size_field=0 (extends to end of buffer)
            // 
            // Convert raw OBU stream to Annex G: scan for OBU boundaries using header byte patterns.
            int convertedLength = ConvertRawObusToAnnexG(_obuBuffer, length);
            OnEncodedNALU?.Invoke(_obuBuffer, convertedLength, isKeyframe);
            _frameCounter++;
        }
    }

    /// <summary>
    /// Converts raw OBU stream to Annex G format (has_size_field=1 + LEB128-encoded size).
    /// FFmpeg's av1_nvenc outputs low-overhead bitstream format where OBUs have no size field.
    /// SIPSorcery's AV1Packetiser expects Annex G: intermediate OBUs have has_size_field=1,
    /// only the final OBU has has_size_field=0 (extends to end of buffer).
    /// </summary>
    /// <param name="buffer">OBU data buffer (will be modified in-place). Must have buffer.Length > length * 2.</param>
    /// <param name="length">Valid data length in buffer.</param>
    /// <returns>Converted data length in buffer.</returns>
    private int ConvertRawObusToAnnexG(byte[] buffer, int length)
    {
        if (length < 2) return length;

        // Check if this is Annex B format (start code delimited: 0x00000001 or 0x000001)
        bool isAnnexB = (length >= 4 && buffer[0] == 0 && buffer[1] == 0 && buffer[2] == 0 && buffer[3] == 1) ||
                        (length >= 3 && buffer[0] == 0 && buffer[1] == 0 && buffer[2] == 1);

        if (!isAnnexB)
        {
            // This is low-overhead bitstream format (raw OBUs).
            // Check the first OBU header to see if it already has has_size_field=1.
            byte firstHeader = buffer[0];
            bool hasSizeField = (firstHeader & 0x02) != 0;

            if (hasSizeField)
            {
                // Already in Annex G format — pass through
                return length;
            }

            // Raw OBU format: convert to Annex G.
            // Strategy: parse each OBU, prepend LEB128 size, set has_size_field=1.
            // Last OBU keeps has_size_field=0 (Per ParseObus spec: no size field means extends to end of buffer).
            return ConvertLowOverheadToAnnexG(buffer, length);
        }

        // Annex B format: split by start codes, convert to Annex G
        return ConvertAnnexBToAnnexG(buffer, length);
    }

    private int ConvertLowOverheadToAnnexG(byte[] buffer, int length)
    {
        // Copy original to avoid overwriting during conversion
        if (_scratchBuffer.Length < length)
            _scratchBuffer = new byte[Math.Max(_scratchBuffer.Length * 2, length + length / 2)];
        System.Buffer.BlockCopy(buffer, 0, _scratchBuffer, 0, length);

        // Pre-scan: estimate max possible OBU count (at worst, 1-byte OBU header each)
        // We need extra space for LEB128 size fields (1-4 bytes each for typical sizes)
        int maxNeeded = length + length / 2; // ~50% overhead is generous
        if (buffer.Length < maxNeeded)
            System.Array.Resize(ref buffer, maxNeeded);

        int writePos = 0;

        // Scan for OBUs by looking for valid header patterns.
        // In low-overhead format, each OBU starts with:
        //   header byte where bits 7..2 = [forbidden=0, obu_type, extension_flag]
        //   followed by payload that extends to next OBU or end of stream
        //
        // To detect OBU boundary: scan forward for the next byte that is a valid OBU header:
        //   - forbidden_bit=0 (bit 7 = 0)
        //   - obu_type ∈ {0,1,2,3,4,5,6,7,8,15}
        //   - has_size_field=0 (bit 1 = 0, since we're in low-overhead format)

        int readPos = 0;
        while (readPos < length)
        {
            byte hdr = _scratchBuffer[readPos];
            int obuType = (hdr >> 3) & 0x0F;
            bool extFlag = (hdr & 0x04) != 0;
            int hdrLen = extFlag ? 2 : 1;

            // Find the end of this OBU by scanning for next valid OBU header
            int obuEnd = FindNextObuHeader(_scratchBuffer, readPos + hdrLen, length);

            // If no valid header found, this OBU extends to end of buffer
            if (obuEnd == -1)
                obuEnd = length;

            int payloadLen = obuEnd - (readPos + hdrLen);

            // Check if this is the LAST OBU (extends to end of buffer)
            bool isLastObu = (obuEnd >= length);

            if (isLastObu)
            {
                // Last OBU: keep as raw format (has_size_field=0) — ParseObus accepts this
                System.Buffer.BlockCopy(_scratchBuffer, readPos, buffer, writePos, length - readPos);
                writePos += length - readPos;
                readPos = length;
            }
            else
            {
                // Intermediate OBU: convert to Annex G format
                // Write modified header byte: set has_size_field=1
                buffer[writePos++] = (byte)(hdr | 0x02);

                // Write extension header if present
                if (extFlag)
                    buffer[writePos++] = _scratchBuffer[readPos + 1];

                // Write LEB128-encoded size
                writePos += WriteLeb128(buffer, writePos, payloadLen);

                // Write payload
                System.Buffer.BlockCopy(_scratchBuffer, readPos + hdrLen, buffer, writePos, payloadLen);
                writePos += payloadLen;

                readPos = obuEnd;
            }
        }

        return writePos;
    }

    /// <summary>
    /// Scans forward from startIdx to find the next valid OBU header byte.
    /// Returns -1 if no valid header found before endIdx.
    /// </summary>
    private static int FindNextObuHeader(byte[] data, int startIdx, int endIdx)
    {
        for (int i = startIdx; i < endIdx; i++)
        {
            byte hdr = data[i];

            // forbidden_bit must be 0
            if ((hdr & 0x80) != 0) continue;

            int obuType = (hdr >> 3) & 0x0F;
            // Valid AV1 OBU types: 0..8, 15 (padding)
            if (obuType > 8 && obuType != 15) continue;

            // has_size_field must be 0 for low-overhead mode
            bool hasSizeField = (hdr & 0x02) != 0;
            if (hasSizeField) continue;

            // Found a valid OBU header
            return i;
        }

        return -1;
    }

    /// <summary>
    /// Splits Annex B start-code-delimited stream and converts to Annex G format.
    /// </summary>
    private int ConvertAnnexBToAnnexG(byte[] buffer, int length)
    {
        if (_scratchBuffer.Length < length)
            _scratchBuffer = new byte[Math.Max(_scratchBuffer.Length * 2, length + length / 2)];
        System.Buffer.BlockCopy(buffer, 0, _scratchBuffer, 0, length);

        int maxNeeded = length + length / 2;
        if (buffer.Length < maxNeeded)
            System.Array.Resize(ref buffer, maxNeeded);

        int writePos = 0;
        int readPos = 0;

        while (readPos < length)
        {
            byte b0 = _scratchBuffer[readPos];
            int startCodeLen;

            if (readPos + 3 < length && b0 == 0 && _scratchBuffer[readPos + 1] == 0 && _scratchBuffer[readPos + 2] == 0 && _scratchBuffer[readPos + 3] == 1)
                startCodeLen = 4;
            else if (readPos + 2 < length && b0 == 0 && _scratchBuffer[readPos + 1] == 0 && _scratchBuffer[readPos + 2] == 1)
                startCodeLen = 3;
            else
            {
                // Not a start code — skip this byte
                readPos++;
                continue;
            }

            int obuStart = readPos + startCodeLen;

            // Find next start code
            int nextStart = -1;
            for (int i = obuStart; i < length - 2; i++)
            {
                if (_scratchBuffer[i] == 0 && _scratchBuffer[i + 1] == 0)
                {
                    if (i + 3 < length && _scratchBuffer[i + 2] == 0 && _scratchBuffer[i + 3] == 1)
                    {
                        nextStart = i;
                        break;
                    }
                    if (_scratchBuffer[i + 2] == 1)
                    {
                        nextStart = i;
                        break;
                    }
                }
            }

            int obuLen = (nextStart == -1) ? length - obuStart : nextStart - obuStart;
            bool isLastObu = nextStart == -1;

            if (isLastObu)
            {
                System.Buffer.BlockCopy(_scratchBuffer, obuStart, buffer, writePos, obuLen);
                writePos += obuLen;
            }
            else
            {
                byte firstByte = _scratchBuffer[obuStart];
                buffer[writePos++] = (byte)(firstByte | 0x02); // Set has_size_field=1
                writePos += WriteLeb128(buffer, writePos, obuLen - 1);
                System.Buffer.BlockCopy(_scratchBuffer, obuStart + 1, buffer, writePos, obuLen - 1);
                writePos += obuLen - 1;
            }

            readPos = nextStart == -1 ? length : nextStart;
        }

        return writePos;
    }

    /// <summary>Write LEB128-encoded value into dst at offset, return bytes written.</summary>
    private static int WriteLeb128(byte[] dst, int offset, int value)
    {
        int bytes = 0;
        do
        {
            byte b = (byte)(value & 0x7f);
            value >>= 7;
            if (value > 0) b |= 0x80;
            dst[offset + bytes++] = b;
        } while (value > 0);
        return bytes;
    }
}