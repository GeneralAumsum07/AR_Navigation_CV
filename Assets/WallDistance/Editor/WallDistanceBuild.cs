using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace WallDistance.EditorTools
{
    /// <summary>
    /// Headless Android build entry point for the CLI:
    ///   unity build . --target Android --execute-method WallDistance.EditorTools.WallDistanceBuild.BuildAndroid
    /// Output: Builds/Android/WallDistanceDemo.apk (override with -buildPath &lt;path&gt;).
    /// Also exposed as a menu item for in-Editor use.
    /// </summary>
    public static class WallDistanceBuild
    {
        const string DefaultOutput = "Builds/Android/WallDistanceDemo.apk";

        [MenuItem("WallDistance/Build Android APK")]
        public static void BuildAndroidMenu() => BuildAndroid();

        public static void BuildAndroid()
        {
            string output = ReadArg("-buildPath") ?? DefaultOutput;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output)));

            var options = new BuildPlayerOptions
            {
                scenes = new[] { "Assets/Scenes/WallMeasurement.unity" },
                locationPathName = output,
                target = BuildTarget.Android,
                targetGroup = BuildTargetGroup.Android,
                // Development build keeps the Unity log readable over adb logcat during field tests.
                options = BuildOptions.Development,
            };

            EditorUserBuildSettings.buildAppBundle = false;

            BuildReport report = BuildPipeline.BuildPlayer(options);
            var s = report.summary;
            Debug.Log($"[WallDistanceBuild] {s.result}: {s.outputPath} ({s.totalSize / (1024 * 1024)} MB, {s.totalErrors} errors, {s.totalWarnings} warnings, {s.totalTime.TotalSeconds:F0}s)");

            if (s.result != BuildResult.Succeeded)
            {
                foreach (var step in report.steps)
                    foreach (var msg in step.messages)
                        if (msg.type == LogType.Error || msg.type == LogType.Exception)
                            Debug.LogError($"[WallDistanceBuild] {step.name}: {msg.content}");
                if (Application.isBatchMode) EditorApplication.Exit(1);
                throw new BuildFailedException($"Android build failed: {s.result}");
            }
        }

        static string ReadArg(string name)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
            return null;
        }
    }
}
