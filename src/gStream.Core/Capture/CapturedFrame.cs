using System.Collections.Concurrent;
using System.Collections.Generic;

namespace gStream.Core.Capture;

/// <summary>
/// Pinned native frame buffer — avoids GC pressure and enables zero-copy handoff to encoder.
/// Caller is responsible for <see cref="Dispose"/> after encoder has consumed the frame.
/// </summary>
public sealed unsafe class CapturedFrame : IDisposable
{
    /// <summary>Pointer to raw BGRA32 pixel data (row-major, top-down).</summary>
    public byte* Data { get; }

    /// <summary>Frame width in pixels.</summary>
    public int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; }

    /// <summary>Stride in bytes (Width * 4 for BGRA32, may include padding).</summary>
    public int Stride { get; }

    /// <summary>Presentation timestamp in microseconds (monotonic).</summary>
    public long TimestampUs { get; }

    private readonly nint _handle;
    private readonly byte[]? _pooledBuffer;
    private readonly Action<byte[]>? _onBufferReturn;
    private readonly bool _ownsHandle;
    private bool _disposed;

    public CapturedFrame(byte* data, int width, int height, int stride, long timestampUs, nint gcHandle)
    {
        Data = data;
        Width = width;
        Height = height;
        Stride = stride;
        TimestampUs = timestampUs;
        _handle = gcHandle;
    }

    /// <summary>
    /// Internal constructor for pooled buffer frames — includes return callback.
    /// </summary>
    private CapturedFrame(byte* data, int width, int height, int stride, long timestampUs, nint gcHandle, byte[] pooledBuffer, Action<byte[]> onBufferReturn, bool ownsHandle)
    {
        Data = data;
        Width = width;
        Height = height;
        Stride = stride;
        TimestampUs = timestampUs;
        _handle = gcHandle;
        _pooledBuffer = pooledBuffer;
        _onBufferReturn = onBufferReturn;
        _ownsHandle = ownsHandle;
    }

    /// <summary>
    /// Creates a <see cref="CapturedFrame"/> by copying source bytes into a pinned buffer.
    /// Use this when the source memory is transient (e.g. Godot Image data).
    /// </summary>
    public static CapturedFrame CopyFrom(ReadOnlySpan<byte> source, int width, int height, int stride, long timestampUs)
    {
        var buffer = GC.AllocateUninitializedArray<byte>(source.Length, pinned: true);
        source.CopyTo(buffer);
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(buffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        return new CapturedFrame((byte*)handle.AddrOfPinnedObject(), width, height, stride, timestampUs, System.Runtime.InteropServices.GCHandle.ToIntPtr(handle));
    }

    /// <summary>
    /// Wraps a pre-allocated pinned array without copying.
    /// </summary>
    public static CapturedFrame Wrap(byte[] pinnedBuffer, int width, int height, int stride, long timestampUs)
    {
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pinnedBuffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        return new CapturedFrame((byte*)handle.AddrOfPinnedObject(), width, height, stride, timestampUs, System.Runtime.InteropServices.GCHandle.ToIntPtr(handle));
    }

    /// <summary>
    /// Wraps a pooled buffer with a return callback. When this frame is disposed,
    /// the callback fires so the buffer can be returned to the pool.
    /// Uses the provided pre-existing GCHandle (e.g. from FrameBufferPool) — does NOT create a new one.
    /// </summary>
    public static CapturedFrame WrapPooled(byte[] pooledBuffer, int width, int height, int stride, long timestampUs, nint existingGcHandle, Action<byte[]> onBufferReturn)
    {
        var handle = System.Runtime.InteropServices.GCHandle.FromIntPtr(existingGcHandle);
        return new CapturedFrame((byte*)handle.AddrOfPinnedObject(), width, height, stride, timestampUs, existingGcHandle, pooledBuffer, onBufferReturn, ownsHandle: false);
    }

    /// <summary>
    /// Backward-compatible overload for non-pooled callers. Allocates a new GCHandle.
    /// For pooled buffers, prefer the overload that accepts a pre-existing GCHandle from FrameBufferPool.
    /// </summary>
    public static CapturedFrame WrapPooled(byte[] pooledBuffer, int width, int height, int stride, long timestampUs, Action<byte[]> onBufferReturn)
    {
        var handle = System.Runtime.InteropServices.GCHandle.Alloc(pooledBuffer, System.Runtime.InteropServices.GCHandleType.Pinned);
        return new CapturedFrame((byte*)handle.AddrOfPinnedObject(), width, height, stride, timestampUs, System.Runtime.InteropServices.GCHandle.ToIntPtr(handle), pooledBuffer, onBufferReturn, ownsHandle: true);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHandle && _handle != 0)
        {
            var gch = GCHandle.FromIntPtr(_handle);
            if (gch.IsAllocated) gch.Free();
        }

        // Return pooled buffer if applicable
        if (_pooledBuffer != null && _onBufferReturn != null)
        {
            _onBufferReturn(_pooledBuffer);
        }
    }
}

file static class GCHandle
{
    public static nint ToIntPtr(System.Runtime.InteropServices.GCHandle handle) =>
        System.Runtime.InteropServices.GCHandle.ToIntPtr(handle);

    public static System.Runtime.InteropServices.GCHandle FromIntPtr(nint ptr) =>
        System.Runtime.InteropServices.GCHandle.FromIntPtr(ptr);
}

/// <summary>
/// Thread-safe pool of pre-allocated pinned byte[] buffers for frame capture.
/// Eliminates per-frame heap allocations — rent a buffer, copy frame data, wrap as <see cref="CapturedFrame"/>,
/// then return the buffer after the encoder has consumed it.
/// </summary>
public sealed class FrameBufferPool : IDisposable
{
    private readonly int _bufferSize;
    private readonly ConcurrentQueue<(byte[] Array, System.Runtime.InteropServices.GCHandle Handle)> _pool;
    private readonly List<(byte[] Array, System.Runtime.InteropServices.GCHandle Handle)> _allBuffers;
    private bool _disposed;

    /// <summary>
    /// Pre-allocates <paramref name="poolSize"/> pinned buffers of <paramref name="bufferSize"/> bytes each.
    /// </summary>
    /// <param name="bufferSize">Size of each buffer in bytes (e.g. 1920 * 1080 * 4 for 1080p BGRA32).</param>
    /// <param name="poolSize">Number of buffers to pre-allocate. Default 3 (matches BoundedChannel capacity 2 + 1 safety margin).</param>
    public FrameBufferPool(int bufferSize, int poolSize = 3)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bufferSize);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(poolSize);

        _bufferSize = bufferSize;
        _pool = new ConcurrentQueue<(byte[], System.Runtime.InteropServices.GCHandle)>();
        _allBuffers = new List<(byte[], System.Runtime.InteropServices.GCHandle)>(poolSize);

        for (var i = 0; i < poolSize; i++)
        {
            var array = GC.AllocateUninitializedArray<byte>(bufferSize, pinned: true);
            var handle = System.Runtime.InteropServices.GCHandle.Alloc(array, System.Runtime.InteropServices.GCHandleType.Pinned);
            var entry = (array, handle);
            _allBuffers.Add(entry);
            _pool.Enqueue(entry);
        }
    }

    /// <summary>
    /// Rents a pre-allocated pinned buffer from the pool.
    /// </summary>
    /// <returns>A pooled byte[] ready for use, or <c>null</c> if the pool is exhausted.</returns>
    public byte[]? TryRent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pool.TryDequeue(out var entry))
            return entry.Array;

        return null;
    }

    /// <summary>
    /// Returns a buffer to the pool. The buffer must have been obtained from <see cref="TryRent"/>.
    /// </summary>
    public void Return(byte[] buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Re-find the matching handle — buffer identity is reference-equal to one in _allBuffers.
        foreach (var (array, handle) in _allBuffers)
        {
            if (ReferenceEquals(array, buffer))
            {
                _pool.Enqueue((array, handle));
                return;
            }
        }

        // Buffer doesn't belong to this pool — silently ignore (defensive).
    }

    /// <summary>
    /// Gets the raw pointer for a pooled buffer. Use when wrapping via <see cref="CapturedFrame.Wrap"/>.
    /// </summary>
    public unsafe byte* GetPointer(byte[] buffer)
    {
        foreach (var (array, handle) in _allBuffers)
        {
            if (ReferenceEquals(array, buffer))
                return (byte*)handle.AddrOfPinnedObject();
        }

        throw new ArgumentException("Buffer does not belong to this pool.", nameof(buffer));
    }

    /// <summary>
    /// Gets the pre-existing GCHandle for a pooled buffer as an nint.
    /// Use with <see cref="CapturedFrame.WrapPooled(byte[],int,int,int,long,nint,Action{byte[]})"/> 
    /// to avoid per-frame GCHandle.Alloc/Free calls.
    /// </summary>
    public nint GetGCHandle(byte[] buffer)
    {
        foreach (var (array, handle) in _allBuffers)
        {
            if (ReferenceEquals(array, buffer))
                return System.Runtime.InteropServices.GCHandle.ToIntPtr(handle);
        }

        throw new ArgumentException("Buffer does not belong to this pool.", nameof(buffer));
    }

    /// <summary>
    /// Number of buffers currently available in the pool.
    /// </summary>
    public int AvailableCount => _pool.Count;

    /// <summary>
    /// The size of each buffer in bytes.
    /// </summary>
    public int BufferSize => _bufferSize;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // Drain the queue and free all GCHandles.
        while (_pool.TryDequeue(out _)) { }

        foreach (var (_, handle) in _allBuffers)
        {
            if (handle.IsAllocated) handle.Free();
        }

        _allBuffers.Clear();
    }
}
