using System;
using Godot;

namespace gStream.GDExtension.Native;

/// <summary>
/// Pure C# replacement for the C++ gstream_native GDExtension.
/// In GDE mode (NativeAOT + Godot.Bindings), we call RenderingServer/RenderingDevice
/// APIs directly without the marshaling overhead that made the C++ version necessary.
/// </summary>
public sealed class NativeInterop : IDisposable
{
    private readonly RenderingDevice _rd;
    private bool _disposed;

    public static NativeInterop? TryCreate()
    {
        try
        {
            var rd = RenderingServer.Singleton.GetRenderingDevice();
            return rd != null ? new NativeInterop(rd) : null;
        }
        catch
        {
            return null;
        }
    }

    private NativeInterop(RenderingDevice rd)
    {
        _rd = rd;
    }

    /// <summary>
    /// Copies viewport texture data directly into a pre-pinned C# buffer.
    /// Uses Godot.Bindings API directly — no C++ GDExtension needed.
    /// </summary>
    public unsafe long ReadTextureToPointer(Rid textureRid, nint destPointer, int width, int height)
    {
        if (destPointer == 0 || width <= 0 || height <= 0) return -1;
        if (!textureRid.IsValid) return -1;

        var rdRid = RenderingServer.Singleton.TextureGetRdTexture(textureRid);
        if (!rdRid.IsValid) return -1;

        var data = _rd.TextureGetData(rdRid, 0);
        if (data == null || data.Count == 0) return -1;

        int bytes = data.Count;
        int maxCopy = width * height * 4;
        if (bytes > maxCopy) bytes = maxCopy;

        // Bulk copy via Span — orders of magnitude faster than byte-by-byte indexing
        data.AsSpan(0, bytes).CopyTo(new Span<byte>((void*)destPointer, bytes));

        return bytes;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
        }
    }
}
