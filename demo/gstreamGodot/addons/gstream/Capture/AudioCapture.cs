using System;
using Godot;

namespace gStream.Godot.Capture;

/// <summary>
/// Wraps Godot's AudioEffectCapture to capture game audio from the Master audio bus
/// as interleaved float32 PCM samples suitable for Opus encoding.
///
/// Usage: Create → Start() when streaming begins → TryGetSamples() each _Process frame → Stop() when done.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    /// <summary>Opus standard sample rate (48kHz).</summary>
    public const int SampleRate = 48000;

    /// <summary>Stereo output.</summary>
    public const int Channels = 2;

    /// <summary>20ms frame at 48kHz = 960 samples per channel — one Opus frame.</summary>
    public const int FrameSize = 960;

    private AudioEffectCapture _capture;
    private bool _started;
    private bool _disposed;

    public AudioCapture()
    {
        _capture = new AudioEffectCapture();
    }

    /// <summary>
    /// Adds the AudioEffectCapture to bus 0 (Master) and clears any existing buffer.
    /// Safe to call multiple times — no-ops if already started.
    /// </summary>
    public void Start()
    {
        if (_started) return;

        AudioServer.AddBusEffect(0, _capture);
        _capture.ClearBuffer();
        _started = true;

        GD.Print("[AudioCapture] Started — capturing from Master bus");
    }

    /// <summary>
    /// Removes the effect from the Master bus. Safe to call if not started.
    /// </summary>
    public void Stop()
    {
        if (!_started) return;

        // Find and remove our capture effect from bus 0
        int effectCount = AudioServer.GetBusEffectCount(0);
        for (int i = effectCount - 1; i >= 0; i--)
        {
            if (AudioServer.GetBusEffect(0, i) == _capture)
            {
                AudioServer.RemoveBusEffect(0, i);
                break;
            }
        }

        _started = false;
        GD.Print("[AudioCapture] Stopped");
    }

    /// <summary>
    /// Non-blocking poll for audio samples. Checks if enough frames are available
    /// for one Opus frame (960 frames @ 48kHz). If available, reads from the capture
    /// buffer and converts Godot Vector2[] to interleaved float[] (L,R,L,R...).
    /// </summary>
    /// <param name="samples">Interleaved float32 PCM samples, or null if not enough data.</param>
    /// <returns>True if a complete frame was read, false otherwise.</returns>
    public bool TryGetSamples(out float[]? samples)
    {
        samples = null;

        if (!_started || _disposed)
            return false;

        int available = _capture.GetFramesAvailable();
        if (available < FrameSize)
            return false;

        // Read exactly one Opus frame worth of stereo samples
        var data = _capture.GetBuffer(FrameSize);
        if (data == null || data.Length == 0)
            return false;

        // Convert Vector2[] (stereo) to interleaved float[]: [L0, R0, L1, R1, ...]
        samples = new float[FrameSize * Channels];
        for (int i = 0; i < data.Length; i++)
        {
            int baseIdx = i * Channels;
            samples[baseIdx] = data[i].X;     // Left
            samples[baseIdx + 1] = data[i].Y; // Right
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed) return;

        Stop();
        _capture?.Dispose();
        _capture = null!;
        _disposed = true;
    }
}
