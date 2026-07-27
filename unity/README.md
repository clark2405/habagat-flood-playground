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
reference. **Both currently match the JS byte-for-byte on all 52 lines.**

Running it under Unity as well as dotnet is not redundant: Unity has its own
scripting runtime and compiler settings, and float behaviour is exactly the kind
of thing that can differ between them. dotnet agreeing proves nothing about what
the editor does.


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

## Setup gotchas already paid for

**Defender breaks package resolution.** First-open failed on all 20 packages with
`EPERM: operation not permitted, rename` in `Library/PackageCache`. Real-time
protection holds handles on freshly extracted files and Unity loses the rename
race. Deleting `Library/` and clearing read-only attributes did not help — there
were no read-only files. Fix, as administrator:

```powershell
Add-MpPreference -ExclusionPath "<repo>\unity"
Add-MpPreference -ExclusionPath "C:\Program Files\Unity\Hub\Editor"
```

Worth keeping regardless: Defender scanning `Library/` is a well-known cause of
slow Unity imports.

**The URP template ships a broken Input System pin.** It requests
`com.unity.inputsystem` 1.12.0, which references `BuildTarget.ReservedCFE` — a
member that does not exist in 6000.3. Every script compile failed, and because
the error is raised inside a Unity package rather than project code it looks far
more alarming than it is. Bumped to 1.20.0 in `Packages/manifest.json`.

## Status

- [x] Flood simulation (`step`) — verified against JS
- [x] Terrain presets — verified against JS
- [x] Brush tools (`handlePaint`)
- [x] Unity 6.3 LTS project scaffolded from the Universal 3D (URP) template
- [x] Unity project imports cleanly
- [x] Fingerprint confirmed *inside* Unity — identical on all 52 lines
- [ ] Terrain / water mesh generation
- [ ] Props and scatter — art spec is in `src/ThreeCanvas.jsx`
- [ ] UI — the piece that genuinely has to be rebuilt

The web build in `src/` remains the reference implementation and is not
deprecated by this directory.
