using System;
using Godot;

namespace gStream.GDExtension.Capture;

/// <summary>
/// Wraps Godot's AudioEffectCapture to capture game audio from the Master audio bus.
/// GDExtension port — uses Godot.Bindings types.
/// </summary>
public sealed class AudioCapture : IDisposable
{
    public const int SampleRate = 48000;
    public const int Channels = 2;
    public const int FrameSize = 960;

    private AudioEffectCapture _capture;
    private bool _started;
    private bool _disposed;

    public AudioCapture()
    {
        _capture = new AudioEffectCapture();
    }

    public void Start()
    {
        if (_started) return;

        AudioServer.Singleton.AddBusEffect(0, _capture);
        _capture.ClearBuffer();
        _started = true;

        GD.Print("[AudioCapture] Started — capturing from Master bus");
    }

    public void Stop()
    {
        if (!_started) return;

        int effectCount = AudioServer.Singleton.GetBusEffectCount(0);
        for (int i = effectCount - 1; i >= 0; i--)
        {
            if (AudioServer.Singleton.GetBusEffect(0, i) == _capture)
            {
                AudioServer.Singleton.RemoveBusEffect(0, i);
                break;
            }
        }

        _started = false;
        GD.Print("[AudioCapture] Stopped");
    }

    public bool TryGetSamples(out float[]? samples)
    {
        samples = null;

        if (!_started || _disposed)
            return false;

        int available = _capture.GetFramesAvailable();
        if (available < FrameSize)
            return false;

        var data = _capture.GetBuffer(FrameSize);
        if (data == null || data.Count == 0)
            return false;

        samples = new float[FrameSize * Channels];
        for (int i = 0; i < data.Count; i++)
        {
            int baseIdx = i * Channels;
            samples[baseIdx] = data[i].X;
            samples[baseIdx + 1] = data[i].Y;
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
