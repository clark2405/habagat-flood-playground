using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UI;

namespace Habagat
{
    /// <summary>
    /// Exercises the built player and writes a report, then quits.
    ///
    /// This exists because everything else in this project is verified by rendering
    /// a still frame, and a still frame cannot tell you whether a button does
    /// anything when clicked or whether the brush paints where the cursor is. Both
    /// of those had already shipped broken once — the whole input layer was written
    /// against the legacy Input class in a project configured for the Input System,
    /// which throws the moment it runs.
    ///
    /// Input is synthesised through the Input System's own event queue rather than
    /// by calling the handlers directly, so the raycast, the grid mapping and the
    /// rebuilds all run exactly as they do for a real cursor. It is still not a
    /// person clicking: it proves the paths execute and change what they should, not
    /// that the result feels right.
    ///
    ///   Habagat.exe -selftest -report &lt;path&gt; -screen-width 1280 -screen-height 720
    /// </summary>
    public class SelfTest : MonoBehaviour
    {
        private readonly StringBuilder _log = new();
        private int _pass, _fail;

        private void Check(string name, bool ok, string detail = "")
        {
            if (ok) _pass++; else _fail++;
            _log.AppendLine($"{(ok ? "PASS" : "FAIL")}  {name}{(detail == "" ? "" : "  — " + detail)}");
        }

        private static string Arg(string name, string fallback)
        {
            var a = Environment.GetCommandLineArgs();
            for (int i = 0; i < a.Length - 1; i++) if (a[i] == name) return a[i + 1];
            return fallback;
        }

        public static bool Requested()
        {
            foreach (var a in Environment.GetCommandLineArgs()) if (a == "-selftest") return true;
            return false;
        }

        private IEnumerator Start()
        {
            // Present in every build but inert unless asked for, so the shipped player
            // is the same binary that was tested.
            if (!Requested()) yield break;

            var world = GetComponent<HabagatWorld>();
            var paint = GetComponent<PaintController>();
            var ui = GetComponent<HabagatUI>();

            // Let the world build and the first frames settle.
            for (int i = 0; i < 60; i++) yield return null;

            Check("world built", world.World != null);
            // WorldBuilder returns false and leaves an empty root if the shaders were
            // stripped from the build, which is exactly how a player that renders
            // nothing at all still reported "world built".
            var renderers = world.World != null && world.World.Root != null
                ? world.World.Root.GetComponentsInChildren<MeshRenderer>().Length : 0;
            Check("world has renderers", renderers > 20, $"{renderers} found");
            Check("terrain shader resolved", Shader.Find("Habagat/VertexColorLit") != null);
            Check("sim exists", world.Sim != null);
            Check("camera present", paint != null && paint.cam != null);
            Check("mouse device", Mouse.current != null);
            Check("UI canvas built", ui != null && ui.Canvas != null);

            // ── Frame rate, calm then storm ──────────────────────────────────
            yield return Measure(world, "calm", storm: false, rain: 0f);
            yield return Measure(world, "storm", storm: true, rain: 10f);
            world.storm = false; world.rain = 0f;

            // ── The brush ────────────────────────────────────────────────────
            // Screen centre, where the camera is pointed, so the ray lands on terrain.
            var centre = new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
            yield return MoveMouse(centre, false);
            for (int i = 0; i < 5; i++) yield return null;

            // Diagnostics: if the ray misses, the useful question is what exists to
            // be hit and where the camera is pointing.
            var cols = FindObjectsByType<Collider>(FindObjectsSortMode.None);
            _log.AppendLine($"DIAG  colliders={cols.Length}" +
                            (cols.Length > 0 ? $" first={cols[0].name} type={cols[0].GetType().Name} " +
                                               $"enabled={cols[0].enabled} bounds={cols[0].bounds}" : ""));
            var c = paint.cam;
            _log.AppendLine($"DIAG  cam pos={c.transform.position} fwd={c.transform.forward} " +
                            $"fov={c.fieldOfView} screen={Screen.width}x{Screen.height}");
            bool downHit = Physics.Raycast(new Ray(new Vector3(0, 200, 0), Vector3.down), out var dh, 500f);
            _log.AppendLine($"DIAG  straight-down ray hit={downHit}" + (downHit ? $" on {dh.collider.name} at {dh.point}" : ""));
            var centreRay = c.ScreenPointToRay(centre);
            _log.AppendLine($"DIAG  centre ray origin={centreRay.origin} dir={centreRay.direction}");
            ScreenCapture.CaptureScreenshot(Arg("-shot", "player.png"));
            yield return null;

            float[] elev = world.Sim.Elev;
            var before = (float[])elev.Clone();

            paint.brush = PaintController.Brush.Raise;
            yield return MoveMouse(centre, true);
            for (int i = 0; i < 10; i++) yield return null;
            yield return MoveMouse(centre, false);
            for (int i = 0; i < 3; i++) yield return null;

            int changed = 0;
            for (int i = 0; i < elev.Length; i++) if (Math.Abs(elev[i] - before[i]) > 1e-6f) changed++;
            Check("raise brush paints", changed > 0, $"{changed} cells changed");

            // Painting must move the ground, so the collider has to have been remade
            // with it — otherwise the next ray hits the shape the ground used to be.
            var hitOk = paint.cam != null &&
                        Physics.Raycast(paint.cam.ScreenPointToRay(centre), out _, 2000f);
            Check("terrain collider still hit after paint", hitOk);

            int MangCount()
            {
                int n = 0;
                foreach (var v in world.Sim.Mang) if (v != 0) n++;
                return n;
            }
            int mangBefore = MangCount();
            paint.brush = PaintController.Brush.Mangrove;
            yield return MoveMouse(centre, true);
            for (int i = 0; i < 6; i++) yield return null;
            yield return MoveMouse(centre, false);
            for (int i = 0; i < 3; i++) yield return null;
            Check("mangrove brush plants", MangCount() > mangBefore, $"{mangBefore} -> {MangCount()}");

            paint.brush = PaintController.Brush.None;

            // ── The interface ────────────────────────────────────────────────
            var buttons = ui.Canvas.GetComponentsInChildren<Button>(true);
            Check("UI has buttons", buttons.Length >= 12, $"{buttons.Length} found");

            bool ranBefore = world.running;
            var pause = FindButton(buttons, "Pause Sim");
            Check("pause button exists", pause != null);
            if (pause != null)
            {
                pause.onClick.Invoke();
                yield return null;
                Check("pause toggles the sim", world.running != ranBefore);
                pause.onClick.Invoke();
                yield return null;
            }

            var storm = FindButton(buttons, "SUMMON HABAGAT STORM");
            if (storm != null)
            {
                bool s0 = world.storm;
                storm.onClick.Invoke();
                yield return null;
                Check("storm button toggles", world.storm != s0);
                world.storm = false;
            }

            // Preset switching tears the whole world down and rebuilds it, which is
            // the most destructive thing the UI can do.
            var river = FindButton(buttons, "River Valley");
            Check("river preset button exists", river != null);
            if (river != null)
            {
                river.onClick.Invoke();
                for (int i = 0; i < 10; i++) yield return null;
                Check("preset switch rebuilds", world.preset == PresetType.River && world.World != null);
                Check("sim survives preset switch", world.Sim != null && world.Sim.Elev.Length > 0);
            }

            // ── Report ───────────────────────────────────────────────────────
            _log.AppendLine();
            _log.AppendLine($"{_pass} passed, {_fail} failed");
            string path = Arg("-report", "selftest.txt");
            try { File.WriteAllText(path, _log.ToString()); }
            catch (Exception e) { Debug.LogError(e); }
            Debug.Log("[SelfTest]\n" + _log);

            yield return null;
            Application.Quit(_fail == 0 ? 0 : 1);
        }

        private static Button FindButton(Button[] all, string label)
        {
            foreach (var b in all)
            {
                var t = b.GetComponentInChildren<Text>();
                if (t != null && t.text == label) return b;
            }
            return null;
        }

        private IEnumerator Measure(HabagatWorld world, string name, bool storm, float rain)
        {
            world.storm = storm;
            world.rain = rain;
            // Weather eases, so give it time to reach the state being measured.
            for (int i = 0; i < 120; i++) yield return null;

            int frames = 0;
            float t = 0f, worst = 0f;
            while (t < 3f)
            {
                t += Time.unscaledDeltaTime;
                worst = Mathf.Max(worst, Time.unscaledDeltaTime);
                frames++;
                yield return null;
            }
            float ms = t / frames * 1000f;
            _log.AppendLine($"FPS   {name}: {frames / t:F0} fps  ({ms:F2} ms avg, {worst * 1000f:F1} ms worst)");
        }

        /// <summary>
        /// Push a real mouse event through the Input System, so PaintController reads
        /// it the same way it reads a physical cursor.
        /// </summary>
        private IEnumerator MoveMouse(Vector2 pos, bool leftDown)
        {
            var m = Mouse.current;
            if (m == null) yield break;
            var state = new MouseState { position = pos };
            state = state.WithButton(MouseButton.Left, leftDown);
            InputSystem.QueueStateEvent(m, state);
            InputSystem.Update();
            yield return null;
        }
    }
}
