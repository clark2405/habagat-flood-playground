using System;
using System.IO;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEngine;

namespace HabagatEditor
{
    /// <summary>
    /// Builds a standalone Windows player.
    ///
    /// Worth doing on its own account, not just to get a frame rate: a build is the
    /// first thing that actually proves the project compiles without the editor
    /// propping it up. Editor-only namespaces leaking into runtime code, assets that
    /// exist in the project but were never referenced from a scene, shader variants
    /// that only ever compiled on demand — none of that shows up in Play mode or in
    /// a batch-mode screenshot.
    ///
    ///   Unity.exe -batchmode -quit -projectPath unity/HabagatUnity \
    ///             -executeMethod HabagatEditor.BuildPlayer.Run -buildOut &lt;dir&gt;
    /// </summary>
    public static class BuildPlayer
    {
        private static string Arg(string name, string fallback)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return fallback;
        }

        /// <summary>
        /// Every shader in this project is located with <c>Shader.Find</c> from code,
        /// because the materials are built at runtime rather than authored as assets.
        /// Unity decides what to ship by walking asset references, so from its point
        /// of view nothing uses these and all four get stripped — <c>Shader.Find</c>
        /// then returns null in the player, <c>WorldBuilder</c> bails, and the build
        /// launches to an empty scene while reporting a successful build.
        ///
        /// Registering them as always-included is the fix. It is done here rather than
        /// by hand so it cannot be lost, and it is idempotent.
        /// </summary>
        [MenuItem("Habagat/Register Shaders For Build")]
        public static void EnsureShadersIncluded()
        {
            string[] names =
            {
                "Habagat/VertexColorLit", "Habagat/WaterVertexColor",
                "Habagat/SkyDome", "Habagat/RainLine",
            };

            var gs = AssetDatabase.LoadAllAssetsAtPath("ProjectSettings/GraphicsSettings.asset")[0];
            var so = new UnityEditor.SerializedObject(gs);
            var list = so.FindProperty("m_AlwaysIncludedShaders");

            foreach (var name in names)
            {
                var shader = Shader.Find(name);
                if (shader == null)
                {
                    Debug.LogError($"[BuildPlayer] shader missing from project: {name}");
                    continue;
                }

                bool already = false;
                for (int i = 0; i < list.arraySize; i++)
                    if (list.GetArrayElementAtIndex(i).objectReferenceValue == shader) { already = true; break; }
                if (already) continue;

                list.InsertArrayElementAtIndex(list.arraySize);
                list.GetArrayElementAtIndex(list.arraySize - 1).objectReferenceValue = shader;
                Debug.Log($"[BuildPlayer] registered {name}");
            }

            so.ApplyModifiedProperties();
            AssetDatabase.SaveAssets();
        }

        [MenuItem("Habagat/Build Windows Player")]
        public static void Run() => Build(BuildTarget.StandaloneWindows64);

        /// <summary>
        /// A browser build, which is the only way to put this on a phone until Android
        /// Build Support is installed — and the fastest way to get it in front of an
        /// actual person, which nothing has managed yet.
        ///
        ///   Unity.exe -batchmode -quit -projectPath unity/HabagatUnity \
        ///             -executeMethod HabagatEditor.BuildPlayer.RunWeb -buildOut &lt;dir&gt;
        /// </summary>
        [MenuItem("Habagat/Build WebGL Player")]
        public static void RunWeb() => Build(BuildTarget.WebGL);

        /// <summary>
        /// Android. Scaffolding: the Hub module is not installed here, so this cannot
        /// run yet — but the settings a phone build needs are decisions, and writing
        /// them down in code is the part that does not need the module.
        /// </summary>
        [MenuItem("Habagat/Build Android Player")]
        public static void RunAndroid() => Build(BuildTarget.Android);

        /// <summary>
        /// iOS. Scaffolding in a stronger sense than Android: Unity cannot build for
        /// iOS from Windows at all, module or no module — it emits an Xcode project
        /// and that requires macOS. This path exists so the settings travel with the
        /// repo and the build is one command away on a Mac, not so it runs here.
        /// </summary>
        [MenuItem("Habagat/Build iOS Player")]
        public static void RunIOS() => Build(BuildTarget.iOS);

        private static string DefaultOut(BuildTarget t) => t switch
        {
            BuildTarget.WebGL => "Build/Web",
            BuildTarget.Android => "Build/Android",
            BuildTarget.iOS => "Build/iOS",
            _ => "Build/Windows",
        };

        private static void Build(BuildTarget target)
        {
            // Asked before anything else, because the failure otherwise is a wall of
            // internal Unity errors that never names the actual problem.
            var group = BuildPipeline.GetBuildTargetGroup(target);
            if (!BuildPipeline.IsBuildTargetSupported(group, target))
            {
                Debug.LogError(
                    $"[BuildPlayer] {target} support is not installed in this Unity " +
                    $"({Application.unityVersion}). Add it from Unity Hub → Installs → " +
                    $"the version's gear menu → Add modules. " +
                    (target == BuildTarget.iOS
                        ? "Note that iOS additionally requires macOS; it cannot be built from Windows."
                        : ""));
                EditorApplication.Exit(5);
                return;
            }

            EnsureShadersIncluded();

            // Still the URP template's defaults otherwise, which the browser build
            // shows the world: "com.unity.template.urp-blank" is the tab title and the
            // caption under the canvas. Set for every target, not just the web one —
            // it is the app name on a phone's home screen too.
            PlayerSettings.productName = "Habagat 3D";
            PlayerSettings.companyName = "Habagat";

            bool web = target == BuildTarget.WebGL;
            string outDir = Arg("-buildOut", DefaultOut(target));
            Directory.CreateDirectory(outDir);

            // The play scene is authored by MakeScene, so make sure it is the one that
            // ships rather than whatever happens to be in Build Settings.
            var scenes = new[] { "Assets/Scenes/Habagat.unity" };
            if (!File.Exists(scenes[0]))
            {
                Debug.LogError($"[BuildPlayer] {scenes[0]} missing — run Habagat/Rebuild Play Scene first.");
                EditorApplication.Exit(3);
                return;
            }

            switch (target)
            {
                case BuildTarget.WebGL: ConfigureWeb(); break;
                case BuildTarget.Android: ConfigureAndroid(); break;
                case BuildTarget.iOS: ConfigureIOS(); break;
            }

            var opts = new BuildPlayerOptions
            {
                scenes = scenes,
                // Only the Windows target names a file. WebGL and iOS write a folder
                // (a page and an Xcode project respectively); Android writes an apk.
                locationPathName = target switch
                {
                    BuildTarget.WebGL or BuildTarget.iOS => outDir,
                    BuildTarget.Android => Path.Combine(outDir, "Habagat.apk"),
                    _ => Path.Combine(outDir, "Habagat.exe"),
                },
                target = target,
                options = BuildOptions.None,
            };

            var report = BuildPipeline.BuildPlayer(opts);
            var sum = report.summary;
            Debug.Log($"[BuildPlayer] {sum.result} in {sum.totalTime} — " +
                      $"{sum.totalSize / (1024 * 1024)} MB, {sum.totalErrors} errors, {sum.totalWarnings} warnings");

            if (sum.result != BuildResult.Succeeded)
            {
                EditorApplication.Exit(4);
                return;
            }
            Debug.Log($"[BuildPlayer] wrote {Path.GetFullPath(opts.locationPathName)}");
        }

        /// <summary>
        /// Player settings the browser build needs. Set here rather than left in
        /// ProjectSettings so the reasoning travels with them.
        /// </summary>
        private static void ConfigureWeb()
        {
            // Compression OFF is the one that decides whether this works at all.
            // Unity's default is Brotli, and a .br file only loads if the server sends
            // Content-Encoding: br — which `python -m http.server`, the GitHub Pages
            // root, and every other plain static host do not. The browser then hands
            // the compressed bytes to the loader as if they were wasm and the page
            // fails with a decompression error that says nothing about the real cause.
            // Uncompressed is bigger and always loads.
            PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;

            // Assets/WebGLTemplates/Habagat. The stock template sizes its canvas from
            // a user-agent test and hardcodes 960x600 for anything it does not
            // recognise, and it never sets touch-action — so on a phone the browser
            // eats the two-finger pinch the camera needs. See the comments in it.
            PlayerSettings.WebGL.template = "PROJECT:Habagat";

            // The map is rebuilt from scratch on a preset switch and the sim allocates
            // per-cell arrays, so the default heap is not enough headroom.
            PlayerSettings.WebGL.memorySize = 512;

            // Without this the simulation stops the moment the tab loses focus, and
            // comes back to a flood that did not happen while you were away.
            PlayerSettings.runInBackground = true;

            // URP's colour ramp was authored in linear space, which on the web means
            // WebGL2 — the same requirement the desktop build has, just enforced here.
            PlayerSettings.SetGraphicsAPIs(BuildTarget.WebGL,
                new[] { UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3 });
            PlayerSettings.colorSpace = ColorSpace.Linear;
        }

        /// <summary>
        /// Settings shared by the two phone targets. Landscape-only is the one that is
        /// not a preference: the interface is a wide bottom bar of eight tool pills and
        /// a 300 px storm button, and in portrait it either overflows the screen or
        /// shrinks past the point of being hittable with a thumb.
        /// </summary>
        private static void ConfigureMobile()
        {
            PlayerSettings.defaultInterfaceOrientation = UIOrientation.AutoRotation;
            PlayerSettings.allowedAutorotateToPortrait = false;
            PlayerSettings.allowedAutorotateToPortraitUpsideDown = false;
            PlayerSettings.allowedAutorotateToLandscapeLeft = true;
            PlayerSettings.allowedAutorotateToLandscapeRight = true;

            PlayerSettings.useAnimatedAutorotation = true;
        }

        private static void ConfigureAndroid()
        {
            ConfigureMobile();
            PlayerSettings.SetApplicationIdentifier(
                UnityEditor.Build.NamedBuildTarget.Android, "com.habagat.flood");

            // IL2CPP and ARM64 go together and are not optional: Google Play has
            // required a 64-bit binary for years, and ARM64 is only reachable through
            // IL2CPP. Mono/ARMv7 would build and then be unpublishable.
            PlayerSettings.SetScriptingBackend(
                UnityEditor.Build.NamedBuildTarget.Android, ScriptingImplementation.IL2CPP);
            PlayerSettings.Android.targetArchitectures = AndroidArchitecture.ARM64;

            // 24 is a floor with a reason: below it Vulkan is absent and URP falls back
            // to GLES3 on hardware that struggles with this scene anyway.
            PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel24;
            PlayerSettings.Android.targetSdkVersion = AndroidSdkVersions.AndroidApiLevelAuto;

            PlayerSettings.SetGraphicsAPIs(BuildTarget.Android, new[]
            {
                UnityEngine.Rendering.GraphicsDeviceType.Vulkan,
                UnityEngine.Rendering.GraphicsDeviceType.OpenGLES3,
            });
        }

        private static void ConfigureIOS()
        {
            ConfigureMobile();
            PlayerSettings.SetApplicationIdentifier(
                UnityEditor.Build.NamedBuildTarget.iOS, "com.habagat.flood");
            PlayerSettings.SetScriptingBackend(
                UnityEditor.Build.NamedBuildTarget.iOS, ScriptingImplementation.IL2CPP);

            // 13 is where Metal is dependable across the devices still in use.
            PlayerSettings.iOS.targetOSVersionString = "13.0";
            PlayerSettings.SetGraphicsAPIs(BuildTarget.iOS,
                new[] { UnityEngine.Rendering.GraphicsDeviceType.Metal });
        }
    }
}
