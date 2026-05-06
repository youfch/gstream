// NativeInterop.cs
// C# interop layer for the gstream_native GDExtension.

using Godot;

namespace gStream.Godot.Native;

/// <summary>
/// Thin C# wrapper around the gstream_native GDExtension.
/// The GDExtension class (C++) is registered as "NativeInterop" in ClassDB.
/// </summary>
public sealed partial class NativeInterop
{
    private readonly GodotObject _native;

    /// <summary>
    /// Create a new instance of the native interop class.
    /// Returns null if the GDExtension is not loaded (native DLL missing or not registered).
    /// </summary>
    public static NativeInterop? TryCreate()
    {
        // Check if the native class is registered in ClassDB
        if (!ClassDB.ClassExists("NativeInterop"))
            return null;

        var obj = ClassDB.Instantiate("NativeInterop");
        return obj.AsGodotObject() is GodotObject gobj ? new NativeInterop(gobj) : null;
    }

    private NativeInterop(GodotObject native)
    {
        _native = native;
    }

    /// <summary>
    /// Copies viewport texture data directly into a pre-pinned C# buffer.
    /// </summary>
    /// <param name="textureRid">RenderingServer texture RID (pass as Rid object)</param>
    /// <param name="destPointer">Address of a pinned byte[] buffer</param>
    /// <param name="width">Frame width in pixels</param>
    /// <param name="height">Frame height in pixels</param>
    /// <returns>Number of bytes copied, or -1 on failure.</returns>
    public long ReadTextureToPointer(Rid textureRid, nint destPointer, int width, int height)
    {
        var result = _native.Call(
            "read_texture_to_pointer",
            textureRid,
            (long)destPointer,
            width,
            height
        );
        return result.AsInt64();
    }

    public void Dispose()
    {
        _native.Dispose();
    }
}
