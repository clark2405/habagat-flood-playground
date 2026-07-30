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
- Brush painting: raycast onto the terrain, cursor ring, all six tools, with the
  terrain and painted props rebuilt as needed
- UI: presets, camera views, tool palette, stats, storm, pause, rain slider
- Ambient occlusion, tuned to this world's scale

**The port is feature-complete against the web build.** What follows is polish and
the gaps listed below.
- One construction path: `WorldBuilder` is used by both the play scene and the
  screenshot harness, so a screenshot is evidence about what actually runs

## What is left

### Loose ends
- **The sun disc** (a glowing sphere, hidden on overcast presets via `EnvConfig.Sun`)
  is not built yet. The flag is already carried across.
- **Backdrop silhouettes are smooth-shaded**, where the reference sets
  `flatShading: true`. At 34-78% haze the difference is barely visible, which is why
  it was left; `Prim.Cone` would need a flat-normal variant to match exactly.
- **Nothing has been exercised live.** Painting, buttons, the slider and the camera
  all render correctly and compile, but no mouse has touched them — Play mode is out
  of reach of the headless harness. This is the single biggest untested area.
- **UI styling is plain.** Square panels and the built-in font, against the
  reference's rounded pills and icons. Legible and functional, not yet cozy.
- **No house tool.** The reference's `house` brush appends to React state that the
  scene rebuilds from; it needs the same houses list to exist on the Unity side.
- **No sound.** The reference plays a cue per tool.
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
