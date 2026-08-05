using Habagat.Audio;
using Habagat.Render;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem;

namespace Habagat
{
    /// <summary>
    /// The brush: raycast onto the terrain, show a cursor ring where it lands, and
    /// stamp the simulation while the mouse is held.
    ///
    /// The tools themselves are already ported and fingerprint-verified, so this is
    /// only the input plumbing — plus deciding what has to be rebuilt afterwards,
    /// which is the part that is easy to get wrong.
    /// </summary>
    [RequireComponent(typeof(HabagatWorld))]
    public class PaintController : MonoBehaviour
    {
        /// <summary>
        /// <see cref="None"/> is the reference's "Pan &amp; Orbit" mode. It is not a
        /// no-op tool: while it is selected the camera takes the left drag, and no
        /// paint happens at all.
        /// </summary>
        public enum Brush { None, Raise, Lower, Water, Mangrove, DrainPump, Clear, House }

        public Brush brush = Brush.None;
        public Camera cam;
        public OrbitCamera orbit;

        private HabagatWorld _world;
        private SoundEngine _sound;
        private Transform _cursor;

        private void Awake()
        {
            _world = GetComponent<HabagatWorld>();
            // Optional throughout: the screenshot harness adds this component without
            // a SoundEngine beside it, and a missing cue must not stop the brush.
            _sound = GetComponent<SoundEngine>();
            if (cam == null) cam = Camera.main;
            if (orbit == null && cam != null) orbit = cam.GetComponent<OrbitCamera>();

            var go = new GameObject("BrushCursor");
            go.transform.SetParent(transform, false);
            var data = Prim.Ring(1.2f, 1.6f, 24);
            var mesh = new Mesh { name = "BrushRing" };
            mesh.SetVertices(data.Verts);
            mesh.SetNormals(data.Normals);
            mesh.SetTriangles(data.Tris, 0);
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            var mat = new Material(Shader.Find("Habagat/RainLine"));
            mat.SetColor("_Color", WorldBuilder.Hex(0xd9442b));
            mat.SetFloat("_Opacity", 0.85f);
            r.sharedMaterial = mat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
            _cursor = go.transform;
            _cursor.gameObject.SetActive(false);
        }

        /// <summary>
        /// Where the player is pointing, from whichever device is actually being used.
        ///
        /// Touch wins over the mouse whenever a finger is down, rather than the project
        /// picking one device at build time: a Windows laptop with a touchscreen has
        /// both, and the deliberate act is the one being performed right now.
        ///
        /// A finger that is not touching the glass has no position at all, so the touch
        /// branch reports nothing unless one is down. On a phone there is no mouse to
        /// fall through to and the whole call fails, which is what hides the brush
        /// ring: parked at the last place tapped it reads as a stuck selection.
        /// </summary>
        private readonly struct Pointer
        {
            public readonly Vector2 Position;
            public readonly bool Pressed, Multi;
            public readonly int Id;

            public Pointer(Vector2 position, bool pressed, bool multi, int id)
            {
                Position = position; Pressed = pressed; Multi = multi; Id = id;
            }
        }

        /// <summary>uGUI's own id for the left mouse button; touches use their touchId.</summary>
        private const int MouseLeftPointerId = -1;

        private static bool TryPointer(out Pointer p)
        {
            var touch = Touchscreen.current;
            if (touch != null && touch.primaryTouch.press.isPressed)
            {
                // Two fingers is the camera's gesture — pan and pinch — so it must not
                // also stamp the ground under whichever finger happens to be first.
                int active = 0;
                foreach (var t in touch.touches) if (t.press.isPressed) active++;

                p = new Pointer(touch.primaryTouch.position.ReadValue(), true,
                                active > 1, touch.primaryTouch.touchId.ReadValue());
                return true;
            }

            var mouse = Mouse.current;
            if (mouse != null)
            {
                p = new Pointer(mouse.position.ReadValue(), mouse.leftButton.isPressed,
                                false, MouseLeftPointerId);
                return true;
            }

            p = default;
            return false;
        }

        private void Update()
        {
            var keys = Keyboard.current;

            // Number keys also pick a tool, which is handy while testing.
            if (keys != null)
            {
                if (keys.digit0Key.wasPressedThisFrame) brush = Brush.None;
                if (keys.digit1Key.wasPressedThisFrame) brush = Brush.Raise;
                if (keys.digit2Key.wasPressedThisFrame) brush = Brush.Lower;
                if (keys.digit3Key.wasPressedThisFrame) brush = Brush.Water;
                if (keys.digit4Key.wasPressedThisFrame) brush = Brush.Mangrove;
                if (keys.digit5Key.wasPressedThisFrame) brush = Brush.DrainPump;
                if (keys.digit6Key.wasPressedThisFrame) brush = Brush.Clear;
                if (keys.digit7Key.wasPressedThisFrame) brush = Brush.House;
            }

            // The camera and the brush both want the left drag, so exactly one of
            // them may have it.
            if (orbit != null) orbit.orbitEnabled = brush == Brush.None;

            if (brush == Brush.None || cam == null || _world.World == null ||
                !TryPointer(out var pointer) || pointer.Multi)
            {
                if (_cursor.gameObject.activeSelf) _cursor.gameObject.SetActive(false);
                return;
            }

            // A drag that starts on the tool palette must not also dig a hole in the
            // ground behind it. The id matters: the no-argument overload asks about
            // the last pointer uGUI processed, which on touch is not necessarily the
            // finger being read here.
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject(pointer.Id))
            {
                if (_cursor.gameObject.activeSelf) _cursor.gameObject.SetActive(false);
                return;
            }

            var ray = cam.ScreenPointToRay(pointer.Position);
            if (!Physics.Raycast(ray, out var hit, 2000f))
            {
                if (_cursor.gameObject.activeSelf) _cursor.gameObject.SetActive(false);
                return;
            }

            if (!_cursor.gameObject.activeSelf) _cursor.gameObject.SetActive(true);
            _cursor.position = hit.point + Vector3.up * 0.08f;

            // Z is negated on the way back into grid space, for the same reason it is
            // negated on the way out: the map was mirrored into left-handed space, so
            // reading a cell index straight off the Unity coordinate paints the wrong
            // side of the river from the one under the cursor.
            int gx = Mathf.FloorToInt(hit.point.x + FloodSim.W / 2f);
            int gy = Mathf.FloorToInt(-hit.point.z + FloodSim.H / 2f);
            if (gx < 0 || gx >= FloodSim.W || gy < 0 || gy >= FloodSim.H) return;

            if (pointer.Pressed) Paint(gx, gy);
        }

        private void Paint(int gx, int gy)
        {
            // Homes are placed, not painted: one per cell, and the whole scatter has
            // to be regenerated around them, so this never goes through FloodSim.Paint.
            if (brush == Brush.House)
            {
                // Only on the ones that actually go up: AddHouse refuses water and
                // refuses to crowd, and a cue on every refused frame of a held drag
                // would turn a rejection into a machine-gun.
                if (_world.AddHouse(gx, gy)) _sound?.PlayBuild();
                return;
            }

            if (_sound != null)
                switch (brush)
                {
                    case Brush.Raise: _sound.PlayTerraform(true); break;
                    case Brush.Lower: _sound.PlayTerraform(false); break;
                    case Brush.Water: _sound.PlayWaterSplash(); break;
                    case Brush.Mangrove: _sound.PlayPlant(); break;
                    case Brush.DrainPump: _sound.PlayBuild(); break;
                    // Clear is silent in the reference too.
                }

            var tool = brush switch
            {
                Brush.Raise => FloodSim.Tool.Raise,
                Brush.Lower => FloodSim.Tool.Lower,
                Brush.Water => FloodSim.Tool.Water,
                Brush.Mangrove => FloodSim.Tool.Mangrove,
                Brush.DrainPump => FloodSim.Tool.DrainPump,
                _ => FloodSim.Tool.Clear,
            };
            _world.Sim.Paint(tool, gx, gy);

            // Only re-mesh what actually changed. Terrain is ~6000 vertices and the
            // props rebake every instance of a kind, so doing both on every frame of
            // a drag would stall the brush.
            switch (brush)
            {
                case Brush.Raise:
                case Brush.Lower:
                    _world.World.RebuildTerrain(_world.Sim);
                    break;
                case Brush.Mangrove:
                case Brush.DrainPump:
                case Brush.Clear:
                    _world.World.RebuildSimProps(_world.Sim);
                    break;
                // Water needs nothing: the surface is already re-solved every frame.
            }
        }
    }
}
