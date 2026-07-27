using System;
using System.IO;
using Habagat;
using Habagat.Render;
using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;

namespace HabagatEditor
{
    /// <summary>
    /// Builds the scene from scratch and renders it to a PNG, headlessly.
    ///
    /// This is the Unity-side equivalent of driving the web build with headless
    /// Chromium and looking at screenshots: without it there is no way to tell
    /// whether a change to the mesh, the ramp or the shader actually improved
    /// anything. Everything is constructed in code rather than saved into a
    /// scene asset, so the render is reproducible and reviewable in a diff.
    ///
    /// Run WITHOUT -nographics — this needs a real graphics device:
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt; \
    ///             -executeMethod HabagatEditor.SceneShot.Run \
    ///             -preset coastal -shotOut &lt;path.png&gt;
    /// </summary>
    public static class SceneShot
    {
        private static string Arg(string name, string fallback)
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == name) return args[i + 1];
            return fallback;
        }

        private static (PresetType type, int seed) ParsePreset(string s)
        {
            switch (s.ToLowerInvariant())
            {
                case "river":  return (PresetType.River, 4040);
                case "urban":  return (PresetType.Urban, 9999);
                case "island": return (PresetType.Island, 8888);
                default:       return (PresetType.Coastal, 1337);
            }
        }

        public static void Run()
        {
            string presetName = Arg("-preset", "coastal");
            string outPath = Arg("-shotOut", "shot.png");
            int width = int.Parse(Arg("-shotW", "1600"));
            int height = int.Parse(Arg("-shotH", "900"));

            var (type, seed) = ParsePreset(presetName);
            var sim = FloodSim.FromPreset(type, seed);
            var palette = TerrainPalette.For(type);

            var root = new GameObject("HabagatShot");
            try
            {
                // ── Terrain ──────────────────────────────────────────────────
                var builder = new TerrainMeshBuilder();
                var mesh = builder.Build(sim.Elev, palette);

                var terrain = new GameObject("Terrain");
                terrain.transform.SetParent(root.transform);
                terrain.AddComponent<MeshFilter>().sharedMesh = mesh;
                var mr = terrain.AddComponent<MeshRenderer>();

                var shader = Shader.Find("Habagat/VertexColorLit");
                if (shader == null)
                {
                    Debug.LogError("[SceneShot] Habagat/VertexColorLit not found — shader failed to compile?");
                    EditorApplication.Exit(2);
                    return;
                }
                mr.sharedMaterial = new Material(shader);
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
                mr.receiveShadows = true;

                // ── Environment ──────────────────────────────────────────────
                // Values mirror ENV.coastal in ThreeCanvas.jsx. The horizon band
                // of the sky must equal the fog colour exactly — that identity is
                // what makes land dissolve into sky with no seam.
                var fogColor = new Color(0.874f, 0.933f, 0.957f); // 0xdfeef4
                RenderSettings.fog = true;
                RenderSettings.fogMode = FogMode.Linear;
                RenderSettings.fogColor = fogColor;
                RenderSettings.fogStartDistance = 100f;
                RenderSettings.fogEndDistance = 430f;
                RenderSettings.ambientMode = UnityEngine.Rendering.AmbientMode.Flat;
                // Ambient sits well below the web build's nominal 0.85. three.js
                // folds a 1/PI into its diffuse BRDF that URP's Lambert does not,
                // so carrying the number across literally doubles the fill light
                // and flattens the whole image into pastel.
                RenderSettings.ambientLight = new Color(0.874f, 0.910f, 0.980f) * 0.42f;

                var lightGo = new GameObject("Sun");
                lightGo.transform.SetParent(root.transform);
                var light = lightGo.AddComponent<Light>();
                light.type = LightType.Directional;
                light.color = new Color(1f, 0.941f, 0.678f); // 0xfff0ad
                light.intensity = 1.35f;
                light.shadows = LightShadows.Soft;
                lightGo.transform.position = new Vector3(40, 65, 40);
                lightGo.transform.LookAt(Vector3.zero);

                // ── Camera ───────────────────────────────────────────────────
                // Same framing as the web build's isometric preset.
                var camGo = new GameObject("Cam");
                camGo.transform.SetParent(root.transform);
                var cam = camGo.AddComponent<Camera>();
                cam.transform.position = new Vector3(56, 52, 56);
                cam.transform.LookAt(Vector3.zero);
                cam.fieldOfView = 40f;
                cam.nearClipPlane = 0.5f;
                cam.farClipPlane = 1000f;
                cam.clearFlags = CameraClearFlags.SolidColor;
                cam.backgroundColor = fogColor;
                cam.allowHDR = true;

                // ── Post-processing ──────────────────────────────────────────
                // ACES filmic tone mapping is not a nicety here: the web build
                // renders through it, so every colour in the ramp was chosen
                // against its response curve. Rendering the same vertex colours
                // linearly produces a washed-out pastel version of the same map.
                var camData = camGo.GetComponent<UniversalAdditionalCameraData>();
                if (camData == null) camData = camGo.AddComponent<UniversalAdditionalCameraData>();
                camData.renderPostProcessing = true;

                var volumeGo = new GameObject("GlobalVolume");
                volumeGo.transform.SetParent(root.transform);
                var volume = volumeGo.AddComponent<Volume>();
                volume.isGlobal = true;
                var profile = ScriptableObject.CreateInstance<VolumeProfile>();
                var tonemap = profile.Add<Tonemapping>();
                tonemap.mode.overrideState = true;
                tonemap.mode.value = TonemappingMode.ACES;
                volume.sharedProfile = profile;

                // ── Render ───────────────────────────────────────────────────
                var rt = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32)
                {
                    antiAliasing = 4
                };
                cam.targetTexture = rt;
                cam.Render();

                var prev = RenderTexture.active;
                RenderTexture.active = rt;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
                File.WriteAllBytes(outPath, tex.EncodeToPNG());

                cam.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(rt);
                UnityEngine.Object.DestroyImmediate(tex);

                Debug.Log($"[SceneShot] {presetName} -> {Path.GetFullPath(outPath)} " +
                          $"({mesh.vertexCount} verts, {mesh.triangles.Length / 3} tris)");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
