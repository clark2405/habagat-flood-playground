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
        public static void Run()
        {
            EnsureShadersIncluded();

            string outDir = Arg("-buildOut", "Build/Windows");
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

            var opts = new BuildPlayerOptions
            {
                scenes = scenes,
                locationPathName = Path.Combine(outDir, "Habagat.exe"),
                target = BuildTarget.StandaloneWindows64,
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
    }
}
