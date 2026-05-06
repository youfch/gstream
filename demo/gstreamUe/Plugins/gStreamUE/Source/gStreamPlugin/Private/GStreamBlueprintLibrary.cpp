#include "GStreamBlueprintLibrary.h"
#include "GStreamModule.h"

bool UGStreamBlueprintLibrary::StartStreaming()
{
	return FGStreamModule::Get().StartStreaming();
}

void UGStreamBlueprintLibrary::StopStreaming()
{
	FGStreamModule::Get().StopStreaming();
}

bool UGStreamBlueprintLibrary::IsStreamingActive()
{
	return FGStreamModule::Get().IsStreamingActive();
}

bool UGStreamBlueprintLibrary::IsPeerConnected()
{
	return FGStreamModule::Get().IsPeerConnected();
}

void UGStreamBlueprintLibrary::ForceKeyframe()
{
	FGStreamModule::Get().ForceKeyframe();
}

FString UGStreamBlueprintLibrary::GetEncoderName()
{
	return FGStreamModule::Get().GetEncoderName();
}
