# Habagat 3D — flood simulation sandbox

Browser-based flood-preparedness sandbox for Philippine barangays. React + Three.js,
Vite, no backend. Cozy low-poly look, aiming at Paralives' environment style.

## Commands

```bash
npm run dev                                  # vite dev server
npm run build                                # production build
npm run fingerprint                          # regenerate the JS reference fingerprint
dotnet run --project unity/HabagatSim.Verify # C# sim fidelity check — FAILS on drift
```

Unity, all headless (`Unity.exe -batchmode -quit -projectPath unity/HabagatUnity
-executeMethod <method>`):

| method | what it does |
| --- | --- |
| `HabagatEditor.MakeScene.Run` | regenerates `Assets/Scenes/Habagat.unity` |
| `HabagatEditor.SceneShot.Run` | renders a PNG; `-bench N` times the frame loop |
| `HabagatEditor.BuildPlayer.Run` | standalone player, `-buildOut <dir>` |
| `HabagatEditor.BuildPlayer.RunWeb` | WebGL build — the only one that reaches a phone today |
| `HabagatEditor.BuildPlayer.RunAndroid` / `.RunIOS` | scaffolded; neither module is installed |
| `HabagatEditor.SimVerify.Run` | the fingerprint, inside Unity |

Then `Habagat.exe -selftest -report r.txt` runs 36 checks against the built player
— brush, buttons, preset switching, frame rate, sound, touch, both UI densities —
and exits non-zero on failure. It is the only check that covers anything a still
frame cannot show.

**A synthetic click is a real click.** The house test aimed wherever its projection
landed; once the compact layout made the top bar bigger, that point sat over the
*Urban preset button*, so the test switched presets mid-run. The visible symptom
was touch painting nothing — because the rebuild replaced `Sim.Elev` and the test
was still comparing against the array it had captured earlier. Read state through
`world.Sim` each time, and keep synthetic input clear of the UI.

Input is synthesised through the Input System's own event queue, including a
`Touchscreen` added at runtime, so the raycasts and grid mapping run exactly as
they do for real hardware. Two traps in writing those: queue a multi-finger
gesture **fully before advancing a frame**, or the gap between the two presses is
a legitimate one-finger stroke that will paint; and a synthesised touch has no
backend to compute `delta`, so it must be supplied or every gesture reads as
zero movement.

Anything that eases rather than snaps must be waited out **in seconds, not
frames**. The rain fade is an exponential approach with a 0.5 s time constant, so
a fixed frame count lands on a different point of the curve on every machine: 90
frames read 0.064 at 409 fps, which is the right answer for t=0.22 s and looks
exactly like a broken fade.

There is no test suite. Verification is done by (a) driving headless Chromium and
looking at screenshots, and (b) the C# fingerprint diff.

## Layout

Two files carry almost everything:

- `src/sim.js` — the simulation with no React on it: terrain presets, `createSim`,
  and the flood CA (`step`). Split out so it can run under plain node, which is what
  makes the reference fingerprint reproducible.
- `src/FloodPlayground.jsx` (~415 lines) — brush tools and all React UI. Its `step`
  is now a four-line wrapper that calls `sim.js` and pushes the result into state.
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
- **A WebGL build defaults to Brotli, which no plain static server can serve.** A
  `.br` file only loads if the response carries `Content-Encoding: br`, and
  `python -m http.server` and the GitHub Pages root do not send it. The page then
  fails with a decompression error that names nothing relevant.
  `BuildPlayer.ConfigureWeb` turns compression off; the build is larger and always
  loads.
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
- **The UI has two layouts, and DPI picks between them.** `HabagatUI.DensityFor`
  divides `Screen.width` by the reported DPI, because device pixels cannot answer
  the question — a 2x phone is 1688 px wide and would look roomier than a 1280 px
  laptop while being a third the size. Below 1100 reference pixels the tool pills
  lose their labels and keep their icons. Two consequences: a 125%-scaled Windows
  desktop reports 120 DPI and picks Compact at 1280x720, and the editor harness
  must be told the width via `ui.layoutWidthOverride` (`-uiwidth`), because in
  batch mode `Screen` is the editor's own surface, not the RenderTexture.
- **Touch and mouse coexist.** A Windows laptop reports both devices, so anything
  reading input must take one gesture per frame, not add them together — the
  camera's `TouchUpdate` returns whether it consumed the frame for exactly that
  reason. Touch deltas are also normalised against screen height rather than used
  as raw pixels, or the same swipe means very different things on a 1080p monitor
  and a dense phone panel.
- **A camera built in code has no ears.** `new GameObject` + `AddComponent<Camera>()`
  does not bring the `AudioListener` that the editor's default camera object ships
  with, and without one every cue plays correctly and inaudibly — there is no
  warning, because nothing is wrong. `MakeScene` adds one to the camera and
  `SoundEngine.Awake` adds a fallback if it finds none.
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

**Sound is synthesised, not imported.** `Assets/Scripts/Audio/Synth.cs` generates
PCM — band-limited oscillators, RBJ biquads, exponential envelopes — and
`SoundEngine` bakes each cue into an `AudioClip` once at `Awake`. Two things carry
real cost if changed casually: waveforms are summed from harmonics because a naive
square or saw aliases badly on the low sweeps used here, and the rain loop is
filtered over its buffer **twice** so the biquad's state matches at the wrap
(single-pass leaves a tick every two seconds). The reference fires a cue from
inside the per-cell paint loop — up to 29 voices per call; that is throttled to one
per 90 ms here, which is the one deliberate divergence.

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
bare landform. `-view sun` points at the sun disc, which sits ~60° off every normal
framing (in the reference too) and so cannot be confirmed any other way.

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
to the JS reference across all 52 fingerprint lines. **This is now enforced, not
remembered.** The harness compares itself against
`unity/HabagatSim.Verify/fingerprint.txt` and exits non-zero naming every line that
diverges; that file is generated from the JavaScript by `npm run fingerprint`, which
runs `src/sim.js` — the same module the web build plays.

That arrangement is new, and it immediately found a real divergence. The sim used to
live *inside* the React component, so the reference half of the comparison could not
be produced at all and "verified against JS" rested on a diff someone did once and
did not keep. `BeginSurge` defaulted to **260 ticks against the reference's 200** —
about 5% more standing water on every preset at t=400. It survived because the
fingerprint samples at t=100 and t=300 and the surge starts at t=150, so both sides
are still surging at both sample points. Only the t=400 line ever saw it.

Regenerate the reference **only** when the JavaScript deliberately changes. If C#
drifts, fix C#.

See `unity/README.md` for the current blocker (Defender causing `EPERM` on package
resolution) and remaining port steps.
