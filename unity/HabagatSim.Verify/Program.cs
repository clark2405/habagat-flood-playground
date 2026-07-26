using System;
using System.Globalization;
using Habagat;

// Dumps a deterministic fingerprint of the ported simulation so it can be diffed
// against the same fingerprint taken from the live JavaScript implementation.
// A port you have not diffed against the original is a rewrite, not a port.

static string F(double v) => v.ToString("F6", CultureInfo.InvariantCulture);

var presets = new (string Name, PresetType Type, int Seed)[]
{
    ("coastal", PresetType.Coastal, 1337),
    ("river",   PresetType.River,   4040),
    ("urban",   PresetType.Urban,   9999),
    ("island",  PresetType.Island,  8888),
};

foreach (var (name, type, seed) in presets)
{
    var elev = TerrainPresets.Build(type, seed);

    double sum = 0, min = double.MaxValue, max = double.MinValue;
    foreach (var e in elev)
    {
        sum += e;
        if (e < min) min = e;
        if (e > max) max = e;
    }

    Console.WriteLine($"TERRAIN {name} sum={F(sum)} min={F(min)} max={F(max)}");

    // Spot samples spread across the map, so a localised divergence cannot hide
    // inside an aggregate that happens to match.
    var probes = new[] { 0, 1, 95, 96, 3000, 3071, 4096, 5000, 6143 };
    foreach (var p in probes)
        Console.WriteLine($"  ELEV {name}[{p}]={F(elev[p])}");

    // Run the simulation under a fixed weather script and fingerprint the result.
    var sim = FloodSim.FromPreset(type, seed);
    for (int t = 0; t < 400; t++)
    {
        bool storm = t >= 100 && t < 300;
        float rain = t < 100 ? 4f : 0f;
        if (t == 150) sim.BeginSurge();
        var st = sim.Step(rain, storm);

        if (t == 99 || t == 299 || t == 399)
            Console.WriteLine(
                $"  SIM {name} t={st.Tick} water={F(st.Water)} flooded={st.Flooded} " +
                $"mang={st.MangroveCount} drn={st.DrainCount}");
    }
}
