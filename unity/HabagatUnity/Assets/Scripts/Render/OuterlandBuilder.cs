using System;
using System.Collections.Generic;
using UnityEngine;

namespace Habagat.Render
{
    /// <summary>
    /// The world outside the sandbox. Ported from sections 9b and 9c of
    /// ThreeCanvas.jsx.
    ///
    /// The sandbox border is offset OUTWARD as a rounded rectangle in rings that
    /// grow geometrically — the first exactly one cell wide so the tessellation
    /// matches the play area's, then coarser as it recedes into the fog.
    ///
    /// Two things make the seam invisible:
    ///   • ring 0 sits on the sandbox's own border vertices at their exact
    ///     heights, and every displacement term is multiplied by a ramp that is
    ///     zero at t=0 — so the join is smooth rather than a welded crease;
    ///   • it uses the identical colour ramp as the sandbox.
    ///
    /// Offsetting along each border point's OUTWARD NORMAL, rather than radially
    /// from the centre, is what stops the result reading as a big straight-edged
    /// diamond: rings stay parallel to the edge they came from, corners round off,
    /// and a low-frequency warp makes the outline organic.
    /// </summary>
    public class OuterlandBuilder
    {
        private const int W = FloodSim.W;
        private const int H = FloodSim.H;
        private const double SeaLevel = FloodSim.SeaLevel;
        private const int CornerFan = 7;

        private struct BorderPt
        {
            public double X, Z;   // world position on the sandbox edge (double: see VxD)
            public float Nx, Nz;  // outward normal
            public int I;         // index into the heightmap
        }

        private BorderPt[] _border;
        // float, not double, and deliberately so. In the web build these are
        // Float32Array — `ringD` and `outBaseY` — so every value is narrowed on the
        // store and every later comparison sees the narrowed number. Keeping them as
        // doubles here is the exact mistake already paid for once in the simulation
        // port: invisible per-value, but the world dressing tests `d < 7`, `d > 215`,
        // `d < 18` and `h < 0.6` against them, and a sample that flips one of those
        // draws a different number of random values. That desynchronises the shared
        // RNG stream, which is how it was caught: every distant house came out purple
        // instead of terracotta, because the prototype's colour is drawn from the
        // same stream after ~9000 samples' worth of drift.
        private float[] _ringD;
        private int _p, _rings;
        private float[] _baseY;
        private Vector3[] _landVerts;

        public Mesh Land { get; private set; }
        public Mesh Water { get; private set; }

        // Exposed so the world dressing can scatter onto this same ring lattice
        // rather than inventing its own sampling of the surrounding land. Sharing
        // the lattice is what guarantees a distant tree stands exactly ON the
        // terrain it is meant to be growing out of.
        public int RingCount => _rings;
        public int PerimeterCount => _p;
        public float[] RingDistances => _ringD;
        public float[] BaseY => _baseY;
        public Vector3[] LandVerts => _landVerts;

        private void BuildBorder()
        {
            var b = new List<BorderPt>();

            void AddPt(int col, int row, float nx, float nz) => b.Add(new BorderPt
            {
                X = TerrainMeshBuilder.VxD(col),
                Z = TerrainMeshBuilder.VzD(row),
                Nx = nx, Nz = nz,
                I = row * W + col,
            });

            // Corners get a fan of interpolated normals so the offset rounds off
            // instead of producing a mitre spike.
            void AddCorner(int col, int row, float ax, float az, float bx, float bz)
            {
                for (int k = 1; k <= CornerFan; k++)
                {
                    float a = k / (float)(CornerFan + 1);
                    float nx = ax + (bx - ax) * a, nz = az + (bz - az) * a;
                    float l = Mathf.Sqrt(nx * nx + nz * nz);
                    if (l == 0) l = 1;
                    AddPt(col, row, nx / l, nz / l);
                }
            }

            // Every nz is NEGATED against the web build, because Vz mirrors Z:
            // row 0 sits at +Z here and its outward normal must point +Z.
            for (int col = 0; col < W; col++) AddPt(col, 0, 0, 1);
            AddCorner(W - 1, 0, 0, 1, 1, 0);
            for (int row = 1; row < H; row++) AddPt(W - 1, row, 1, 0);
            AddCorner(W - 1, H - 1, 1, 0, 0, -1);
            for (int col = W - 2; col >= 0; col--) AddPt(col, H - 1, 0, -1);
            AddCorner(0, H - 1, 0, -1, -1, 0);
            for (int row = H - 2; row >= 1; row--) AddPt(0, row, -1, 0);
            AddCorner(0, 0, -1, 0, 0, 1);

            _border = b.ToArray();
            _p = _border.Length;
        }

        /// <summary>
        /// Ring distances: Step0 units at the border, summing to Reach. Solved by
        /// bisection for the geometric ratio, matching the web build exactly.
        /// </summary>
        private void BuildRingSpacing(in OuterConfig oc)
        {
            _rings = oc.Rings;
            _ringD = new float[_rings + 1];

            double lo = 1.0001, hi = 2.0;
            for (int it = 0; it < 80; it++)
            {
                double r = (lo + hi) / 2;
                double s = (oc.Step0 * (Math.Pow(r, _rings) - 1)) / (r - 1);
                if (s < oc.Reach) lo = r; else hi = r;
            }
            double ratio = (lo + hi) / 2;
            double d = 0;
            for (int j = 0; j <= _rings; j++) { _ringD[j] = (float)d; d += oc.Step0 * Math.Pow(ratio, j); }
        }

        public void Build(FloodSim sim, in TerrainPalette palette, in OuterConfig oc)
        {
            BuildBorder();
            BuildRingSpacing(oc);

            int n = (_rings + 1) * _p;
            var verts = new Vector3[n];
            var colors = new Color[n];
            _baseY = new float[n];
            var elev = sim.Elev;

            for (int p = 0; p < _p; p++)
            {
                var bp = _border[p];
                double e0 = elev[bp.I];
                // Is this stretch of border dry land or already water? Decides
                // whether the world continues as coast and ocean or as inland
                // country.
                double landiness = TerrainColors.Smooth(-0.15, 1.5, e0);
                // Low-frequency warp of the offset distance → organic outline.
                // Sampled at the REFERENCE Z, not the Unity one — see
                // TerrainColors.RefZ. Getting this wrong does not merely shift the
                // pattern, it produces a different coastline outline entirely.
                double bzRef = TerrainColors.RefZ(bp.Z);
                double warp = 1
                    + (TerrainColors.Fbm(bp.X * 0.007 + 3.3, bzRef * 0.007 + 7.7) - 0.5) * 0.6
                    + (TerrainColors.Fbm(bp.X * 0.021 + 19.1, bzRef * 0.021 + 4.3) - 0.5) * 0.24;

                for (int j = 0; j <= _rings; j++)
                {
                    double t = j / (double)_rings;
                    // Warp fades out far away so distant rings can never fold over
                    // each other.
                    double dEff = _ringD[j] * (1 + (warp - 1) * (1 - TerrainColors.Smooth(0.3, 0.95, t)));
                    double wx = bp.X + bp.Nx * dEff;
                    double wz = bp.Z + bp.Nz * dEff;

                    double far;
                    if (oc.Sea)
                    {
                        double landFar = Math.Max(e0 * 0.55, 0.7) + oc.Rise * 0.45;
                        far = oc.Seabed + (landFar - oc.Seabed) * landiness;
                    }
                    else
                    {
                        // Inland maps open DOWNWARD into lower country, so the
                        // surrounding land can never rise up and occlude the play
                        // area — and so a river leaving the map keeps its channel
                        // instead of being filled in.
                        far = e0 * 0.45;
                    }

                    // Every noise term below samples the reference frame too.
                    double wzRef = TerrainColors.RefZ(wz);
                    double h = e0 + (far - e0) * TerrainColors.Smooth(0, 1, t);
                    double nr = TerrainColors.Smooth(0, 0.16, t); // wrinkles start at zero on the seam
                    h += (TerrainColors.Fbm(wx * oc.Freq, wzRef * oc.Freq) - 0.5) * oc.Amp * nr;
                    h += (TerrainColors.Fbm(wx * oc.Freq * 3.1 + 31.7, wzRef * oc.Freq * 3.1 + 13.3) - 0.5) * oc.Amp * 0.3 * nr;
                    h += (TerrainColors.Fbm(wx * oc.Freq * 0.33 + 5.1, wzRef * oc.Freq * 0.33 + 9.3) - 0.5) * oc.Rise * TerrainColors.Smooth(0.04, 0.6, t);
                    // Extra relief concentrated in the band where land crosses sea
                    // level. Without it the coast tracks the border's own elevation
                    // and runs in long straight lines parallel to the map edge; this
                    // is what turns it into bays, spits and offshore sandbars.
                    h += (TerrainColors.Fbm(wx * oc.Freq * 1.7 + 61.3, wzRef * oc.Freq * 1.7 + 45.9) - 0.5)
                         * oc.Amp * 1.5 * TerrainColors.Smooth(0.02, 0.14, t) * (1 - TerrainColors.Smooth(0.34, 0.8, t));

                    int k = j * _p + p;
                    verts[k] = new Vector3((float)wx, (float)h, (float)wz);
                    _baseY[k] = (float)h;

                    var cc = TerrainColors.ColorFor(h, palette);
                    double tn = TerrainColors.GroundTint(wx, wz);
                    colors[k] = new Color(
                        (float)(cc.R + tn), (float)(cc.G + tn * 0.92), (float)(cc.B + tn * 0.78), 1f);
                }
            }

            var tris = new int[_rings * _p * 6];
            int ti = 0;
            for (int j = 0; j < _rings; j++)
            {
                for (int p = 0; p < _p; p++)
                {
                    int pn = (p + 1) % _p;
                    int a = j * _p + p, b = j * _p + pn, c = (j + 1) * _p + p, d = (j + 1) * _p + pn;
                    // Same winding as the web build, NOT swapped like the play-area
                    // grid. The perimeter is walked in a fixed order (north, east,
                    // south, west) whose direction around the map is itself reversed
                    // by the Z mirror, so the orientation flip has already happened
                    // and swapping again would point every normal at the ground —
                    // which renders as an unlit near-black landscape.
                    tris[ti++] = a; tris[ti++] = c; tris[ti++] = b;
                    tris[ti++] = b; tris[ti++] = c; tris[ti++] = d;
                }
            }

            _landVerts = verts;
            Land = new Mesh { name = "HabagatOuterland", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            Land.vertices = verts;
            Land.colors = colors;
            Land.triangles = tris;
            // Smooth-shaded from averaged face normals rather than an analytic
            // gradient: unlike the play area this is not a regular grid, so there is
            // no fixed diagonal to bias the average.
            Land.RecalculateNormals();
            Land.RecalculateBounds();

            BuildWater(palette, tris);
        }

        /// <summary>
        /// Open water, laid on the very same ring mesh and shaded by depth with the
        /// identical shallow→deep rule the play area uses. That identity is what
        /// makes the shoreline continue past the border: pale shallows over the
        /// beach, darkening as the seabed drops away, all the way into the fog.
        /// </summary>
        private void BuildWater(in TerrainPalette palette, int[] tris)
        {
            int n = (_rings + 1) * _p;
            var verts = new Vector3[n];
            var colors = new Color[n];
            var wS = palette.WaterShallow;
            var wD = palette.WaterDeep;

            for (int p = 0; p < _p; p++)
            {
                for (int j = 0; j <= _rings; j++)
                {
                    int k = j * _p + p;
                    // Exactly the land ring's XZ, flattened to sea level. Sharing
                    // the horizontal layout is what guarantees the two sheets cannot
                    // drift apart at the shoreline.
                    float wx = _landVerts[k].x, wz = _landVerts[k].z;
                    verts[k] = new Vector3(wx, (float)SeaLevel, wz);

                    double depth = SeaLevel - _baseY[k];
                    // Same world-space dither the ground and play-area water use, or
                    // the long shallow gradient bands into visible contours.
                    double kk = Math.Min(1, Math.Max(0,
                        TerrainColors.Smooth(0.02, 1.7, depth) + TerrainColors.GroundTint(wx, wz) * 1.1));
                    colors[k] = new Color(
                        (float)(wS.R + (wD.R - wS.R) * kk),
                        (float)(wS.G + (wD.G - wS.G) * kk),
                        (float)(wS.B + (wD.B - wS.B) * kk),
                        depth <= 0 ? 0f : (float)(0.25 + 0.7 * kk));
                }
            }

            Water = new Mesh { name = "HabagatOuterWater", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            Water.vertices = verts;
            Water.colors = colors;
            Water.triangles = tris;
            Water.RecalculateNormals();
            Water.RecalculateBounds();
        }
    }
}
