namespace Habagat
{
    /// <summary>
    /// Per-preset atmosphere, ported from the ENV table in ThreeCanvas.jsx.
    ///
    /// Each preset gets a distinct surrounding world, and the sky is the largest
    /// part of that: a smoggy Manila barangay under flat grey haze should not share
    /// a sky with a tropical beach. Until now everything used the coastal values,
    /// which made all four maps read as the same weather.
    ///
    /// <see cref="Fog"/> and the sky's horizon band are deliberately the SAME value.
    /// That identity is what makes terrain fading into fog meet a sky of exactly the
    /// colour it faded to, so the sandbox has no visible end.
    ///
    /// Ambient intensity is NOT the reference's figure: three.js folds a 1/PI into
    /// its diffuse BRDF that URP's Lambert does not, so the numbers here are scaled
    /// to roughly half. See the note in CLAUDE.md.
    /// </summary>
    public readonly struct EnvConfig
    {
        public readonly int Zenith, Mid, Fog;
        public readonly float FogNear, FogFar;
        public readonly int AmbientColor;
        public readonly float AmbientIntensity;
        public readonly int DirColor;
        public readonly float DirIntensity;
        /// <summary>False on presets whose sky is overcast — the smoggy city.</summary>
        public readonly bool Sun;

        public EnvConfig(int zenith, int mid, int fog, float fogNear, float fogFar,
                         int ambientColor, float ambientIntensity,
                         int dirColor, float dirIntensity, bool sun)
        {
            Zenith = zenith; Mid = mid; Fog = fog;
            FogNear = fogNear; FogFar = fogFar;
            AmbientColor = ambientColor; AmbientIntensity = ambientIntensity;
            DirColor = dirColor; DirIntensity = dirIntensity; Sun = sun;
        }

        // Storm is a shared dark override layered over every clear-weather palette.
        public const int StormZenith = 0x18202b;
        public const int StormMid = 0x27333f;
        public const int StormFog = 0x3d4a58;
        public const float StormFogNear = 30f, StormFogFar = 300f;
        public const float StormDirIntensity = 1.05f;

        /// <summary>The web build's ambient intensities, halved for URP's Lambert.</summary>
        private const float AmbScale = 0.494f;

        public static EnvConfig For(PresetType t) => t switch
        {
            // Lush highland basin — the valley opens into rolling green hills.
            PresetType.River => new EnvConfig(
                0x5aa3c4, 0xa8cfd6, 0xdce8dd, 100f, 425f,
                0xe4eee0, 0.82f * AmbScale, 0xfff1c2, 1.5f, true),
            // Metro Manila barangay — paved ground under a hazy, sunless skyline.
            PresetType.Urban => new EnvConfig(
                0x8fa6b8, 0xb6c4cf, 0xd2dae0, 72f, 360f,
                0xdbe3ea, 0.72f * AmbScale, 0xf2efe0, 1.15f, false),
            // Storm-lashed island — coast breaking up on every side.
            PresetType.Island => new EnvConfig(
                0x2a8fd0, 0x83cbe9, 0xdcf0f6, 100f, 430f,
                0xe1ecfd, 0.85f * AmbScale, 0xffeeb8, 1.55f, true),
            // Tropical beach village.
            _ => new EnvConfig(
                0x2f86c9, 0x8bcde9, 0xdfeef4, 100f, 430f,
                0xdfe8fa, 0.85f * AmbScale, 0xfff0ad, 1.55f, true),
        };
    }
}
