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

                // ── The world outside the sandbox ────────────────────────────
                // Deliberately NOT receiving shadows: the shadow frustum only
                // covers the play area, and sampling it out here paints a hard
                // straight frustum edge across the landscape — exactly the kind of
                // artificial line this whole mesh exists to remove.
                var outer = new OuterlandBuilder();
                outer.Build(sim, palette, OuterConfig.For(type));

                var outerLand = new GameObject("Outerland");
                outerLand.transform.SetParent(root.transform);
                outerLand.AddComponent<MeshFilter>().sharedMesh = outer.Land;
                var olr = outerLand.AddComponent<MeshRenderer>();
                var outerMat = new Material(shader);
                outerMat.SetFloat("_Cull", 0f); // double-sided, like the web build
                olr.sharedMaterial = outerMat;
                olr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                olr.receiveShadows = false;

                // ── Water ────────────────────────────────────────────────────
                // Optionally advance the sim first, so a shot can show a flood
                // rather than only the starting waterline.
                int ticks = int.Parse(Arg("-ticks", "0"));
                float rain = float.Parse(Arg("-rain", "0"));
                bool storm = Arg("-storm", "0") == "1";
                for (int i = 0; i < ticks; i++) sim.Step(rain, storm);

                var waterBuilder = new WaterMeshBuilder();
                var waterMesh = waterBuilder.Build(sim, palette);

                var water = new GameObject("Water");
                water.transform.SetParent(root.transform);
                water.AddComponent<MeshFilter>().sharedMesh = waterMesh;
                var wr = water.AddComponent<MeshRenderer>();
                var waterShader = Shader.Find("Habagat/WaterVertexColor");
                if (waterShader == null)
                {
                    Debug.LogError("[SceneShot] Habagat/WaterVertexColor not found — shader failed to compile?");
                    EditorApplication.Exit(2);
                    return;
                }
                wr.sharedMaterial = new Material(waterShader);
                wr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                wr.receiveShadows = false;

                var outerWater = new GameObject("OuterWater");
                outerWater.transform.SetParent(root.transform);
                outerWater.AddComponent<MeshFilter>().sharedMesh = outer.Water;
                var owr = outerWater.AddComponent<MeshRenderer>();
                owr.sharedMaterial = new Material(waterShader);
                owr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                owr.receiveShadows = false;

                // ── Environment ──────────────────────────────────────────────
                // Values mirror ENV.coastal in ThreeCanvas.jsx. The horizon band
                // of the sky must equal the fog colour exactly — that identity is
                // what makes land dissolve into sky with no seam.
                var fogColor = new Color(0.874f, 0.933f, 0.957f); // 0xdfeef4
                // The plan view exists to check the outerland's OUTLINE, and at 460
                // units up everything is past fogEnd and comes back as flat grey.
                // Fog off for that diagnostic only.
                RenderSettings.fog = Arg("-view", "iso") != "plan";
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
                // Z negated against the web build's (40,65,40), for the same reason
                // the mesh negates it: the geometry was mirrored into Unity's
                // left-handed space, so anything positioned in that space has to be
                // mirrored with it or the sun comes from the wrong quarter.
                lightGo.transform.position = new Vector3(40, 65, -40);
                lightGo.transform.LookAt(Vector3.zero);

                // ── Camera ───────────────────────────────────────────────────
                // Same framing as the web build's isometric preset.
                var camGo = new GameObject("Cam");
                camGo.transform.SetParent(root.transform);
                var cam = camGo.AddComponent<Camera>();
                // Mirrored in Z from the web build's (56,52,56), for the same reason
                // the mesh is: the scene was reflected into left-handed space.
                // `-view top` gives a plan view, which is the only framing that
                // makes an orientation mismatch against the reference unambiguous.
                switch (Arg("-view", "iso"))
                {
                    case "top":
                        cam.transform.position = new Vector3(0, 92, 0);
                        cam.transform.rotation = Quaternion.Euler(90, 0, 0);
                        break;
                    case "plan":
                        // High plan view of the whole world, for checking that the
                        // outerland's outline is organic rather than a rectangle.
                        cam.transform.position = new Vector3(0, 460, 0);
                        cam.transform.rotation = Quaternion.Euler(90, 0, 0);
                        break;
                    case "far":
                        cam.transform.position = new Vector3(150, 120, -150);
                        cam.transform.LookAt(Vector3.zero);
                        break;
                    default:
                        cam.transform.position = new Vector3(56, 52, -56);
                        cam.transform.LookAt(Vector3.zero);
                        break;
                }
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
                // Supersample rather than rely on the RenderTexture's antiAliasing
                // field — URP takes its MSAA setting from the pipeline asset and
                // ignores that field, so the first shots came back with visibly
                // jagged water edges. Rendering at 2x and box-filtering down is
                // pipeline-independent and gives cleaner alpha edges than MSAA would.
                const int ss = 2;
                var rtBig = new RenderTexture(width * ss, height * ss, 24, RenderTextureFormat.ARGB32);
                var rtSmall = new RenderTexture(width, height, 0, RenderTextureFormat.ARGB32)
                {
                    filterMode = FilterMode.Bilinear
                };

                cam.targetTexture = rtBig;
                cam.Render();
                Graphics.Blit(rtBig, rtSmall);

                var prev = RenderTexture.active;
                RenderTexture.active = rtSmall;
                var tex = new Texture2D(width, height, TextureFormat.RGB24, false);
                tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                tex.Apply();
                RenderTexture.active = prev;

                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath)));
                File.WriteAllBytes(outPath, tex.EncodeToPNG());

                cam.targetTexture = null;
                UnityEngine.Object.DestroyImmediate(rtBig);
                UnityEngine.Object.DestroyImmediate(rtSmall);
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
