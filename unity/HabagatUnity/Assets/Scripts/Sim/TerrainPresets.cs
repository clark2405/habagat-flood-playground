using System;

namespace Habagat
{
    public enum PresetType { Coastal, River, Urban, Island, Basin }

    /// <summary>
    /// Heightmap generation, ported from makeTerrainPreset in FloodPlayground.jsx.
    /// Value-noise on a 13x9 control grid, smoothstep-interpolated, then shaped
    /// per preset. Deterministic for a given seed — the presets must produce the
    /// same land every run or shared maps stop matching.
    ///
    /// NOTE ON PRECISION: every intermediate here is a double, and the result is
    /// narrowed to float only on the store into the elevation array. That mirrors
    /// JavaScript exactly, where all arithmetic happens in double and rounding to
    /// float32 occurs solely when writing into a Float32Array. Computing in float
    /// throughout — the obvious C# translation — rounds at every step instead, and
    /// the accumulated error shows up in the seventh significant figure of the
    /// terrain. Verified byte-identical against the JS source by HabagatSim.Verify.
    /// </summary>
    public static class TerrainPresets
    {
        public const int W = FloodSim.W;
        public const int H = FloodSim.H;
        public const double SeaLevel = FloodSim.SeaLevel;

        private const int Gw = 13, Gh = 9;

        private static double Lerp(double a, double b, double t)
        {
            double s = t * t * (3.0 - 2.0 * t);
            return a + (b - a) * s;
        }

        public static float[] Build(PresetType type, int seed = 1337)
        {
            var rnd = new JsMath.Lcg(seed);

            // Control grid is filled row-major, matching the order of
            // Array.from({length:gh}, () => Array.from({length:gw}, rnd)).
            var g = new double[Gh][];
            for (int y = 0; y < Gh; y++)
            {
                g[y] = new double[Gw];
                for (int x = 0; x < Gw; x++) g[y][x] = rnd.Next();
            }

            var elev = new float[W * H];

            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    double gx = (x / (double)(W - 1)) * (Gw - 1);
                    double gy = (y / (double)(H - 1)) * (Gh - 1);
                    int x0 = (int)Math.Floor(gx), y0 = (int)Math.Floor(gy);
                    double fx = gx - x0, fy = gy - y0;

                    int x1 = Math.Min(x0 + 1, Gw - 1);
                    int y1 = Math.Min(y0 + 1, Gh - 1);

                    double n = Lerp(
                        Lerp(g[y0][x0], g[y0][x1], fx),
                        Lerp(g[y1][x0], g[y1][x1], fx),
                        fy);

                    int idx = y * W + x;

                    switch (type)
                    {
                        case PresetType.Coastal:
                        {
                            double slope = 1.0 - y / (double)(H - 1);
                            double e = n * 2.2 + slope * 4.2 - 1.5;
                            double riverX = W * 0.5 + Math.Sin(y * 0.1) * 8.0;
                            double rd = Math.Abs(x - riverX);
                            if (rd < 5.6)
                            {
                                double c = Math.Cos((rd / 5.6) * Math.PI * 0.5);
                                e -= 2.4 * (c * c);
                            }
                            elev[idx] = (float)e;
                            break;
                        }
                        case PresetType.River:
                        {
                            // Highland valley: lush green basin with a deep winding river.
                            double distFromCenter = Math.Abs(x - W / 2.0) / (W / 2.0);
                            double e = n * 2.5 + distFromCenter * 3.2 + 0.3;
                            double riverX = W * 0.5 + Math.Sin(y * 0.15) * 14.0;
                            double rd = Math.Abs(x - riverX);
                            if (rd < 9.6)
                            {
                                double c = Math.Cos((rd / 9.6) * Math.PI * 0.5);
                                e -= 4.2 * (c * c);
                            }
                            elev[idx] = (float)e;
                            break;
                        }
                        case PresetType.Island:
                        {
                            double cx = W / 2.0, cy = H / 2.0;
                            double dist = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) / (W * 0.42);
                            elev[idx] = (float)((1.0 - dist) * 4.5 + (n - 0.5) * 1.8 - 0.8);
                            break;
                        }
                        default:
                        {
                            // Metro Manila urban barangay: paved streets in a flat lowland
                            // basin. The high ground is a wobbled oval bowl rather than a
                            // rim measured from the map edges, so it reads as a landform
                            // instead of drawing a rectangle around the play area.
                            double cxn = (x - W / 2.0) / (W * 0.44);
                            double cyn = (y - H / 2.0) / (H * 0.44);
                            double bowl = Math.Sqrt(cxn * cxn + cyn * cyn) + (n - 0.5) * 0.55;
                            double rim = Math.Min(Math.Max((bowl - 0.62) / 0.53, 0.0), 1.0);
                            double e = 0.4 + (n - 0.5) * 0.6 + rim * rim * (3.0 - 2.0 * rim) * 2.6;
                            double canalX = W * 0.48;
                            double cd = Math.Abs(x - canalX);
                            if (cd < 4.4)
                            {
                                double c = Math.Cos((cd / 4.4) * Math.PI * 0.5);
                                e -= 1.15 * (c * c);
                            }
                            elev[idx] = (float)e;
                            break;
                        }
                    }
                }
            }

            return elev;
        }

        /// <summary>
        /// The starting mangroves / drains each preset ships with, matching
        /// loadPreset in FloodPlayground.jsx.
        /// </summary>
        public static void ApplyPresetFeatures(PresetType type, FloodSim sim)
        {
            if (type == PresetType.Coastal)
            {
                for (int x = 10; x < 86; x += 3)
                {
                    int i = 50 * W + x;
                    if (sim.Elev[i] > SeaLevel && sim.Elev[i] < 0.6f)
                    {
                        sim.Mang[i] = 1;
                        sim.Absorb[i] = 0.008f;
                    }
                }
            }
            else if (type == PresetType.Urban || type == PresetType.Basin)
            {
                foreach (int i in new[] { 30 * W + 46, 32 * W + 46, 40 * W + 46 })
                {
                    if (sim.Elev[i] > SeaLevel)
                    {
                        sim.Drn[i] = 1;
                        sim.Drain[i] = 0.03f;
                    }
                }
            }
        }
    }
}
