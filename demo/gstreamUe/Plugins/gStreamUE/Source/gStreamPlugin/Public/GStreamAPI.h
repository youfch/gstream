// gStream.Core C API declarations for dynamic loading.
// The native library is AOT-compiled from gStream.Core.dll/.so/.dylib.

#pragma once

#include "CoreMinimal.h"

// ── Enum mappings (must match C# VideoCodec / EncoderPreset) ──

UENUM()
enum class EGStreamVideoCodec : uint8
{
	Auto               = 0,
	H264_High_L31      = 1,
	H264_Main_L31      = 2,
	H264_CBaseline_L31 = 3,
	H264_Baseline_L31  = 4,
	H265_Main_L41      = 10,
	AV1_Main_L5        = 20,
	VP9_Profile0       = 30,
	VP9_Profile2       = 31,
};

UENUM()
enum class EGStreamEncoderPreset : uint8
{
	UltraLowLatency = 0,
	LowLatency      = 1,
	Balanced        = 2,
	HighQuality     = 3,
};

// ── Function pointer typedefs ──

typedef int64_t (*GStream_SessionCreateFn)(
	int32 Width, int32 Height, int32 Fps, int32 BitrateKbps,
	int32 Codec, int32 Preset,
	const char* SignalingUrl, const char* BindAddress, int32 ReceiveRemoteVideo);

typedef void (*GStream_SessionDestroyFn)(int64_t SessionHandle);

typedef void (*GStream_PushFrameFn)(
	int64_t SessionHandle, int32 Width, int32 Height, int32 Stride, const uint8_t* Data);

typedef void (*GStream_PushFrameDirectFn)(
	int64_t SessionHandle, int32 Width, int32 Height, int32 Stride, const uint8_t* Data);

typedef void (*GStream_PushAudioFn)(
	int64_t SessionHandle, const float* Samples, int32 SampleCount);

typedef void (*GStream_ForceKeyframeFn)(int64_t SessionHandle);

typedef int32 (*GStream_IsConnectedFn)(int64_t SessionHandle);

typedef const char* (*GStream_GetEncoderNameFn)(int64_t SessionHandle);

typedef void (*GStream_FreeFn)(void* Ptr);

// ── Dynamic loader ──

class FGStreamAPI
{
public:
	// Load the native library. Returns true on success.
	static bool Load();
	static void Unload();

	// Check if the library is loaded and all functions resolved.
	static bool IsLoaded() { return bLoaded; }

	// ── Function pointers (valid after Load() succeeds) ──
	static GStream_SessionCreateFn    SessionCreate;
	static GStream_SessionDestroyFn   SessionDestroy;
	static GStream_PushFrameFn        PushFrame;
	static GStream_PushFrameDirectFn  PushFrameDirect;
	static GStream_PushAudioFn        PushAudio;
	static GStream_ForceKeyframeFn    ForceKeyframe;
	static GStream_IsConnectedFn      IsConnected;
	static GStream_GetEncoderNameFn   GetEncoderName;
	static GStream_FreeFn             Free;

private:
	static void* LibraryHandle;
	static bool  bLoaded;

	static void* GetProcAddress(const char* Name);
};
