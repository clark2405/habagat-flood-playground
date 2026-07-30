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

            var (type, _) = ParsePreset(presetName);

            var root = new GameObject("HabagatShot");
            try
            {
                // ── The world ────────────────────────────────────────────────
                // Built through the SAME component the play scene uses, so this
                // screenshot is evidence about what actually runs rather than about
                // a parallel scene the harness assembled for itself.
                var worldGo = new GameObject("World");
                worldGo.transform.SetParent(root.transform);
                var world = worldGo.AddComponent<HabagatWorld>();
                world.preset = type;
                world.buildProps = Arg("-props", "1") != "0";
                world.running = false; // the harness advances the sim explicitly

                // Mangroves and drains are painted, not scattered, so a fresh sim has
                // none. `-paint mangrove` / `-paint drain` exercises that path for a
                // screenshot; the real scene gets them from the player.
                string paint = Arg("-paint", "");
                world.Rebuild(s =>
                {
                    if (paint == "") return;
                    var tool = paint == "drain" ? FloodSim.Tool.DrainPump : FloodSim.Tool.Mangrove;
                    for (int y = 8; y < FloodSim.H - 8; y += 9)
                        for (int x = 8; x < FloodSim.W - 8; x += 11)
                            s.Paint(tool, x, y, 2);
                });
                if (world.World == null)
                {
                    EditorApplication.Exit(2);
                    return;
                }

                // Optionally advance the sim, so a shot can show a flood rather than
                // only the starting waterline.
                int ticks = int.Parse(Arg("-ticks", "0"));
                float rain = float.Parse(Arg("-rain", "0"));
                bool storm = Arg("-storm", "0") == "1";
                world.Advance(ticks, rain, storm);

                // Atmosphere, sun and tone mapping all come from WorldBuilder now.
                // The one harness-only override: the plan view exists to check the
                // outerland's OUTLINE, and at 460 units up everything is past fogEnd
                // and comes back as flat grey.
                if (Arg("-view", "iso") == "plan") RenderSettings.fog = false;

                // ── Camera ───────────────────────────────────────────────────
                // Same framing as the web build's isometric preset.
                var camGo = new GameObject("Cam");
                camGo.transform.SetParent(root.transform);
                var cam = camGo.AddComponent<Camera>();
                // Mirrored in Z from the web build's (56,52,56), for the same reason
                // the mesh is: the scene was reflected into left-handed space.
                // `-view top` gives a plan view, which is the only framing that
                // makes an orientation mismatch against the reference unambiguous.
                // `-focus x,z -dist d` frames a close-up on any world position, which
                // is how a single suspect prop gets inspected. Reasoning about what a
                // 5-pixel smudge in the wide shot "must be" is exactly how the
                // outerland investigation nearly invented a bug that did not exist.
                string focus = Arg("-focus", "");
                switch (focus != "" ? "focus" : Arg("-view", "iso"))
                {
                    case "focus":
                    {
                        var parts = focus.Split(',');
                        float fx = float.Parse(parts[0]), fz = float.Parse(parts[1]);
                        float d = float.Parse(Arg("-dist", "14"));
                        var target = new Vector3(fx, 0, fz);
                        cam.transform.position = target + new Vector3(d * 0.7f, d * 0.62f, -d * 0.7f);
                        cam.transform.LookAt(target);
                        break;
                    }
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
                // Track the fog the weather actually settled on, not the clear-sky
                // constant — otherwise a storm renders dark fog against a bright sky
                // and the horizon shows as a hard line.
                cam.backgroundColor = RenderSettings.fogColor;
                cam.allowHDR = true;

                // ── UI ───────────────────────────────────────────────────────
                // A ScreenSpaceOverlay canvas never appears in a RenderTexture, so
                // for the screenshot it is retargeted to render through this camera.
                // The layout is identical either way — only the compositing differs.
                if (Arg("-ui", "0") == "1")
                {
                    worldGo.AddComponent<PaintController>().cam = cam;
                    var ui = worldGo.AddComponent<HabagatUI>();
                    ui.Build();
                    ui.Canvas.renderMode = RenderMode.ScreenSpaceCamera;
                    ui.Canvas.worldCamera = cam;
                    ui.Canvas.planeDistance = 1f;
                    // Called directly rather than via SendMessage: that would invoke
                    // Update on every component here, including PaintController, whose
                    // Awake never runs outside Play mode.
                    Canvas.ForceUpdateCanvases();
                    ui.Refresh();
                    Canvas.ForceUpdateCanvases();
                }

                // ── Post-processing ──────────────────────────────────────────
                // ACES filmic tone mapping is not a nicety here: the web build
                // renders through it, so every colour in the ramp was chosen
                // against its response curve. Rendering the same vertex colours
                // linearly produces a washed-out pastel version of the same map.
                var camData = camGo.GetComponent<UniversalAdditionalCameraData>();
                if (camData == null) camData = camGo.AddComponent<UniversalAdditionalCameraData>();
                camData.renderPostProcessing = true;

                // ── Benchmark ────────────────────────────────────────────────
                // `-bench N` runs the real per-frame path N times and reports the
                // cost. Batch-mode timing is not a player frame rate — there is no
                // present, no vsync, and Camera.Render is synchronous — but it does
                // measure the work this project actually adds per frame, which is
                // where a regression would show up first.
                int bench = int.Parse(Arg("-bench", "0"));
                if (bench > 0)
                {
                    var rtB = new RenderTexture(width, height, 24, RenderTextureFormat.ARGB32);
                    cam.targetTexture = rtB;
                    cam.Render(); // warm shaders and buffers before timing anything

                    var swSim = new System.Diagnostics.Stopwatch();
                    var swRender = new System.Diagnostics.Stopwatch();
                    for (int i = 0; i < bench; i++)
                    {
                        swSim.Start();
                        world.Advance(2, rain, storm, settleWeather: false);
                        swSim.Stop();
                        swRender.Start();
                        cam.Render();
                        swRender.Stop();
                    }
                    cam.targetTexture = null;
                    UnityEngine.Object.DestroyImmediate(rtB);

                    double sim = swSim.Elapsed.TotalMilliseconds / bench;
                    double ren = swRender.Elapsed.TotalMilliseconds / bench;
                    Debug.Log($"[Bench] {presetName} frames={bench} " +
                              $"update={sim:F2}ms render={ren:F2}ms total={sim + ren:F2}ms " +
                              $"({1000.0 / (sim + ren):F0} fps equivalent)");
                }

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

                int renderers = root.GetComponentsInChildren<MeshRenderer>().Length;
                Debug.Log($"[SceneShot] {presetName} -> {Path.GetFullPath(outPath)} ({renderers} renderers)");
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(root);
            }
        }
    }
}
