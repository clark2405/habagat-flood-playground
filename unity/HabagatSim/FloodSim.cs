using System;

namespace Habagat
{
    public struct SimStats
    {
        public double Water;       // total standing water on land
        public int Flooded;        // land cells holding more than 3 cm
        public int FloodedHouses;
        public int MangroveCount;
        public int DrainCount;
        public int Tick;
    }

    /// <summary>
    /// Shallow-water cellular automaton, ported from step() in FloodPlayground.jsx.
    /// Engine-agnostic on purpose: no UnityEngine types and no allocation in the
    /// hot loop, so it unit-tests under plain dotnet and drives a MonoBehaviour
    /// without modification.
    ///
    /// PRECISION: state lives in float[] (matching Float32Array) but every
    /// intermediate is computed in double and narrowed only on the store. That is
    /// JavaScript's exact behaviour — numbers are doubles, and rounding to float32
    /// happens solely when writing into a typed array. Doing the arithmetic in
    /// float instead rounds at each operation, and over 400 ticks the divergence
    /// is visible in the water totals.
    /// </summary>
    public sealed class FloodSim
    {
        public const int W = 96;
        public const int H = 64;
        public const double FlowRate = 0.15;
        public const double MinWater = 0.001;
        public const double SeaLevel = 0.0;
        public const double MaxColumn = 3.0;

        public float[] Elev;
        public float[] Water;
        public float[] Next;
        public float[] Drain;
        public float[] Absorb;
        public byte[] Mang;
        public byte[] Drn;

        public int Tick;
        public int SurgeTicks;

        public FloodSim(float[] elev)
        {
            if (elev == null || elev.Length != W * H)
                throw new ArgumentException($"elevation must be {W * H} cells", nameof(elev));

            Elev = elev;
            Water = new float[W * H];
            Next = new float[W * H];
            Drain = new float[W * H];
            Absorb = new float[W * H];
            Mang = new byte[W * H];
            Drn = new byte[W * H];
        }

        public static FloodSim FromPreset(PresetType type, int seed)
        {
            var sim = new FloodSim(TerrainPresets.Build(type, seed));
            TerrainPresets.ApplyPresetFeatures(type, sim);
            return sim;
        }

        /// <summary>Trigger a storm surge rolling in from the southern edge.</summary>
        public void BeginSurge(int ticks = 260) => SurgeTicks = ticks;

        /// <param name="rain">Rain intensity slider, 0..10.</param>
        /// <param name="storm">Whether the habagat storm is active.</param>
        /// <param name="houseCells">Flat indices of houses, for the flooded-homes count.</param>
        public SimStats Step(double rain, bool storm, int[] houseCells = null)
        {
            double rainRate = rain * 0.0004 + (storm ? 0.002 : 0.0);

            var elev = Elev;
            var water = Water;
            var next = Next;

            if (rainRate > 0.0)
            {
                for (int i = 0; i < water.Length; i++)
                    if (elev[i] > SeaLevel)
                        water[i] = (float)Math.Min(water[i] + rainRate, MaxColumn);
            }

            if (SurgeTicks > 0)
            {
                SurgeTicks--;
                const double surgeDepth = 0.6;
                for (int y = H - 6; y < H; y++)
                {
                    for (int x = 0; x < W; x++)
                    {
                        int i = y * W + x;
                        double floor = surgeDepth + (elev[i] < SeaLevel ? SeaLevel - elev[i] : 0.0);
                        water[i] = (float)Math.Min(Math.Max(water[i], floor), MaxColumn);
                    }
                }
            }

            // Anything at or below sea level is held full to the waterline.
            for (int i = 0; i < water.Length; i++)
                if (elev[i] <= SeaLevel)
                    water[i] = (float)Math.Min(Math.Max(water[i], SeaLevel - elev[i]), MaxColumn);

            // Flow pass. Reads current depths and accumulates into `next`, so the
            // order cells are visited cannot bias the result.
            Array.Copy(water, next, water.Length);
            for (int y = 0; y < H; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    if (water[i] <= MinWater) continue;
                    double hi = elev[i] + (double)water[i];

                    if (x > 0) Flow(i, i - 1, hi);
                    if (x < W - 1) Flow(i, i + 1, hi);
                    if (y > 0) Flow(i, i - W, hi);
                    if (y < H - 1) Flow(i, i + W, hi);
                }
            }

            // Swap: `next` becomes the live surface.
            var tmp = Water;
            Water = Next;
            Next = tmp;

            var w2 = Water;
            double totalWater = 0.0;
            int floodedCells = 0, mangCount = 0, drnCount = 0;

            for (int i = 0; i < w2.Length; i++)
            {
                if (Mang[i] != 0) mangCount++;
                if (Drn[i] != 0) drnCount++;

                if (elev[i] > SeaLevel && w2[i] > 0f)
                {
                    const double naturalSeepage = 0.0004;
                    w2[i] = (float)Math.Max(0.0, w2[i] - naturalSeepage - Drain[i] - Absorb[i]);
                    if (w2[i] < MinWater) w2[i] = 0f;
                }

                if (elev[i] > SeaLevel)
                {
                    totalWater += w2[i];
                    if (w2[i] > 0.03) floodedCells++;
                }
            }

            int floodedH = 0;
            if (houseCells != null)
                foreach (int i in houseCells)
                    if (i >= 0 && i < w2.Length && w2[i] > 0.08) floodedH++;

            Tick++;

            return new SimStats
            {
                Water = totalWater,
                Flooded = floodedCells,
                FloodedHouses = floodedH,
                MangroveCount = mangCount,
                DrainCount = drnCount,
                Tick = Tick,
            };

            void Flow(int i, int n, double hi)
            {
                double diff = hi - (elev[n] + (double)water[n]);
                if (diff > 0.0)
                {
                    // Cap outflow at a fifth of the cell so a steep drop cannot
                    // drain it below zero within a single tick.
                    double maxOutflow = next[i] * 0.2;
                    double moved = Math.Min(diff * FlowRate, maxOutflow);
                    next[i] = (float)(next[i] - moved);
                    next[n] = (float)(next[n] + moved);
                }
            }
        }

        // ── Brush tools ──────────────────────────────────────────────────────
        public enum Tool { Raise, Lower, Water, Mangrove, DrainPump, Clear }

        /// <summary>Circular falloff brush, matching handlePaint's radius-3 stamp.</summary>
        public void Paint(Tool tool, int x, int y, int radius = 3)
        {
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    int px = x + dx, py = y + dy;
                    if (px < 0 || px >= W || py < 0 || py >= H) continue;
                    int d2 = dx * dx + dy * dy;
                    if (d2 > radius * radius) continue;
                    int i = py * W + px;
                    double fall = 1.0 - Math.Sqrt(d2) / radius;

                    switch (tool)
                    {
                        case Tool.Raise: Elev[i] = (float)(Elev[i] + 0.15 * fall); break;
                        case Tool.Lower: Elev[i] = (float)(Elev[i] - 0.15 * fall); break;
                        case Tool.Water: Water[i] = (float)(Water[i] + 0.25 * fall); break;
                        case Tool.Mangrove:
                            if (Elev[i] > SeaLevel) { Mang[i] = 1; Absorb[i] = 0.008f; }
                            break;
                        case Tool.DrainPump:
                            if (Elev[i] > SeaLevel) { Drn[i] = 1; Drain[i] = 0.025f; }
                            break;
                        case Tool.Clear:
                            Mang[i] = 0; Drn[i] = 0; Absorb[i] = 0f; Drain[i] = 0f; Water[i] = 0f;
                            break;
                    }
                }
            }
        }
    }
}
