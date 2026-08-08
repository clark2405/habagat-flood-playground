using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.EventSystems;
using UnityEngine.UI;
// UnityEngine has a TouchPhase of its own, left over from the legacy input class.
// This project is Input System only, so the ambiguity is resolved once here rather
// than by qualifying every call site.
using TouchPhase = UnityEngine.InputSystem.TouchPhase;

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
            // Which layout was chosen and why. Screen.dpi is the input nobody can
            // guess: a hidpi desktop reports well over 96 and pushes a perfectly
            // roomy 1280-wide window into the phone layout.
            _log.AppendLine($"DIAG  dpi={Screen.dpi} density={ui.DensityName} " +
                            $"layoutBuilds={ui.LayoutBuilds}");

            // ── Sound ────────────────────────────────────────────────────────
            // Whether the cues sound good is not checkable here. What is checkable is
            // that they were generated at all, that they contain signal rather than
            // silence, and that nothing clips — a synthesis bug shows up as one of
            // those three long before anybody notices it by ear.
            var sound = GetComponent<Audio.SoundEngine>();
            Check("sound engine present", sound != null);
            Check("audio listener present", FindFirstObjectByType<AudioListener>() != null);
            if (sound != null)
            {
                var rain = sound.RainClip;
                Check("rain loop baked", rain != null && rain.samples > 1000,
                      rain == null ? "null" : $"{rain.samples} samples @ {rain.frequency} Hz");
                if (rain != null)
                {
                    var data = new float[rain.samples];
                    rain.GetData(data, 0);
                    float peak = 0f, energy = 0f;
                    foreach (var v in data) { peak = Mathf.Max(peak, Mathf.Abs(v)); energy += v * v; }
                    float rms = Mathf.Sqrt(energy / data.Length);
                    Check("rain loop carries signal", rms > 0.01f, $"rms {rms:F4}");
                    // Over 1.0 wraps to a crackle on some backends and is clamped on
                    // others, so it is never merely "a bit loud".
                    Check("rain loop does not clip", peak <= 1f, $"peak {peak:F3}");
                }
                Check("cue plays without error", PlaysCleanly(sound));
                // Waited in seconds, not frames: the fade is an exponential approach
                // with a 0.5 s time constant and is frame-rate independent by design,
                // so a fixed frame count measures a different point on the curve on
                // every machine. Counting frames here first read 0.064 after 90 of
                // them at 409 fps — which is the correct value at t=0.22 s, not a bug.
                sound.SetStorm(true);
                yield return Settle(1.6f);
                Check("storm brings up the rain bed", sound.RainSource.volume > 0.02f,
                      $"volume {sound.RainSource.volume:F3}");
                sound.SetStorm(false);
                yield return Settle(1.6f);
                Check("calm takes it back down", sound.RainSource.volume < 0.02f,
                      $"volume {sound.RainSource.volume:F3}");
            }

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
            // Read through world.Sim every time rather than held in a local: a preset
            // switch replaces the whole simulation, so a captured array quietly stops
            // being the one the brush is writing to and every later check reads zero
            // change from a brush that is working perfectly.
            var before = (float[])world.Sim.Elev.Clone();

            paint.brush = PaintController.Brush.Raise;
            yield return MoveMouse(centre, true);
            for (int i = 0; i < 10; i++) yield return null;
            yield return MoveMouse(centre, false);
            for (int i = 0; i < 3; i++) yield return null;

            int changed = 0;
            for (int i = 0; i < before.Length; i++)
                if (Math.Abs(world.Sim.Elev[i] - before[i]) > 1e-6f) changed++;
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

            // Homes: placed on dry ground, refused on water and refused next to an
            // existing one. The refusal matters as much as the placement — without it
            // a held drag would stack a house on every cell it crossed.
            // Aimed at a cell known to be dry and clear, not at screen centre: the
            // middle of the coastal map is the river mouth, so the first version of
            // this test was clicking on water and reading a correct refusal as a
            // failure.
            // Kept to the middle band of the screen, clear of the bars. A synthetic
            // press is a real press: the first version aimed wherever the projection
            // landed, the compact layout put the top bar over that point, and the test
            // clicked the Urban preset — rebuilding the world mid-test, which then
            // showed up as touch painting nothing and the house rule placing five.
            var spot = centre;
            for (int gy = 6; gy < FloodSim.H - 6 && spot == centre; gy += 2)
                for (int gx = 6; gx < FloodSim.W - 6; gx += 2)
                {
                    if (world.Sim.Elev[gy * FloodSim.W + gx] <= FloodSim.SeaLevel + 0.4f) continue;
                    bool crowded = false;
                    foreach (var h in world.Houses)
                        if (Mathf.Sqrt((h.X - gx) * (h.X - gx) + (h.Y - gy) * (h.Y - gy)) < 3f) { crowded = true; break; }
                    if (crowded) continue;
                    var wp = new Vector3(gx - FloodSim.W / 2f + 0.5f,
                                         world.Sim.Elev[gy * FloodSim.W + gx],
                                         -(gy - FloodSim.H / 2f + 0.5f));
                    var sp = paint.cam.WorldToScreenPoint(wp);
                    if (sp.z <= 0) continue;
                    if (sp.x < Screen.width * 0.15f || sp.x > Screen.width * 0.85f) continue;
                    if (sp.y < Screen.height * 0.42f || sp.y > Screen.height * 0.60f) continue;
                    spot = new Vector2(sp.x, sp.y);
                    break;
                }

            // Verified rather than assumed, because the band above is a guess about
            // where the bars are and the bars move with the layout.
            yield return MoveMouse(spot, false);
            yield return null;
            bool spotClear = EventSystem.current == null ||
                             !EventSystem.current.IsPointerOverGameObject(-1);
            Check("house target is clear of the UI", spotClear && spot != centre, $"at {spot}");
            _log.AppendLine($"DIAG  house target screen={spot}");

            int homesBefore = world.Houses.Count;
            paint.brush = PaintController.Brush.House;
            yield return MoveMouse(spot, false);
            yield return null;
            yield return MoveMouse(spot, true);
            for (int i = 0; i < 8; i++) yield return null;
            yield return MoveMouse(spot, false);
            for (int i = 0; i < 3; i++) yield return null;
            Check("house tool builds", world.Houses.Count > homesBefore,
                  $"{homesBefore} -> {world.Houses.Count}");
            Check("house tool refuses to crowd", world.Houses.Count == homesBefore + 1,
                  $"{world.Houses.Count - homesBefore} placed in one hold");

            // ── Touch ────────────────────────────────────────────────────────
            // A desktop player has no touchscreen, so one is added for the duration.
            // That is not a shortcut around the real path: PaintController reads
            // Touchscreen.current like any other device, and a synthesised device is
            // the same object a phone's driver would produce.
            var screen = InputSystem.AddDevice<Touchscreen>();
            // A freshly added device is not always current on the very next frame, and
            // when it is not, every touch check fails as though touch were broken. One
            // run in five did that before this wait — which is worse than a real
            // failure, because it teaches you to re-run until it passes.
            for (int i = 0; i < 10 && Touchscreen.current == null; i++) yield return null;
            yield return null;
            Check("touch device ready", Touchscreen.current != null);

            var touchBefore = (float[])world.Sim.Elev.Clone();
            paint.brush = PaintController.Brush.Raise;
            int paintsBefore = paint.PaintCalls;
            Finger(screen, 1, centre, TouchPhase.Began);
            yield return Step(8);
            var pt = Touchscreen.current != null ? Touchscreen.current.primaryTouch : null;
            _log.AppendLine($"DIAG  paintCalls +{paint.PaintCalls - paintsBefore} " +
                            $"brush={paint.brush}");
            _log.AppendLine($"DIAG  touch pressed={(pt != null && pt.press.isPressed)} " +
                            $"pos={(pt != null ? pt.position.ReadValue() : Vector2.zero)} " +
                            $"overUI={(EventSystem.current != null && pt != null && EventSystem.current.IsPointerOverGameObject(pt.touchId.ReadValue()))} " +
                            $"target={centre}");
            Finger(screen, 1, centre, TouchPhase.Ended);
            yield return Step(3);

            int touched = 0;
            for (int i = 0; i < touchBefore.Length; i++)
                if (Math.Abs(world.Sim.Elev[i] - touchBefore[i]) > 1e-6f) touched++;
            Check("brush paints under a finger", touched > 0, $"{touched} cells changed");

            // Two fingers is the camera's gesture. If the brush still stamped, every
            // attempt to pan or pinch would gouge the map on the way past. Both go down
            // in the same frame — see Step — or the gap between them is a legitimate
            // one-finger stroke and the check measures nothing.
            var pinchBefore = (float[])world.Sim.Elev.Clone();
            Finger(screen, 1, centre + new Vector2(-60f, 0f), TouchPhase.Began);
            Finger(screen, 2, centre + new Vector2(60f, 0f), TouchPhase.Began);
            yield return Step(8);
            int gouged = 0;
            for (int i = 0; i < pinchBefore.Length; i++)
                if (Math.Abs(world.Sim.Elev[i] - pinchBefore[i]) > 1e-6f) gouged++;
            Check("two fingers do not paint", gouged == 0, $"{gouged} cells changed");

            // And the camera has to actually respond to them, or the tool palette is
            // the only thing on a phone that does anything.
            float distBefore = paint.orbit != null ? paint.orbit.distance : 0f;
            for (int step = 1; step <= 6; step++)
            {
                Finger(screen, 1, centre + new Vector2(-60f - step * 12f, 0f), TouchPhase.Moved);
                Finger(screen, 2, centre + new Vector2(60f + step * 12f, 0f), TouchPhase.Moved);
                yield return Step();
            }
            Check("pinch changes the camera distance",
                  paint.orbit != null && Mathf.Abs(paint.orbit.distance - distBefore) > 0.5f,
                  $"{distBefore:F1} -> {(paint.orbit != null ? paint.orbit.distance : 0f):F1}");

            Finger(screen, 1, centre, TouchPhase.Ended);
            Finger(screen, 2, centre, TouchPhase.Ended);
            yield return Step();
            InputSystem.RemoveDevice(screen);
            yield return null;

            paint.brush = PaintController.Brush.None;

            // Captured here rather than at the start or the end: this is the one frame
            // that shows what the brushes actually did. At the start nothing had been
            // touched, and by the end the preset switch has thrown it all away.
            ScreenCapture.CaptureScreenshot(Arg("-shot", "player.png"));
            for (int i = 0; i < 4; i++) yield return null;

            // ── The interface ────────────────────────────────────────────────
            var buttons = ui.Canvas.GetComponentsInChildren<Button>(true);
            Check("UI has buttons", buttons.Length >= 12, $"{buttons.Length} found");

            // Both layouts get built, whichever one this machine happens to pick. On a
            // 125%-scaled desktop the density rule reports 1024 reference pixels and
            // chooses Compact even at 1280x720, which would leave the desktop layout
            // never exercised by anything here.
            foreach (var (label, width) in new (string, float)[] { ("comfortable", 1920f), ("compact", 800f) })
            {
                ui.layoutWidthOverride = width;
                // Two frames, not one: the rebuild happens in HabagatUI.Update, which
                // may already have run for the frame this coroutine is resumed in.
                yield return null;
                yield return null;
                var b = ui.Canvas.GetComponentsInChildren<Button>(true);
                Check($"{label} layout builds", b.Length >= 12 && FindButton(b, "Pause") != null,
                      $"{b.Length} buttons");
            }
            ui.layoutWidthOverride = 0f;
            yield return null;
            yield return null;
            buttons = ui.Canvas.GetComponentsInChildren<Button>(true);

            // A press that lands on the palette must not also stamp the ground behind
            // it. Worth its own check because the guard is now asked about a specific
            // pointer id rather than "the last pointer uGUI saw" — a change made for
            // touch, which could quietly have stopped covering the mouse.
            var overBtn = FindButton(buttons, "Clear");
            if (overBtn != null)
            {
                var onUI = (Vector2)overBtn.GetComponent<RectTransform>().position;
                var uiBefore = (float[])world.Sim.Elev.Clone();
                paint.brush = PaintController.Brush.Raise;
                yield return MoveMouse(onUI, false);
                yield return MoveMouse(onUI, true);
                for (int i = 0; i < 8; i++) yield return null;
                yield return MoveMouse(onUI, false);
                paint.brush = PaintController.Brush.None;

                int leaked = 0;
                for (int i = 0; i < uiBefore.Length; i++)
                    if (Math.Abs(world.Sim.Elev[i] - uiBefore[i]) > 1e-6f) leaked++;

                // Without this the check is a tautology: a button floating over empty
                // sky would report a clean pass while proving only that the ray missed.
                bool groundBehind = Physics.Raycast(paint.cam.ScreenPointToRay(onUI), out _, 2000f);
                Check("terrain lies behind the tested button", groundBehind, $"at {onUI}");
                Check("brush does not paint through the UI", leaked == 0,
                      $"{leaked} cells changed at {onUI}");
            }

            bool ranBefore = world.running;
            var pause = FindButton(buttons, "Pause");
            Check("pause button exists", pause != null);
            if (pause != null)
            {
                pause.onClick.Invoke();
                yield return null;
                Check("pause toggles the sim", world.running != ranBefore);
                pause.onClick.Invoke();
                yield return null;
            }

            var storm = FindButton(buttons, "Storm");
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

        /// <summary>
        /// The pop cues are baked on demand rather than up front, so this is the only
        /// check that the on-demand path runs at all — under a headless or device-less
        /// player it is also where a missing audio backend would surface.
        /// </summary>
        private static bool PlaysCleanly(Audio.SoundEngine sound)
        {
            try
            {
                sound.PlayPop(440f);
                sound.PlayTerraform(true);
                sound.PlayWaterSplash();
                return true;
            }
            catch (Exception e)
            {
                Debug.LogError(e);
                return false;
            }
        }

        /// <summary>
        /// Push one finger's state through the Input System.
        ///
        /// <c>delta</c> is supplied rather than left to the device to work out: the
        /// touch delta is normally computed from the previous frame's position by the
        /// backend that owns the hardware, and a synthesised device has none, so the
        /// pinch and orbit gestures would read every move as zero movement.
        /// </summary>
        private void Finger(Touchscreen screen, int id, Vector2 pos, TouchPhase phase)
        {
            _fingerAt.TryGetValue(id, out var last);
            var state = new TouchState
            {
                touchId = id,
                phase = phase,
                position = pos,
                delta = phase == TouchPhase.Began ? Vector2.zero : pos - last,
            };
            _fingerAt[id] = pos;
            if (phase == TouchPhase.Ended) _fingerAt.Remove(id);

            InputSystem.QueueStateEvent(screen, state);
        }

        private readonly Dictionary<int, Vector2> _fingerAt = new();

        /// <summary>
        /// Flush queued touches and let one frame run.
        ///
        /// Separate from <see cref="Finger"/> so a two-finger gesture can be assembled
        /// before any frame sees it. Advancing between the two Begans leaves a frame in
        /// which exactly one finger is down — which is a paint stroke, correctly — and
        /// the first version of the multi-touch test read that as the guard failing.
        /// </summary>
        private static IEnumerator Step(int frames = 1)
        {
            InputSystem.Update();
            for (int i = 0; i < frames; i++) yield return null;
        }

        /// <summary>Let real time pass, for anything that eases rather than snapping.</summary>
        private static IEnumerator Settle(float seconds)
        {
            for (float t = 0f; t < seconds; t += Time.unscaledDeltaTime) yield return null;
        }

        /// <summary>
        /// By GameObject name, not by the text on the face. The compact layout
        /// shortens "Pause Sim" to "Pause" and drops the tool labels entirely, so
        /// matching on what a button says finds nothing the moment the window is
        /// narrow — which is how this test started failing on a hidpi screen.
        /// </summary>
        private static Button FindButton(Button[] all, string id)
        {
            foreach (var b in all) if (b.name == "Btn_" + id) return b;
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
