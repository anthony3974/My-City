using System;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;
using UnityEngine.Rendering;

/// <summary>
/// Tools > Android > Build APK, or from a shell with Unity closed: Tools/android-build.sh
/// </summary>
public static class AndroidBuild
{
    public const string PackageId = "com.Tony3974.MyCity";
    public const string ApkPath = "Builds/Android/MyCity.apk";
    const string ScenePath = "Assets/Scenes/SampleScene.unity";

    [MenuItem("Tools/Android/Apply Android Settings")]
    public static void ApplySettings()
    {
        var android = NamedBuildTarget.Android;
        PlayerSettings.productName = "My City";
        PlayerSettings.companyName = "Tony3974";
        PlayerSettings.SetApplicationIdentifier(android, PackageId);
        PlayerSettings.SetScriptingBackend(android, ScriptingImplementation.IL2CPP);
        PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;
        PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
        PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;
        PlayerSettings.SetUseDefaultGraphicsAPIs(BuildTarget.Android, false);
        PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[] { GraphicsDeviceType.Vulkan, GraphicsDeviceType.OpenGLES3 });

        PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
        PlayerSettings.allowedAutorotateToPortrait = false;
        PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
        PlayerSettings.allowedAutorotateToLandscapeLeft = true;
        PlayerSettings.allowedAutorotateToLandscapeRight = true;
        PlayerSettings.Android.startInFullscreen = true;

        if (PlayerSettings.Android.bundleVersionCode < 1) PlayerSettings.Android.bundleVersionCode = 1;
        EditorUserBuildSettings.buildAppBundle = false;
        AssetDatabase.SaveAssets();
    }

    [MenuItem("Tools/Android/Build APK")]
    static void MenuBuild() => Build(ApkPath);

    public static bool Build(string path, bool development = false)
    {
        ApplySettings();
        // Set for this build only, then put back, so builds don't leave ProjectSettings.asset changed.
        var stackTraces = PlayerSettings.GetIl2CppStacktraceInformation(NamedBuildTarget.Android);
        PlayerSettings.SetIl2CppStacktraceInformation(NamedBuildTarget.Android,
            development ? Il2CppStacktraceInformation.MethodFileLineNumber : Il2CppStacktraceInformation.MethodOnly);
        try { return BuildPlayer(path, development); }
        finally
        {
            PlayerSettings.SetIl2CppStacktraceInformation(NamedBuildTarget.Android, stackTraces);
            AssetDatabase.SaveAssets();
        }
    }

    static bool BuildPlayer(string path, bool development)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
        {
            scenes = new[] { ScenePath },
            locationPathName = path,
            target = BuildTarget.Android,
            targetGroup = BuildTargetGroup.Android,
            options = development ? BuildOptions.Development : BuildOptions.None,
        });

        var s = report.summary;
        if (s.result == BuildResult.Succeeded)
        {
            long bytes = File.Exists(path) ? new FileInfo(path).Length : 0;
            Debug.Log($"My City: built {path} ({bytes / (1024f * 1024f):0.0} MB, {s.totalTime.TotalSeconds:0} s).");
            return true;
        }
        Debug.LogError($"My City: Android build {s.result} with {s.totalErrors} errors.");
        return false;
    }

    /// <summary>Batch mode entry. Switches the build target back afterwards so the editor still opens on PC.</summary>
    public static void CommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        int outIndex = Array.IndexOf(args, "-cityOut");
        string path = outIndex >= 0 && outIndex + 1 < args.Length ? args[outIndex + 1] : ApkPath;

        var previous = EditorUserBuildSettings.activeBuildTarget;
        var previousGroup = BuildPipeline.GetBuildTargetGroup(previous);
        bool ok;
        try { ok = Build(path, args.Contains("-cityDev")); }
        catch (Exception e) { Debug.LogException(e); ok = false; }

        if (previous != BuildTarget.Android && EditorUserBuildSettings.activeBuildTarget != previous)
            EditorUserBuildSettings.SwitchActiveBuildTarget(previousGroup, previous);
        EditorApplication.Exit(ok ? 0 : 1);
    }
}
