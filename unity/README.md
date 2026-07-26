# Unity port

Lives on the `unity-port` branch. `main` keeps the web build only.

```
unity/
  HabagatUnity/                     the Unity 6.3 LTS project (URP)
    Assets/Scripts/Sim/             the ported simulation — single source of truth
      JsMath.cs                     JavaScript numeric semantics the presets depend on
      TerrainPresets.cs             heightmap generation (makeTerrainPreset)
      FloodSim.cs                   the shallow-water CA (step) and brush tools
    Assets/Editor/SimVerify.cs      runs the fingerprint inside Unity
  HabagatSim.Verify/                same fingerprint under plain dotnet
```

The simulation is **not** duplicated. It lives in `Assets/Scripts/Sim/` and the
dotnet harness compiles those same files via a linked glob in its `.csproj`. If
it were copied, a fix applied on one side would silently stop being the thing the
other side verifies — which is the whole point of the harness.

Nothing in `Assets/Scripts/Sim/` references `UnityEngine`, so it builds under
both toolchains untouched.

## Running the fidelity check

Under plain dotnet, no Unity required:

```bash
dotnet run --project unity/HabagatSim.Verify
```

Inside Unity, headless:

```bash
"C:/Program Files/Unity/Hub/Editor/6000.3.20f1/Editor/Unity.exe" \
  -batchmode -quit -nographics \
  -projectPath <repo>/unity/HabagatUnity \
  -executeMethod HabagatEditor.SimVerify.Run \
  -simOut <path>/unity-fp.txt -logFile <path>/unity.log
```

Both print terrain sums, spot elevations and simulation state at ticks 100, 300
and 400 for all four presets, under a fixed weather script (rain for 100 ticks,
storm from 100-300, a surge at 150). Diff either against the JavaScript
reference. dotnet currently matches the JS byte-for-byte on all 52 lines; the
in-Unity run is still blocked (see below).

## Two porting hazards worth knowing about

**1. The preset RNG relies on JavaScript losing precision.**

The generator is `s = (s * 1103515245 + 12345) & 0x7fffffff`. In JS every number
is a double, and with `s` approaching 2^31 the product reaches ~2.3e18 — well
past 2^53, where doubles stop representing integers exactly. The multiply
*discards low bits*, and `&` then coerces that inexact double to int32.

A natural C# port using `int` or `long` is exact, does **not** lose those bits,
and therefore produces a completely different random stream and completely
different terrain. `JsMath.Lcg` reproduces the imprecision deliberately.

**2. JS rounds to float32 only on the store.**

State lives in `Float32Array`, but all arithmetic happens in double; narrowing
occurs solely when writing into the array. The obvious C# translation using
`float` locals rounds at *every* operation instead. That difference is invisible
per-operation and clearly visible after 400 ticks — it was caught by the
fingerprint diff, not by reading the code. Every intermediate is a `double`, cast
to `float` only on assignment.

## Known issue: package resolution fails with EPERM

Headless first-open fails to resolve packages on this machine:

```
EPERM: operation not permitted, rename
  Library/PackageCache/.tmp-*/package -> Library/PackageCache/com.unity.*@<hash>
```

...for every package. Windows Defender real-time protection is enabled and holds
handles on freshly extracted files, so Unity's rename loses the race. Deleting
`Library/` and clearing read-only attributes does not help; there were no
read-only files to begin with.

Fix (needs administrator):

```powershell
Add-MpPreference -ExclusionPath "D:\Codes\habagat-flood-playground\unity"
Add-MpPreference -ExclusionPath "C:\Program Files\Unity\Hub\Editor"
```

This is worth doing regardless — Defender scanning `Library/` is a well-known
cause of slow Unity imports.

## Status

- [x] Flood simulation (`step`) — verified against JS under dotnet
- [x] Terrain presets — verified against JS under dotnet
- [x] Brush tools (`handlePaint`)
- [x] Unity 6.3 LTS project scaffolded from the Universal 3D (URP) template
- [ ] First successful Unity import (blocked on the EPERM above)
- [ ] Fingerprint confirmed *inside* Unity
- [ ] Terrain / water mesh generation
- [ ] Props and scatter — art spec is in `src/ThreeCanvas.jsx`
- [ ] UI — the piece that genuinely has to be rebuilt

The web build in `src/` remains the reference implementation and is not
deprecated by this directory.
