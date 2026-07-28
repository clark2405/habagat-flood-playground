using System;

namespace Habagat
{
    public struct Rgb
    {
        public float R, G, B;
        public Rgb(float r, float g, float b) { R = r; G = g; B = b; }
        public static Rgb Lerp(Rgb a, Rgb b, double k) => new Rgb(
            (float)(a.R + (b.R - a.R) * k),
            (float)(a.G + (b.G - a.G) * k),
            (float)(a.B + (b.B - a.B) * k));
    }

    /// <summary>
    /// Per-preset ground colour ramp, mirroring ENV[...].colors in ThreeCanvas.jsx.
    /// The sandbox and the land surrounding it share one palette on purpose — they
    /// are meant to be literally the same material response, or the border shows.
    /// </summary>
    public struct TerrainPalette
    {
        public Rgb Sea, Deep, Low, Mid, High;
        public double Beach;
        // Water is shaded by DEPTH using the same shallow→deep rule everywhere,
        // which is what lets the play area's surface meet the open water outside
        // it without a visible step.
        public Rgb WaterShallow, WaterDeep;

        public static TerrainPalette For(PresetType t)
        {
            switch (t)
            {
                case PresetType.Coastal:
                    return new TerrainPalette {
                        Sea = new Rgb(0.86f, 0.75f, 0.56f), Deep = new Rgb(0.07f, 0.25f, 0.33f), Beach = 0.85,
                        Low = new Rgb(0.48f, 0.62f, 0.34f), Mid = new Rgb(0.38f, 0.55f, 0.28f), High = new Rgb(0.56f, 0.54f, 0.38f),
                        WaterShallow = new Rgb(0.42f, 0.80f, 0.76f), WaterDeep = new Rgb(0.05f, 0.24f, 0.33f) };
                case PresetType.River:
                    return new TerrainPalette {
                        Sea = new Rgb(0.33f, 0.31f, 0.22f), Deep = new Rgb(0.13f, 0.17f, 0.14f), Beach = 1.50,
                        Low = new Rgb(0.34f, 0.52f, 0.24f), Mid = new Rgb(0.28f, 0.45f, 0.20f), High = new Rgb(0.45f, 0.44f, 0.36f),
                        WaterShallow = new Rgb(0.36f, 0.64f, 0.62f), WaterDeep = new Rgb(0.09f, 0.26f, 0.30f) };
                case PresetType.Island:
                    return new TerrainPalette {
                        Sea = new Rgb(0.86f, 0.76f, 0.56f), Deep = new Rgb(0.06f, 0.26f, 0.34f), Beach = 0.90,
                        Low = new Rgb(0.34f, 0.56f, 0.28f), Mid = new Rgb(0.26f, 0.48f, 0.22f), High = new Rgb(0.52f, 0.52f, 0.40f),
                        WaterShallow = new Rgb(0.42f, 0.80f, 0.78f), WaterDeep = new Rgb(0.05f, 0.25f, 0.34f) };
                default: // Urban / Basin
                    return new TerrainPalette {
                        Sea = new Rgb(0.26f, 0.30f, 0.32f), Deep = new Rgb(0.09f, 0.13f, 0.15f), Beach = 0.60,
                        Low = new Rgb(0.44f, 0.45f, 0.45f), Mid = new Rgb(0.50f, 0.51f, 0.49f), High = new Rgb(0.46f, 0.51f, 0.40f),
                        WaterShallow = new Rgb(0.40f, 0.58f, 0.60f), WaterDeep = new Rgb(0.11f, 0.22f, 0.26f) };
            }
        }
    }

    /// <summary>
    /// How the world continues past the sandbox border, per preset. `Sea` decides
    /// whether it opens into coast and ocean or into lower inland country.
    /// </summary>
    public struct OuterConfig
    {
        public bool Sea;
        public int Rings;
        public double Reach, Step0, Amp, Rise, Seabed, Freq;

        public static OuterConfig For(PresetType t)
        {
            switch (t)
            {
                case PresetType.Coastal:
                    return new OuterConfig { Sea = true, Reach = 430, Rings = 64, Step0 = 1.0, Amp = 2.4, Rise = 3.4, Seabed = -6.0, Freq = 0.019 };
                case PresetType.River:
                    return new OuterConfig { Sea = false, Reach = 430, Rings = 64, Step0 = 1.0, Amp = 3.2, Rise = 3.0, Freq = 0.016 };
                case PresetType.Island:
                    return new OuterConfig { Sea = true, Reach = 430, Rings = 64, Step0 = 1.0, Amp = 2.0, Rise = 1.2, Seabed = -6.0, Freq = 0.024 };
                default: // Urban / Basin
                    return new OuterConfig { Sea = false, Reach = 430, Rings = 64, Step0 = 1.0, Amp = 0.6, Rise = 0.8, Freq = 0.02 };
            }
        }
    }

    /// <summary>
    /// Value noise and the ground colour ramp, ported from ThreeCanvas.jsx.
    ///
    /// The hash relies on JavaScript's int32 coercions (`|0`, `Math.imul`, `>>>`),
    /// so it is reproduced with explicit unchecked int arithmetic and unsigned
    /// shifts. Everything downstream stays in double, matching JS, and narrows to
    /// float only where a colour is stored.
    /// </summary>
    public static class TerrainColors
    {
        public const double SeaLevel = FloodSim.SeaLevel;

        private static double Hash2(int ix, int iz)
        {
            unchecked
            {
                int h = ix * 374761393 + iz * 668265263;
                h = (h ^ (int)((uint)h >> 13)) * 1274126177;
                return (uint)(h ^ (int)((uint)h >> 16)) / 4294967295.0;
            }
        }

        private static double VNoise(double x, double z)
        {
            int x0 = (int)Math.Floor(x), z0 = (int)Math.Floor(z);
            double fx = x - x0, fz = z - z0;
            double sx = fx * fx * (3 - 2 * fx), sz = fz * fz * (3 - 2 * fz);
            double n00 = Hash2(x0, z0), n10 = Hash2(x0 + 1, z0);
            double n01 = Hash2(x0, z0 + 1), n11 = Hash2(x0 + 1, z0 + 1);
            double a = n00 + (n10 - n00) * sx;
            double b = n01 + (n11 - n01) * sx;
            return a + (b - a) * sz;
        }

        public static double Fbm(double x, double z) =>
            VNoise(x, z) * 0.6 +
            VNoise(x * 2.13 + 11.3, z * 2.13 + 5.7) * 0.3 +
            VNoise(x * 4.31 + 27.1, z * 4.31 + 17.9) * 0.1;

        public static double Smooth(double a, double b, double x)
        {
            double t = Math.Min(Math.Max((x - a) / (b - a), 0), 1);
            return t * t * (3 - 2 * t);
        }

        private static double Lin(double a, double b, double x) =>
            Math.Min(Math.Max((x - a) / (b - a), 0), 1);

        /// <summary>
        /// Fine ground variegation, sampled in WORLD space so the play area and the
        /// land around it share one continuous pattern. The low-frequency term is
        /// the important one: without broad patchiness the ground is a single flat
        /// colour over enormous stretches, which is most of what made the web
        /// version's landscape read as dry and bland.
        /// </summary>
        /// <remarks>
        /// Takes a UNITY world position and flips Z back into the reference frame
        /// before sampling — see <see cref="RefZ"/>.
        /// </remarks>
        public static double GroundTint(double wx, double wzUnity)
        {
            double wz = RefZ(wzUnity);
            return (Fbm(wx * 0.055 + 13.7, wz * 0.055 + 31.1) - 0.5) * 0.105 +
                   (Fbm(wx * 0.34 + 71.3, wz * 0.34 + 19.7) - 0.5) * 0.075 +
                   (Fbm(wx * 1.13 + 3.9, wz * 1.13 + 47.1) - 0.5) * 0.035;
        }

        /// <summary>
        /// Convert a Unity world Z back to the coordinate the web build would have
        /// used, for the purpose of sampling noise.
        ///
        /// This is easy to miss and it is not cosmetic. The map is mirrored into
        /// Unity's left-handed space, so a point at Unity Z sits at -Z in the
        /// reference. Sampling <c>Fbm</c> at the Unity coordinate therefore reads a
        /// DIFFERENT part of the noise field, and the result is not the reference
        /// mirrored — it is an unrelated landform that merely looks similar.
        ///
        /// It stayed hidden for a while because the outerland's seam ring multiplies
        /// every noise term by a ramp that is zero at t=0, so ring 0 matches the
        /// reference exactly no matter what this returns. The divergence only opens
        /// up further out, where nothing was being compared numerically.
        /// </summary>
        public static double RefZ(double unityZ) => -unityZ;

        /// <summary>
        /// Underwater ground darkens with depth — a bright sand shelf that stops at
        /// the map border is the single most obvious "this is a square tile" tell —
        /// and a wet-sand band sits just above the waterline so shorelines read as
        /// shorelines.
        ///
        /// Each band must span at least ~1 cell of slope. A narrow band is
        /// effectively a step in the vertex colours, and a step interpolated across
        /// a diagonally-split grid scallops into crescents along any steep bank.
        /// Linear inside each band on purpose: the mesh interpolates vertex colours
        /// linearly, so a piecewise-linear ramp reproduces exactly and leaves no
        /// interpolation error to alternate with the triangulation.
        /// </summary>
        public static Rgb ColorFor(double e, in TerrainPalette c)
        {
            if (e <= SeaLevel) return Rgb.Lerp(c.Sea, c.Deep, Lin(0, -2.6, e));
            if (e < c.Beach) return Rgb.Lerp(c.Sea, c.Low, Lin(0, c.Beach, e));
            double t = Math.Min(Math.Max((e - c.Beach) / (3.4 - c.Beach), 0), 1);
            if (t < 0.5) return Rgb.Lerp(c.Low, c.Mid, t / 0.5);
            return Rgb.Lerp(c.Mid, c.High, (t - 0.5) / 0.5);
        }
    }
}
