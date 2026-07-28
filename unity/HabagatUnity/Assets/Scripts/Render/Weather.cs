using UnityEngine;

namespace Habagat.Render
{
    /// <summary>
    /// Weather applied to the whole world rather than just the play area — ported
    /// from the loop in section 15 of ThreeCanvas.jsx.
    ///
    /// The background is scenery, not simulation: it cannot compute its own
    /// flooding. But it shares every visual CONSEQUENCE of the weather — wet ground,
    /// rougher darker water, a swell that grows with the storm — and it has to, or
    /// the sandbox soaks through while the land around it stays dry and the border
    /// the outerland exists to hide reappears the moment it rains.
    /// </summary>
    public class Weather
    {
        // Storm is a shared dark override layered over the clear-weather palette.
        private static readonly Color StormFog = Hex(0x3d4a58);
        private const float StormFogNear = 30f, StormFogFar = 300f;
        private const float ClearFogNear = 100f, ClearFogFar = 430f;

        private static Color Hex(int h) => new Color(
            ((h >> 16) & 0xff) / 255f, ((h >> 8) & 0xff) / 255f, (h & 0xff) / 255f);

        /// <summary>0 = bone dry, 1 = soaked. Eased, never snapped.</summary>
        public float Wetness { get; private set; }

        /// <summary>The single water displacement both sheets share.</summary>
        public float SwellAmp { get; private set; } = 0.035f;

        /// <summary>
        /// Mean depth the rain is lying at on the play area's LAND — the level the
        /// world outside floods to. Eased at 0.08 rather than tracked exactly: the
        /// outside world is scenery catching up, not a second simulation.
        /// </summary>
        public double BgDepth { get; private set; }

        private readonly RainSystem _rain;
        private float _sunIntensity = 1.35f;

        public Weather(Transform parent) => _rain = new RainSystem(parent);

        public void Tick(WorldBuilder world, FloodSim sim, float rain, bool storm, float dt)
        {
            // How deep the rain is lying inside the sandbox. Sea cells are excluded —
            // the ocean is already at sea level and averaging it in would drag the
            // figure toward zero however hard it rained.
            int landCells = 0;
            double landWaterSum = 0;
            var elev = sim.Elev;
            var water = sim.Water;
            for (int i = 0; i < elev.Length; i++)
                if (elev[i] > FloodSim.SeaLevel) { landCells++; landWaterSum += water[i]; }
            double target = landCells > 0 ? landWaterSum / landCells : 0;
            BgDepth += (target - BgDepth) * 0.08;

            float norm = Mathf.Clamp01(rain / 10f);
            float targetWet = storm ? 1f : norm * 0.85f;
            // Deliberately slow. Ground that darkens the instant the slider moves
            // reads as a lighting bug; weather should look like it is soaking in.
            Wetness += (targetWet - Wetness) * 0.025f;
            SwellAmp = 0.035f + Wetness * 0.5f;

            // One shared value, so the sandbox and the surrounding land always soak
            // through together.
            world.TerrainMat.SetFloat("_Wetness", Wetness);
            world.OuterMat.SetFloat("_Wetness", Wetness);

            // Wet water is rougher: the tight specular highlight of calm water broadens
            // and dulls as the surface is churned up.
            float smooth = 0.88f - 0.45f * Wetness;
            world.WaterMat.SetFloat("_Smoothness", smooth);
            world.OuterWaterMat.SetFloat("_Smoothness", smooth);

            // Atmosphere. The fog colour and the horizon band track each other exactly,
            // so however the weather moves, land and sky still meet in one colour.
            RenderSettings.fogColor = Color.Lerp(RenderSettings.fogColor,
                storm ? StormFog : WorldBuilder.FogColor, 0.05f);
            RenderSettings.fogStartDistance = Mathf.Lerp(RenderSettings.fogStartDistance,
                storm ? StormFogNear : ClearFogNear, 0.05f);
            RenderSettings.fogEndDistance = Mathf.Lerp(RenderSettings.fogEndDistance,
                storm ? StormFogFar : ClearFogFar, 0.05f);

            // Sheet lightning. A flash is a jump, not a lerp — that asymmetry between
            // the instant spike and the slow decay is what makes it read as lightning
            // rather than as a flicker.
            if (storm && Random.value < 0.018f) _sunIntensity = 3.5f;
            else _sunIntensity = Mathf.Lerp(_sunIntensity, storm ? 1.05f : 1.35f, 0.1f);
            world.Sun.intensity = _sunIntensity;

            _rain.Update(rain, storm);
        }

        /// <summary>
        /// Float the boats on the surface they are sitting on, swell and all. They
        /// are the only props kept as individual transforms; everything else is baked.
        /// </summary>
        public void RideSwell(WorldBuilder world, double time)
        {
            foreach (var t in world.Boats)
            {
                if (t == null) continue;
                var p = t.position;
                p.y = (float)(FloodSim.SeaLevel + WaterMeshBuilder.SwellAt(p.x, p.z, time) * SwellAmp);
                t.position = p;

                // Sampled a little along the hull so the boat rolls with the wave
                // rather than bobbing flat on it.
                float roll = (float)(WaterMeshBuilder.SwellAt(p.x + 1.2, p.z, time) * SwellAmp * 0.35);
                var e = t.eulerAngles;
                t.rotation = Quaternion.Euler(0f, e.y, roll * Mathf.Rad2Deg);
            }
        }
    }
}
