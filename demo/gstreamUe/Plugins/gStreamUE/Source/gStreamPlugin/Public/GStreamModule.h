// gStream UE plugin module. Singleton that owns the session and frame capture lifecycle.

#pragma once

#include "CoreMinimal.h"
#include "Modules/ModuleManager.h"

class FGStreamSessionManager;
class FGStreamFrameCapture;

class FGStreamModule : public IModuleInterface
{
public:
	static FGStreamModule& Get()
	{
		return FModuleManager::LoadModuleChecked<FGStreamModule>("gStreamPlugin");
	}

	// ── IModuleInterface ──
	virtual void StartupModule() override;
	virtual void ShutdownModule() override;

	// ── Public API (used by BlueprintLibrary) ──
	bool StartStreaming();
	void StopStreaming();
	bool IsStreamingActive() const;
	bool IsPeerConnected() const;
	void ForceKeyframe();
	FString GetEncoderName() const;

private:
	TUniquePtr<FGStreamSessionManager> SessionManager;
	TUniquePtr<FGStreamFrameCapture>   FrameCapture;

	bool bInitialized = false;

	// PIE auto-start
	void OnPIEStarted(bool bIsSimulating);
	void OnPIEEnded(bool bIsSimulating);
	FDelegateHandle PIEStartedHandle;
	FDelegateHandle PIEEndedHandle;
};
