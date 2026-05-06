using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using FFmpeg.AutoGen;

namespace gStream.Core.Interop;

/// <summary>
/// Cross-platform FFmpeg native library loader.
///
/// Handles the library name mismatch between FFmpeg.AutoGen (expects "avcodec-62")
/// and system-installed FFmpeg on Linux/macOS (provides "libavcodec.so.60").
///
/// Strategy:
///   Windows  — use bundled FFmpeg.GPL NuGet DLLs from app directory
///   Linux    — probe system FFmpeg, set up DllImportResolver + LibraryVersionMap
///   macOS    — same as Linux, different search paths and file extensions
/// </summary>
public static unsafe class FFmpegLibraryLoader
{
    private static bool _configured;
    private static int? _systemFfmpegVersion;

    /// <summary>
    /// FFmpeg library sub-version map, keyed by the avcodec major version.
    /// Each FFmpeg release ships all sub-libraries at specific versions that
    /// do NOT always match the avcodec version number.
    /// </summary>
    private static readonly Dictionary<int, Dictionary<string, int>> VersionMaps = new()
    {
        // FFmpeg 8.x (avcodec-62)
        [62] = new()
        {
            { "avcodec", 62 }, { "avdevice", 62 }, { "avfilter", 11 },
            { "avformat", 62 }, { "avutil", 60 }, { "swresample", 6 }, { "swscale", 9 }
        },
        // FFmpeg 7.x (avcodec-61)
        [61] = new()
        {
            { "avcodec", 61 }, { "avdevice", 61 }, { "avfilter", 10 },
            { "avformat", 61 }, { "avutil", 59 }, { "swresample", 5 }, { "swscale", 8 }
        },
        // FFmpeg 6.x (avcodec-60) — Ubuntu 24.04 ships this
        [60] = new()
        {
            { "avcodec", 60 }, { "avdevice", 60 }, { "avfilter", 9 },
            { "avformat", 60 }, { "avutil", 58 }, { "swresample", 4 }, { "swscale", 7 }
        },
        // FFmpeg 5.x (avcodec-59)
        [59] = new()
        {
            { "avcodec", 59 }, { "avdevice", 59 }, { "avfilter", 8 },
            { "avformat", 59 }, { "avutil", 57 }, { "swresample", 4 }, { "swscale", 6 }
        },
        // FFmpeg 4.x (avcodec-58)
        [58] = new()
        {
            { "avcodec", 58 }, { "avdevice", 58 }, { "avfilter", 7 },
            { "avformat", 58 }, { "avutil", 56 }, { "swresample", 3 }, { "swscale", 5 }
        }
    };

    /// <summary>
    /// Configures FFmpeg.AutoGen for cross-platform library loading.
    /// Must be called before any FFmpeg API calls (before the ffmpeg static ctor runs).
    /// </summary>
    public static void Configure()
    {
        if (_configured) return;
        _configured = true;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            ConfigureWindows();
        }
        else
        {
            ConfigureUnix();
        }
    }

    // ───────────────────────── Windows ─────────────────────────

    // FFmpeg library names in dependency-safe load order.
    // avutil/swresample are leaves; avcodec/avformat depend on them.
    private static readonly string[] WindowsFFmpegLibraries =
        new[] { "avutil-60", "swresample-6", "swscale-9", "avcodec-62", "avformat-62", "avdevice-62", "avfilter-11" };

    private static void ConfigureWindows()
    {
        var appDir = AppContext.BaseDirectory;
        if (File.Exists(Path.Combine(appDir, "avcodec-62.dll")) ||
            File.Exists(Path.Combine(appDir, "avcodec-61.dll")))
        {
            ffmpeg.RootPath = appDir;
            return;
        }

        // GDExtension scenario: AppContext.BaseDirectory is the Godot executable
        // directory, but FFmpeg DLLs are in the addon directory next to the
        // GDExtension DLL. Pre-load them via NativeLibrary.Load() so the OS
        // loader finds them already in-process when NativeAOT direct P/Invokes
        // try to resolve them.
        PreloadFFmpegFromAddonPath();
    }

    /// <summary>
    /// Pre-loads FFmpeg DLLs from the addon directory using NativeLibrary.Load().
    /// In NativeAOT, P/Invokes are compiled as direct calls that bypass
    /// DllImportResolver. By loading the DLLs into the process first, the
    /// OS loader's LoadLibrary returns the existing handle.
    /// </summary>
    private static void PreloadFFmpegFromAddonPath()
    {
        if (_gdeAddonPath == null) return;

        foreach (var name in WindowsFFmpegLibraries)
        {
            var fullPath = Path.Combine(_gdeAddonPath, name + ".dll");
            if (File.Exists(fullPath))
            {
                try
                {
                    NativeLibrary.Load(fullPath);
                }
                catch
                {
                    // If one lib fails (e.g. wrong version), continue —
                    // the error will surface when FFmpeg tries to use it.
                }
            }
        }
    }

    /// <summary>
    /// Path to the GDExtension addon directory (set during initialization).
    /// FFmpeg DLLs are located here in the GDExtension deployment model.
    /// </summary>
    private static string? _gdeAddonPath;

    /// <summary>
    /// Sets the addon directory path for FFmpeg DLL resolution in GDExtension mode.
    /// Call this before FFmpegLibraryLoader.Configure() if running as a GDExtension.
    /// Also pre-loads all FFmpeg DLLs via kernel32.LoadLibrary so the OS loader
    /// finds them in-process.
    /// </summary>
    public static void SetAddonPath(string addonPath)
    {
        _gdeAddonPath = addonPath;
        ffmpeg.RootPath = addonPath;

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Pre-load all FFmpeg DLLs into the process. FFmpeg.AutoGen uses
            // WindowsFunctionResolver which calls kernel32.LoadLibrary(path).
            // If the DLL is already loaded, LoadLibrary returns the existing handle.
            PreloadFFmpegFromAddonPath();
        }
        else
        {
            // On Linux/macOS, use dlopen with RTLD_NOW | RTLD_GLOBAL so that
            // FFmpeg inter-library dependencies (e.g. avcodec → avutil) resolve.
            // NativeLibrary.Load() uses RTLD_LOCAL which hides symbols.
            PreloadFFmpegFromAddonPathUnix();
        }
    }

    // ───────────────────────── Linux / macOS dlopen ─────────────────────────

    private const int RTLD_NOW = 2;
    private const int RTLD_GLOBAL_LINUX = 0x100;
    private const int RTLD_GLOBAL_MACOS = 0x8;

    [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr dlopen(string filename, int flags);

    [DllImport("/usr/lib/libSystem.dylib", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr dlopen_macos(string filename, int flags);

    [DllImport("libdl.so.2", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr dlerror();

    [DllImport("/usr/lib/libSystem.dylib", CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr dlerror_macos();

    /// <summary>
    /// FFmpeg library base names in dependency-safe load order.
    /// Used for Unix addon-path preloading where we construct
    /// platform-specific filenames with version numbers.
    /// </summary>
    private static readonly string[] UnixFFmpegLibNames =
        new[] { "avutil", "swresample", "swscale", "avcodec", "avformat", "avdevice", "avfilter" };

    /// <summary>
    /// Pre-loads FFmpeg shared libraries from the addon directory using dlopen
    /// with RTLD_NOW | RTLD_GLOBAL. On Linux/macOS, NativeLibrary.Load() uses
    /// RTLD_LOCAL which hides symbols from inter-library dependencies (e.g.
    /// avcodec can't find avutil symbols). dlopen with RTLD_GLOBAL makes all
    /// FFmpeg symbols visible across the entire process.
    /// </summary>
    private static void PreloadFFmpegFromAddonPathUnix()
    {
        if (_gdeAddonPath == null) return;

        var isMacOS = RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
        var globalFlag = isMacOS ? RTLD_GLOBAL_MACOS : RTLD_GLOBAL_LINUX;
        var avcodecVer = ffmpeg.LibraryVersionMap.TryGetValue("avcodec", out var acVer) ? acVer : 62;

        // Look up the version map for the detected avcodec version
        Dictionary<string, int>? versionMap = null;
        if (VersionMaps.TryGetValue(avcodecVer, out var map))
            versionMap = map;

        foreach (var name in UnixFFmpegLibNames)
        {
            // Resolve version number: prefer VersionMaps, fall back to LibraryVersionMap
            int ver;
            if (versionMap != null && versionMap.TryGetValue(name, out var mappedVer))
                ver = mappedVer;
            else if (ffmpeg.LibraryVersionMap.TryGetValue(name, out var lvmVer))
                ver = lvmVer;
            else
                continue;

            string fileName = isMacOS
                ? $"lib{name}.{ver}.dylib"
                : $"lib{name}.so.{ver}";

            var fullPath = Path.Combine(_gdeAddonPath, fileName);
            if (File.Exists(fullPath))
            {
                if (isMacOS)
                    dlopen_macos(fullPath, RTLD_NOW | globalFlag);
                else
                    dlopen(fullPath, RTLD_NOW | globalFlag);
            }
        }
    }

    // ───────────────────────── Linux / macOS ─────────────────────────

    private static void ConfigureUnix()
    {
        // Step 1: Probe system FFmpeg version (pure file-system check, no ffmpeg.* access)
        _systemFfmpegVersion = ProbeSystemFfmpegVersion();

        if (_systemFfmpegVersion == null)
        {
            return;
        }

        // Step 2: Override LibraryVersionMap so DynamicallyLoadedBindings uses correct versions
        if (_systemFfmpegVersion.Value != 62)
        {
            ApplyVersionMap(_systemFfmpegVersion.Value);
        }

        // Step 3: Install DllImportResolver to handle name mapping
        //   FFmpeg.AutoGen requests "avcodec-62" but the system file is
        //   "libavcodec.so.60" (Linux) or "libavcodec.60.dylib" (macOS).
        //   The resolver bridges this gap.
        try
        {
            NativeLibrary.SetDllImportResolver(
                typeof(ffmpeg).Assembly,
                OnResolveLibrary);
        }
        catch (InvalidOperationException)
        {
            // Already set — FFmpeg.AutoGen or another caller registered one first.
        }
    }

    /// <summary>
    /// Callback for NativeLibrary.SetDllImportResolver.
    /// Maps FFmpeg.AutoGen naming ("avcodec-62") to system library naming
    /// ("libavcodec.so.62" / "libavcodec.62.dylib") and probes known directories.
    /// </summary>
    private static IntPtr OnResolveLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        // 1. Try default resolution (searches RootPath + system paths)
        if (NativeLibrary.TryLoad(libraryName, assembly, searchPath, out var handle))
            return handle;

        // 2. Map FFmpeg.AutoGen name → system library name
        //    "avcodec-62" → "/usr/lib/x86_64-linux-gnu/libavcodec.so.62"
        var fullPath = FindLibraryInSystemPaths(libraryName);
        if (fullPath != null)
        {
            // Use the string-only overload to avoid resolver recursion
            if (NativeLibrary.TryLoad(fullPath, out handle))
            {
                return handle;
            }
        }

        return IntPtr.Zero;
    }

    // ───────────────────────── Probing ─────────────────────────

    /// <summary>
    /// Probes known system library directories for avcodec shared libraries.
    /// Returns the detected major version (e.g. 60 for libavcodec.so.60), or null.
    /// </summary>
    private static int? ProbeSystemFfmpegVersion()
    {
        var searchPaths = GetSystemSearchPaths();
        int[] knownVersions = { 62, 61, 60, 59, 58 };

        foreach (var dir in searchPaths)
        {
            if (!Directory.Exists(dir)) continue;

            foreach (var ver in knownVersions)
            {
                var linuxFile = Path.Combine(dir, $"libavcodec.so.{ver}");
                var macFile = Path.Combine(dir, $"libavcodec.{ver}.dylib");

                if (File.Exists(linuxFile))
                {
                    return ver;
                }

                if (File.Exists(macFile))
                {
                    return ver;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Searches system library directories for a full path to the requested library.
    /// Handles name mapping: "avcodec-62" → "libavcodec.so.62" (Linux) / "libavcodec.62.dylib" (macOS).
    /// Also tries unversioned symlinks: "libavcodec.so" → resolves to the installed version.
    /// </summary>
    private static string? FindLibraryInSystemPaths(string libraryName)
    {
        var mapped = MapToSystemName(libraryName);
        if (mapped == null) return null;

        foreach (var dir in GetSystemSearchPaths())
        {
            var fullPath = Path.Combine(dir, mapped);
            if (File.Exists(fullPath))
                return fullPath;
        }

        // Fallback: try unversioned symlink (e.g. libavcodec.so → libavcodec.so.60)
        var unversioned = MapToUnversionedName(libraryName);
        if (unversioned != null)
        {
            foreach (var dir in GetSystemSearchPaths())
            {
                var fullPath = Path.Combine(dir, unversioned);
                if (File.Exists(fullPath))
                    return fullPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Maps FFmpeg.AutoGen library name to system library name.
    /// "avcodec-62" → "libavcodec.so.62" (Linux), "libavcodec.62.dylib" (macOS)
    /// </summary>
    private static string? MapToSystemName(string libraryName)
    {
        var dashIndex = libraryName.LastIndexOf('-');
        if (dashIndex < 0) return null;

        var libBase = libraryName[..dashIndex];   // "avcodec"
        var version = libraryName[(dashIndex + 1)..]; // "62"

        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? $"lib{libBase}.{version}.dylib"
            : $"lib{libBase}.so.{version}";
    }

    /// <summary>
    /// Maps to unversioned symlink name.
    /// "avcodec-62" → "libavcodec.so" (Linux), "libavcodec.dylib" (macOS)
    /// </summary>
    private static string? MapToUnversionedName(string libraryName)
    {
        var dashIndex = libraryName.LastIndexOf('-');
        if (dashIndex < 0) return null;

        var libBase = libraryName[..dashIndex];

        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? $"lib{libBase}.dylib"
            : $"lib{libBase}.so";
    }

    private static string[] GetSystemSearchPaths()
    {
        return RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
            ? new[] { "/opt/homebrew/lib", "/usr/local/lib", "/usr/lib" }
            : new[]
            {
                "/usr/lib/x86_64-linux-gnu",
                "/usr/lib/aarch64-linux-gnu",
                "/usr/lib",
                "/usr/local/lib"
            };
    }

    // ───────────────────────── Version Map ─────────────────────────

    /// <summary>
    /// Overrides ffmpeg.LibraryVersionMap to match the detected system FFmpeg version.
    /// This ensures DynamicallyLoadedBindings constructs the correct library names
    /// during function resolution.
    /// </summary>
    private static void ApplyVersionMap(int avcodecVersion)
    {
        if (!VersionMaps.TryGetValue(avcodecVersion, out var map))
        {
            return;
        }

        foreach (var (lib, ver) in map)
        {
            if (ffmpeg.LibraryVersionMap.ContainsKey(lib))
            {
                ffmpeg.LibraryVersionMap[lib] = ver;
            }
        }
    }
}
