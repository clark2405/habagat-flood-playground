# Habagat — what's left

Running list for the Unity port. The web build in `src/` is the reference: for
almost everything below, the answer already exists there and the job is to carry it
across. See `CLAUDE.md` for the invariants and `unity/README.md` for the porting
hazards that have already cost time.

## Done

- Flood simulation and terrain presets — byte-identical to JS on all 52 fingerprint
  lines, verified under both dotnet and Unity's own runtime
- Terrain, water and outerland meshes; outerland heights now bit-identical too
- Every prop in `ThreeCanvas.jsx`: buildings, vegetation, set dressing, street
  furniture, boats, mangroves, drains, and the world dressing past the border
- Placement bit-exact with the browser (shared mulberry32 stream)
- A runtime scene — `Assets/Scenes/Habagat.unity`, press Play
- Weather: rain, ground wetness, storm sky, lightning, boats riding the swell
- The world outside the sandbox floods with the play area, so a storm no longer
  renders as a rectangle of water in a dry landscape
- Gradient sky dome, per-preset atmosphere, and distant backdrop silhouettes
- One construction path: `WorldBuilder` is used by both the play scene and the
  screenshot harness, so a screenshot is evidence about what actually runs

## Next up, in order

### 1. Interaction  ← next
Reference: §13 (brush cursor ring) and `handlePaint` in `FloodPlayground.jsx`.

Raycast onto the terrain, convert the hit to a grid cell, drive `FloodSim.Paint`.
The tools already exist in C# and are fingerprint-verified; this is the input
plumbing and the cursor ring. Painting must trigger a props rebuild.

### 2. UI
The one piece that is genuinely a rewrite rather than a port: preset switcher,
tool palette, stats readout, storm button, rain slider. React does not translate.

### 3. Loose ends
- **The sun disc** (a glowing sphere, hidden on overcast presets via `EnvConfig.Sun`)
  is not built yet. The flag is already carried across.
- **Backdrop silhouettes are smooth-shaded**, where the reference sets
  `flatShading: true`. At 34-78% haze the difference is barely visible, which is why
  it was left; `Prim.Cone` would need a flat-normal variant to match exactly.
- **Mangroves and drains** are built once at world-build time and never rebuilt,
  which blocks interactive painting. They do not move, so they can stay baked — they
  just need a rebuild trigger.
- **Ambient occlusion.** The web build's GTAO pass is a large part of why props sit
  in the ground rather than float on it; Unity has no equivalent yet. `thickness`
  mattering more than `radius` is recorded in `CLAUDE.md`.
- **Performance.** Nothing has been profiled. The web baseline to beat is 115–143
  fps across all four presets at 1600×900 on an RTX 4050.
- **The `-view plan` fog override** is the last harness-only branch left in
  `SceneShot`; everything else now comes from `WorldBuilder`.

## Verifying

```bash
dotnet run --project unity/HabagatSim.Verify     # sim fidelity, must stay identical

Unity.exe -batchmode -quit -projectPath unity/HabagatUnity \
  -executeMethod HabagatEditor.SceneShot.Run \
  -preset coastal -ticks 200 -view iso -shotOut shot.png -logFile u.log
```

No `-nographics` — it needs a real graphics device. `-focus x,z -dist d` frames a
close-up on any world position; use it instead of reasoning about what a smudge in
the wide shot must be. `-props 0` renders bare landform.

Two habits worth keeping, both learned the hard way here:

- **Compare against the running web build, not against expectation.** Two bugs were
  found this way and one imagined bug was disproved.
- **Matching the reference proves the port is faithful, not that it is right.** The
  urban roads were invisible in both builds for exactly that reason.
