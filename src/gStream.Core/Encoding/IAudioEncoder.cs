namespace gStream.Core.Encoding;

/// <summary>
/// Interface for audio encoders that process PCM samples into encoded audio frames.
/// Thread-safe: Encode can be called from any thread.
/// </summary>
public unsafe interface IAudioEncoder : IDisposable
{
    /// <summary>
    /// Configures the encoder with audio parameters.
    /// Must be called before any Encode calls.
    /// </summary>
    /// <param name="sampleRate">Sample rate in Hz (typically 48000 for Opus).</param>
    /// <param name="channels">Number of audio channels (1=mono, 2=stereo).</param>
    /// <param name="bitrateKbps">Target bitrate in kilobits per second.</param>
    void Configure(int sampleRate, int channels, int bitrateKbps);

    /// <summary>
    /// Encodes a block of interleaved float32 PCM samples.
    /// The encoder buffers internally and emits complete frames via OnEncodedFrame
    /// when enough samples accumulate (e.g., 960 samples per channel for 20ms @ 48kHz).
    /// </summary>
    /// <param name="samples">Interleaved float32 PCM samples (e.g., L,R,L,R for stereo).</param>
    void Encode(ReadOnlySpan<float> samples);

    /// <summary>
    /// Not applicable for audio. Included for interface symmetry with IVideoEncoder.
    /// </summary>
    void ForceKeyframe();

    /// <summary>
    /// Fired when an encoded audio frame is ready.
    /// </summary>
    /// <param name="frameData">Raw encoded audio data (from ArrayPool, valid only during this call).</param>
    /// <param name="length">Actual length of the frame data.</param>
    event Action<byte[], int>? OnEncodedFrame;
}
