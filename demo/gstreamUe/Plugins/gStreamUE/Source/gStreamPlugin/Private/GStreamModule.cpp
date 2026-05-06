#include "GStreamModule.h"
#include "GStreamAPI.h"
#include "GStreamSessionManager.h"
#include "GStreamFrameCapture.h"
#include "GStreamSettings.h"
#include "Editor.h"

#define LOCTEXT_NAMESPACE "FGStreamModule"

void FGStreamModule::StartupModule()
{
	// Load the native library on module startup
	if (!FGStreamAPI::Load())
	{
		UE_LOG(LogTemp, Warning, TEXT("[gStream] Native library not found. Streaming will not be available until the library is placed correctly."));
		// Don't return — still create managers so module is usable
	}

	SessionManager = MakeUnique<FGStreamSessionManager>();
	FrameCapture   = MakeUnique<FGStreamFrameCapture>();
	FrameCapture->SetSession(SessionManager.Get());

	// Register PIE auto-start delegate
#if WITH_EDITOR
	PIEStartedHandle = FEditorDelegates::PostPIEStarted.AddRaw(this, &FGStreamModule::OnPIEStarted);
	PIEEndedHandle   = FEditorDelegates::EndPIE.AddRaw(this, &FGStreamModule::OnPIEEnded);
#endif

	bInitialized = true;
	UE_LOG(LogTemp, Log, TEXT("[gStream] Module initialized"));
}

void FGStreamModule::ShutdownModule()
{
	if (!bInitialized) return;

#if WITH_EDITOR
	if (PIEStartedHandle.IsValid())
	{
		FEditorDelegates::PostPIEStarted.Remove(PIEStartedHandle);
		PIEStartedHandle.Reset();
	}
	if (PIEEndedHandle.IsValid())
	{
		FEditorDelegates::EndPIE.Remove(PIEEndedHandle);
		PIEEndedHandle.Reset();
	}
#endif

	StopStreaming();

	FrameCapture.Reset();
	SessionManager.Reset();
	FGStreamAPI::Unload();

	bInitialized = false;
	UE_LOG(LogTemp, Log, TEXT("[gStream] Module shutdown"));
}

void FGStreamModule::OnPIEStarted(bool bIsSimulating)
{
	const UGStreamSettings* Settings = GetDefault<UGStreamSettings>();
	if (Settings->bAutoStart)
	{
		UE_LOG(LogTemp, Log, TEXT("[gStream] Auto-start enabled, starting streaming..."));
		StartStreaming();
	}
}

void FGStreamModule::OnPIEEnded(bool bIsSimulating)
{
	if (IsStreamingActive())
	{
		UE_LOG(LogTemp, Log, TEXT("[gStream] PIE ended, stopping streaming..."));
		StopStreaming();
	}
}

bool FGStreamModule::StartStreaming()
{
	if (!bInitialized)
	{
		UE_LOG(LogTemp, Error, TEXT("[gStream] Module not initialized"));
		return false;
	}

	if (!FGStreamAPI::IsLoaded())
	{
		// Retry loading
		if (!FGStreamAPI::Load())
		{
			UE_LOG(LogTemp, Error, TEXT("[gStream] Native library not loaded. Cannot start streaming."));
			return false;
		}
	}

	if (!SessionManager->StartStreaming())
		return false;

	// Start frame capture — driven by FTickableGameObject::Tick
	FrameCapture->Start();

	UE_LOG(LogTemp, Log, TEXT("[gStream] Streaming started"));
	return true;
}

void FGStreamModule::StopStreaming()
{
	if (!bInitialized) return;

	FrameCapture->Stop();
	SessionManager->StopStreaming();

	UE_LOG(LogTemp, Log, TEXT("[gStream] Streaming stopped"));
}

bool FGStreamModule::IsStreamingActive() const
{
	return bInitialized && SessionManager->IsActive();
}

bool FGStreamModule::IsPeerConnected() const
{
	return bInitialized && SessionManager->IsConnected();
}

void FGStreamModule::ForceKeyframe()
{
	if (bInitialized) SessionManager->ForceKeyframe();
}

FString FGStreamModule::GetEncoderName() const
{
	return bInitialized ? SessionManager->GetEncoderName() : TEXT("");
}

#undef LOCTEXT_NAMESPACE

IMPLEMENT_MODULE(FGStreamModule, gStreamPlugin)
