namespace gStream.Core.Capture;

/// <summary>
/// Abstract capture source. Both Godot Viewport capture and OBS-style window capture implement this.
/// </summary>
public interface ICaptureSource : IAsyncDisposable
{
    /// <summary>Current capture resolution.</summary>
    (int Width, int Height) Resolution { get; }

    /// <summary>Starts capture. Fires <see cref="OnFrame"/> on each captured frame.</summary>
    Task StartAsync(CancellationToken ct);

    /// <summary>
    /// Fired when a new frame is available.
    /// The consumer MUST call <see cref="CapturedFrame.Dispose"/> after processing.
    /// </summary>
    event Action<CapturedFrame>? OnFrame;
}
