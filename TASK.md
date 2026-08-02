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
- All eight brushes, including Barangay Home

**No features are missing against the web build.** What is left is polish.
- Ambient occlusion, tuned to this world's scale
- Sun disc, and flat-shaded backdrop mountains

**The port is feature-complete against the web build.** What follows is polish and
the gaps listed below.
- One construction path: `WorldBuilder` is used by both the play scene and the
  screenshot harness, so a screenshot is evidence about what actually runs

## What is left

### Loose ends
- **A human still has not clicked anything.** `Habagat.exe -selftest` drives
  synthetic mouse events through the real input path and checks 17 behaviours, but
  it proves the paths execute and change what they should — not that the result
  feels right. Camera feel, brush responsiveness and UI scale are unjudged.
- **UI has no icons.** The reference pairs every button with a small glyph; these
  are text-only. Rounded corners and hover states are done.
- **No sound.** The reference plays a cue per tool.
- **Performance** in a real build: ~390 fps calm, ~175 fps storm at 1280×720,
  measured with nothing else running. An earlier 150/142 reading was taken with the
  editor open and is not a usable baseline. `-bench N` in the editor harness still reports the per-frame work in
  isolation (coastal calm 3.29 ms, storm 8.45 ms) which is the better signal for
  spotting a regression.
- **The `-view plan` fog override** is the last harness-only branch left in
  `SceneShot`; everything else now comes from `WorldBuilder`.

## Verifying

```bash
Unity.exe -batchmode -quit -projectPath unity/HabagatUnity   -executeMethod HabagatEditor.BuildPlayer.Run -buildOut <dir>   # standalone build
<dir>/Habagat.exe -selftest -report r.txt -screen-width 1280 -screen-height 720

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
