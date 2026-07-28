# Unity port

Merged into `main` via PR #1; ongoing work continues on `unity-port`.

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

## Resolved: the outerland outline is NOT too straight

`SceneShot -view plan` shows the surrounding coastline running nearly straight
for hundreds of units, with pale streaks trailing south of the play area. That
looks wrong next to the web build's iso views, which read as bays and inlets.

It is not wrong. Rendering the **web build from the identical plan view** — 460
units up, 40° FOV, fog pinned off — produces the same nearly-straight coastline
and the same streaks. The port is faithful.

What makes the reference look organic at normal viewing angles is props, fog and
the low camera, not a wigglier coastline. Do not "fix" this by inventing extra
relief; it would diverge from the reference.

To reproduce that comparison, temporarily expose the scene from `ThreeCanvas.jsx`
(`window.__dbg = { camera, controls, scene, renderer }` after `controls.update()`)
and drive it from the browser. Two traps:

- `controls.update()` clamps to `maxDistance` (185), so raise it first.
- The render loop **lerps `scene.fog.near`/`far` toward the preset every frame**,
  so assigning them is undone before the next paint. Pin them with
  `Object.defineProperty(scene.fog, 'near', { get: () => 99000, set: () => {} })`,
  leaving `fog.color` writable since the loop copies into it.

## The mirror applies to noise inputs too, not just positions

Found while porting the world dressing, and the most expensive bug of the port so
far. The map is reflected into Unity's left-handed space, so a point at Unity `z`
is at `-z` in the reference. **Anything that feeds a world position into `Fbm` has
to flip Z back first** (`TerrainColors.RefZ`) — otherwise it reads a different part
of the noise field, and the result is not the reference mirrored, it is an
unrelated landform that merely looks plausible.

Two things kept this hidden:

- The outerland's seam multiplies every noise term by a ramp that is zero at
  `t = 0`, so **ring 0 matches the reference exactly no matter what**. Spot-checking
  the seam proves nothing about the rest.
- The earlier plan-view check compared the *outline* and asked only whether it was
  organic. It was. It was also the wrong outline.

It surfaced indirectly: every distant house rendered purple instead of terracotta.
That colour is one RNG draw from a stream shared with the placement sampler, so a
height field that differs shifts which samples pass `h < 0.6`, and every draw after
it. Cosmetic symptom, structural cause.

Two smaller precision rules came out of the same hunt, both correct by construction
and worth keeping:

- `ringD` and `outBaseY` are `Float32Array` in JS, so the C# copies are `float[]`.
  They feed threshold tests (`d > 215`, `h < 0.6`), and a double compares
  differently near the boundary.
- Border coordinates are plain JS numbers — doubles. `TerrainMeshBuilder.VxD/VzD`
  exist so anything feeding a *decision* is computed in double, while vertices stay
  float.

### How to check a stream is really in sync

Counts alone are not evidence. The first comparison showed `tree=620 scrub=520
house=150` on both sides and looked like proof — but all three were at their caps,
so they would have matched under almost any stream. What actually localised it was
instrumenting both sides with a **draw counter** (`28992` vs `28757`) and then
accepted-sample and per-branch counts, which pinned the divergence to the `h < 0.6`
test rather than to the creators.

## A bug found in the reference, fixed in both builds

The urban preset's roads were invisible — in the web build too. The ribbon
generator wound its triangles `(a,c,b)/(b,c,d)`, which points an east-west
carriageway's geometric normal at the ground, so every road was back-face culled.
Only the north-south cross streets survived, because a ribbon running the other way
winds the opposite way. Corrected to `(a,b,c)/(b,d,c)` in `ThreeCanvas.jsx` and
`PropScatter`.

Worth noting how it was nearly missed: the Unity render matched the reference
exactly, which looked like success. Faithfulness to the reference is only evidence
of a correct *port* — it says nothing about whether the reference is right.

## Status

- [x] Flood simulation (`step`) — verified against JS
- [x] Terrain presets — verified against JS
- [x] Brush tools (`handlePaint`)
- [x] Unity 6.3 LTS project scaffolded from the Universal 3D (URP) template
- [x] Unity project imports cleanly
- [x] Fingerprint confirmed *inside* Unity — identical on all 52 lines
- [x] Terrain and water mesh generation
- [x] Outerland ring mesh + open water — heights now bit-identical to the reference
- [x] Props: buildings, vegetation, set dressing — placement bit-identical to the web build
- [x] World dressing outside the sandbox — forest, hamlets and city sprawl to the horizon
- [x] Basketball court, bangka boats, mangroves and drain pumps
- [x] Urban streets, footbridges, utility poles, market stalls, tricycles
- [x] Runtime scene — `Assets/Scenes/Habagat.unity`, shared construction path
- [ ] Weather and the animation loop
- [ ] Sky dome and backdrop silhouettes
- [ ] Interaction — raycast painting and the brush cursor
- [ ] UI — the piece that genuinely has to be rebuilt

See `TASK.md` at the repo root for the ordered list and the known blockers.

The web build in `src/` remains the reference implementation and is not
deprecated by this directory.
