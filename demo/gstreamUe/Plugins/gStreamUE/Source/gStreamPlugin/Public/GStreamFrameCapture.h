// Captures viewport frames and pushes to gStream encoder.
// Uses FViewport::ReadPixels for reliable cross-platform capture.

#pragma once

#include "CoreMinimal.h"
#include "Tickable.h"

class FGStreamSessionManager;

class FGStreamFrameCapture : public FTickableGameObject
{
public:
	FGStreamFrameCapture();
	virtual ~FGStreamFrameCapture();

	void Start();
	void Stop();
	bool IsCapturing() const { return bCapturing; }
	void SetSession(FGStreamSessionManager* InSession) { Session = InSession; }

	// ── FTickableGameObject ──
	virtual void Tick(float DeltaTime) override;
	virtual TStatId GetStatId() const override;
	virtual bool IsTickable() const override { return bCapturing; }
	virtual bool IsTickableInEditor() const override { return false; }

private:
	bool bCapturing = false;
	FGStreamSessionManager* Session = nullptr;

	// Pre-allocated buffer for pixel readback (avoids per-frame allocation)
	TArray<FColor> PixelBuffer;
	int32 BufferWidth = 0;
	int32 BufferHeight = 0;

	// Frame timing
	float FrameAccumulator = 0.0f;
	int32 TargetFps = 60;
};
