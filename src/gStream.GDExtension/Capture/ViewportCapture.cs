using System;
using System.Collections.Generic;
using Godot;
using Godot.Collections;
using gStream.Core.Capture;
using gStream.GDExtension.Native;

namespace gStream.GDExtension.Capture;

/// <summary>
/// Captures frames from a Godot Viewport/SubViewport.
/// GDExtension port — uses Godot.Bindings types.
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

    private FrameBufferPool? _bufferPool;
    private Callable _framePostDrawCallable;
    private readonly Queue<AsyncCaptureState> _asyncStatePool = new();
    private readonly object _asyncStateLock = new();

    private NativeInterop? _nativeInterop;
    private bool _useNativePath;

    public event Action<CapturedFrame>? OnFrame;
    public event Action<int, int>? OnResolutionChanged;

    public ulong LastCaptureUs;
    public int PoolExhaustionCount;

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

        public void OnGpuDataReady(PackedByteArray data)
        {
            try
            {
                if (!Owner._running || data.Count == 0 || data.Count > Buffer.Length)
                {
                    Owner._bufferPool?.Return(Buffer);
                    return;
                }

                // Bulk copy via Span — avoids per-byte PackedByteArray indexer overhead
                data.AsSpan(0, data.Count).CopyTo(Buffer.AsSpan(0, data.Count));

                var frame = CapturedFrame.WrapPooled(
                    Buffer, Width, Height, Width * 4,
                    FrameTimestamp, GcHandle,
                    buf => Owner._bufferPool?.Return(buf)
                );
                Owner.OnFrame?.Invoke(frame);
            }
            finally
            {
                lock (Owner._asyncStateLock)
                    Owner._asyncStatePool.Enqueue(this);
            }
        }
    }

    public (int Width, int Height) Resolution => (_width, _height);

    public void Initialize(SubViewport viewport) => InitializeViewport(viewport);
    public void Initialize(Viewport viewport) => InitializeViewport(viewport);

    private void InitializeViewport(Viewport viewport)
    {
        _viewport = viewport;

        if (viewport is SubViewport sub)
        {
            sub.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
            sub.RenderTargetClearMode = SubViewport.ClearMode.Always;
        }

        var size = viewport.GetVisibleRect().Size;
        _width = (int)size.X;
        _height = (int)size.Y;

        _rd = RenderingServer.Singleton.GetRenderingDevice();
        _isAsyncAvailable = CheckAsyncSupport();

        _nativeInterop = NativeInterop.TryCreate();
        _useNativePath = _nativeInterop != null;

        var bufferSize = _width * _height * 4;
        _bufferPool = new FrameBufferPool(bufferSize, poolSize: 5);

        if (!_useNativePath)
        {
            for (int i = 0; i < 4; i++)
            {
                var state = new AsyncCaptureState();
                state.Callback = Callable.From((PackedByteArray data) => state.OnGpuDataReady(data));
                _asyncStatePool.Enqueue(state);
            }
        }

        _framePostDrawCallable = Callable.From(OnFramePostDraw);

        var pathName = _useNativePath ? "Native(ZeroGC)" : (_isAsyncAvailable ? "Async(C#)" : "Sync(C#)");
        GD.Print($"[ViewportCapture] Init {_width}x{_height}, path={pathName}");
    }

    public void Start()
    {
        if (_running) return;
        _running = true;
        _frameCount = 0;

        RenderingServer.Singleton.Connect(
            RenderingServer.SignalName.FramePostDraw,
            _framePostDrawCallable);

        GD.Print("[ViewportCapture] Started — driven by FramePostDraw signal");
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;

        if (RenderingServer.Singleton.IsConnected(
            RenderingServer.SignalName.FramePostDraw, _framePostDrawCallable))
        {
            RenderingServer.Singleton.Disconnect(
                RenderingServer.SignalName.FramePostDraw, _framePostDrawCallable);
        }

        GD.Print("[ViewportCapture] Stopped");
    }

    public void CaptureFrame() { }

    private void OnFramePostDraw()
    {
        if (!_running) return;
        _frameCount++;
        CheckResolutionChange();

        if (_useNativePath) CaptureNative();
        else if (_isAsyncAvailable) CaptureAsync();
        else CaptureSync();
    }

    private void CheckResolutionChange()
    {
        var currentSize = _viewport.GetVisibleRect().Size;
        int newW = (int)currentSize.X;
        int newH = (int)currentSize.Y;

        if (newW == _width && newH == _height) return;

        _width = newW;
        _height = newH;

        _bufferPool?.Dispose();
        var bufferSize = _width * _height * 4;
        _bufferPool = new FrameBufferPool(bufferSize, poolSize: 5);

        GD.Print($"[ViewportCapture] Resolution changed: {newW}x{newH}");
        OnResolutionChanged?.Invoke(newW, newH);
    }

    private unsafe void CaptureNative()
    {
        var tex = _viewport.GetTexture();
        if (tex == null) return;

        var rsTexRid = tex.GetRid();
        if (!rsTexRid.IsValid) return;

        var rdTexRid = RenderingServer.Singleton.TextureGetRdTexture(rsTexRid);
        if (!rdTexRid.IsValid) return;

        var pooledBuffer = _bufferPool?.TryRent();
        if (pooledBuffer == null) { PoolExhaustionCount++; return; }

        var captureStart = Time.Singleton.GetTicksUsec();

        byte* ptr = _bufferPool!.GetPointer(pooledBuffer);
        nint gcHandle = _bufferPool.GetGCHandle(pooledBuffer);

        long bytesCopied = _nativeInterop!.ReadTextureToPointer(rsTexRid, (nint)ptr, _width, _height);

        if (bytesCopied <= 0)
        {
            _bufferPool.Return(pooledBuffer);
            LastCaptureUs = Time.Singleton.GetTicksUsec() - captureStart;
            return;
        }

        var frame = CapturedFrame.WrapPooled(
            pooledBuffer, _width, _height, _width * 4,
            _frameCount * 16_667, gcHandle,
            buf => _bufferPool.Return(buf)
        );
        LastCaptureUs = Time.Singleton.GetTicksUsec() - captureStart;
        OnFrame?.Invoke(frame);
    }

    private void CaptureAsync()
    {
        var tex = _viewport.GetTexture();
        if (tex == null) return;

        var rsTexRid = tex.GetRid();
        if (!rsTexRid.IsValid) return;

        var rdTexRid = RenderingServer.Singleton.TextureGetRdTexture(rsTexRid);
        if (!rdTexRid.IsValid) return;

        if (!_rd.TextureIsValid(rdTexRid)) return;

        var pooledBuffer = _bufferPool?.TryRent();
        if (pooledBuffer == null) { PoolExhaustionCount++; return; }

        nint gcHandle = _bufferPool!.GetGCHandle(pooledBuffer);

        AsyncCaptureState? state;
        lock (_asyncStateLock)
            state = _asyncStatePool.Count > 0 ? _asyncStatePool.Dequeue() : null;

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
        var tex = _viewport.GetTexture();
        if (tex == null) return;

        var image = tex.GetImage();
        if (image == null || image.IsEmpty()) return;

        var data = image.GetData();
        if (data.Count == 0) return;

        var captureStart = Time.Singleton.GetTicksUsec();

        var pooledBuffer = _bufferPool?.TryRent();
        if (pooledBuffer != null)
        {
            int copyLen = Math.Min(data.Count, pooledBuffer.Length);
            // Bulk copy via Span — avoids per-byte PackedByteArray indexer overhead
            data.AsSpan(0, copyLen).CopyTo(pooledBuffer.AsSpan(0, copyLen));
            nint gcHandle = _bufferPool!.GetGCHandle(pooledBuffer);
            var frame = CapturedFrame.WrapPooled(
                pooledBuffer, _width, _height, _width * 4,
                _frameCount * 16_667, gcHandle,
                buf => _bufferPool?.Return(buf)
            );
            LastCaptureUs = Time.Singleton.GetTicksUsec() - captureStart;
            OnFrame?.Invoke(frame);
        }
        else
        {
            PoolExhaustionCount++;
            // Bulk copy via Span
            var bytes = new byte[data.Count];
            data.AsSpan().CopyTo(bytes);
            var frame = CapturedFrame.CopyFrom(bytes, _width, _height, _width * 4, _frameCount * 16_667);
            LastCaptureUs = Time.Singleton.GetTicksUsec() - captureStart;
            OnFrame?.Invoke(frame);
        }
    }

    private static bool CheckAsyncSupport()
    {
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
