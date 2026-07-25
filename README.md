# Habagat — Flood CA Playground

Interactive prototype of the flood cellular automata that powers **Habagat**
(see the project's `AGENTS.md`, Phase 1: "the water is the game").

This is the reference implementation the Godot port (`src/core/grid/flood_ca.gd`)
should be validated against — same algorithm, same constants.

## Run it

```bash
npm install
npm run dev
```

Then open the printed localhost URL.

## The algorithm

Per tick, in this fixed order (determinism law — never reorder):

1. **Rain** — uniform rate added to every land cell.
2. **Surge** — coastal rows clamped up to surge depth while a storm is active.
3. **Flow** — for each cell, water moves toward each lower-*head* 4-neighbor,
   where `head = elevation + water`, at `FLOW_RATE` (0.25) of the head difference.
   Two-buffer scheme (read `water`, write `next`, then swap) keeps it
   order-independent enough for gameplay while staying deterministic.
4. **Drainage + absorption** — drain tiles (pumps) and absorb tiles (mangroves,
   green cover) subtract from depth; values below `MIN_WATER` snap to 0.

## Tuning knobs

| Constant | Where | Effect |
|---|---|---|
| `FLOW_RATE` | top of `FloodPlayground.jsx` | how fast water spreads. >0.5 goes unstable |
| `MIN_WATER` | same | denormal cleanup threshold |
| `SEA_LEVEL` | same | cells at/below this are infinite-reservoir sea |
| surge depth / ticks | `step()` | storm severity and duration |
| `drain` / `absorb` values | `paint()` | pump and mangrove strength |

## Files

- `src/FloodPlayground.jsx` — the whole thing: CA, canvas renderer, tools, UI
- `src/main.jsx` — React entry
- `src/index.css` — minimal class shims (swap for real Tailwind if you prefer)
