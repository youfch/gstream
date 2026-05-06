using System.Runtime.InteropServices;
using Godot;
using Godot.Bridge;

[assembly: DisableGodotEntryPointGeneration]

namespace gStream.GDExtension;

public class Main
{
    // ── Windows P/Invoke ────────────────────────────────────────────────
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetDllDirectoryW(string? lpPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetModuleFileNameW(nint hModule, char[] lpFilename, uint nSize);

    // ── POSIX P/Invoke ──────────────────────────────────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct DlInfo
    {
        public nint Dli_fname;
        public nint Dli_fbase;
        public nint Dli_sname;
        public nint Dli_saddr;
    }

    // Linux uses libdl.so.2, macOS uses /usr/lib/libSystem.dylib (both export dladdr)
    [DllImport("libdl.so.2", EntryPoint = "dladdr")]
    private static extern int dladdr_linux(nint addr, ref DlInfo info);

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "dladdr")]
    private static extern int dladdr_macos(nint addr, ref DlInfo info);

    [DllImport("libdl.so.2", EntryPoint = "dlopen")]
    private static extern nint dlopen_linux(string filename, int flags);

    [DllImport("/usr/lib/libSystem.dylib", EntryPoint = "dlopen")]
    private static extern nint dlopen_macos(string filename, int flags);

    private const int RTLD_NOW = 2;
    private const int RTLD_GLOBAL_LINUX = 0x100;
    private const int RTLD_GLOBAL_MACOS = 0x8;

    /// <summary>
    /// Absolute path to the addon directory (where GDE + FFmpeg libraries reside).
    /// Resolved once during library init from the native library handle.
    /// </summary>
    private static string? _addonDir;

    public static void InitializeGStreamTypes(InitializationLevel level)
    {
        if (level != InitializationLevel.Scene)
            return;

        // Point FFmpegLibraryLoader to the addon directory so FFmpeg libraries
        // are found next to the GDExtension library without copying them
        // to the Godot executable directory.
        if (_addonDir != null)
        {
            gStream.Core.Interop.FFmpegLibraryLoader.SetAddonPath(_addonDir);
        }

        GodotRegistry.RegisterClass<Nodes.StreamServer>(Nodes.StreamServer.BindMembers);
        GodotRegistry.RegisterClass<Nodes.MultiStreamServer>(Nodes.MultiStreamServer.BindMembers);
    }

    public static void DeinitializeGStreamTypes(InitializationLevel level)
    {
        if (level != InitializationLevel.Scene)
            return;
    }

    [UnmanagedCallersOnly(EntryPoint = "gstream_library_init")]
    public static bool GStreamLibraryInit(nint getProcAddress, nint library, nint initialization)
    {
        ResolveAddonDir(library);

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Set the DLL search directory so kernel32.LoadLibrary (used by
            // FFmpeg.AutoGen's WindowsFunctionResolver) finds FFmpeg DLLs
            // in the addon directory, not just in the Godot.exe directory.
            if (_addonDir != null)
            {
                SetDllDirectoryW(_addonDir);
            }
        }
        else if (_addonDir != null)
        {
            // On Linux/macOS we must preload FFmpeg shared libraries with
            // RTLD_GLOBAL so inter-library symbol resolution works.
            // FFmpeg.AutoGen's LinuxFunctionResolver uses dlopen which
            // requires globally visible symbols.
            PreloadSiblingLibraries(_addonDir);
        }

        GodotBridge.Initialize(getProcAddress, library, initialization, config =>
        {
            config.SetMinimumLibraryInitializationLevel(InitializationLevel.Scene);
            config.RegisterInitializer(InitializeGStreamTypes);
            config.RegisterTerminator(DeinitializeGStreamTypes);
        });

        return true;
    }

    // ── Addon directory resolution ──────────────────────────────────────

    private static void ResolveAddonDir(nint library)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && library != nint.Zero)
        {
            var buffer = new char[512];
            if (GetModuleFileNameW(library, buffer, 512) > 0)
            {
                var dllPath = new string(buffer).TrimEnd('\0');
                _addonDir = System.IO.Path.GetDirectoryName(dllPath);
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) && library != nint.Zero)
        {
            var info = new DlInfo();
            if (dladdr_linux(library, ref info) != 0 && info.Dli_fname != nint.Zero)
            {
                var libPath = Marshal.PtrToStringUTF8(info.Dli_fname);
                if (libPath != null)
                {
                    _addonDir = System.IO.Path.GetDirectoryName(libPath);
                }
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX) && library != nint.Zero)
        {
            var info = new DlInfo();
            if (dladdr_macos(library, ref info) != 0 && info.Dli_fname != nint.Zero)
            {
                var libPath = Marshal.PtrToStringUTF8(info.Dli_fname);
                if (libPath != null)
                {
                    _addonDir = System.IO.Path.GetDirectoryName(libPath);
                }
            }
        }

        // Fallback: probe common paths relative to CWD for the marker library.
        if (_addonDir == null)
        {
            _addonDir = ProbeAddonPath();
        }
    }

    /// <summary>
    /// Probes for the addon directory by looking for the FFmpeg marker library
    /// (platform-appropriate naming) in common relative paths from CWD.
    /// </summary>
    private static string? ProbeAddonPath()
    {
        var cwd = System.IO.Directory.GetCurrentDirectory();
        var candidates = new[]
        {
            System.IO.Path.Combine(cwd, "addons", "gstream"),
            System.IO.Path.Combine(cwd, "demo", "gstream-gdedemo", "addons", "gstream"),
        };

        string marker = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? "avcodec-62.dll"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                ? "libavcodec.62.dylib"
                : "libavcodec.so.62";

        foreach (var dir in candidates)
        {
            if (System.IO.File.Exists(System.IO.Path.Combine(dir, marker)))
            {
                return System.IO.Path.GetFullPath(dir);
            }
        }

        return null;
    }

    // ── Linux / macOS FFmpeg preloading with RTLD_GLOBAL ────────────────

    /// <summary>
    /// Preloads FFmpeg shared libraries with RTLD_NOW | RTLD_GLOBAL so that
    /// inter-library symbol references resolve correctly.
    /// Must happen BEFORE FFmpegLibraryLoader.SetAddonPath is called.
    /// </summary>
    private static void PreloadSiblingLibraries(string addonDir)
    {
        // Dependency order: avutil → swresample → swscale → avcodec → avformat → avdevice → avfilter
        var libs = new (string name, int version)[]
        {
            ("avutil", 60),
            ("swresample", 6),
            ("swscale", 9),
            ("avcodec", 62),
            ("avformat", 62),
            ("avdevice", 62),
            ("avfilter", 11),
        };

        bool isMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        int flags = RTLD_NOW | (isMacOS ? RTLD_GLOBAL_MACOS : RTLD_GLOBAL_LINUX);

        foreach (var (name, version) in libs)
        {
            var fileName = isMacOS
                ? $"lib{name}.{version}.dylib"
                : $"lib{name}.so.{version}";
            var fullPath = System.IO.Path.Combine(addonDir, fileName);

            if (!System.IO.File.Exists(fullPath))
                continue;

            var handle = isMacOS
                ? dlopen_macos(fullPath, flags)
                : dlopen_linux(fullPath, flags);

            if (handle == nint.Zero)
            {
                // Library failed to load; subsequent dependent libs will likely
                // fail too, but we continue trying in case of partial loads.
                System.Diagnostics.Debug.WriteLine(
                    $"[gStream.GDExtension] dlopen failed for {fullPath}");
            }
        }
    }
}
