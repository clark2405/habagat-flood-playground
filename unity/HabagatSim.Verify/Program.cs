using System;
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
// The lines themselves come from Habagat.SimFingerprint, which lives beside the
// simulation and is shared with the editor's SimVerify. This file is only the
// dotnet front end.
//
//   dotnet run --project unity/HabagatSim.Verify              # verify, non-zero on drift
//   dotnet run --project unity/HabagatSim.Verify -- --print   # just emit the lines
//   npm run fingerprint                                       # regenerate the reference

var lines = SimFingerprint.Build();

if (Array.IndexOf(args, "--print") >= 0)
{
    foreach (var line in lines) Console.WriteLine(line);
    return 0;
}

// Alongside the binary, so the check does not depend on the working directory
// dotnet happened to be run from. The csproj copies it there.
var expectedPath = Path.Combine(AppContext.BaseDirectory, "fingerprint.txt");
int bad = SimFingerprint.Compare(lines, expectedPath, Console.Error.WriteLine);

if (bad < 0) return 2;
if (bad > 0)
{
    Console.Error.WriteLine($"\nFAIL — {bad} lines diverge from the JS reference.");
    return 1;
}

Console.WriteLine($"OK — {lines.Count} lines identical to the JS reference.");
return 0;
