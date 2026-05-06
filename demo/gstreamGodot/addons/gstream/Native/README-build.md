# gstream_native GDExtension — Zero-GC Texture Read

## What this does

Replaces the Godot C# `TextureGetDataAsync` callback (which allocates a new `byte[]` per frame, ~8.3MB@1080p) with a C++ extension that copies GPU data directly into a pre-allocated pinned C# buffer via raw pointer. **Zero GC allocation per frame.**

## Architecture

```
┌─────────────────────────────────────────────────────────────────┐
│  ViewportCapture.CaptureNative()                                │
│  1. Rent pinned buffer from FrameBufferPool                     │
│  2. bufferPool.GetPointer() → raw byte* (pre-pinned, no alloc)  │
│  3. NativeInterop.ReadTextureToPointer(rid, ptr, w, h)          │
└──────────────────────┬──────────────────────────────────────────┘
                       │ Godot Object.Call()
                       ▼
┌─────────────────────────────────────────────────────────────────┐
│  gstream_native.dll (GDExtension)                               │
│  NativeInterop::read_texture_to_pointer()                       │
│  1. RenderingServer::texture_get_rd_texture(rs_rid)             │
│  2. RenderingDevice::texture_get_data(rd_rid) → PackedByteArray │
│  3. memcpy(data.ptr(), dest_pointer, size)  ◄── zero alloc      │
│  4. Return bytes_copied                                         │
└─────────────────────────────────────────────────────────────────┘
```

**Key**: `PackedByteArray::ptr()` gives a raw `uint8_t*` which we `memcpy` directly into the C# pinned buffer. No `byte[]` is ever created by Godot's marshal layer.

## Prerequisites

| Tool | Version | Purpose |
|------|---------|---------|
| [uv](https://docs.astral.sh/uv/) | >= 0.5.0 | Python package manager (runs scons) |
| scons | >= 4.0 | C++ build system (auto-installed by uvx) |
| MSVC | VS 2022+ with C++ workload | C++ compiler |
| Godot | 4.6.1 | Engine + C# SDK |
| godot-cpp | master (v10.x Beta), commit `4862a9d` | C++ bindings |

### Step 1: Clone godot-cpp

```bash
cd src/gStream_Godot/addons/gstream/Native
git clone --depth 1 --branch master https://github.com/godotengine/godot-cpp.git
```

**Current version**: master (v10.x Beta), commit `4862a9d`。v10 支持 Godot 4.3 到 4.6+。

可选稳定分支：
- `godot-4.5-stable` → godot-cpp 4.5 稳定版
- `4.5` → godot-cpp 4.5 分支

### Step 2: Generate godot-cpp bindings

```bash
cd godot-cpp

uvx scons target=template_debug platform=windows generate_bindings=yes
```

`uvx` will automatically install scons if not already present.

### Step 3: Build the extension

```bash
cd ..
uvx scons target=template_debug platform=windows
```

Output: `bin/gstream_native.windows.template_debug.x86_64.dll`

For release builds:
```bash
uvx scons target=template_release platform=windows
```

### Alternative: without uv

If you prefer to install scons manually:
```bash
pip install scons
scons target=template_debug platform=windows
```

### Step 4: Verify

When Godot loads the project, the `.gdextension` file auto-registers the DLL.
The `ViewportCapture` log will show `path=Native(ZeroGC)` instead of `path=Async(C#)`.

## Important: Class Registration

Each custom class must be explicitly registered in the init callback via `GDREGISTER_CLASS()`:

```cpp
static void init_native_module(ModuleInitializationLevel p_level) {
    if (p_level != MODULE_INITIALIZATION_LEVEL_SCENE) return;
    GDREGISTER_CLASS(NativeInterop);
}
```

**GDCLASS macro only generates static helper methods** (`_bind_methods`, `initialize_class`), it does NOT auto-register the class. Without `GDREGISTER_CLASS()`, the class won't appear in ClassDB and C# won't find it.

## Fallback behavior

If the GDExtension DLL is missing or fails to load:
- `NativeInterop.TryCreate()` returns `null`
- `_useNativePath` stays `false`
- Capture falls back to the existing `TextureGetDataAsync` (async) or `GetImage()` (sync) path
- **No errors, fully backward compatible**

## Performance comparison

| Path | GC alloc/frame@1080p | 60fps GC throughput |
|------|----------------------|---------------------|
| TextureGetDataAsync (old) | ~8.3 MB byte[] | ~500 MB/s |
| GetImage() (sync) | ~8.3 MB byte[] | ~500 MB/s |
| **Native(ZeroGC)** | **0 bytes** | **0 MB/s** |

The native path adds ~0.1ms of C#/native transition overhead (negligible vs. the ~2-5ms GPU readback).
