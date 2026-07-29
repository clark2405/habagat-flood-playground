using UnityEngine;

namespace Habagat.Render
{
    /// <summary>
    /// The gradient sky, ported from section 4 of ThreeCanvas.jsx.
    ///
    /// A vertex-coloured sphere seen from the inside, three bands wide: horizon to
    /// mid to zenith. The bottom band IS the fog colour, and that identity is the
    /// whole point — terrain fading into fog meets a sky of exactly the colour it
    /// faded to, so there is no horizon line and the sandbox has no visible end.
    /// Painting it per-vertex rather than with a gradient texture keeps it on the
    /// same tone-mapping path as the fog, which is what holds the two in agreement.
    ///
    /// Anything beyond <see cref="Radius"/> is hidden behind the sky.
    /// </summary>
    public class SkyDome
    {
        public const float Radius = 450f;

        private readonly Mesh _mesh;
        private readonly Vector3[] _verts;
        private readonly Color[] _colors;

        public SkyDome(Transform parent)
        {
            var data = Prim.Sphere(Radius, 32, 24);
            _verts = data.Verts;
            _colors = new Color[_verts.Length];

            _mesh = new Mesh { name = "SkyDome", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            _mesh.SetVertices(_verts);
            _mesh.SetTriangles(data.Tris, 0);
            _mesh.SetColors(_colors);
            // The camera sits inside it, so a real bounding volume would let the dome
            // be frustum-culled the moment the camera pulled back.
            _mesh.bounds = new Bounds(Vector3.zero, Vector3.one * (Radius * 4f));

            var go = new GameObject("Sky");
            go.transform.SetParent(parent, false);
            go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = new Material(Shader.Find("Habagat/SkyDome"));
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows = false;
        }

        /// <summary>Repaint the three bands. Cheap enough to run every frame.</summary>
        public void Paint(Color horizon, Color mid, Color zenith)
        {
            for (int i = 0; i < _verts.Length; i++)
            {
                float yn = _verts[i].y / Radius; // -1 at the nadir, +1 at the zenith
                var c = Color.Lerp(horizon, mid, (float)TerrainColors.Smooth(0.10, 0.58, yn));
                _colors[i] = Color.Lerp(c, zenith, (float)TerrainColors.Smooth(0.42, 0.96, yn));
            }
            _mesh.SetColors(_colors);
        }
    }
}
