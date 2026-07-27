using System;
using System.Globalization;
using System.IO;
using System.Text;
using Habagat;
using UnityEditor;

namespace HabagatEditor
{
    /// <summary>
    /// Runs the ported simulation inside the Unity editor and writes the same
    /// fingerprint that HabagatSim.Verify produces under plain dotnet.
    ///
    /// Compiling under dotnet is not by itself proof that the sim behaves the
    /// same in Unity: Unity uses its own scripting runtime and its own compiler
    /// settings, and float behaviour is exactly the kind of thing that can differ
    /// between them. Running the fingerprint here and diffing it against the
    /// JavaScript reference closes that gap.
    ///
    /// Invoke headlessly:
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt; \
    ///             -executeMethod HabagatEditor.SimVerify.Run \
    ///             -simOut &lt;path-to-write&gt;
    /// </summary>
    public static class SimVerify
    {
        private static string F(double v) => v.ToString("F6", CultureInfo.InvariantCulture);

        public static void Run()
        {
            string outPath = "sim-fingerprint.txt";
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-simOut") outPath = args[i + 1];

            var sb = new StringBuilder();

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

                sb.AppendLine($"TERRAIN {name} sum={F(sum)} min={F(min)} max={F(max)}");

                foreach (var p in new[] { 0, 1, 95, 96, 3000, 3071, 4096, 5000, 6143 })
                    sb.AppendLine($"  ELEV {name}[{p}]={F(elev[p])}");

                var sim = FloodSim.FromPreset(type, seed);
                for (int t = 0; t < 400; t++)
                {
                    bool storm = t >= 100 && t < 300;
                    float rain = t < 100 ? 4f : 0f;
                    if (t == 150) sim.BeginSurge();
                    var st = sim.Step(rain, storm);

                    if (t == 99 || t == 299 || t == 399)
                        sb.AppendLine(
                            $"  SIM {name} t={st.Tick} water={F(st.Water)} flooded={st.Flooded} " +
                            $"mang={st.MangroveCount} drn={st.DrainCount}");
                }
            }

            File.WriteAllText(outPath, sb.ToString());
            UnityEngine.Debug.Log($"[SimVerify] wrote fingerprint to {Path.GetFullPath(outPath)}");
        }
    }
}
