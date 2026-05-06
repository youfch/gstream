using FFmpeg.AutoGen;

namespace gStream.Core.Encoding;

internal static unsafe class EncoderOptionsBuilder
{
    /// <summary>
    /// Builds an AVDictionary of encoder options. The caller owns the returned dictionary
    /// and must pass it to <c>avcodec_open2</c> and then free it with <c>av_dict_free</c>.
    /// </summary>
    public static unsafe AVDictionary* BuildOptions(string? encoderName, EncoderPreset preset, int bitrateKbps, float maxRateMultiplier = 2.0f)
    {
        var options = (AVDictionary*)IntPtr.Zero;

        switch (encoderName)
        {
            case "h264_nvenc":
            case "hevc_nvenc":
                ffmpeg.av_dict_set(&options, "preset", GetNvencPreset(preset), 0);
                ffmpeg.av_dict_set(&options, "tune", "ull", 0);
                ffmpeg.av_dict_set(&options, "rc", "vbr", 0);
                ffmpeg.av_dict_set(&options, "maxrate", $"{(int)(bitrateKbps * maxRateMultiplier)}k", 0);
                ffmpeg.av_dict_set(&options, "zerolatency", "1", 0);
                ffmpeg.av_dict_set(&options, "delay", "0", 0);
                // Force IDR (not just I-frame) when ForceKeyframe() is requested.
                // Default (0) produces non-IDR I-frames that lack SPS/PPS,
                // causing browser decoder failure on reconnect (refresh).
                ffmpeg.av_dict_set(&options, "forced-idr", "1", 0);
                break;

            case "h264_amf":
            case "hevc_amf":
                ffmpeg.av_dict_set(&options, "preset", GetAmfPreset(preset), 0);
                ffmpeg.av_dict_set(&options, "rc", "vbr_peak", 0);
                ffmpeg.av_dict_set(&options, "maxrate", $"{(int)(bitrateKbps * maxRateMultiplier)}k", 0);
                ffmpeg.av_dict_set(&options, "latency", "0", 0); // Minimize AMF internal buffering
                break;

            case "h264_videotoolbox":
            case "hevc_videotoolbox":
                ffmpeg.av_dict_set(&options, "preset", GetVideoToolboxPreset(preset), 0);
                break;

            case "h264_vaapi":
            case "hevc_vaapi":
                ffmpeg.av_dict_set(&options, "preset", GetVaapiPreset(preset), 0);
                break;

            case "h264_qsv":
            case "hevc_qsv":
                ffmpeg.av_dict_set(&options, "preset", GetQsvPreset(preset), 0);
                ffmpeg.av_dict_set(&options, "look_ahead", "0", 0);
                break;

            case "libx264":
                ffmpeg.av_dict_set(&options, "preset", GetLibx264Preset(preset), 0);
                ffmpeg.av_dict_set(&options, "tune", "zerolatency", 0);
                break;

            case "libx265":
                ffmpeg.av_dict_set(&options, "preset", GetLibx265Preset(preset), 0);
                ffmpeg.av_dict_set(&options, "tune", "zerolatency", 0);
                ffmpeg.av_dict_set(&options, "x265-params", "bframes=0:keyint=-1", 0);
                break;

            case "av1_nvenc":
                ffmpeg.av_dict_set(&options, "preset", GetNvencPreset(preset), 0);
                ffmpeg.av_dict_set(&options, "tune", "ull", 0);
                ffmpeg.av_dict_set(&options, "rc", "vbr", 0);
                ffmpeg.av_dict_set(&options, "maxrate", $"{(int)(bitrateKbps * maxRateMultiplier)}k", 0);
                ffmpeg.av_dict_set(&options, "zerolatency", "1", 0);
                ffmpeg.av_dict_set(&options, "delay", "0", 0);
                break;

            case "av1_qsv":
                ffmpeg.av_dict_set(&options, "preset", GetQsvPreset(preset), 0);
                ffmpeg.av_dict_set(&options, "look_ahead", "0", 0);
                break;

            case "av1_amf":
                ffmpeg.av_dict_set(&options, "preset", GetAmfPreset(preset), 0);
                ffmpeg.av_dict_set(&options, "rc", "vbr_peak", 0);
                ffmpeg.av_dict_set(&options, "maxrate", $"{(int)(bitrateKbps * maxRateMultiplier)}k", 0);
                ffmpeg.av_dict_set(&options, "latency", "0", 0);
                break;

            case "av1_vaapi":
                ffmpeg.av_dict_set(&options, "preset", GetVaapiPreset(preset), 0);
                break;

            case "svt_av1":
                ffmpeg.av_dict_set(&options, "preset", GetSvtAv1Preset(preset), 0);
                ffmpeg.av_dict_set(&options, "lookahead", "0", 0);
                ffmpeg.av_dict_set(&options, "keyint", $"{int.MaxValue}", 0);
                break;

            case "libaom-av1":
                ffmpeg.av_dict_set(&options, "cpu-used", GetLibaomPreset(preset), 0);
                ffmpeg.av_dict_set(&options, "deadline", "realtime", 0);
                ffmpeg.av_dict_set(&options, "lag-in-frames", "0", 0);
                ffmpeg.av_dict_set(&options, "keyint", $"{int.MaxValue}", 0);
                break;

            case "libvpx-vp9":
                ffmpeg.av_dict_set(&options, "cpu-used", GetLibvpxVp9Preset(preset), 0);
                ffmpeg.av_dict_set(&options, "deadline", "realtime", 0);
                ffmpeg.av_dict_set(&options, "lag-in-frames", "0", 0);
                ffmpeg.av_dict_set(&options, "row-mt", "1", 0);
                ffmpeg.av_dict_set(&options, "error-resilient", "1", 0);
                // Tile-based parallelism: tile-columns=log2(num_tile_cols).
                // At 1152px wide, 2 tile columns allow parallel encoding within a frame.
                ffmpeg.av_dict_set(&options, "tile-columns", "1", 0);  // 2^1 = 2 tile columns
                ffmpeg.av_dict_set(&options, "tile-rows", "0", 0);     // 1 tile row (height too small for 2)
                ffmpeg.av_dict_set(&options, "static-thresh", "1", 0); // Skip static blocks to save bits
                break;
        }

        return options;
    }

    private static string GetNvencPreset(EncoderPreset preset) => preset switch
    {
        EncoderPreset.UltraLowLatency => "p4",   // p1→p4: ~1ms extra latency, significant quality gain
        EncoderPreset.LowLatency => "p4",
        EncoderPreset.Balanced => "p5",
        EncoderPreset.HighQuality => "p7",
        _ => "p4"
    };

    private static string GetAmfPreset(EncoderPreset preset) => preset switch
    {
        EncoderPreset.UltraLowLatency => "speed",
        EncoderPreset.LowLatency => "balanced",
        EncoderPreset.Balanced => "balanced",
        EncoderPreset.HighQuality => "quality",
        _ => "speed"
    };

    private static string GetVideoToolboxPreset(EncoderPreset preset) => preset switch
    {
        EncoderPreset.UltraLowLatency => "low_latency",
        EncoderPreset.LowLatency => "low_latency",
        EncoderPreset.Balanced => "normal",
        EncoderPreset.HighQuality => "quality",
        _ => "low_latency"
    };

    private static string GetLibx264Preset(EncoderPreset preset) => preset switch
    {
        EncoderPreset.UltraLowLatency => "superfast",  // ultrafast→superfast: better quality, minimal latency cost
        EncoderPreset.LowLatency => "superfast",
        EncoderPreset.Balanced => "fast",
        EncoderPreset.HighQuality => "medium",
        _ => "superfast"
    };

    private static string GetQsvPreset(EncoderPreset preset) => preset switch
    {
        EncoderPreset.UltraLowLatency => "ultrafast",
        EncoderPreset.LowLatency => "fast",
        EncoderPreset.Balanced => "medium",
        EncoderPreset.HighQuality => "slow",
        _ => "ultrafast"
    };

    private static string GetVaapiPreset(EncoderPreset preset) => preset switch
    {
        EncoderPreset.UltraLowLatency => "ultrafast",
        EncoderPreset.LowLatency => "fast",
        EncoderPreset.Balanced => "medium",
        EncoderPreset.HighQuality => "slow",
        _ => "ultrafast"
    };

    private static string GetLibx265Preset(EncoderPreset preset) => preset switch
    {
        EncoderPreset.UltraLowLatency => "ultrafast",
        EncoderPreset.LowLatency => "superfast",
        EncoderPreset.Balanced => "fast",
        EncoderPreset.HighQuality => "medium",
        _ => "ultrafast"
    };

    private static string GetSvtAv1Preset(EncoderPreset preset) => preset switch
    {
        EncoderPreset.UltraLowLatency => "8",
        EncoderPreset.LowLatency => "7",
        EncoderPreset.Balanced => "6",
        EncoderPreset.HighQuality => "4",
        _ => "8"
    };

    private static string GetLibaomPreset(EncoderPreset preset) => preset switch
    {
        EncoderPreset.UltraLowLatency => "8",
        EncoderPreset.LowLatency => "6",
        EncoderPreset.Balanced => "4",
        EncoderPreset.HighQuality => "2",
        _ => "8"
    };

    private static string GetLibvpxVp9Preset(EncoderPreset preset) => preset switch
    {
        EncoderPreset.UltraLowLatency => "8",
        EncoderPreset.LowLatency => "6",
        EncoderPreset.Balanced => "4",
        EncoderPreset.HighQuality => "2",
        _ => "8"
    };
}
