namespace gStream.Core.Encoding;

/// <summary>
/// Interface for video encoders that process captured frames into H.264 NAL units.
/// Thread-safe: Encode can be called from any thread.
/// </summary>
public unsafe interface IVideoEncoder : IDisposable
{
    /// <summary>
    /// Configures the encoder with video parameters.
    /// Must be called before any Encode calls.
    /// </summary>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    /// <param name="fps">Frames per second.</param>
    /// <param name="bitrateKbps">Target bitrate in kilobits per second.</param>
    /// <param name="maxRateMultiplier">Peak bitrate as multiplier of target (default 2.0).</param>
    void Configure(int width, int height, int fps, int bitrateKbps, float maxRateMultiplier = 2.0f);

    /// <summary>
    /// Encodes a captured BGRA32 frame.
    /// The encoder takes ownership of the frame and will dispose it when done.
    /// </summary>
    /// <param name="frame">The captured frame to encode. Caller should NOT dispose after passing.</param>
    void Encode(Capture.CapturedFrame frame);

    /// <summary>
    /// Fired when an encoded H.264 NAL unit is ready.
    /// </summary>
    /// <param name="nalu">Raw H.264 NAL unit data (from ArrayPool, valid only during this call).</param>
    /// <param name="length">Actual length of the NALU data.</param>
    /// <param name="isKeyframe">1 if this is a keyframe (IDR/I-frame), 0 if delta frame (P/B-frame).</param>
    event Action<byte[], int, int>? OnEncodedNALU;

    /// <summary>
    /// Forces a keyframe on the next encoded frame.
    /// </summary>
    void ForceKeyframe();
}