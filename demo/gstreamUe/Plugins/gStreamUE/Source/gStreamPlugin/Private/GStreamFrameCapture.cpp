#include "GStreamFrameCapture.h"
#include "GStreamSessionManager.h"
#include "GStreamSettings.h"
#include "Engine/Engine.h"
#include "Engine/GameViewportClient.h"
#include "UnrealClient.h"

FGStreamFrameCapture::FGStreamFrameCapture()
{
}

FGStreamFrameCapture::~FGStreamFrameCapture()
{
	Stop();
}

void FGStreamFrameCapture::Start()
{
	if (bCapturing) return;

	const UGStreamSettings* Settings = GetDefault<UGStreamSettings>();
	TargetFps = Settings->Fps;
	FrameAccumulator = 0.0f;

	bCapturing = true;
	UE_LOG(LogTemp, Log, TEXT("[gStream] Frame capture started @ %d fps"), TargetFps);
}

void FGStreamFrameCapture::Stop()
{
	if (!bCapturing) return;
	bCapturing = false;
	PixelBuffer.Empty();
	UE_LOG(LogTemp, Log, TEXT("[gStream] Frame capture stopped"));
}

void FGStreamFrameCapture::Tick(float DeltaTime)
{
	if (!bCapturing || !Session || !Session->IsActive()) return;

	// Frame rate limiting — only capture at target FPS
	FrameAccumulator += DeltaTime;
	float FrameInterval = 1.0f / TargetFps;
	if (FrameAccumulator < FrameInterval) return;
	FrameAccumulator = FMath::Fmod(FrameAccumulator, FrameInterval);

	// Get the game viewport
	UGameViewportClient* GVC = GEngine->GameViewport;
	if (!GVC || !GVC->Viewport) return;

	FViewport* Viewport = GVC->Viewport;
	const int32 ViewportWidth = Viewport->GetSizeXY().X;
	const int32 ViewportHeight = Viewport->GetSizeXY().Y;

	if (ViewportWidth <= 0 || ViewportHeight <= 0) return;

	const UGStreamSettings* Settings = GetDefault<UGStreamSettings>();
	const int32 TargetWidth = Settings->Width;
	const int32 TargetHeight = Settings->Height;

	// Read pixels from viewport (FColor = BGRA on little-endian)
	if (BufferWidth != ViewportWidth || BufferHeight != ViewportHeight)
	{
		PixelBuffer.SetNumUninitialized(ViewportWidth * ViewportHeight);
		BufferWidth = ViewportWidth;
		BufferHeight = ViewportHeight;
	}

	Viewport->ReadPixels(PixelBuffer);

	// Push frame data — FColor is 4 bytes (BGRA in memory on x86)
	const int32 Stride = ViewportWidth * 4;

	// If resolution matches target, push directly
	if (ViewportWidth == TargetWidth && ViewportHeight == TargetHeight)
	{
		Session->PushFrame(ViewportWidth, ViewportHeight, Stride,
			reinterpret_cast<const uint8*>(PixelBuffer.GetData()));
	}
	else
	{
		// Resolution mismatch: push at viewport resolution
		// The gStream encoder will handle whatever size we give it
		Session->PushFrame(ViewportWidth, ViewportHeight, Stride,
			reinterpret_cast<const uint8*>(PixelBuffer.GetData()));
	}
}

TStatId FGStreamFrameCapture::GetStatId() const
{
	RETURN_QUICK_DECLARE_CYCLE_STAT(FGStreamFrameCapture, STATGROUP_Tickables);
}
