// Manages a gStream streaming session lifecycle.
// Owns the native session handle and coordinates frame capture.

#pragma once

#include "CoreMinimal.h"
#include "GStreamAPI.h"

class UGStreamSettings;

class FGStreamSessionManager
{
public:
	FGStreamSessionManager();
	~FGStreamSessionManager();

	// Create a streaming session from UE project settings.
	// Returns true on success.
	bool StartStreaming();

	// Destroy the active session and release resources.
	void StopStreaming();

	// Push a captured BGRA/RGBA frame to the encoder.
	void PushFrame(int32 Width, int32 Height, int32 Stride, const uint8_t* Data);

	// Push a captured frame using zero-copy (caller must keep data alive until return).
	void PushFrameDirect(int32 Width, int32 Height, int32 Stride, const uint8_t* Data);

	// Push float32 PCM audio samples.
	void PushAudio(const float* Samples, int32 SampleCount);

	// Force next encoded frame to be a keyframe.
	void ForceKeyframe();

	// Is the WebRTC peer connection established?
	bool IsConnected() const;

	// Get the active encoder name (e.g. "h264_nvenc").
	FString GetEncoderName() const;

	// Is a session currently active?
	bool IsActive() const { return SessionHandle != 0; }

private:
	int64_t SessionHandle = 0;
};
