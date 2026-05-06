#include "GStreamSessionManager.h"
#include "GStreamSettings.h"

FGStreamSessionManager::FGStreamSessionManager()
{
}

FGStreamSessionManager::~FGStreamSessionManager()
{
	StopStreaming();
}

bool FGStreamSessionManager::StartStreaming()
{
	if (!FGStreamAPI::IsLoaded())
	{
		if (!FGStreamAPI::Load())
		{
			UE_LOG(LogTemp, Error, TEXT("[gStream] Cannot start: native library not loaded"));
			return false;
		}
	}

	if (IsActive())
	{
		UE_LOG(LogTemp, Warning, TEXT("[gStream] Session already active, stopping previous"));
		StopStreaming();
	}

	const UGStreamSettings* Settings = GetDefault<UGStreamSettings>();

	UE_LOG(LogTemp, Display, TEXT("[gStream] Creating session: %dx%d @ %dfps, %dkbps, codec=%d, preset=%d, url=%s"),
		Settings->Width, Settings->Height, Settings->Fps, Settings->BitrateKbps,
		static_cast<int32>(Settings->Codec), static_cast<int32>(Settings->Preset),
		*Settings->SignalingUrl);

	int64_t Handle = FGStreamAPI::SessionCreate(
		Settings->Width,
		Settings->Height,
		Settings->Fps,
		Settings->BitrateKbps,
		static_cast<int32>(Settings->Codec),
		static_cast<int32>(Settings->Preset),
		TCHAR_TO_UTF8(*Settings->SignalingUrl),
		Settings->BindAddress.IsEmpty() ? nullptr : TCHAR_TO_UTF8(*Settings->BindAddress),
		Settings->bReceiveRemoteVideo ? 1 : 0
	);

	if (Handle == 0)
	{
		UE_LOG(LogTemp, Error, TEXT("[gStream] Session creation failed"));
		return false;
	}

	SessionHandle = Handle;
	UE_LOG(LogTemp, Log, TEXT("[gStream] Session created: %dx%d @ %d fps, %d kbps, codec=%d"),
		Settings->Width, Settings->Height, Settings->Fps, Settings->BitrateKbps,
		static_cast<int32>(Settings->Codec));
	return true;
}

void FGStreamSessionManager::StopStreaming()
{
	if (SessionHandle != 0 && FGStreamAPI::IsLoaded())
	{
		FGStreamAPI::SessionDestroy(SessionHandle);
		UE_LOG(LogTemp, Log, TEXT("[gStream] Session destroyed: %lld"), SessionHandle);
	}
	SessionHandle = 0;
}

void FGStreamSessionManager::PushFrame(int32 Width, int32 Height, int32 Stride, const uint8_t* Data)
{
	if (SessionHandle == 0) return;
	FGStreamAPI::PushFrame(SessionHandle, Width, Height, Stride, Data);
}

void FGStreamSessionManager::PushFrameDirect(int32 Width, int32 Height, int32 Stride, const uint8_t* Data)
{
	if (SessionHandle == 0) return;
	FGStreamAPI::PushFrameDirect(SessionHandle, Width, Height, Stride, Data);
}

void FGStreamSessionManager::PushAudio(const float* Samples, int32 SampleCount)
{
	if (SessionHandle == 0) return;
	FGStreamAPI::PushAudio(SessionHandle, Samples, SampleCount);
}

void FGStreamSessionManager::ForceKeyframe()
{
	if (SessionHandle == 0) return;
	FGStreamAPI::ForceKeyframe(SessionHandle);
}

bool FGStreamSessionManager::IsConnected() const
{
	if (SessionHandle == 0) return false;
	return FGStreamAPI::IsConnected(SessionHandle) != 0;
}

FString FGStreamSessionManager::GetEncoderName() const
{
	if (SessionHandle == 0) return TEXT("");
	if (!FGStreamAPI::IsLoaded()) return TEXT("");

	const char* Name = FGStreamAPI::GetEncoderName(SessionHandle);
	if (!Name) return TEXT("");

	FString Result = UTF8_TO_TCHAR(Name);
	FGStreamAPI::Free((void*)Name);
	return Result;
}
