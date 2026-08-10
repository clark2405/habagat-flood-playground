using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using Habagat;

// Fingerprints the ported simulation and CHECKS it against the reference, rather
// than printing it and trusting somebody to look.
//
// A port you have not diffed against the original is a rewrite, not a port — and
// a diff you did once by hand is not a check, it is a memory. fingerprint.txt is
// produced by scripts/fingerprint.mjs from the JavaScript in src/sim.js, which is
// the same code the web build runs.
//
//   dotnet run --project unity/HabagatSim.Verify              # verify, non-zero on drift
//   dotnet run --project unity/HabagatSim.Verify -- --print   # just emit the lines
//   node scripts/fingerprint.mjs > unity/HabagatSim.Verify/fingerprint.txt

static string F(double v) => v.ToString("F6", CultureInfo.InvariantCulture);

var lines = new List<string>();

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

    lines.Add($"TERRAIN {name} sum={F(sum)} min={F(min)} max={F(max)}");

    // Spot samples spread across the map, so a localised divergence cannot hide
    // inside an aggregate that happens to match.
    var probes = new[] { 0, 1, 95, 96, 3000, 3071, 4096, 5000, 6143 };
    foreach (var p in probes)
        lines.Add($"  ELEV {name}[{p}]={F(elev[p])}");

    // Run the simulation under a fixed weather script and fingerprint the result.
    var sim = FloodSim.FromPreset(type, seed);
    for (int t = 0; t < 400; t++)
    {
        bool storm = t >= 100 && t < 300;
        float rain = t < 100 ? 4f : 0f;
        if (t == 150) sim.BeginSurge();
        var st = sim.Step(rain, storm);

        if (t == 99 || t == 299 || t == 399)
            lines.Add(
                $"  SIM {name} t={st.Tick} water={F(st.Water)} flooded={st.Flooded} " +
                $"mang={st.MangroveCount} drn={st.DrainCount}");
    }
}

if (Array.IndexOf(args, "--print") >= 0)
{
    foreach (var line in lines) Console.WriteLine(line);
    return 0;
}

// Alongside this file, so it travels with the harness rather than living at a
// path that depends on where dotnet was invoked from.
var expectedPath = Path.Combine(AppContext.BaseDirectory, "fingerprint.txt");
if (!File.Exists(expectedPath))
{
    // The csproj copies it next to the binary; missing means the build did not,
    // which is a broken check rather than a passing one.
    Console.Error.WriteLine($"MISSING {expectedPath} — regenerate with scripts/fingerprint.mjs");
    return 2;
}

// Split on \n after dropping \r: the reference is written by node with Unix
// endings and this runs on Windows. A line-ending difference is not a
// divergence in the simulation and must not be reported as one.
var expected = File.ReadAllText(expectedPath).Replace("\r", "").Trim('\n').Split('\n');

int bad = 0;
for (int i = 0; i < Math.Max(expected.Length, lines.Count); i++)
{
    string want = i < expected.Length ? expected[i] : "<missing>";
    string got = i < lines.Count ? lines[i] : "<missing>";
    if (want == got) continue;
    bad++;
    Console.Error.WriteLine($"line {i + 1}:\n  reference {want}\n  port      {got}");
}

if (bad > 0)
{
    Console.Error.WriteLine($"\nFAIL — {bad} of {expected.Length} lines diverge from the JS reference.");
    return 1;
}

Console.WriteLine($"OK — {lines.Count} lines identical to the JS reference.");
return 0;
