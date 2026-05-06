// UE project settings for gStream. Accessible via Project Settings → Plugins → gStream.

#pragma once

#include "CoreMinimal.h"
#include "Engine/DeveloperSettings.h"
#include "GStreamAPI.h"
#include "GStreamSettings.generated.h"

UCLASS(Config=Game, DefaultConfig, meta=(DisplayName="gStream"))
class GSTREAMPLUGIN_API UGStreamSettings : public UDeveloperSettings
{
	GENERATED_BODY()

public:
	UGStreamSettings();

	// ── Video ──

	/** Target frame width in pixels. */
	UPROPERTY(Config, EditAnywhere, Category="Video", meta=(ClampMin=64, ClampMax=7680))
	int32 Width = 1920;

	/** Target frame height in pixels. */
	UPROPERTY(Config, EditAnywhere, Category="Video", meta=(ClampMin=64, ClampMax=4320))
	int32 Height = 1080;

	/** Target framerate. */
	UPROPERTY(Config, EditAnywhere, Category="Video", meta=(ClampMin=1, ClampMax=240))
	int32 Fps = 60;

	/** Target bitrate in kbps. */
	UPROPERTY(Config, EditAnywhere, Category="Video", meta=(ClampMin=100, ClampMax=100000))
	int32 BitrateKbps = 8000;

	/** Video codec selection. */
	UPROPERTY(Config, EditAnywhere, Category="Video")
	EGStreamVideoCodec Codec = EGStreamVideoCodec::Auto;

	/** Encoder preset (latency vs quality tradeoff). */
	UPROPERTY(Config, EditAnywhere, Category="Video")
	EGStreamEncoderPreset Preset = EGStreamEncoderPreset::UltraLowLatency;

	// ── Streaming ──

	/** Automatically start streaming when Play-In-Editor begins. */
	UPROPERTY(Config, EditAnywhere, Category="Streaming")
	bool bAutoStart = true;

	// ── Network ──

	/** WebSocket URL of the signaling server (e.g. "ws://192.168.1.100:80"). */
	UPROPERTY(Config, EditAnywhere, Category="Network")
	FString SignalingUrl = TEXT("ws://localhost:80");

	/** Bind address for ICE/RTP sockets. Leave empty for auto-select. */
	UPROPERTY(Config, EditAnywhere, Category="Network")
	FString BindAddress;

	/** Whether to receive remote video (for bidirectional streaming). */
	UPROPERTY(Config, EditAnywhere, Category="Network")
	bool bReceiveRemoteVideo = false;

#if WITH_EDITOR
	virtual FName GetCategoryName() const override;
	virtual FName GetSectionName() const override;
#endif
};
