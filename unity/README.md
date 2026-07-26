# Unity port — simulation core

Engine-agnostic C# port of the flood simulation, ahead of a possible Unity
rebuild. Nothing here references `UnityEngine`, so it builds and tests under
plain dotnet today and can be dropped into `Assets/Scripts/` unchanged later.

```
unity/
  HabagatSim/          netstandard2.1 class library — the port
    JsMath.cs          JavaScript numeric semantics the presets depend on
    TerrainPresets.cs  heightmap generation (makeTerrainPreset)
    FloodSim.cs        the shallow-water CA (step) and brush tools
  HabagatSim.Verify/   console app that prints a deterministic fingerprint
```

## Running the fidelity check

```bash
dotnet run --project unity/HabagatSim.Verify
```

This prints terrain sums, spot elevations and simulation state at ticks 100,
300 and 400 for all four presets, under a fixed weather script (rain for 100
ticks, storm from 100–300, a surge at 150).

The same fingerprint can be taken from the live JavaScript — the reference
harness lifts `makeTerrainPreset` straight out of `src/FloodPlayground.jsx`
rather than retyping it, so the terrain half compares against the real source.
**Both currently agree byte-for-byte on all 52 output lines.**

## Two porting hazards worth knowing about

**1. The preset RNG relies on JavaScript losing precision.**

The generator is `s = (s * 1103515245 + 12345) & 0x7fffffff`. In JS every
number is a double, and with `s` approaching 2^31 the product reaches ~2.3e18 —
well past 2^53, where doubles stop representing integers exactly. The multiply
*discards low bits*, and `&` then coerces that inexact double to int32.

A natural C# port using `int` or `long` is exact, does **not** lose those bits,
and therefore produces a completely different random stream and completely
different terrain. `JsMath.Lcg` reproduces the imprecision deliberately.

**2. JS rounds to float32 only on the store.**

State lives in `Float32Array`, but all arithmetic happens in double; narrowing
occurs solely when writing into the array. The obvious C# translation using
`float` locals rounds at *every* operation instead. That difference is invisible
per-operation and clearly visible after 400 ticks — it was caught by the
fingerprint diff, not by reading the code. Every intermediate in this port is a
`double`, cast to `float` only on assignment.

## Status

- [x] Flood simulation (`step`) — verified against JS
- [x] Terrain presets — verified against JS
- [x] Brush tools (`handlePaint`)
- [ ] Terrain / water mesh generation
- [ ] Props and scatter (art spec is in `src/ThreeCanvas.jsx`)
- [ ] UI — the piece that genuinely has to be rebuilt

The web build in `src/` remains the live reference implementation. It is not
deprecated by this directory.
