// Blueprint-callable functions for gStream streaming control.

#pragma once

#include "CoreMinimal.h"
#include "Kismet/BlueprintFunctionLibrary.h"
#include "GStreamBlueprintLibrary.generated.h"

UCLASS()
class GSTREAMPLUGIN_API UGStreamBlueprintLibrary : public UBlueprintFunctionLibrary
{
	GENERATED_BODY()

public:
	/** Start the streaming session (uses Project Settings). */
	UFUNCTION(BlueprintCallable, Category="gStream", meta=(DisplayName="Start Streaming"))
	static bool StartStreaming();

	/** Stop the active streaming session. */
	UFUNCTION(BlueprintCallable, Category="gStream", meta=(DisplayName="Stop Streaming"))
	static void StopStreaming();

	/** Is a streaming session currently active? */
	UFUNCTION(BlueprintCallable, Category="gStream", meta=(DisplayName="Is Streaming Active"))
	static bool IsStreamingActive();

	/** Is the WebRTC peer connection established? */
	UFUNCTION(BlueprintCallable, Category="gStream", meta=(DisplayName="Is Peer Connected"))
	static bool IsPeerConnected();

	/** Force next encoded frame to be a keyframe. */
	UFUNCTION(BlueprintCallable, Category="gStream", meta=(DisplayName="Force Keyframe"))
	static void ForceKeyframe();

	/** Get the name of the active encoder (e.g. "h264_nvenc"). */
	UFUNCTION(BlueprintCallable, Category="gStream", meta=(DisplayName="Get Encoder Name"))
	static FString GetEncoderName();
};
