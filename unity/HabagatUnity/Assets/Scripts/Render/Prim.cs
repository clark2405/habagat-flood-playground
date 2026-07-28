using System.Collections.Generic;
using UnityEngine;

namespace Habagat.Render
{
    /// <summary>Raw triangle soup, before it is transformed into a prop.</summary>
    public struct MeshData
    {
        public Vector3[] Verts;
        public Vector3[] Normals;
        public int[] Tris;
    }

    /// <summary>
    /// Parametric primitives, matching three.js's geometry conventions.
    ///
    /// Unity's built-in primitives cannot stand in for these. It ships no cone at
    /// all, and its cylinder is a fixed 20-sided tube — whereas the entire look of
    /// this project comes from low segment counts chosen per prop: a hipped roof is
    /// <c>Cone(r, h, 4)</c>, a bamboo post is <c>Cylinder(.045,.045,.75, 4)</c>, a
    /// grass blade is a 3-sided sliver. Those numbers are the art direction, so the
    /// generator has to expose them.
    ///
    /// The angular convention is three.js's — <c>x = r·sin θ, z = r·cos θ</c>, so a
    /// 4-segment cone puts its base CORNERS on the axes and reads as a diamond from
    /// above. Every roof in the library then carries a <c>rotation.y = π/4</c> to
    /// square it up. Change the convention here and every roof in the barangay
    /// rotates 45°.
    /// </summary>
    public static class Prim
    {
        /// <summary>
        /// three.js's default Euler order is XYZ, which composes as Rx·Ry·Rz.
        /// Unity's <c>Quaternion.Euler</c> composes as Ry·Rx·Rz. For a prop that
        /// rotates about a single axis the two agree, but several here do not — a
        /// palm frond is set to <c>(π/2, -angle, 0.62)</c>, and under Unity's order
        /// the whole crown splays wrong. Ported rotations must go through this.
        /// </summary>
        public static Quaternion EulerXYZ(float x, float y, float z) =>
            Quaternion.AngleAxis(x * Mathf.Rad2Deg, Vector3.right) *
            Quaternion.AngleAxis(y * Mathf.Rad2Deg, Vector3.up) *
            Quaternion.AngleAxis(z * Mathf.Rad2Deg, Vector3.forward);

        // ── Triangle emission ────────────────────────────────────────────────
        // Every primitive below is convex, so "outward" is always knowable. Rather
        // than hand-reason each face's winding against Unity's left-handed rule —
        // which is exactly the sort of thing that silently produces an inside-out
        // prop that only shows up as a black smear in a screenshot — triangles are
        // emitted with a reference direction and flipped if they disagree with it.
        private static void Tri(List<Vector3> v, List<Vector3> n, List<int> t,
                                Vector3 a, Vector3 b, Vector3 c,
                                Vector3 na, Vector3 nb, Vector3 nc, Vector3 outward)
        {
            if (Vector3.Dot(Vector3.Cross(b - a, c - a), outward) < 0f)
            {
                (b, c) = (c, b);
                (nb, nc) = (nc, nb);
            }
            int i = v.Count;
            v.Add(a); v.Add(b); v.Add(c);
            n.Add(na); n.Add(nb); n.Add(nc);
            t.Add(i); t.Add(i + 1); t.Add(i + 2);
        }

        private static void Quad(List<Vector3> v, List<Vector3> n, List<int> t,
                                 Vector3 a, Vector3 b, Vector3 c, Vector3 d,
                                 Vector3 na, Vector3 nb, Vector3 nc, Vector3 nd, Vector3 outward)
        {
            Tri(v, n, t, a, b, c, na, nb, nc, outward);
            Tri(v, n, t, a, c, d, na, nc, nd, outward);
        }

        private static MeshData Pack(List<Vector3> v, List<Vector3> n, List<int> t) =>
            new MeshData { Verts = v.ToArray(), Normals = n.ToArray(), Tris = t.ToArray() };

        // ── Box ──────────────────────────────────────────────────────────────
        /// <summary>Centred box with hard face normals, like THREE.BoxGeometry.</summary>
        public static MeshData Box(float w, float h, float d)
        {
            var v = new List<Vector3>(24); var n = new List<Vector3>(24); var t = new List<int>(36);
            float x = w * 0.5f, y = h * 0.5f, z = d * 0.5f;

            void Face(Vector3 nrm, Vector3 a, Vector3 b, Vector3 c, Vector3 e) =>
                Quad(v, n, t, a, b, c, e, nrm, nrm, nrm, nrm, nrm);

            Face(Vector3.right,   new(x, -y, -z), new(x, -y, z), new(x, y, z), new(x, y, -z));
            Face(Vector3.left,    new(-x, -y, -z), new(-x, -y, z), new(-x, y, z), new(-x, y, -z));
            Face(Vector3.up,      new(-x, y, -z), new(-x, y, z), new(x, y, z), new(x, y, -z));
            Face(Vector3.down,    new(-x, -y, -z), new(-x, -y, z), new(x, -y, z), new(x, -y, -z));
            Face(Vector3.forward, new(-x, -y, z), new(x, -y, z), new(x, y, z), new(-x, y, z));
            Face(Vector3.back,    new(-x, -y, -z), new(x, -y, -z), new(x, y, -z), new(-x, y, -z));
            return Pack(v, n, t);
        }

        // ── Cylinder / cone ──────────────────────────────────────────────────
        /// <summary>
        /// THREE.CylinderGeometry(rTop, rBottom, h, seg). Centred on the origin,
        /// spanning -h/2..+h/2. Side normals are smooth around the ring and tilt
        /// with the taper, which is what keeps a 5-sided banana trunk from reading
        /// as a faceted crystal; the caps stay hard.
        /// </summary>
        public static MeshData Cylinder(float rTop, float rBottom, float h, int seg, bool caps = true)
        {
            var v = new List<Vector3>(); var n = new List<Vector3>(); var t = new List<int>();
            float y0 = -h * 0.5f, y1 = h * 0.5f;
            // The taper term: without it a cone's sides get horizontal normals and
            // light as though they were a tube.
            float slope = (rBottom - rTop) / Mathf.Max(h, 1e-6f);

            Vector3 Ring(float th, float r, float y) => new(Mathf.Sin(th) * r, y, Mathf.Cos(th) * r);
            Vector3 Nrm(float th) => new Vector3(Mathf.Sin(th), slope, Mathf.Cos(th)).normalized;

            for (int i = 0; i < seg; i++)
            {
                float t0 = i / (float)seg * Mathf.PI * 2f;
                float t1 = (i + 1) / (float)seg * Mathf.PI * 2f;
                Vector3 n0 = Nrm(t0), n1 = Nrm(t1);
                Vector3 bl = Ring(t0, rBottom, y0), br = Ring(t1, rBottom, y0);
                Vector3 tl = Ring(t0, rTop, y1), tr = Ring(t1, rTop, y1);
                Vector3 outward = (n0 + n1) * 0.5f;

                // A cone collapses its top ring to a point, so the quad degenerates
                // into a single triangle. Emitting it as a quad anyway leaves a
                // zero-area sliver that shows up as a shading seam at the apex.
                if (rTop <= 1e-6f) Tri(v, n, t, bl, br, tl, n0, n1, (n0 + n1).normalized, outward);
                else if (rBottom <= 1e-6f) Tri(v, n, t, tl, tr, bl, n0, n1, (n0 + n1).normalized, outward);
                else Quad(v, n, t, bl, br, tr, tl, n0, n1, n1, n0, outward);

                if (!caps) continue;
                if (rTop > 1e-6f)
                    Tri(v, n, t, new Vector3(0, y1, 0), tl, tr, Vector3.up, Vector3.up, Vector3.up, Vector3.up);
                if (rBottom > 1e-6f)
                    Tri(v, n, t, new Vector3(0, y0, 0), bl, br, Vector3.down, Vector3.down, Vector3.down, Vector3.down);
            }
            return Pack(v, n, t);
        }

        /// <summary>THREE.ConeGeometry(r, h, seg) — a cylinder with no top.</summary>
        public static MeshData Cone(float r, float h, int seg) => Cylinder(0f, r, h, seg);

        // ── Dodecahedron ─────────────────────────────────────────────────────
        // Vertex and face tables from three.js's DodecahedronGeometry. Used flat
        // shaded at detail 0 for rocks, bush blobs and coconuts: twelve pentagons
        // catch the light in a way a sphere cannot, and it is the shape that makes
        // a 0.3u lump read as a rock rather than a pebble-coloured dot.
        private static readonly float T = (1f + Mathf.Sqrt(5f)) / 2f;
        private static readonly float R = 1f / ((1f + Mathf.Sqrt(5f)) / 2f);

        private static readonly int[] DodecaTris =
        {
             3, 11,  7,   3,  7, 15,   3, 15, 13,
             7, 19, 17,   7, 17,  6,   7,  6, 15,
            17,  4,  8,  17,  8, 10,  17, 10,  6,
             8,  0, 16,   8, 16,  2,   8,  2, 10,
             0, 12,  1,   0,  1, 18,   0, 18, 16,
             6, 10,  2,   6,  2, 13,   6, 13, 15,
             2, 16, 18,   2, 18,  3,   2,  3, 13,
            18,  1,  9,  18,  9, 11,  18, 11,  3,
             4, 14, 12,   4, 12,  0,   4,  0,  8,
            11,  9,  5,  11,  5, 19,  11, 19,  7,
            19,  5, 14,  19, 14,  4,  19,  4, 17,
             1, 12, 14,   1, 14,  5,   1,  5,  9,
        };

        public static MeshData Dodecahedron(float radius) =>
            Polyhedron(DodecaPoints, DodecaTris, radius);

        // (±1,±1,±1) plus the three cyclic permutations of (0, ±1/φ, ±φ).
        private static readonly Vector3[] DodecaPoints = BuildDodecaPoints();

        private static Vector3[] BuildDodecaPoints()
        {
            float t = T, r = R;
            return new Vector3[]
            {
                new(-1, -1, -1), new(-1, -1,  1), new(-1,  1, -1), new(-1,  1,  1),
                new( 1, -1, -1), new( 1, -1,  1), new( 1,  1, -1), new( 1,  1,  1),
                new( 0, -r, -t), new( 0, -r,  t), new( 0,  r, -t), new( 0,  r,  t),
                new(-r, -t,  0), new(-r,  t,  0), new( r, -t,  0), new( r,  t,  0),
                new(-t,  0, -r), new( t,  0, -r), new(-t,  0,  r), new( t,  0,  r),
            };
        }

        /// <summary>Flat-shaded convex polyhedron: each face gets its own normal.</summary>
        private static MeshData Polyhedron(Vector3[] pts, int[] idx, float radius)
        {
            var v = new List<Vector3>(); var n = new List<Vector3>(); var t = new List<int>();
            for (int i = 0; i < idx.Length; i += 3)
            {
                Vector3 a = pts[idx[i]].normalized * radius;
                Vector3 b = pts[idx[i + 1]].normalized * radius;
                Vector3 c = pts[idx[i + 2]].normalized * radius;
                // Convex and origin-centred, so the face centroid points outward.
                Vector3 outward = (a + b + c).normalized;
                Tri(v, n, t, a, b, c, outward, outward, outward, outward);
            }
            return Pack(v, n, t);
        }
    }
}
