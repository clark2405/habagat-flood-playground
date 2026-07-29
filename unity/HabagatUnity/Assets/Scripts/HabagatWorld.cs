using Habagat.Render;
using UnityEngine;

namespace Habagat
{
    /// <summary>
    /// The runtime entry point: builds the world, runs the flood simulation and
    /// keeps the water surface in step with it.
    ///
    /// Drop this on an empty GameObject and press Play. It is also what the
    /// screenshot harness drives, so the two cannot drift apart.
    /// </summary>
    public class HabagatWorld : MonoBehaviour
    {
        [Header("Preset")]
        public PresetType preset = PresetType.Coastal;

        [Header("Weather")]
        [Range(0f, 1f)] public float rain = 0f;
        public bool storm = false;

        [Header("Simulation")]
        public bool running = true;
        /// <summary>
        /// The web build calls <c>step()</c> TWICE inside its requestAnimationFrame
        /// loop, so its flood rate is tied to display refresh — the same map floods
        /// faster on a 144 Hz monitor than on a 60 Hz one. That is reproduced here
        /// rather than quietly corrected, because the sim is verified against the
        /// reference and a different cadence would make the two diverge on screen.
        /// Set to 0 to decouple and step on a fixed clock instead.
        /// </summary>
        public int stepsPerFrame = 2;

        public bool buildProps = true;

        public FloodSim Sim { get; private set; }
        public WorldBuilder World { get; private set; }
        public Weather Weather { get; private set; }

        /// <summary>
        /// Latest simulation stats. Step returns them, so they are cached here rather
        /// than recomputed — and they deliberately persist while paused, or the
        /// readout would blank out the moment you stopped to look at it.
        /// </summary>
        public SimStats Stats { get; private set; }

        private int[] _houseCells;

        private double _time;

        private static int SeedFor(PresetType t) => t switch
        {
            PresetType.River => 4040,
            PresetType.Urban => 9999,
            PresetType.Island => 8888,
            _ => 1337,
        };

        private void Start()
        {
            if (World == null) Rebuild();
        }

        /// <summary>
        /// Tear down and rebuild everything for the current preset.
        ///
        /// <paramref name="beforeBuild"/> runs after the simulation exists but before
        /// the world is built. Props read painted state — mangroves and drains — at
        /// build time, so anything that wants to seed them has to act in that gap.
        /// </summary>
        public void Rebuild(System.Action<FloodSim> beforeBuild = null)
        {
            World?.Destroy();
            Sim = FloodSim.FromPreset(preset, SeedFor(preset));
            beforeBuild?.Invoke(Sim);
            // Which cells hold a home, so the sim can report how many are underwater.
            var houses = Barangay.For(preset);
            _houseCells = new int[houses.Length];
            for (int i = 0; i < houses.Length; i++)
                _houseCells[i] = houses[i].Y * FloodSim.W + houses[i].X;

            World = new WorldBuilder();
            if (!World.Build(Sim, preset, buildProps, transform)) { enabled = false; return; }
            Weather ??= new Weather(transform);
        }

        private void Update()
        {
            if (World == null) return;

            if (running)
                for (int i = 0; i < stepsPerFrame; i++)
                    Stats = Sim.Step(rain, storm, _houseCells);

            // The surface animates whether or not the sim is running — a paused
            // flood should still have moving water, or the whole scene reads as a
            // screenshot.
            _time += Time.deltaTime;
            Weather.Tick(World, Sim, rain, storm, Time.deltaTime);
            World.RefreshWater(Sim, preset, _time, Weather.SwellAmp);
            World.RefreshOuterWater(Sim, preset, Weather.BgDepth, _time, Weather.SwellAmp);
            Weather.RideSwell(World, _time);
        }

        /// <summary>Advance the simulation without rendering — used by the harness.</summary>
        public void Advance(int ticks, float rainAmount, bool stormy)
        {
            for (int i = 0; i < ticks; i++) Stats = Sim.Step(rainAmount, stormy, _houseCells);
            // Weather eases rather than snapping, so a still frame has to be given
            // enough ticks to actually get wet — 400 lands within a per-mille of the
            // target at the 0.025 rate. Without this a `-storm 1` screenshot shows
            // storm water under a bone-dry landscape.
            for (int i = 0; i < 400; i++) Weather.Tick(World, Sim, rainAmount, stormy, 1f / 60f);
            World.RefreshWater(Sim, preset, _time, Weather.SwellAmp);
            World.RefreshOuterWater(Sim, preset, Weather.BgDepth, _time, Weather.SwellAmp);
            Weather.RideSwell(World, _time);
        }

        private void OnDestroy() => World?.Destroy();
    }
}
