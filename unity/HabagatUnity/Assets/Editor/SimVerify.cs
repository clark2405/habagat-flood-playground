using System;
using System.IO;
using System.Text;
using Habagat;
using UnityEditor;

namespace HabagatEditor
{
    /// <summary>
    /// Runs the fingerprint inside the Unity editor and checks it against the same
    /// JavaScript reference the dotnet harness uses.
    ///
    /// Compiling under dotnet is not by itself proof that the sim behaves the same
    /// in Unity: Unity has its own scripting runtime and its own compiler settings,
    /// and float behaviour is exactly the kind of thing that can differ between
    /// them. This is the run that answers that.
    ///
    /// The lines come from <see cref="SimFingerprint"/> rather than being generated
    /// here. They used to be generated here, in a hand-written copy of the dotnet
    /// harness's loop — and the copy had silently fallen behind, still sampling
    /// three ticks per preset with no brush section at all. Two harnesses that are
    /// supposed to agree cannot each own their own definition of what they check.
    ///
    /// Exits non-zero on drift, so a batch run fails rather than writing a file
    /// nobody reads:
    ///   Unity.exe -batchmode -quit -projectPath &lt;proj&gt; \
    ///             -executeMethod HabagatEditor.SimVerify.Run \
    ///             [-simOut &lt;path&gt;]
    /// </summary>
    public static class SimVerify
    {
        [MenuItem("Habagat/Verify Simulation Fingerprint")]
        public static void Run()
        {
            string outPath = null;
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "-simOut") outPath = args[i + 1];

            var lines = SimFingerprint.Build();

            if (outPath != null)
            {
                File.WriteAllText(outPath, string.Join("\n", lines) + "\n");
                UnityEngine.Debug.Log($"[SimVerify] wrote fingerprint to {Path.GetFullPath(outPath)}");
            }

            // Application.dataPath is <project>/Assets, so the reference sits two
            // levels up beside the dotnet harness. Resolved rather than hardcoded so
            // this works whatever directory Unity was launched from.
            string expected = Path.GetFullPath(Path.Combine(
                UnityEngine.Application.dataPath, "..", "..", "HabagatSim.Verify", "fingerprint.txt"));

            var report = new StringBuilder();
            int bad = SimFingerprint.Compare(lines, expected, m => report.AppendLine(m));

            if (bad < 0)
            {
                UnityEngine.Debug.LogError($"[SimVerify] {report}");
                EditorApplication.Exit(2);
                return;
            }

            if (bad > 0)
            {
                UnityEngine.Debug.LogError(
                    $"[SimVerify] FAIL — {bad} lines diverge from the JS reference.\n{report}");
                EditorApplication.Exit(1);
                return;
            }

            UnityEngine.Debug.Log($"[SimVerify] OK — {lines.Count} lines identical to the JS reference.");
        }
    }
}
