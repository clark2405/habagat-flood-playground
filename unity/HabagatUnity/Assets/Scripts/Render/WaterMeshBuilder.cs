using System;
using UnityEngine;

namespace Habagat.Render
{
    /// <summary>
    /// The flood surface. Ported from the water half of the render loop in
    /// ThreeCanvas.jsx, including the three-stage surface solve that turns the
    /// simulation's per-cell water column into something that reads as standing
    /// water rather than as a staircase of blocks.
    /// </summary>
    public class WaterMeshBuilder
    {
        public const int W = FloodSim.W;
        public const int H = FloodSim.H;
        private const float DX = TerrainMeshBuilder.DX;
        private const float DZ = TerrainMeshBuilder.DZ;
        private const double SeaLevel = FloodSim.SeaLevel;

        private const double NoWater = -1e30;

        private readonly Vector3[] _verts = new Vector3[W * H];
        private readonly Vector3[] _normals = new Vector3[W * H];
        private readonly Color[] _colors = new Color[W * H];
        private readonly double[] _tint = new double[W * H];
        private readonly double[] _surf = new double[W * H];
        private readonly double[] _tmp = new double[W * H];
        private Mesh _mesh;

        /// <summary>
        /// The RELAXED water surface, one entry per cell, or <c>NoWater</c>. The
        /// surrounding world reads its border level from here rather than from raw
        /// water depth, because that is the only way the two sheets can agree
        /// exactly where they meet.
        /// </summary>
        public double[] Surface => _surf;

        public const double NoWaterLevel = NoWater;

        public Mesh Mesh => _mesh;

        public WaterMeshBuilder()
        {
            for (int row = 0; row < H; row++)
                for (int col = 0; col < W; col++)
                    _tint[row * W + col] = TerrainColors.GroundTint(
                        TerrainMeshBuilder.Vx(col), TerrainMeshBuilder.Vz(row));

            // The sheet is flat-normalled: it is lit mostly by the sky, and
            // per-vertex normals from a rippling surface add nothing but noise at
            // this scale.
            for (int i = 0; i < W * H; i++) _normals[i] = Vector3.up;
        }

        /// <summary>
        /// Water-surface height of a cell, or NoWater if it holds none.
        ///
        /// A submerged cell normally sits exactly at sea level, but it must be
        /// allowed to RISE when flood water is piled on it — pinning it meant rain
        /// standing on the shore had nowhere to spill, so flooded land ended in a
        /// cell-high wall of water against a flat sea. The cap on land is only a
        /// guard against a runaway column.
        /// </summary>
        private static double SurfaceAt(float[] elev, float[] water, int n)
        {
            double en = elev[n], wn = water[n];
            if (en <= SeaLevel) return Math.Max(SeaLevel, en + wn);
            return wn > 0.015 ? Math.Min(en + wn, en + 3.2) : NoWater;
        }

        private void SolveSurface(FloodSim sim)
        {
            var elev = sim.Elev;
            var water = sim.Water;

            for (int i = 0; i < W * H; i++) _surf[i] = SurfaceAt(elev, water, i);

            // Push the surface a couple of cells INTO the surrounding dry land, so
            // the sheet reaches under the bank and the opaque terrain decides where
            // the waterline falls. Without this the water mesh's own cell boundary
            // decides it, which is what made flood edges look stair-stepped.
            for (int pass = 0; pass < 2; pass++)
            {
                Array.Copy(_surf, _tmp, _surf.Length);
                for (int y = 0; y < H; y++)
                {
                    for (int x = 0; x < W; x++)
                    {
                        int i = y * W + x;
                        if (_tmp[i] > NoWater) continue;
                        double m = NoWater;
                        if (x > 0) m = Math.Max(m, _tmp[i - 1]);
                        if (x < W - 1) m = Math.Max(m, _tmp[i + 1]);
                        if (y > 0) m = Math.Max(m, _tmp[i - W]);
                        if (y < H - 1) m = Math.Max(m, _tmp[i + W]);
                        _surf[i] = m;
                    }
                }
            }

            // How deep the rain is lying on land, which sets how hard to level.
            double landSum = 0; int landCells = 0;
            for (int i = 0; i < W * H; i++)
                if (elev[i] > SeaLevel) { landCells++; landSum += water[i]; }
            double bgDepth = landCells > 0 ? landSum / landCells : 0;

            // Relax toward level with an EDGE-PRESERVING average: neighbours only
            // count if their surface is within the threshold. Adjacent cells of one
            // pool settle into a single sheet — the sim leaves a cell-wide staircase
            // that renders as blocky terraces — while a genuine step, water held
            // behind a dike, is left standing, because that is gameplay not noise.
            // The threshold scales with how flooded things are: in a deep flood the
            // sim saturates every cell at its rain cap and the surface drapes over
            // the terrain in plateaus, needing levelling hard; a puddle keeps detail.
            double relaxThresh = 0.5 + 1.7 * Math.Min(1, bgDepth);
            for (int pass = 0; pass < 8; pass++)
            {
                Array.Copy(_surf, _tmp, _surf.Length);
                for (int y = 0; y < H; y++)
                {
                    for (int x = 0; x < W; x++)
                    {
                        int i = y * W + x;
                        double c = _tmp[i];
                        if (c <= NoWater) continue;
                        double sum = c; int n = 1;
                        void Take(int m)
                        {
                            double v = _tmp[m];
                            if (v > NoWater && Math.Abs(v - c) < relaxThresh) { sum += v; n++; }
                        }
                        if (x > 0) Take(i - 1);
                        if (x < W - 1) Take(i + 1);
                        if (y > 0) Take(i - W);
                        if (y < H - 1) Take(i + W);
                        // Open water may be lifted by a flood spilling in, never dragged below.
                        _surf[i] = Math.Max(sum / n, elev[i] <= SeaLevel ? SeaLevel : NoWater);
                    }
                }
            }
        }

        /// <summary>Shared world swell — the same expression the open water outside uses.</summary>
        public static double SwellAt(double wx, double wz, double t) =>
            Math.Sin(wx * 0.085 + t * 1.25) * 0.55 +
            Math.Sin(wz * 0.061 - t * 0.92) * 0.36 +
            Math.Sin((wx + wz) * 0.155 + t * 2.05) * 0.2;

        public Mesh Build(FloodSim sim, in TerrainPalette palette, double time = 0, double swellAmp = 0.035)
        {
            _mesh = new Mesh { name = "HabagatWater" };

            var tris = new int[(W - 1) * (H - 1) * 6];
            int t = 0;
            for (int row = 0; row < H - 1; row++)
            {
                for (int col = 0; col < W - 1; col++)
                {
                    // Same reversed winding as the terrain — Vz mirrors Z.
                    int a = row * W + col, b = a + 1, c = a + W, d = c + 1;
                    tris[t++] = a; tris[t++] = b; tris[t++] = c;
                    tris[t++] = b; tris[t++] = d; tris[t++] = c;
                }
            }

            UpdateGeometry(sim, palette, time, swellAmp);
            _mesh.triangles = tris;
            _mesh.normals = _normals;
            _mesh.RecalculateBounds();
            return _mesh;
        }

        public void UpdateGeometry(FloodSim sim, in TerrainPalette palette, double time, double swellAmp)
        {
            SolveSurface(sim);
            var elev = sim.Elev;
            var wS = palette.WaterShallow;
            var wD = palette.WaterDeep;

            for (int row = 0; row < H; row++)
            {
                for (int col = 0; col < W; col++)
                {
                    int i = row * W + col;
                    double e = elev[i];
                    double surf = _surf[i];
                    float wx = TerrainMeshBuilder.Vx(col), wz = TerrainMeshBuilder.Vz(row);

                    if (surf <= NoWater)
                    {
                        // Well away from any water: lay the sheet ON the ground at
                        // zero alpha. Dropping it under instead left the quads
                        // bridging to the dilated cells tilted and faintly visible —
                        // pale wedges poking out of slopes along every flood edge.
                        _verts[i] = new Vector3(wx, (float)(e - 0.04), wz);
                        _colors[i] = new Color(wS.R, wS.G, wS.B, 0f);
                        continue;
                    }

                    double depth = surf - e;
                    // Swell scaled down in shallow water so a puddle does not slosh
                    // through the ground.
                    double shal = Math.Min(1, Math.Max(depth, 0) / 0.8);
                    _verts[i] = new Vector3(wx, (float)(surf + SwellAt(wx, wz, time) * swellAmp * shal), wz);

                    // Dither the depth ramp with the same world-space noise the
                    // ground uses. Over a gently shelving bed this gradient covers a
                    // lot of screen, and 8-bit output quantises it into contour bands
                    // that the grid's diagonal split serrates into terraces — it
                    // reads as a staircase even though the surface is dead level.
                    double k = Math.Min(1, Math.Max(0, TerrainColors.Smooth(0.02, 1.7, depth) + _tint[i] * 1.1));
                    _colors[i] = new Color(
                        (float)(wS.R + (wD.R - wS.R) * k),
                        (float)(wS.G + (wD.G - wS.G) * k),
                        (float)(wS.B + (wD.B - wS.B) * k),
                        (float)(0.25 + 0.7 * k));
                }
            }

            _mesh.vertices = _verts;
            _mesh.colors = _colors;
        }
    }
}
