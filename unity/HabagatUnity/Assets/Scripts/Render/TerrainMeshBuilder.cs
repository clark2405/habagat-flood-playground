using UnityEngine;

namespace Habagat.Render
{
    /// <summary>
    /// Builds the play-area terrain mesh from the simulation's heightmap.
    ///
    /// Mirrors the geometry the web build produces in ThreeCanvas.jsx: one vertex
    /// per grid cell, one world unit per cell, centred on the origin, with vertex
    /// colours carrying the ground ramp.
    /// </summary>
    public class TerrainMeshBuilder
    {
        public const int W = FloodSim.W;
        public const int H = FloodSim.H;

        // World-space spacing between heightmap columns and rows. Note this is
        // W/(W-1), not 1: the mesh spans exactly W units across W-1 quads, which is
        // what keeps it flush with the surrounding world at the border.
        public const float DX = (float)W / (W - 1);
        public const float DZ = (float)H / (H - 1);

        private readonly Vector3[] _verts = new Vector3[W * H];
        private readonly Vector3[] _normals = new Vector3[W * H];
        private readonly Color[] _colors = new Color[W * H];
        private readonly double[] _tint = new double[W * H];
        private Mesh _mesh;

        public Mesh Mesh => _mesh;

        public static float Vx(int col) => -W / 2f + col * DX;

        // Z is NEGATED relative to the web build's `-H/2 + row*DZ`. three.js is
        // right-handed and Unity is left-handed, so laying the grid out identically
        // produces a mirror image of the same map — the coast ends up on the wrong
        // side. Flipping here keeps world space agreeing with the reference
        // implementation, which matters as soon as seeded prop placement arrives:
        // otherwise every scattered object lands mirrored too.
        public static float Vz(int row) => H / 2f - row * DZ;

        public TerrainMeshBuilder()
        {
            // The world-space tint is fixed geometry, so it is sampled once rather
            // than every time the heightmap changes.
            for (int row = 0; row < H; row++)
                for (int col = 0; col < W; col++)
                    _tint[row * W + col] = TerrainColors.GroundTint(Vx(col), Vz(row));
        }

        public Mesh Build(float[] elev, in TerrainPalette palette)
        {
            _mesh = new Mesh { name = "HabagatTerrain" };

            for (int row = 0; row < H; row++)
                for (int col = 0; col < W; col++)
                    _verts[row * W + col] = new Vector3(Vx(col), elev[row * W + col], Vz(row));

            var tris = new int[(W - 1) * (H - 1) * 6];
            int t = 0;
            for (int row = 0; row < H - 1; row++)
            {
                for (int col = 0; col < W - 1; col++)
                {
                    int a = row * W + col;
                    int b = a + 1;
                    int c = a + W;
                    int d = c + 1;
                    // Winding is reversed from the obvious order because Vz mirrors
                    // the Z axis; mirroring one axis flips triangle orientation, and
                    // without swapping these the whole surface faces downward.
                    tris[t++] = a; tris[t++] = b; tris[t++] = c;
                    tris[t++] = b; tris[t++] = d; tris[t++] = c;
                }
            }

            _mesh.vertices = _verts;
            _mesh.triangles = tris;
            UpdateShading(elev, palette);
            _mesh.RecalculateBounds();
            return _mesh;
        }

        /// <summary>Re-upload positions, colours and normals after the sim moves the ground.</summary>
        public void UpdateHeights(float[] elev, in TerrainPalette palette)
        {
            for (int row = 0; row < H; row++)
                for (int col = 0; col < W; col++)
                    _verts[row * W + col].y = elev[row * W + col];
            _mesh.vertices = _verts;
            UpdateShading(elev, palette);
            _mesh.RecalculateBounds();
        }

        private void UpdateShading(float[] elev, in TerrainPalette palette)
        {
            for (int row = 0; row < H; row++)
            {
                for (int col = 0; col < W; col++)
                {
                    int i = row * W + col;
                    var c = TerrainColors.ColorFor(elev[i], palette);
                    double tn = _tint[i];
                    // The tint is applied with descending weight per channel, which
                    // warms the lighter patches slightly instead of just brightening
                    // them uniformly.
                    _colors[i] = new Color(
                        (float)(c.R + tn),
                        (float)(c.G + tn * 0.92),
                        (float)(c.B + tn * 0.78),
                        1f);
                }
            }

            // Normals come from the heightmap's analytic central differences, NOT
            // from RecalculateNormals(). Averaging the triangles around a vertex is
            // biased by the grid's fixed diagonal split, and that bias alternates
            // cell to cell — which corrugates smooth slopes into light/dark ribs,
            // very visible on riverbanks. An analytic gradient has no such bias and
            // is cheaper. This is the same reason the web build avoids
            // computeVertexNormals().
            for (int row = 0; row < H; row++)
            {
                for (int col = 0; col < W; col++)
                {
                    int xm = col > 0 ? col - 1 : col, xp = col < W - 1 ? col + 1 : col;
                    int ym = row > 0 ? row - 1 : row, yp = row < H - 1 ? row + 1 : row;
                    float gx = -(elev[row * W + xp] - elev[row * W + xm]) / ((xp - xm) * DX);
                    // No leading minus: world Z decreases as `row` increases (see Vz),
                    // so the sign of this gradient is already inverted relative to the
                    // web build's expression.
                    float gz = (elev[yp * W + col] - elev[ym * W + col]) / ((yp - ym) * DZ);
                    float inv = 1f / Mathf.Sqrt(gx * gx + 1f + gz * gz);
                    _normals[row * W + col] = new Vector3(gx * inv, inv, gz * inv);
                }
            }

            _mesh.colors = _colors;
            _mesh.normals = _normals;
        }
    }
}
