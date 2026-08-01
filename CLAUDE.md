# Habagat 3D — flood simulation sandbox

Browser-based flood-preparedness sandbox for Philippine barangays. React + Three.js,
Vite, no backend. Cozy low-poly look, aiming at Paralives' environment style.

## Commands

```bash
npm run dev                                  # vite dev server
npm run build                                # production build
dotnet run --project unity/HabagatSim.Verify # C# sim fidelity check (see below)
```

Unity, all headless (`Unity.exe -batchmode -quit -projectPath unity/HabagatUnity
-executeMethod <method>`):

| method | what it does |
| --- | --- |
| `HabagatEditor.MakeScene.Run` | regenerates `Assets/Scenes/Habagat.unity` |
| `HabagatEditor.SceneShot.Run` | renders a PNG; `-bench N` times the frame loop |
| `HabagatEditor.BuildPlayer.Run` | standalone player, `-buildOut <dir>` |
| `HabagatEditor.SimVerify.Run` | the fingerprint, inside Unity |

Then `Habagat.exe -selftest -report r.txt` runs 17 checks against the built player
— brush, buttons, preset switching, frame rate — and exits non-zero on failure.
It is the only check that covers anything a still frame cannot show.

There is no test suite. Verification is done by (a) driving headless Chromium and
looking at screenshots, and (b) the C# fingerprint diff.

## Layout

Two files carry almost everything:

- `src/FloodPlayground.jsx` (~690 lines) — terrain presets, the flood CA (`step`),
  brush tools, and all React UI.
- `src/ThreeCanvas.jsx` (~2900 lines) — the entire 3D scene: terrain and water
  meshes, the surrounding world, every prop, weather, post-processing. Numbered
  section comments (`// 9b. ...`) are the navigation aid.

Grid is `W=96 × H=64`, one cell = one world unit, `SEA_LEVEL = 0`.

## Invariants — break these and the scene visibly regresses

**World scale: 1 cell ≈ 4 m, so a house is ~2.5 units, a palm ~4.4.** Props were
originally built at ~0.75u — smaller than the tile they stood on — which made the
whole map read as confetti. Anything new must be built to this scale.

**Prop placement must be deterministic.** Use `seededRng(strSeed(...))`, never
`Math.random()`. Placing a single house re-runs the whole scene effect; unseeded
scatter reshuffles the entire world every time.

**The world does not end at the sandbox.** The `outerland` ring mesh (section 9b)
continues the terrain outward using the *same* material and colour ramp, welded at
ring 0. The sky's horizon band is painted the exact fog colour so land dissolves
into sky with no seam. Scatter and detail must continue past the border too — that
continuity is what stops the background reading as a flat detached backdrop.

**Density is instanced.** ~1500 scatter objects exist; they cost one draw call per
*kind* via `scatterInstanced` + `flattenProp` + `getProto` caching. Adding props as
individual meshes will wreck the frame budget. Houses are merged per style variant
for the same reason.

**Terrain is smooth-shaded, not flat.** Flat shading a heightmap corrugates every
slope into a herringbone. Normals come from analytic central differences, not
`computeVertexNormals()`.

Baseline performance to hold: **115–143 fps** across all four presets, calm and
storm, at 1600×900 on an RTX 4050. The Unity player measures **~390 fps calm / ~175
storm** at 1280×720 (`Habagat.exe -selftest`). Take that reading with nothing else
on the GPU: the first figures recorded here were 150/142, measured with the editor
still running, and an A/B against an unchanged build put the real number 2.5x
higher. Measure a delta against a build from the same sitting, never against a
number written down on another day.

## Gotchas already paid for

- **Ambient occlusion fails silently in BOTH builds, for different reasons.** In
  Unity, URP renders the SSAO buffer whether or not anything consumes it: the AO is
  applied by the shader that samples it, so a custom lit shader without
  `#pragma multi_compile_fragment _ _SCREEN_SPACE_OCCLUSION` and a
  `GetScreenSpaceAmbientOcclusion` call discards it entirely. Every surface in this
  scene uses `VertexColorLit`, so for a long time sweeping SSAO radius and intensity
  through their whole range changed not one pixel. If an AO setting appears to do
  nothing, check that something is sampling it before touching the numbers.
- **A shader found only by `Shader.Find` gets stripped from a build.** Every material
  here is made in code, so from Unity's asset-reference point of view nothing uses
  these shaders. `Shader.Find` then returns null in the player, `WorldBuilder` bails,
  and the build launches to an EMPTY SCENE while reporting a successful build with
  zero errors. `BuildPlayer.EnsureShadersIncluded` registers all four as
  always-included and runs automatically before every build.
- **A generated scene freezes serialized fields.** `Assets/Scenes/Habagat.unity` is
  authored by `Habagat/Rebuild Play Scene`; once it exists it carries the values that
  were written into it, and editing a C# field initialiser changes nothing. Set
  anything that matters explicitly in `MakeScene` and regenerate.
- **GTAO `thickness` matters more than `radius`.** Left at its default of 1.0 the
  horizon search passes straight through props 2–4 units deep and the AO buffer
  comes back blank regardless of radius or blend intensity. It is set to match the
  geometry depth.
- The fog `near` must sit inside the visible frame (72–100u), or the mid-ground
  renders as flat cardboard.
- Vite's HMR error overlay persists over the canvas after a failed build — force a
  reload before trusting a screenshot.

## Code style

Comments explain **why**, and frequently record what went wrong before ("the old
version put two long boxes across the hull, which looked like a hammer"). Match
that voice: a comment that only restates the code is not worth adding.

## Branches

`unity-port` was merged into `main` (PR #1), so **both now carry the web build and
the Unity port**. Ongoing Unity work continues on `unity-port` and merges back.

The web build in `src/` is not deprecated by the port. It remains the reference
implementation: the C# simulation is verified against it, and the art direction
recorded above was worked out there first.

## The Unity port

Unity 6.3 LTS (`6000.3.20f1`), URP, at `unity/HabagatUnity/`. Two things to know
before touching the rendering side:

- **Handedness.** three.js is right-handed, Unity is left-handed, so identical
  coordinates give a mirrored map. `TerrainMeshBuilder.Vz` negates Z, triangle
  winding is swapped to match the reflected orientation, and the analytic normal's
  `gz` term drops its leading minus. Camera and sun are mirrored too. Anything new
  that positions objects in world space must follow the same convention.

  The mirror reaches further than positions, and both extensions cost real time to
  find:

  **Noise inputs must be flipped back.** A point at Unity `z` is at `-z` in the
  reference, so anything feeding a world position into `Fbm` has to go through
  `TerrainColors.RefZ` first. Sampling at the Unity coordinate reads a different
  part of the noise field — not the reference mirrored, an unrelated landform that
  merely looks plausible. The outerland's zero-at-`t=0` seam ramp hides this
  completely at ring 0, so checking the seam proves nothing.

  **Prop geometry must be mirrored, not just prop placement.** `PropBuilder.Build`
  flips Z and reverses winding on the way out. Without it every building keeps its
  original chirality and presents its back to the default isometric camera —
  porticos, doors and windows all end up on the hidden side.
- **Lighting does not transfer numerically.** three.js folds a `1/PI` into its
  diffuse BRDF that URP's Lambert does not, so ambient carried across literally is
  roughly double. And the web build renders through ACES filmic tone mapping —
  every colour in the ramp was chosen against that curve, so URP needs a
  Tonemapping volume override or the same colours come out pale.

**Props are ported and placed bit-identically to the web build.** `Prim` supplies
parametric primitives at three.js's angular convention (Unity ships no cone, and the
low segment counts are the art direction), `PropBuilder` bakes each part's colour
into vertex colours so a whole prop is one mesh and one draw call, `PropLibrary`
holds the creators and `PropScatter`/`WorldDress` the placement. The placement RNG
(`JsMath.Rng`, mulberry32) is bit-exact with the browser's, so the *order and count*
of draws inside a creator is load-bearing — an extra `Next()` shifts every prop
placed after it.

**There is a runtime scene now**: `Assets/Scenes/Habagat.unity`, built from code by
`Habagat/Rebuild Play Scene`. `HabagatWorld` owns the sim and ticks it; `WorldBuilder`
constructs terrain, outerland, props, water and atmosphere. Both the play scene and
the screenshot harness go through `WorldBuilder`, and they must stay that way — when
the harness assembled its own scene, every screenshot was evidence about the harness
rather than about anything you could press Play on.

`TASK.md` at the repo root is the running list of what is left.

Render a headless screenshot to check any visual change:

```bash
Unity.exe -batchmode -quit -projectPath unity/HabagatUnity \
  -executeMethod HabagatEditor.SceneShot.Run \
  -preset coastal -ticks 200 -view iso -shotOut shot.png -logFile u.log
```

Note: no `-nographics` — it needs a real graphics device. `-view top` gives a plan
view, which is the only framing that makes an orientation mismatch unambiguous.
`-focus x,z -dist d` frames a close-up on any world position — use it instead of
reasoning about what a small smudge in the wide shot must be. `-props 0` renders
bare landform.

## The C# simulation port

Engine-agnostic — nothing references `UnityEngine`. Two JavaScript behaviours are
reproduced *deliberately*; both were caught by fingerprint diffing, not review:

1. **The preset RNG relies on doubles losing precision.** In
   `s = (s * 1103515245 + 12345) & 0x7fffffff`, `s` approaches 2^31 so the product
   reaches ~2.3e18 — past 2^53, where doubles stop being exact. The multiply
   *discards low bits*. A C# port using `int`/`long` is exact, does not lose them,
   and generates entirely different terrain. See `JsMath.Lcg`.
2. **JS narrows to float32 only on the store.** State lives in `Float32Array` but
   arithmetic happens in double. Using `float` locals rounds at every operation
   instead — invisible per-op, clearly divergent after 400 ticks. Every
   intermediate is `double`, cast to `float` only on assignment.

Any change to the sim in either language must keep `HabagatSim.Verify` byte-identical
to the JS reference across all 52 fingerprint lines.

See `unity/README.md` for the current blocker (Defender causing `EPERM` on package
resolution) and remaining port steps.
