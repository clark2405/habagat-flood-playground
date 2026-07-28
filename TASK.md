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
- One construction path: `WorldBuilder` is used by both the play scene and the
  screenshot harness, so a screenshot is evidence about what actually runs

## Next up, in order

### 1. Flood the world outside the sandbox  ← next
Reference: the block at `ThreeCanvas.jsx` ~2303, "The world outside the sandbox,
reacting to the same weather".

**This is now visibly needed.** With weather in, a heavy storm fills the play area
and stops dead at the border, so the flood renders as a hard-edged rectangle of
water sitting in a bone-dry landscape — the most glaring possible way to advertise
where the sandbox ends, and exactly what the outerland exists to prevent. The web
build does not have this problem because its open water welds to the sandbox's own
water level at ring 0 and relaxes outward to sea level.

It needs four things that do not exist on the C# side yet:

- `bgDepth` — mean rainwater depth over the play area's land cells, eased at 0.08,
  which is the level the outside world floods to
- `outerBorderSurf[p]` — per border point, taken from the *relaxed* water surface so
  the two sheets agree exactly. Only low-lying shoreline cells (`elev <= SEA_LEVEL +
  0.8`) may raise it, or rain pooling on a hillside drags the horizon's water up
- `waterRelax[j]` — per-ring falloff from the border level back to sea level
- `outSmoothY` — smoothed ring heights, so flooded land meets the open sea flush
  instead of standing above it

`WaterMeshBuilder` computes the relaxed surface internally and would need to expose
it. Do not half-build this: the four arrays are coupled, and a partial version will
look worse than the hard edge.

### 2. Weather — done, with one gap
Rain particles, storm slant, ground wetness, water smoothness, fog and lightning
all landed. Boats now ride the swell.

Still missing: **per-preset environment**. `WorldBuilder` and `Weather` hardcode
`ENV.coastal`, so fog colour, sun tint and the clear-sky palette are identical on
all four maps. Storm values are shared in the reference, so only the clear-weather
side needs the table.

Also: mangroves and drains are built once at world-build time and never rebuilt,
which blocks interactive painting. Unlike the boats they do not move, so they can
stay baked — they just need a rebuild trigger.

### 3. Sky and backdrop
Reference: §4 (gradient sky dome) and §7 (distant silhouettes).

Right now the camera just clears to the fog colour. The dome's horizon band must be
painted the *exact* fog colour — that identity is what makes land dissolve into sky
with no seam.

### 4. Interaction
Reference: §13 (brush cursor ring) and `handlePaint` in `FloodPlayground.jsx`.

Raycast onto the terrain, convert the hit to a grid cell, drive `FloodSim.Paint`.
The tools already exist in C# and are fingerprint-verified; this is the input
plumbing and the cursor ring. Painting must trigger a props rebuild.

### 5. UI
The one piece that is genuinely a rewrite rather than a port: preset switcher,
tool palette, stats readout, storm button, rain slider. React does not translate.

### 6. Loose ends
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
