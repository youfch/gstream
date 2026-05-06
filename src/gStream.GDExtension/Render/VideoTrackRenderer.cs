using System;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Godot;
using Godot.Collections;

namespace gStream.GDExtension.Render;

/// <summary>
/// Renders received remote video frames to a Godot ImageTexture.
/// GDExtension port — uses Godot.Bindings types.
/// </summary>
public sealed class VideoTrackRenderer : IDisposable
{
    private Image? _image;
    private ImageTexture? _texture;
    private int _width;
    private int _height;
    private bool _disposed;
    private readonly object _renderLock = new();
    private readonly ConcurrentQueue<PendingFrame> _frameQueue = new();
    private const int MaxQueuedFrames = 3;

    public Texture2D? Texture => _texture;
    public event Action? OnFirstFrameReceived;
    public event Action<int, int>? OnResolutionChanged;

    private bool _firstFrameSignaled;

    public void EnqueueFrame(byte[] bgraData, int width, int height)
    {
        if (_disposed) return;

        while (_frameQueue.Count >= MaxQueuedFrames)
            _frameQueue.TryDequeue(out _);

        _frameQueue.Enqueue(new PendingFrame
        {
            Data = bgraData,
            Width = width,
            Height = height
        });
    }

    public void Process()
    {
        if (_disposed) return;

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

                if (w != _width || h != _height || _image == null)
                {
                    _image = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
                    _texture = ImageTexture.CreateFromImage(_image);
                    _width = w;
                    _height = h;
                    OnResolutionChanged?.Invoke(w, h);
                }

                var data = latest.Data;
                if (data.Length >= w * h * 4)
                {
                    // BGRA → RGBA swap using unsafe pointers (avoids per-byte array indexing)
                    SwapBgraToRgba(data, w * h);
                    // Convert byte[] to PackedByteArray for Godot.Bindings Image.SetData
                    var packed = new PackedByteArray(data);
                    _image.SetData(w, h, false, Image.Format.Rgba8, packed);
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

        while (_frameQueue.TryDequeue(out var frame))
            frame.Return();

        lock (_renderLock)
        {
            _image = null;
            _texture = null;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe void SwapBgraToRgba(byte[] data, int pixelCount)
    {
        fixed (byte* ptr = data)
        {
            uint* pixels = (uint*)ptr;
            for (int i = 0; i < pixelCount; i++)
            {
                uint p = pixels[i];
                // Rotate: BGRA (0xAARRGGBB in little-endian) → RGBA
                pixels[i] = (p & 0xFF00FF00u) | ((p & 0x00FF0000u) >> 16) | ((p & 0x000000FFu) << 16);
            }
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
                return buffer;
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
