namespace gStream.Core.Encoding;

/// <summary>
/// Encoder performance presets, optimized for different latency/quality tradeoffs.
/// </summary>
public enum EncoderPreset
{
    /// <summary>
    /// Lowest possible latency, minimal buffering. Best for real-time interactive streaming.
    /// May sacrifice quality and compression efficiency.
    /// </summary>
    UltraLowLatency,

    /// <summary>
    /// Low latency with reasonable quality. Good for most streaming scenarios.
    /// </summary>
    LowLatency,

    /// <summary>
    /// Balanced between latency and quality. Suitable for delayed streaming.
    /// </summary>
    Balanced,

    /// <summary>
    /// Maximum quality, higher latency. Best for recording or non-real-time use.
    /// </summary>
    HighQuality
}