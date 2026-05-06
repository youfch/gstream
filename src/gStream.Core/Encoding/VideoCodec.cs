namespace gStream.Core.Encoding;

/// <summary>
/// Video codec preference with detailed SDP profile/level variants.
/// Godot Inspector displays enum member names with underscores replaced by spaces.
/// </summary>
public enum VideoCodec
{
    /// <summary>
    /// Auto — SDP declares all supported formats; encoder is lazily
    /// initialized after SDP negotiation picks the mutually supported codec.
    /// </summary>
    Auto = 0,

    // ── H.264 variants ──
    // Godot Inspector shows: "H264 High L31" etc.

    /// <summary>H.264 High Profile, Level 3.1 (recommended, best quality)</summary>
    H264_High_L31 = 1,

    /// <summary>H.264 Main Profile, Level 3.1</summary>
    H264_Main_L31 = 2,

    /// <summary>H.264 Constrained Baseline Profile, Level 3.1</summary>
    H264_CBaseline_L31 = 3,

    /// <summary>H.264 Baseline Profile, Level 3.1</summary>
    H264_Baseline_L31 = 4,

    // ── H.265 / HEVC variants ──

    /// <summary>HEVC Main Profile, Level 4.1, Main Tier</summary>
    H265_Main_L41 = 10,

    // ── AV1 variants ──

    /// <summary>AV1 Main Profile, Level 5, Main Tier</summary>
    AV1_Main_L5 = 20,

    // ── VP9 variants ──

    /// <summary>VP9 Profile 0 (8-bit 4:2:0)</summary>
    VP9_Profile0 = 30,

    /// <summary>VP9 Profile 2 (10/12-bit)</summary>
    VP9_Profile2 = 31,
}

/// <summary>
/// SDP parameter mapping for <see cref="VideoCodec"/> variants.
/// </summary>
public static class VideoCodecSdp
{
    /// <summary>
    /// Returns the SDP codec name and fmtp parameters for the given codec variant.
    /// </summary>
    public static (string codecName, string fmtp) ToSdp(this VideoCodec codec) => codec switch
    {
        VideoCodec.H264_High_L31 => ("H264", "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=64001f"),
        VideoCodec.H264_Main_L31 => ("H264", "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=4d001f"),
        VideoCodec.H264_CBaseline_L31 => ("H264", "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42e01f"),
        VideoCodec.H264_Baseline_L31 => ("H264", "level-asymmetry-allowed=1;packetization-mode=1;profile-level-id=42001f"),
        VideoCodec.H265_Main_L41 => ("H265", "level-id=123;profile-id=1;tier-flag=0;tx-mode=SRST"),
        VideoCodec.AV1_Main_L5 => ("AV1", "level-idx=5;profile=0;tier=0"),
        VideoCodec.VP9_Profile0 => ("VP9", "profile-id=0"),
        VideoCodec.VP9_Profile2 => ("VP9", "profile-id=2"),
        _ => throw new System.ArgumentException($"Unknown or Auto codec has no single SDP mapping: {codec}")
    };

    /// <summary>
    /// Returns true if the codec is H.264 (any variant).
    /// </summary>
    public static bool IsH264Family(this VideoCodec codec) =>
        codec == VideoCodec.H264_High_L31 ||
        codec == VideoCodec.H264_Main_L31 ||
        codec == VideoCodec.H264_CBaseline_L31 ||
        codec == VideoCodec.H264_Baseline_L31;

    /// <summary>
    /// Returns true if the codec is H.265/HEVC.
    /// </summary>
    public static bool IsH265Family(this VideoCodec codec) =>
        codec == VideoCodec.H265_Main_L41;

    /// <summary>
    /// Returns true if the codec is AV1.
    /// </summary>
    public static bool IsAV1Family(this VideoCodec codec) =>
        codec == VideoCodec.AV1_Main_L5;

    /// <summary>
    /// Returns true if the codec is VP9 (any variant).
    /// </summary>
    public static bool IsVP9Family(this VideoCodec codec) =>
        codec == VideoCodec.VP9_Profile0 ||
        codec == VideoCodec.VP9_Profile2;
}
