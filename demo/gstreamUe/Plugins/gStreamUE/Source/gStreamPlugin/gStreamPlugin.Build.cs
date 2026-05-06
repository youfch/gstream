using UnrealBuildTool;
using System.IO;

public class gStreamPlugin : ModuleRules
{
	public gStreamPlugin(ReadOnlyTargetRules Target) : base(Target)
	{
		PCHUsage = PCHUsageMode.UseExplicitOrSharedPCHs;

		PublicDependencyModuleNames.AddRange(new string[]
		{
			"Core",
			"CoreUObject",
			"Engine",
			"InputCore",
			"EnhancedInput",
			"RHI",
			"RenderCore",
			"Slate",
			"SlateCore",
			"DeveloperSettings",
		});

		PrivateDependencyModuleNames.AddRange(new string[]
		{
			"ApplicationCore",
			"UnrealEd",
		});

		// ── Third-party: gStream.Core native library ──
		string ThirdParty = Path.Combine(ModuleDirectory, "..", "..", "ThirdParty");

		if (Target.Platform == UnrealTargetPlatform.Win64)
		{
			string LibPath = Path.Combine(ThirdParty, "Win64");

			// Link against the import library
			string LibFile = Path.Combine(LibPath, "gStream.Core.lib");
			if (File.Exists(LibFile))
			{
				PublicAdditionalLibraries.Add(LibFile);
			}

			// Copy DLLs to binary output directory at build time
			CopyDllToBinaries(Target, LibPath, "gStream.Core.dll");

			string[] FfmpegDlls = {
				"avcodec-62.dll", "avdevice-62.dll", "avfilter-11.dll",
				"avformat-62.dll", "avutil-60.dll", "swresample-6.dll",
				"swscale-9.dll"
			};
			foreach (string Dll in FfmpegDlls)
			{
				CopyDllToBinaries(Target, LibPath, Dll);
			}
		}
		else if (Target.Platform == UnrealTargetPlatform.Linux)
		{
			string LibPath = Path.Combine(ThirdParty, "Linux");
			PublicAdditionalLibraries.Add(Path.Combine(LibPath, "libgStream.Core.so"));
		}
		else if (Target.Platform == UnrealTargetPlatform.Mac)
		{
			string LibPath = Path.Combine(ThirdParty, "Mac");
			PublicAdditionalLibraries.Add(Path.Combine(LibPath, "libgStream.Core.dylib"));
		}

		// Module include paths
		PrivateIncludePaths.Add(Path.Combine(ModuleDirectory, "Private"));
		PublicIncludePaths.Add(Path.Combine(ModuleDirectory, "Public"));
	}

	/// <summary>
	/// Registers a DLL as a runtime dependency so it gets deployed alongside the module.
	/// </summary>
	private void CopyDllToBinaries(ReadOnlyTargetRules Target, string SourceDir, string DllName)
	{
		string Source = Path.Combine(SourceDir, DllName);
		if (!File.Exists(Source)) return;

		RuntimeDependencies.Add(Path.Combine("$(BinaryOutputDir)", DllName), Source);
	}
}
