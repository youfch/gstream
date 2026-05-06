#include "GStreamSettings.h"

UGStreamSettings::UGStreamSettings()
{
}

#if WITH_EDITOR
FName UGStreamSettings::GetCategoryName() const
{
	return FName(TEXT("Plugins"));
}

FName UGStreamSettings::GetSectionName() const
{
	return FName(TEXT("gStream"));
}
#endif
