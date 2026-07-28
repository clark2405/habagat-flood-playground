using UnityEngine;

namespace Habagat.Render
{
    /// <summary>
    /// Falling rain, ported from section 11 of ThreeCanvas.jsx.
    ///
    /// Rain has to fall on the WHOLE world, not just the play area. Confining it to
    /// a box around the sandbox meant that during a downpour the rain itself drew a
    /// rectangle around the border — the one thing the outerland exists to prevent.
    /// Hence the 300-unit span, far wider than the 96x64 map.
    ///
    /// One mesh with <see cref="MeshTopology.Lines"/>, matching the reference's
    /// THREE.LineSegments: 9000 drops as separate objects would be absurd, and a
    /// particle system would not give the same taut streak. Drops beyond the current
    /// intensity are parked far below the ground rather than removed, so the buffer
    /// never has to be resized.
    /// </summary>
    public class RainSystem
    {
        private const int Count = 9000;
        private const float Span = 300f;
        private const float Top = 70f;
        private const float Parked = -999f;

        private readonly Vector3[] _verts = new Vector3[Count * 2];
        private readonly Mesh _mesh;
        private readonly MeshRenderer _renderer;
        private readonly GameObject _go;

        public RainSystem(Transform parent)
        {
            for (int i = 0; i < Count; i++)
            {
                float rx = (Random.value - 0.5f) * Span;
                float ry = Random.value * Top;
                float rz = (Random.value - 0.5f) * Span;
                _verts[i * 2] = new Vector3(rx, ry, rz);
                _verts[i * 2 + 1] = new Vector3(rx - 0.4f, ry - 1.6f, rz);
            }

            var idx = new int[Count * 2];
            for (int i = 0; i < idx.Length; i++) idx[i] = i;

            _mesh = new Mesh
            {
                name = "Rain",
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
            };
            _mesh.SetVertices(_verts);
            _mesh.SetIndices(idx, MeshTopology.Lines, 0);
            // The drops move every frame and the mesh is world-sized anyway, so a
            // recomputed bounding volume would only ever cause pointless culling work.
            _mesh.bounds = new Bounds(Vector3.zero, new Vector3(Span * 2, Top * 4, Span * 2));

            _go = new GameObject("Rain");
            _go.transform.SetParent(parent, false);
            _go.AddComponent<MeshFilter>().sharedMesh = _mesh;
            _renderer = _go.AddComponent<MeshRenderer>();
            _renderer.sharedMaterial = new Material(Shader.Find("Habagat/RainLine"));
            _renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _renderer.receiveShadows = false;
            _go.SetActive(false);
        }

        /// <summary><paramref name="rain"/> is the 0-10 intensity the UI exposes.</summary>
        public void Update(float rain, bool storm)
        {
            bool hasRain = storm || rain > 0.05f;
            if (_go.activeSelf != hasRain) _go.SetActive(hasRain);
            if (!hasRain) return;

            float norm = Mathf.Clamp01(rain / 10f);
            float fall = storm ? 1.9f : 0.9f + norm * 1.1f;
            float streak = storm ? 2.6f : 1.2f + norm * 1.0f;
            float slant = storm ? 1.1f : 0.3f;
            int active = Mathf.CeilToInt(Count * (storm ? 1f : 0.15f + 0.85f * norm));

            for (int i = 0; i < Count; i++)
            {
                int a = i * 2, b = a + 1;
                if (i >= active)
                {
                    _verts[a].y = Parked;
                    _verts[b].y = Parked;
                    continue;
                }

                _verts[a].y -= fall;
                _verts[b].y -= fall;

                // The second test catches drops that were parked and are now active
                // again, which would otherwise fall forever from -999.
                if (_verts[a].y < -8f || _verts[a].y < -900f)
                {
                    float rx = (Random.value - 0.5f) * Span;
                    float ry = Top * (0.55f + Random.value * 0.45f);
                    float rz = (Random.value - 0.5f) * Span;
                    _verts[a] = new Vector3(rx, ry, rz);
                    _verts[b] = new Vector3(rx - slant, ry - streak, rz);
                }
            }
            _mesh.SetVertices(_verts);
        }
    }
}
