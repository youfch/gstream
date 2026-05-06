using System;
using System.Collections.Generic;
using Godot;
using gStream.Core.Capture;
using gStream.Godot.Native;

namespace gStream.Godot.Capture;

/// <summary>
/// Scheme 1: Captures frames from a Godot Viewport/SubViewport using the non-blocking
/// RenderingDevice.texture_get_data_async API (Godot 4.4+, PR #100110).
/// Falls back to sync get_image() if async is unavailable.
///
/// Key design: capture is driven by RenderingServer.FramePostDraw so the GPU has
/// finished rendering the current frame before we read back the texture.
/// No CaptureFrame() call from _Process is needed — the signal drives capture automatically.
///
/// Uses FrameBufferPool to eliminate per-frame heap allocations (~8.3MB @ 1080p per frame).
///
/// Native interop path: when a gstream_native GDExtension is loaded, texture reads go
/// through a C++ extension that copies GPU data directly into a pinned C# buffer via
/// pointer — completely bypassing the Godot C# byte[] marshal (the source of GC pressure).
/// </summary>
public sealed partial class ViewportCapture : IDisposable
{
    private Viewport _viewport = null!;
    private RenderingDevice _rd = null!;
    private int _width;
    private int _height;
    private bool _isAsyncAvailable;
    private bool _running;
    private long _frameCount;

    /// <summary>Pre-allocated pinned buffer pool — eliminates ~500MB/s GC pressure.</summary>
    private FrameBufferPool? _bufferPool;

    /// <summary>Stored callable for the FramePostDraw signal so we can disconnect cleanly.</summary>
    private Callable _framePostDrawCallable;

    /// <summary>Pool of async callback states — eliminates per-frame closure allocation.</summary>
    private readonly Queue<AsyncCaptureState> _asyncStatePool = new();
    private readonly object _asyncStateLock = new();

    /// <summary>Native GDExtension interop — zero-GC texture read via pointer copy.</summary>
    private NativeInterop? _nativeInterop;
    private bool _useNativePath;

    /// <summary>Fired when a frame has been captured and is ready for encoding.</summary>
    public event Action<CapturedFrame>? OnFrame;

    /// <summary>Fired when the viewport resolution changes during streaming.</summary>
    public event Action<int, int>? OnResolutionChanged;

    /// <summary>Capture duration in microseconds for the last synchronous frame (CaptureNative/CaptureSync). 0 for async.</summary>
    public ulong LastCaptureUs;

    /// <summary>Number of frames dropped due to buffer pool exhaustion since last read/reset.</summary>
    public int PoolExhaustionCount;

    /// <summary>Reusable state object for async texture read callbacks.</summary>
    private sealed class AsyncCaptureState
    {
        public ViewportCapture Owner = null!;
        public byte[] Buffer = null!;
        public int Width;
        public int Height;
        public long FrameTimestamp;
        public nint GcHandle;
        public Callable Callback;

        public void Init(ViewportCapture owner, byte[] buffer, int width, int height, long frameTimestamp, nint gcHandle)
        {
            Owner = owner;
            Buffer = buffer;
            Width = width;
            Height = height;
            FrameTimestamp = frameTimestamp;
            GcHandle = gcHandle;
        }

        public void OnGpuDataReady(byte[] data)
        {
            try
            {
                if (!Owner._running || data.Length == 0 || data.Length > Buffer.Length)
                {
                    Owner._bufferPool?.Return(Buffer);
                    return;
                }

                new ReadOnlySpan<byte>(data, 0, data.Length).CopyTo(Buffer);

                var frame = CapturedFrame.WrapPooled(
                    Buffer, Width, Height, Width * 4,
                    FrameTimestamp,
                    GcHandle,
                    buf => Owner._bufferPool?.Return(buf)
                );
                Owner.OnFrame?.Invoke(frame);
            }
            finally
            {
                lock (Owner._asyncStateLock)
                {
                    Owner._asyncStatePool.Enqueue(this);
                }
            }
        }
    }

    public (int Width, int Height) Resolution => (_width, _height);

    /// <summary>
    /// Initialize with a SubViewport (off-screen render target).
    /// </summary>
    public void Initialize(SubViewport viewport)
    {
        InitializeViewport(viewport);
    }

    /// <summary>
    /// Initialize with the main window viewport (current running window).
    /// Captures whatever is displayed on screen each frame.
    /// </summary>
    public void Initialize(Viewport viewport)
    {
        InitializeViewport(viewport);
    }

    private void InitializeViewport(Viewport viewport)
    {
        _viewport = viewport;

        // Only set render target modes for SubViewport (main viewport ignores these)
        if (viewport is SubViewport sub)
        {
            sub.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            sub.RenderTargetClearMode = SubViewport.ClearMode.Always;
        }

        // Get actual render size (accounts for window size for main viewport)
        var size = viewport.GetVisibleRect().Size;
        _width = (int)size.X;
        _height = (int)size.Y;

        _rd = RenderingServer.Singleton.GetRenderingDevice();
        _isAsyncAvailable = CheckAsyncSupport();

        // Try to load the native GDExtension for zero-GC texture reads.
        // Falls back to the C# async/sync path if the extension is unavailable.
        _nativeInterop = NativeInterop.TryCreate();
        _useNativePath = _nativeInterop != null;

        // Pre-allocate buffer pool.
        // Pool must accommodate: BoundedChannel(2) + 1 being encoded + 1 being captured + 1 margin for slow encoders (VP9).
        // Total: 5 buffers (~41MB @ 1080p). Prevents pool exhaustion under CPU-heavy software encoding.
        var bufferSize = _width * _height * 4; // RGBA8 = 4 bytes per pixel
        _bufferPool = new FrameBufferPool(bufferSize, poolSize: 5);

        // Pre-warm async callback state pool (4 states = enough for in-flight async reads)
        // Only needed when NOT using the native path.
        if (!_useNativePath)
        {
            for (int i = 0; i < 4; i++)
            {
                var state = new AsyncCaptureState();
                state.Callback = Callable.From((byte[] data) => state.OnGpuDataReady(data));
                _asyncStatePool.Enqueue(state);
            }
        }

        // Prepare a reusable callable for the FramePostDraw signal
        _framePostDrawCallable = Callable.From(OnFramePostDraw);

        var pathName = _useNativePath ? "Native(ZeroGC)" : (_isAsyncAvailable ? "Async(C#)" : "Sync(C#)");
        GD.Print($"[ViewportCapture] Init {_width}x{_height}, path={pathName}, pool={bufferSize}x3");
    }

    /// <summary>
    /// Start capturing. Connects to RenderingServer.FramePostDraw to capture after
    /// every GPU frame render. No need to call CaptureFrame() from _Process.
    /// </summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _frameCount = 0;

        // Connect to FramePostDraw — fires after GPU finishes each frame render
        RenderingServer.Singleton.Connect(
            RenderingServer.SignalName.FramePostDraw,
            _framePostDrawCallable);

        GD.Print("[ViewportCapture] Started — driven by FramePostDraw signal");
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        // Disconnect the signal
        if (RenderingServer.Singleton.IsConnected(
            RenderingServer.SignalName.FramePostDraw, _framePostDrawCallable))
        {
            RenderingServer.Singleton.Disconnect(
                RenderingServer.SignalName.FramePostDraw, _framePostDrawCallable);
        }

        GD.Print("[ViewportCapture] Stopped");
    }

    /// <summary>
    /// No-op kept for API compatibility. Capture is now driven by FramePostDraw signal.
    /// Safe to call or remove from _Process.
    /// </summary>
    public void CaptureFrame()
    {
        // Capture is driven by FramePostDraw signal, not by explicit calls.
    }

    /// <summary>
    /// Called by RenderingServer after the current frame has been drawn to GPU.
    /// The viewport texture is now valid and safe to read.
    /// </summary>
    private void OnFramePostDraw()
    {
        if (!_running) return;
        _frameCount++;

        // Detect viewport resolution changes (e.g. window resize) and reconfigure.
        CheckResolutionChange();

        if (_useNativePath)
        {
            CaptureNative();
        }
        else if (_isAsyncAvailable)
        {
            CaptureAsync();
        }
        else
        {
            CaptureSync();
        }
    }

    /// <summary>
    /// Checks if the viewport size has changed since last capture setup.
    /// If so, updates cached dimensions, recreates the buffer pool, and fires OnResolutionChanged.
    /// </summary>
    private void CheckResolutionChange()
    {
        var currentSize = _viewport.GetVisibleRect().Size;
        int newW = (int)currentSize.X;
        int newH = (int)currentSize.Y;

        if (newW == _width && newH == _height)
            return;

        int oldW = _width;
        int oldH = _height;

        _width = newW;
        _height = newH;

        // Recreate buffer pool for new resolution
        _bufferPool?.Dispose();
        var bufferSize = _width * _height * 4; // RGBA8
        _bufferPool = new FrameBufferPool(bufferSize, poolSize: 5);

        GD.Print($"[ViewportCapture] Resolution changed: {oldW}x{oldH} → {newW}x{newH}");
        OnResolutionChanged?.Invoke(newW, newH);
    }

    /// <summary>
    /// Zero-GC capture path: copies GPU data directly into a pinned C# buffer
    /// via the native GDExtension. No byte[] allocation at all.
    /// </summary>
    private unsafe void CaptureNative()
    {
        var tex = _viewport.GetTexture();
        if (tex == null) return;

        var rsTexRid = tex.GetRid();
        if (!rsTexRid.IsValid) return;

        var rdTexRid = RenderingServer.TextureGetRdTexture(rsTexRid);
        if (!rdTexRid.IsValid) return;

        var pooledBuffer = _bufferPool?.TryRent();
        if (pooledBuffer == null)
        {
            PoolExhaustionCount++;
            return;
        }

        var captureStart = Time.GetTicksUsec();

        // FrameBufferPool pre-pins all buffers at creation — reuse the pool's GCHandle.
        byte* ptr = _bufferPool!.GetPointer(pooledBuffer);
        nint gcHandle = _bufferPool.GetGCHandle(pooledBuffer);

        long bytesCopied = _nativeInterop!.ReadTextureToPointer(
            rsTexRid, (nint)ptr, _width, _height);

        if (bytesCopied <= 0)
        {
            _bufferPool.Return(pooledBuffer);
            LastCaptureUs = Time.GetTicksUsec() - captureStart;
            return;
        }

        var frame = CapturedFrame.WrapPooled(
            pooledBuffer, _width, _height, _width * 4,
            _frameCount * 16_667,
            gcHandle,
            buf => _bufferPool.Return(buf)
        );
        LastCaptureUs = Time.GetTicksUsec() - captureStart;
        OnFrame?.Invoke(frame);
    }

    private void CaptureAsync()
    {
        var tex = _viewport.GetTexture();
        if (tex == null) return;

        var rsTexRid = tex.GetRid();
        if (!rsTexRid.IsValid) return;

        var rdTexRid = RenderingServer.TextureGetRdTexture(rsTexRid);
        if (!rdTexRid.IsValid)
        {
            return;
        }

        if (!_rd.TextureIsValid(rdTexRid)) return;

        var pooledBuffer = _bufferPool?.TryRent();
        if (pooledBuffer == null)
        {
            PoolExhaustionCount++;
            return;
        }

        nint gcHandle = _bufferPool!.GetGCHandle(pooledBuffer);

        AsyncCaptureState? state;
        lock (_asyncStateLock)
        {
            state = _asyncStatePool.Count > 0 ? _asyncStatePool.Dequeue() : null;
        }

        if (state == null)
        {
            _bufferPool?.Return(pooledBuffer);
            PoolExhaustionCount++;
            return;
        }

        state.Init(this, pooledBuffer, _width, _height, _frameCount * 16_667, gcHandle);

        _rd.TextureGetDataAsync(rdTexRid, 0, state.Callback);
    }

    private void CaptureSync()
    {
        // Fallback: blocking capture (may cause micro-stutter on Vulkan)
        var tex = _viewport.GetTexture();
        if (tex == null)
        {
            GD.PrintErr("[ViewportCapture] GetTexture() returned null (sync)");
            return;
        }

        var image = tex.GetImage();
        if (image == null || image.IsEmpty())
        {
            GD.PrintErr("[ViewportCapture] GetImage() returned null/empty (sync)");
            return;
        }

        // Godot Image data is RGBA8
        var data = image.GetData();
        if (data.Length == 0) return;

        var captureStart = Time.GetTicksUsec();

        // Try pooled buffer first, fall back to CopyFrom if pool exhausted
        var pooledBuffer = _bufferPool?.TryRent();
        if (pooledBuffer != null)
        {
            new ReadOnlySpan<byte>(data).CopyTo(pooledBuffer);

            nint gcHandle = _bufferPool!.GetGCHandle(pooledBuffer);
            var frame = CapturedFrame.WrapPooled(
                pooledBuffer, _width, _height, _width * 4,
                _frameCount * 16_667,
                gcHandle,
                buf => _bufferPool?.Return(buf)
            );
            LastCaptureUs = Time.GetTicksUsec() - captureStart;
            OnFrame?.Invoke(frame);
        }
        else
        {
            // Pool exhausted — allocate once (rare fallback)
            PoolExhaustionCount++;
            var frame = CapturedFrame.CopyFrom(
                data, _width, _height, _width * 4,
                _frameCount * 16_667
            );
            LastCaptureUs = Time.GetTicksUsec() - captureStart;
            OnFrame?.Invoke(frame);
        }
    }

    private static bool CheckAsyncSupport()
    {
        // texture_get_data_async was added in PR #100110 (merged Dec 2024, Godot 4.4+)
        var ver = Engine.Singleton.GetVersionInfo();
        var major = (int)ver["major"];
        var minor = (int)ver["minor"];
        return major > 4 || (major == 4 && minor >= 4);
    }

    public void Dispose()
    {
        Stop();
        _nativeInterop?.Dispose();
        _nativeInterop = null;
        _bufferPool?.Dispose();
        _bufferPool = null;
    }
}
