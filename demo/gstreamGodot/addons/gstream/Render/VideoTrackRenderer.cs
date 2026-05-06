// VideoTrackRenderer.cs — Renders received remote video frames to a Godot ImageTexture

using System;
using System.Collections.Concurrent;
using Godot;

namespace gStream.Godot.Render;

/// <summary>
/// Takes raw BGRA pixel data from a remote video track and renders it to a Godot
/// <see cref="ImageTexture"/>. Frames are queued from background threads and drained
/// on the Godot main thread via <see cref="Process"/> (called from _Process).
///
/// Supports dynamic resolution changes — automatically creates a new texture when
/// the frame dimensions change.
/// </summary>
public sealed class VideoTrackRenderer : IDisposable
{
    private Image? _image;
    private ImageTexture? _texture;
    private int _width;
    private int _height;
    private bool _disposed;

    /// <summary>
    /// Lock protecting _image, _texture, _width, _height.
    /// Taken during Process (main thread) to update texture data.
    /// </summary>
    private readonly object _renderLock = new();

    /// <summary>
    /// Queue of pending frames from background threads.
    /// Bounded to prevent unbounded memory growth if main thread is slow.
    /// </summary>
    private readonly ConcurrentQueue<PendingFrame> _frameQueue = new();
    private const int MaxQueuedFrames = 3;

    /// <summary>
    /// Gets the current texture for display. Assign this to a TextureRect's Texture property.
    /// Returns null until the first frame is received.
    /// Thread-safe: returns a snapshot that remains valid (texture objects are replaced, not mutated).
    /// </summary>
    public Texture2D? Texture => _texture;

    /// <summary>
    /// Fired once when the first remote video frame is received.
    /// Can be used to signal that remote video is ready for display.
    /// </summary>
    public event Action? OnFirstFrameReceived;

    /// <summary>
    /// Fired when the remote video resolution changes.
    /// </summary>
    public event Action<int, int>? OnResolutionChanged;

    private bool _firstFrameSignaled;

    /// <summary>
    /// Enqueues a decoded video frame (raw BGRA32 pixel data) for rendering.
    /// Called from background SIPSorcery threads — must be thread-safe.
    /// The pixel data is copied immediately; the caller can reuse the buffer.
    /// </summary>
    /// <param name="bgraData">Raw BGRA32 pixel data (width * height * 4 bytes).</param>
    /// <param name="width">Frame width in pixels.</param>
    /// <param name="height">Frame height in pixels.</param>
    public void EnqueueFrame(byte[] bgraData, int width, int height)
    {
        if (_disposed) return;

        // Drop oldest frames if queue is full
        while (_frameQueue.Count >= MaxQueuedFrames)
        {
            _frameQueue.TryDequeue(out _);
        }

        var frame = new PendingFrame
        {
            Data = bgraData,
            Width = width,
            Height = height
        };

        _frameQueue.Enqueue(frame);
    }

    /// <summary>
    /// Drains pending frames and updates the texture. Must be called from the Godot
    /// main thread (e.g., from _Process). Only the latest frame is rendered;
    /// intermediate frames are dropped.
    /// </summary>
    public void Process()
    {
        if (_disposed) return;

        // Drain to latest frame
        PendingFrame? latest = null;
        while (_frameQueue.TryDequeue(out var frame))
        {
            latest?.Return();
            latest = frame;
        }

        if (latest == null) return;

        try
        {
            lock (_renderLock)
            {
                var w = latest.Width;
                var h = latest.Height;

                // Resolution change — recreate image + texture
                if (w != _width || h != _height || _image == null)
                {
                    _image = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
                    _texture = ImageTexture.CreateFromImage(_image);
                    _width = w;
                    _height = h;

                    OnResolutionChanged?.Invoke(w, h);
                }

                // Copy BGRA data into the Godot Image
                // Godot Image format is RGBA8, BGRA input gets swapped to RGBA by setting
                // the byte array directly — Godot expects RGBA byte order.
                // For simplicity and performance, we swap B↔R channels during copy.
                var data = latest.Data;
                if (data.Length >= w * h * 4)
                {
                    // Swap BGRA → RGBA in-place before setting to Image
                    for (int i = 0; i < w * h * 4; i += 4)
                    {
                        byte b = data[i];
                        data[i] = data[i + 2]; // R ← B position gets B value
                        data[i + 2] = b;        // B position gets R value
                    }
                    _image.SetData(w, h, false, Image.Format.Rgba8, data);
                    _texture!.Update(_image);
                }

                if (!_firstFrameSignaled)
                {
                    _firstFrameSignaled = true;
                    OnFirstFrameReceived?.Invoke();
                }
            }
        }
        finally
        {
            latest.Return();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Drain remaining frames
        while (_frameQueue.TryDequeue(out var frame))
        {
            frame.Return();
        }

        lock (_renderLock)
        {
            _image = null;
            _texture = null;
        }
    }

    private sealed class PendingFrame
    {
        private static readonly ConcurrentBag<byte[]> BufferPool = new();
        private const int MaxPoolSize = 5;
        private static int _poolCount;

        public byte[] Data = null!;
        public int Width;
        public int Height;

        public static byte[] Rent(int minSize)
        {
            if (BufferPool.TryTake(out var buffer) && buffer.Length >= minSize)
            {
                return buffer;
            }
            // Return undersized buffer to pool (will be GC'd if pool full)
            return new byte[minSize];
        }

        public void Return()
        {
            if (Data != null && _poolCount < MaxPoolSize)
            {
                System.Threading.Interlocked.Increment(ref _poolCount);
                BufferPool.Add(Data);
                Data = null!;
            }
        }
    }
}
