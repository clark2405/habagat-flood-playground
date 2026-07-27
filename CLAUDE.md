# Habagat 3D — flood simulation sandbox

Browser-based flood-preparedness sandbox for Philippine barangays. React + Three.js,
Vite, no backend. Cozy low-poly look, aiming at Paralives' environment style.

## Commands

```bash
npm run dev                                  # vite dev server
npm run build                                # production build
dotnet run --project unity/HabagatSim.Verify # C# sim fidelity check (see below)
```

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
storm, at 1600×900 on an RTX 4050.

## Gotchas already paid for

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
- **Lighting does not transfer numerically.** three.js folds a `1/PI` into its
  diffuse BRDF that URP's Lambert does not, so ambient carried across literally is
  roughly double. And the web build renders through ACES filmic tone mapping —
  every colour in the ramp was chosen against that curve, so URP needs a
  Tonemapping volume override or the same colours come out pale.

Render a headless screenshot to check any visual change:

```bash
Unity.exe -batchmode -quit -projectPath unity/HabagatUnity \
  -executeMethod HabagatEditor.SceneShot.Run \
  -preset coastal -ticks 200 -view iso -shotOut shot.png -logFile u.log
```

Note: no `-nographics` — it needs a real graphics device. `-view top` gives a plan
view, which is the only framing that makes an orientation mismatch unambiguous.

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
