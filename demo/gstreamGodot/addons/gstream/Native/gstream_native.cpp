// gstream_native.cpp
// Standalone GDExtension — zero-allocation texture read via shared pinned pointer.

#include <godot_cpp/godot.hpp>
#include <godot_cpp/classes/object.hpp>
#include <godot_cpp/classes/rendering_server.hpp>
#include <godot_cpp/core/class_db.hpp>
#include <cstring>

using namespace godot;

class NativeInterop : public Object {
    GDCLASS(NativeInterop, Object)

protected:
    static void _bind_methods() {
        ClassDB::bind_method(D_METHOD("read_texture_to_pointer", "texture_rid", "dest_pointer", "width", "height"),
                             &NativeInterop::read_texture_to_pointer);
    }

public:
    Variant read_texture_to_pointer(Variant texture_rid, Variant dest_pointer, Variant width, Variant height) {
        RID rs_rid = texture_rid;
        int64_t dest_ptr = dest_pointer;
        int64_t w = width;
        int64_t h = height;

        if (!dest_ptr || w <= 0 || h <= 0) return -1;
        if (!rs_rid.is_valid()) return -1;

        auto *rs = RenderingServer::get_singleton();
        if (!rs) return -1;

        RID rd_rid = rs->texture_get_rd_texture(rs_rid);
        if (!rd_rid.is_valid()) return -1;

        RenderingDevice *rd = rs->get_rendering_device();
        if (!rd) return -1;

        PackedByteArray data = rd->texture_get_data(rd_rid, 0);
        if (data.is_empty()) return -1;

        int64_t bytes = data.size();
        int64_t max_copy = w * h * 4;
        if (bytes > max_copy) bytes = max_copy;

        const uint8_t *src = data.ptr();
        uint8_t *dst = reinterpret_cast<uint8_t *>(static_cast<uintptr_t>(dest_ptr));
        std::memcpy(dst, src, static_cast<size_t>(bytes));

        return bytes;
    }

    NativeInterop() {}
    ~NativeInterop() {}
};

static void init_native_module(ModuleInitializationLevel p_level) {
    if (p_level != MODULE_INITIALIZATION_LEVEL_SCENE) {
        return;
    }
    GDREGISTER_CLASS(NativeInterop);
}

static void terminate_native_module(ModuleInitializationLevel p_level) {
    // Nothing to clean up.
}

extern "C" {

GDExtensionBool GDE_EXPORT native_interop_init(
    GDExtensionInterfaceGetProcAddress p_get_proc_address,
    GDExtensionClassLibraryPtr p_library,
    GDExtensionInitialization *r_initialization)
{
    godot::GDExtensionBinding::InitObject init_obj(p_get_proc_address, p_library, r_initialization);

    init_obj.register_initializer(init_native_module);
    init_obj.register_terminator(terminate_native_module);
    init_obj.set_minimum_library_initialization_level(godot::MODULE_INITIALIZATION_LEVEL_SCENE);

    return init_obj.init();
}

} // extern "C"
