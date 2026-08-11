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
    //
    // t=160 and t=360 bracket the surge, which begins at 150 and lasts 200 ticks.
    // Without them the only sample after the surge ends is t=400, and that nearly
    // let a surge running 30% too long through — at t=100 neither side has begun
    // and at t=300 both are still going, so one line carried the whole check.
    var sim = FloodSim.FromPreset(type, seed);
    for (int t = 0; t < 400; t++)
    {
        bool storm = t >= 100 && t < 300;
        float rain = t < 100 ? 4f : 0f;
        if (t == 150) sim.BeginSurge();
        var st = sim.Step(rain, storm);

        if (t == 99 || t == 159 || t == 299 || t == 359 || t == 399)
            lines.Add(
                $"  SIM {name} t={st.Tick} water={F(st.Water)} flooded={st.Flooded} " +
                $"mang={st.MangroveCount} drn={st.DrainCount}");
    }

    // The brush tools, which nothing verified until now: six of them, ported cell
    // for cell from the JavaScript, and never once compared against it.
    var brush = FloodSim.FromPreset(type, seed);
    brush.Paint(FloodSim.Tool.Raise, 30, 20);
    brush.Paint(FloodSim.Tool.Lower, 60, 40);
    brush.Paint(FloodSim.Tool.Water, 45, 30);
    brush.Paint(FloodSim.Tool.Mangrove, 20, 45);
    brush.Paint(FloodSim.Tool.DrainPump, 70, 25);
    brush.Paint(FloodSim.Tool.Clear, 20, 45); // over the mangroves, so Clear has work

    // Probed at the stamp centres, not summed over the grid. The first version of
    // this summed, and the sum was worthless: raise and lower cancel each other
    // exactly, so it came back equal to the untouched terrain to six decimals and
    // would have passed with both tools broken.
    double eR = brush.Elev[20 * 96 + 30], eL = brush.Elev[40 * 96 + 60];
    double wW = brush.Water[30 * 96 + 45], wEdge = brush.Water[30 * 96 + 47];
    int mang = 0, drn = 0;
    for (int i = 0; i < brush.Elev.Length; i++)
    {
        if (brush.Mang[i] != 0) mang++;
        if (brush.Drn[i] != 0) drn++;
    }
    lines.Add(
        $"  PAINT {name} raise={F(eR)} lower={F(eL)} water={F(wW)} " +
        $"falloff={F(wEdge)} mang={mang} drn={drn}");

    // And again after the water has had somewhere to go, so the painted state is
    // checked through the CA rather than only at the moment it was stamped.
    for (int t = 0; t < 50; t++) brush.Step(0f, false);
    var ps = brush.Step(0f, false);
    lines.Add(
        $"  PAINT {name} t={ps.Tick} water={F(ps.Water)} flooded={ps.Flooded} " +
        $"mang={ps.MangroveCount} drn={ps.DrainCount}");
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
