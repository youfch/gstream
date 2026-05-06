// gStream.Core C API dynamic loader implementation.

#include "GStreamAPI.h"

#if PLATFORM_WINDOWS
	#include "Windows/WindowsHWrapper.h"
	#include "HAL/PlatformProcess.h"
	#define GSTREAM_LOADLIB(path) FPlatformProcess::GetDllHandle(*FString(path))
	#define GSTREAM_GETPROC(h, n) ::GetProcAddress((HMODULE)(h), n)
	#define GSTREAM_FREELIB(h)    FPlatformProcess::FreeDllHandle((void*)(h))
#elif PLATFORM_LINUX
	#include <dlfcn.h>
	#include "HAL/PlatformProcess.h"
	#define GSTREAM_LOADLIB(path) FPlatformProcess::GetDllHandle(*FString(path))
	#define GSTREAM_GETPROC(h, n) dlsym(h, n)
	#define GSTREAM_FREELIB(h)    FPlatformProcess::FreeDllHandle((void*)(h))
#elif PLATFORM_MAC
	#include <dlfcn.h>
	#include "HAL/PlatformProcess.h"
	#define GSTREAM_LOADLIB(path) FPlatformProcess::GetDllHandle(*FString(path))
	#define GSTREAM_GETPROC(h, n) dlsym(h, n)
	#define GSTREAM_FREELIB(h)    FPlatformProcess::FreeDllHandle((void*)(h))
#endif

// ── Statics ──
void* FGStreamAPI::LibraryHandle = nullptr;
bool  FGStreamAPI::bLoaded = false;

GStream_SessionCreateFn    FGStreamAPI::SessionCreate   = nullptr;
GStream_SessionDestroyFn   FGStreamAPI::SessionDestroy  = nullptr;
GStream_PushFrameFn        FGStreamAPI::PushFrame       = nullptr;
GStream_PushFrameDirectFn  FGStreamAPI::PushFrameDirect = nullptr;
GStream_PushAudioFn        FGStreamAPI::PushAudio       = nullptr;
GStream_ForceKeyframeFn    FGStreamAPI::ForceKeyframe   = nullptr;
GStream_IsConnectedFn      FGStreamAPI::IsConnected     = nullptr;
GStream_GetEncoderNameFn   FGStreamAPI::GetEncoderName  = nullptr;
GStream_FreeFn             FGStreamAPI::Free            = nullptr;

void* FGStreamAPI::GetProcAddress(const char* Name)
{
	if (!LibraryHandle) return nullptr;
	return GSTREAM_GETPROC(LibraryHandle, Name);
}

bool FGStreamAPI::Load()
{
	if (bLoaded) return true;

	FString LibName;
#if PLATFORM_WINDOWS
	LibName = TEXT("gStream.Core.dll");
#elif PLATFORM_LINUX
	LibName = TEXT("libgStream.Core.so");
#elif PLATFORM_MAC
	LibName = TEXT("libgStream.Core.dylib");
#else
	UE_LOG(LogTemp, Error, TEXT("[gStream] Unsupported platform"));
	return false;
#endif

	// Search paths in priority order
	TArray<FString> SearchPaths;

	// 1. Plugin ThirdParty directory (most reliable)
	FString ThirdPartyBase = FPaths::ConvertRelativePathToFull(
		FPaths::ProjectPluginsDir() / TEXT("gStreamUE/ThirdParty"));
#if PLATFORM_WINDOWS
	SearchPaths.Add(FPaths::Combine(ThirdPartyBase, TEXT("Win64")));
#elif PLATFORM_LINUX
	SearchPaths.Add(FPaths::Combine(ThirdPartyBase, TEXT("Linux")));
#elif PLATFORM_MAC
	SearchPaths.Add(FPaths::Combine(ThirdPartyBase, TEXT("Mac")));
#endif

	// 2. Binary output directory
	SearchPaths.Add(FPaths::ConvertRelativePathToFull(
		FPaths::ProjectDir() / TEXT("Binaries") / FPlatformProcess::GetBinariesSubdirectory()));

	// 3. Engine binary directory (in case DLL was placed there)
	SearchPaths.Add(FPaths::ConvertRelativePathToFull(
		FPaths::EngineDir() / TEXT("Binaries") / FPlatformProcess::GetBinariesSubdirectory()));

	FString LoadedFrom;
	for (const FString& Dir : SearchPaths)
	{
		FString LibPath = FPaths::Combine(Dir, LibName);
		if (FPaths::FileExists(LibPath))
		{
			LibraryHandle = GSTREAM_LOADLIB(LibPath);
			if (LibraryHandle)
			{
				LoadedFrom = LibPath;
				break;
			}
		}
	}

	if (!LibraryHandle)
	{
		UE_LOG(LogTemp, Error, TEXT("[gStream] Failed to load native library '%s'. Searched paths:"), *LibName);
		for (const FString& Dir : SearchPaths)
		{
			UE_LOG(LogTemp, Error, TEXT("  - %s"), *FPaths::Combine(Dir, LibName));
		}
		return false;
	}

	// Resolve all function pointers (names must match [UnmanagedCallersOnly(EntryPoint=...)] in C#)
	#define RESOLVE(var, exp) \
		var = reinterpret_cast<decltype(var)>(GetProcAddress(exp)); \
		if (!var) { UE_LOG(LogTemp, Error, TEXT("[gStream] Failed to resolve: %s"), TEXT(exp)); }

	RESOLVE(SessionCreate,   "gstream_session_create");
	RESOLVE(SessionDestroy,  "gstream_session_destroy");
	RESOLVE(PushFrame,       "gstream_push_frame");
	RESOLVE(PushFrameDirect, "gstream_push_frame_direct");
	RESOLVE(PushAudio,       "gstream_push_audio");
	RESOLVE(ForceKeyframe,   "gstream_force_keyframe");
	RESOLVE(IsConnected,     "gstream_is_connected");
	RESOLVE(GetEncoderName,  "gstream_get_encoder_name");
	RESOLVE(Free,            "gstream_free");

	#undef RESOLVE

	// Verify all resolved
	if (!SessionCreate || !SessionDestroy || !PushFrame || !PushFrameDirect ||
		!PushAudio || !ForceKeyframe || !IsConnected || !GetEncoderName || !Free)
	{
		UE_LOG(LogTemp, Error, TEXT("[gStream] One or more functions failed to resolve. Unloading."));
		Unload();
		return false;
	}

	// Pre-load FFmpeg DLLs from the same directory as gStream.Core.dll.
	// gStream.Core.dll (NativeAOT) needs FFmpeg DLLs (avcodec-62, etc.) at runtime,
	// but AppContext.BaseDirectory points to the UE editor dir — NOT where FFmpeg lives.
	// By loading them into the process here, the OS loader returns existing handles
	// when gStream.Core.dll's NativeAOT P/Invokes try to resolve them.
	{
		FString DllDir = FPaths::GetPath(LoadedFrom);
		TArray<FString> FFmpegDlls = {
			TEXT("avutil-60.dll"),
			TEXT("swresample-6.dll"),
			TEXT("swscale-9.dll"),
			TEXT("avcodec-62.dll"),
			TEXT("avformat-62.dll"),
			TEXT("avdevice-62.dll"),
			TEXT("avfilter-11.dll")
		};
		for (const FString& Dll : FFmpegDlls)
		{
			FString DllPath = FPaths::Combine(DllDir, Dll);
			if (FPaths::FileExists(DllPath))
			{
				void* hFFmpeg = GSTREAM_LOADLIB(DllPath);
				if (hFFmpeg)
				{
					UE_LOG(LogTemp, Display, TEXT("[gStream] Pre-loaded FFmpeg: %s"), *Dll);
				}
				else
				{
					UE_LOG(LogTemp, Error, TEXT("[gStream] Failed to pre-load FFmpeg: %s"), *DllPath);
				}
			}
			else
			{
				UE_LOG(LogTemp, Error, TEXT("[gStream] FFmpeg DLL not found: %s"), *DllPath);
			}
		}
	}

	bLoaded = true;
	UE_LOG(LogTemp, Log, TEXT("[gStream] Native library loaded successfully from: %s"), *LoadedFrom);
	return true;
}

void FGStreamAPI::Unload()
{
	if (LibraryHandle)
	{
		GSTREAM_FREELIB(LibraryHandle);
		LibraryHandle = nullptr;
	}

	SessionCreate   = nullptr;
	SessionDestroy  = nullptr;
	PushFrame       = nullptr;
	PushFrameDirect = nullptr;
	PushAudio       = nullptr;
	ForceKeyframe   = nullptr;
	IsConnected     = nullptr;
	GetEncoderName  = nullptr;
	Free            = nullptr;
	bLoaded         = false;
}
