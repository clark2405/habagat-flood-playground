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
        private Transform _cursor;

        private void Awake()
        {
            _world = GetComponent<HabagatWorld>();
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

        private void Update()
        {
            var mouse = Mouse.current;
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

            if (brush == Brush.None || cam == null || mouse == null || _world.World == null)
            {
                if (_cursor.gameObject.activeSelf) _cursor.gameObject.SetActive(false);
                return;
            }

            // A drag that starts on the tool palette must not also dig a hole in the
            // ground behind it.
            if (EventSystem.current != null && EventSystem.current.IsPointerOverGameObject())
            {
                if (_cursor.gameObject.activeSelf) _cursor.gameObject.SetActive(false);
                return;
            }

            var ray = cam.ScreenPointToRay(mouse.position.ReadValue());
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

            if (mouse.leftButton.isPressed) Paint(gx, gy);
        }

        private void Paint(int gx, int gy)
        {
            // Homes are placed, not painted: one per cell, and the whole scatter has
            // to be regenerated around them, so this never goes through FloodSim.Paint.
            if (brush == Brush.House)
            {
                _world.AddHouse(gx, gy);
                return;
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
