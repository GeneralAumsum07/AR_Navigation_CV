#if UNITY_ANDROID
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.Android;
using UnityEditor.Build;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace WallDistance.EditorTools
{
    /// <summary>
    /// Two build-time guarantees for ML depth:
    /// 1. The APK contains the plugin, the QNN runtime and a context binary. Missing files fail
    ///    the build, because an APK without them silently measures with ARCore planes only.
    ///    WALLDEPTH_ALLOW_MISSING=1 builds an ARCore-only APK on purpose.
    /// 2. Native libraries are extracted to disk at install. The DSP loads
    ///    libQnnHtpV75Skel.so through ADSP_LIBRARY_PATH from a real file path, which an
    ///    uncompressed-in-APK library does not have.
    /// </summary>
    public sealed class QnnAndroidBuild : IPreprocessBuildWithReport, IPostGenerateGradleAndroidProject
    {
        const string LibDir = "Assets/Plugins/Android/libs/arm64-v8a";
        const string ModelDir = "Assets/StreamingAssets/Models";
        // A versioned marker also upgrades an existing incremental Gradle project that
        // still contains the original extraction-only block.
        const string Marker = "// walldepth: QNN runtime packaging v2";
        static readonly string[] RequiredLibs =
            { "libwalldepth.so", "libQnnHtp.so", "libQnnHtpV75Stub.so", "libQnnHtpV75Skel.so", "libQnnSystem.so" };

        public int callbackOrder => 0;

        public void OnPreprocessBuild(BuildReport report)
        {
            if (report.summary.platform != BuildTarget.Android) return;
            var missing = new List<string>();
            foreach (var lib in RequiredLibs)
                if (!File.Exists(Path.Combine(LibDir, lib))) missing.Add(lib);
            if (!Directory.Exists(ModelDir) || Directory.GetFiles(ModelDir, "*.ctx.bin").Length == 0)
                missing.Add(ModelDir + "/*.ctx.bin");
            if (missing.Count == 0) return;

            string msg = "ML depth files missing: " + string.Join(", ", missing)
                         + ". Run native/walldepth/build-android.ps1 and copy the context binary (Task 7, Step 9),"
                         + " or set WALLDEPTH_ALLOW_MISSING=1 to build an ARCore-only APK.";
            if (Environment.GetEnvironmentVariable("WALLDEPTH_ALLOW_MISSING") == "1") Debug.LogWarning(msg);
            else throw new BuildFailedException(msg);
        }

        public void OnPostGenerateGradleAndroidProject(string unityLibraryPath)
        {
            string manifest = Path.Combine(unityLibraryPath, "src", "main", "AndroidManifest.xml");
            File.WriteAllText(manifest, QnnAndroidManifest.Patch(File.ReadAllText(manifest)));
            // Packaging options belong to the application module ("launcher"), next to unityLibrary.
            string launcher = Path.GetFullPath(Path.Combine(unityLibraryPath, "..", "launcher"));
            string gradle = Path.Combine(launcher, "build.gradle");
            if (!File.Exists(gradle)) gradle = Path.Combine(launcher, "build.gradle.kts");
            if (!File.Exists(gradle)) throw new BuildFailedException("No launcher build.gradle(.kts) under " + launcher);
            if (File.ReadAllText(gradle).Contains(Marker)) return;
            // The same block is valid Groovy and Kotlin DSL; Gradle merges repeated android {} blocks.
            File.AppendAllText(gradle,
                "\n" + Marker + "\nandroid {\n    packaging {\n        jniLibs {\n            useLegacyPackaging = true\n"
                // Preserve the pinned native bytes. In particular, the Hexagon skeleton is
                // DSP code and must not be rewritten by Android's ARM64 stripping stage.
                + "            keepDebugSymbols.add(\"**/libQnn*.so\")\n"
                + "            keepDebugSymbols.add(\"**/libwalldepth.so\")\n"
                + "        }\n    }\n}\n");
        }
    }
}
#endif
