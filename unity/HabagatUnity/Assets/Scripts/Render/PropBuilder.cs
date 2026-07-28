using System.Collections.Generic;
using UnityEngine;

namespace Habagat.Render
{
    /// <summary>
    /// The barangay's material palette, ported from the reusable materials at the
    /// top of section 14 in ThreeCanvas.jsx.
    ///
    /// These are sRGB hex literals and they are converted to linear here. That
    /// conversion is NOT optional and it is not what the terrain palette does:
    /// <see cref="TerrainPalette"/> carries float triples that three.js already
    /// treats as working-space (linear) values, so they cross over untouched. But
    /// <c>new MeshStandardMaterial({ color: 0x9a6b45 })</c> goes through
    /// <c>setHex(..., SRGBColorSpace)</c>, so three.js converts it and we have to
    /// as well. Skipping it makes every prop noticeably too bright and chalky —
    /// mid-tones drift most, which is where all of this wood and thatch lives.
    /// </summary>
    public static class PropPalette
    {
        private static float S2L(float c) =>
            c <= 0.04045f ? c / 12.92f : Mathf.Pow((c + 0.055f) / 1.055f, 2.4f);

        public static Color Hex(int hex)
        {
            float r = ((hex >> 16) & 0xff) / 255f;
            float g = ((hex >> 8) & 0xff) / 255f;
            float b = (hex & 0xff) / 255f;
            return new Color(S2L(r), S2L(g), S2L(b), 0f);
        }

        public static readonly Color Bamboo   = Hex(0x9a6b45);
        public static readonly Color Nipa     = Hex(0xc49a45);
        public static readonly Color NipaDark = Hex(0x9d7530);
        public static readonly Color Sawali   = Hex(0xe6cd9c);
        public static readonly Color Trim     = Hex(0xf7f3e8);
        public static readonly Color Concrete = Hex(0xbfb8ab);
        public static readonly Color Rust     = Hex(0x9c5b3c);
        public static readonly Color Tarp     = Hex(0x2f7fb5);
        public static readonly Color Wood     = Hex(0x5c4033);
        public static readonly Color PalmLeaf = Hex(0x477a3d);
        public static readonly Color LeafMid  = Hex(0x3d6b34);
        public static readonly Color LeafLight= Hex(0x6a9b4a);
        public static readonly Color Banana   = Hex(0x5c9440);
        public static readonly Color Rock     = Hex(0x8d8b82);

        /// <summary>
        /// Windows. Dark and slightly emissive so they still read as windows once
        /// fog and distance have flattened everything else — see
        /// <see cref="PropBuilder.Add"/> for how the emissive term is carried.
        /// </summary>
        public static readonly Color Glass = Hex(0x2c3f4d);
        public const float GlassEmissive = 0.35f;

        public static readonly Color[] RoofColors =
        {
            Hex(0xd9442b), Hex(0x4a90e2), Hex(0x5b8c3a), Hex(0xe07a5f),
            Hex(0x9b59b6), Hex(0xe8a33d), Hex(0x3f8f86),
        };

        public static readonly Color[] WallColors =
        {
            Hex(0xf4f1de), Hex(0xe0e1dd), Hex(0xfceade), Hex(0xe2ece9),
            Hex(0xfff1e6), Hex(0xf6e7c9), Hex(0xe8eef3),
        };

        public static readonly Color[] ClothColors =
        {
            Hex(0xe8556d), Hex(0x49a6e0), Hex(0xf2c14e), Hex(0xf7f3e8), Hex(0x6cc070),
        };
    }

    /// <summary>
    /// Accumulates transformed primitives into a single mesh, baking each part's
    /// colour into vertex colours.
    ///
    /// This diverges from the web build on purpose, and it comes out simpler. There,
    /// <c>flattenProp</c> groups a prop's parts BY MATERIAL and produces a
    /// multi-material geometry — a nipa hut costs four draw calls because it uses
    /// four materials. Here the ground shader is already vertex-colour driven, so a
    /// part's colour can travel in the vertex stream instead: every prop becomes one
    /// mesh with one material, and the whole barangay draws in one call per KIND
    /// however many materials went into it.
    ///
    /// The transform stack mirrors nesting a THREE.Group — the palm's crown is
    /// positioned once and its fronds are placed relative to it, exactly as in the
    /// original.
    /// </summary>
    public sealed class PropBuilder
    {
        private readonly List<Vector3> _v = new();
        private readonly List<Vector3> _n = new();
        private readonly List<Color> _c = new();
        private readonly List<int> _t = new();
        private readonly Stack<Matrix4x4> _stack = new();
        private Matrix4x4 _cur = Matrix4x4.identity;

        public void Push(Vector3 pos, Quaternion rot, Vector3 scale)
        {
            _stack.Push(_cur);
            _cur = _cur * Matrix4x4.TRS(pos, rot, scale);
        }

        public void Push(Vector3 pos) => Push(pos, Quaternion.identity, Vector3.one);

        public void Pop() => _cur = _stack.Pop();

        /// <summary>
        /// Place one primitive. <paramref name="emissive"/> rides in the vertex
        /// alpha channel; the shader adds it back as unlit light, which is how the
        /// glass keeps glowing faintly at distance without needing its own material
        /// and therefore its own draw call.
        /// </summary>
        public void Add(MeshData m, Color color, Vector3 pos = default,
                        Quaternion rot = default, Vector3 scale = default, float emissive = 0f)
        {
            if (rot.x == 0f && rot.y == 0f && rot.z == 0f && rot.w == 0f) rot = Quaternion.identity;
            if (scale == default) scale = Vector3.one;

            Matrix4x4 mtx = _cur * Matrix4x4.TRS(pos, rot, scale);
            // Non-uniform scale is everywhere in this library — a bush blob is
            // squashed to 0.78 in Y, a palm frond to 0.3 in Z — and transforming a
            // normal by the model matrix under non-uniform scale skews it. The
            // inverse transpose is what keeps the lighting correct.
            Matrix4x4 nmtx = mtx.inverse.transpose;

            color.a = emissive;
            int b = _v.Count;
            for (int i = 0; i < m.Verts.Length; i++)
            {
                _v.Add(mtx.MultiplyPoint3x4(m.Verts[i]));
                _n.Add(nmtx.MultiplyVector(m.Normals[i]).normalized);
                _c.Add(color);
            }
            for (int i = 0; i < m.Tris.Length; i++) _t.Add(b + m.Tris[i]);
        }

        /// <summary>
        /// Finish the prop, mirroring it in Z on the way out.
        ///
        /// The map was reflected into Unity's left-handed space — see
        /// <see cref="TerrainMeshBuilder.Vz"/> — and <see cref="PropScatter"/>
        /// reflects each prop's POSITION and yaw to match. That is not enough on its
        /// own: a reflection has to be applied to the prop's own geometry too, or
        /// every building keeps its original chirality and ends up presenting its
        /// back to the camera. The barangay hall showed this plainly — portico,
        /// columns, steps and doors were all on the far side, leaving a blank dark
        /// roof facing the default isometric view.
        ///
        /// Mirroring inverts triangle orientation, so the winding is reversed and
        /// the normals' Z negated to go with it.
        /// </summary>
        public Mesh Build(string name)
        {
            for (int i = 0; i < _v.Count; i++)
            {
                _v[i] = new Vector3(_v[i].x, _v[i].y, -_v[i].z);
                _n[i] = new Vector3(_n[i].x, _n[i].y, -_n[i].z);
            }
            for (int i = 0; i < _t.Count; i += 3) (_t[i + 1], _t[i + 2]) = (_t[i + 2], _t[i + 1]);

            var mesh = new Mesh { name = name };
            if (_v.Count > 65535) mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            mesh.SetVertices(_v);
            mesh.SetNormals(_n);
            mesh.SetColors(_c);
            mesh.SetTriangles(_t, 0);
            mesh.RecalculateBounds();
            return mesh;
        }
    }
}
