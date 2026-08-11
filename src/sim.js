/* ─────────────────────────────────────────────────────────────
   HABAGAT 3D — the simulation, on its own

   Lifted out of FloodPlayground.jsx unchanged. It lived inside the React
   component, which meant the only way to run it was to render the app — so the
   fingerprint that the C# port is verified against could not be reproduced by
   anything, and "byte-identical on all 52 lines" rested on a diff somebody did
   once and did not keep.

   Nothing here imports React, so scripts/fingerprint.mjs can run it under plain
   node. The Unity side has had this arrangement all along: one copy of the sim,
   two consumers.
   ───────────────────────────────────────────────────────────── */

const W = 96;
const H = 64;
const FLOW_RATE = 0.15;
const MIN_WATER = 0.001;
const SEA_LEVEL = 0.0;

function makeTerrainPreset(type, seed = 1337) {
  let s = seed;
  const rnd = () => {
    s = (s * 1103515245 + 12345) & 0x7fffffff;
    return s / 0x7fffffff;
  };
  const gw = 13, gh = 9;
  const g = Array.from({ length: gh }, () => Array.from({ length: gw }, rnd));
  const lerp = (a, b, t) => a + (b - a) * (t * t * (3 - 2 * t));
  const elev = new Float32Array(W * H);

  for (let y = 0; y < H; y++) {
    for (let x = 0; x < W; x++) {
      const gx = (x / (W - 1)) * (gw - 1), gy = (y / (H - 1)) * (gh - 1);
      const x0 = Math.floor(gx), y0 = Math.floor(gy);
      const fx = gx - x0, fy = gy - y0;
      const n = lerp(
        lerp(g[y0][x0], g[y0][Math.min(x0 + 1, gw - 1)], fx),
        lerp(g[Math.min(y0 + 1, gh - 1)][x0], g[Math.min(y0 + 1, gh - 1)][Math.min(x0 + 1, gw - 1)], fx),
        fy
      );

      const idx = y * W + x;

      if (type === "coastal") {
        const slope = 1 - y / (H - 1);
        let e = n * 2.2 + slope * 4.2 - 1.5;
        const riverX = W * 0.5 + Math.sin(y * 0.1) * 8;
        const rd = Math.abs(x - riverX);
        if (rd < 5.6) e -= 2.4 * Math.cos((rd / 5.6) * Math.PI * 0.5) ** 2;
        elev[idx] = e;
      } else if (type === "river") {
        // Highland Valley: Lush green river basin with deep winding river (NO SAND!)
        const distFromCenter = Math.abs(x - W / 2) / (W / 2);
        let e = n * 2.5 + distFromCenter * 3.2 + 0.3;
        const riverX = W * 0.5 + Math.sin(y * 0.15) * 14;
        const rd = Math.abs(x - riverX);
        if (rd < 9.6) e -= 4.2 * Math.cos((rd / 9.6) * Math.PI * 0.5) ** 2;
        elev[idx] = e;
      } else if (type === "island") {
        const cx = W / 2, cy = H / 2;
        const dist = Math.hypot(x - cx, y - cy) / (W * 0.42);
        let e = (1 - dist) * 4.5 + (n - 0.5) * 1.8 - 0.8;
        elev[idx] = e;
      } else if (type === "urban" || type === "basin") {
        // Metro Manila Urban Barangay: paved streets in a flat lowland basin.
        // The ground that pens the water in used to be a rim measured from the
        // map's edges, which drew a literal rectangle around the play area. It
        // is now a wobbled oval bowl, so the higher ground is a landform in its
        // own right and simply keeps going past the border.
        const cxn = (x - W / 2) / (W * 0.44);
        const cyn = (y - H / 2) / (H * 0.44);
        const bowl = Math.hypot(cxn, cyn) + (n - 0.5) * 0.55;
        const rim = Math.min(Math.max((bowl - 0.62) / 0.53, 0), 1);
        let e = 0.4 + (n - 0.5) * 0.6 + rim * rim * (3 - 2 * rim) * 2.6;
        const canalX = W * 0.48;
        const cd = Math.abs(x - canalX);
        if (cd < 4.4) e -= 1.15 * Math.cos((cd / 4.4) * Math.PI * 0.5) ** 2;
        elev[idx] = e;
      } else {
        elev[idx] = n * 2.5 - 0.5;
      }
    }
  }
  return elev;
}

const PRESETS = {
  coastal: {
    name: "Coastal Barangay",
    type: "coastal",
    seed: 1337,
    houses: [
      { id: 1, x: 18, y: 38, style: "nipa" },
      { id: 2, x: 24, y: 36, style: "nipa" },
      { id: 3, x: 30, y: 35, style: "store" },
      { id: 4, x: 36, y: 37, style: "house" },
      { id: 5, x: 42, y: 40, style: "hall" },
      { id: 6, x: 62, y: 38, style: "nipa" },
      { id: 7, x: 68, y: 36, style: "house" },
      { id: 8, x: 74, y: 35, style: "nipa" },
      { id: 9, x: 80, y: 39, style: "store" },
      { id: 10, x: 22, y: 28, style: "house" },
      { id: 11, x: 28, y: 26, style: "house" },
      { id: 12, x: 34, y: 25, style: "nipa" },
      { id: 13, x: 70, y: 27, style: "nipa" },
      { id: 14, x: 76, y: 28, style: "house" },
    ],
  },
  river: {
    name: "River Valley",
    type: "river",
    seed: 4040,
    houses: [
      { id: 1, x: 20, y: 15, style: "house" },
      { id: 2, x: 25, y: 20, style: "nipa" },
      { id: 3, x: 28, y: 28, style: "store" },
      { id: 4, x: 30, y: 36, style: "nipa" },
      { id: 5, x: 32, y: 44, style: "hall" },
      { id: 6, x: 65, y: 18, style: "house" },
      { id: 7, x: 68, y: 26, style: "nipa" },
      { id: 8, x: 70, y: 34, style: "house" },
      { id: 9, x: 72, y: 42, style: "nipa" },
      { id: 10, x: 75, y: 50, style: "store" },
      { id: 11, x: 15, y: 30, style: "nipa" },
      { id: 12, x: 18, y: 42, style: "house" },
      { id: 13, x: 80, y: 22, style: "nipa" },
      { id: 14, x: 82, y: 38, style: "house" },
    ],
  },
  urban: {
    name: "Urban Barangay",
    type: "urban",
    seed: 9999,
    houses: [
      { id: 1, x: 24, y: 20, style: "apartment" },
      { id: 2, x: 30, y: 20, style: "apartment" },
      { id: 3, x: 36, y: 20, style: "store" },
      { id: 4, x: 42, y: 20, style: "hall" },
      { id: 5, x: 54, y: 20, style: "apartment" },
      { id: 6, x: 60, y: 20, style: "apartment" },
      { id: 7, x: 66, y: 20, style: "store" },
      { id: 8, x: 24, y: 30, style: "apartment" },
      { id: 9, x: 30, y: 30, style: "apartment" },
      { id: 10, x: 36, y: 30, style: "house" },
      { id: 11, x: 54, y: 30, style: "apartment" },
      { id: 12, x: 60, y: 30, style: "apartment" },
      { id: 13, x: 66, y: 30, style: "house" },
      { id: 14, x: 24, y: 40, style: "store" },
      { id: 15, x: 30, y: 40, style: "house" },
      { id: 16, x: 36, y: 40, style: "house" },
      { id: 17, x: 54, y: 40, style: "house" },
      { id: 18, x: 60, y: 40, style: "store" },
      { id: 19, x: 66, y: 40, style: "house" },
    ],
  },
  island: {
    name: "Typhoon Island",
    type: "island",
    seed: 8888,
    houses: [
      { id: 1, x: 38, y: 25, style: "nipa" },
      { id: 2, x: 44, y: 22, style: "house" },
      { id: 3, x: 52, y: 22, style: "hall" },
      { id: 4, x: 58, y: 25, style: "nipa" },
      { id: 5, x: 32, y: 32, style: "store" },
      { id: 6, x: 64, y: 32, style: "nipa" },
      { id: 7, x: 34, y: 40, style: "nipa" },
      { id: 8, x: 40, y: 44, style: "house" },
      { id: 9, x: 48, y: 46, style: "house" },
      { id: 10, x: 56, y: 44, style: "nipa" },
      { id: 11, x: 62, y: 40, style: "store" },
      { id: 12, x: 48, y: 32, style: "hall" },
    ],
  },
};

/**
 * A fresh simulation for a preset, including the mangroves and drains that some
 * presets start with. This was the body of loadPreset; the React version now
 * calls it and keeps only the state-setting to itself.
 */
export function createSim(presetKey = "coastal") {
  const config = PRESETS[presetKey] || PRESETS.coastal;

  const S = {
    elev: makeTerrainPreset(config.type, config.seed),
    water: new Float32Array(W * H),
    next: new Float32Array(W * H),
    drain: new Float32Array(W * H),
    absorb: new Float32Array(W * H),
    mang: new Uint8Array(W * H),
    drn: new Uint8Array(W * H),
    tick: 0,
    surgeTicks: 0,
  };

  if (config.type === "coastal") {
    for (let x = 10; x < 86; x += 3) {
      const i = 50 * W + x;
      if (S.elev[i] > SEA_LEVEL && S.elev[i] < 0.6) {
        S.mang[i] = 1;
        S.absorb[i] = 0.008;
      }
    }
  } else if (config.type === "urban") {
    [30 * W + 46, 32 * W + 46, 40 * W + 46].forEach((i) => {
      if (S.elev[i] > SEA_LEVEL) {
        S.drn[i] = 1;
        S.drain[i] = 0.03;
      }
    });
  }

  return S;
}

/**
 * One tick of the flood CA. The body is exactly what ran inside the React
 * component; only its inputs changed from refs and closures into arguments, and
 * the stats it used to push into setState are returned instead.
 *
 * Called twice per animation frame by the app, which ties the flood rate to the
 * display refresh. That is reproduced deliberately in the C# port rather than
 * corrected, so the two stay comparable.
 */
export function step(S, rain, storm, houses) {
  if (!S) return null;
  const { elev, water, next, drain, absorb } = S;
  const rainRate = rain * 0.0004 + (storm ? 0.002 : 0);

    if (rainRate > 0) {
      for (let i = 0; i < water.length; i++) {
        if (elev[i] > SEA_LEVEL) {
          water[i] = Math.min(water[i] + rainRate, 3.0);
        }
      }
    }

    if (S.surgeTicks > 0) {
      S.surgeTicks--;
      const surgeDepth = 0.6;
      for (let y = H - 6; y < H; y++) {
        for (let x = 0; x < W; x++) {
          const i = y * W + x;
          water[i] = Math.min(Math.max(water[i], surgeDepth + (elev[i] < SEA_LEVEL ? SEA_LEVEL - elev[i] : 0)), 3.0);
        }
      }
    }

    for (let i = 0; i < water.length; i++) {
      if (elev[i] <= SEA_LEVEL) {
        water[i] = Math.min(Math.max(water[i], SEA_LEVEL - elev[i]), 3.0);
      }
    }

    next.set(water);
    for (let y = 0; y < H; y++) {
      for (let x = 0; x < W; x++) {
        const i = y * W + x;
        if (water[i] <= MIN_WATER) continue;
        const hi = elev[i] + water[i];

        if (x > 0) flow(i, i - 1, hi);
        if (x < W - 1) flow(i, i + 1, hi);
        if (y > 0) flow(i, i - W, hi);
        if (y < H - 1) flow(i, i + W, hi);
      }
    }

    function flow(i, n, hi) {
      const diff = hi - (elev[n] + water[n]);
      if (diff > 0) {
        const maxOutflow = next[i] * 0.2;
        const moved = Math.min(diff * FLOW_RATE, maxOutflow);
        next[i] -= moved;
        next[n] += moved;
      }
    }

    const tmp = S.water;
    S.water = S.next;
    S.next = tmp;

    const w2 = S.water;
    let totalWater = 0, floodedCells = 0, mangCount = 0, drnCount = 0;

    for (let i = 0; i < w2.length; i++) {
      if (S.mang[i]) mangCount++;
      if (S.drn[i]) drnCount++;

      if (elev[i] > SEA_LEVEL && w2[i] > 0) {
        const naturalSeepage = 0.0004;
        w2[i] = Math.max(0, w2[i] - naturalSeepage - drain[i] - absorb[i]);
        if (w2[i] < MIN_WATER) w2[i] = 0;
      }

      if (elev[i] > SEA_LEVEL) {
        totalWater += w2[i];
        if (w2[i] > 0.03) floodedCells++;
      }
    }

    let floodedH = 0;
    houses.forEach((h) => {
      const gx = Math.floor(h.x);
      const gy = Math.floor(h.y);
      const i = gy * W + gx;
      if (S.water[i] > 0.08) floodedH++;
    });

    S.tick++;

  return {
    water: totalWater,
    flooded: floodedCells,
    floodedHouses: floodedH,
    mangroveCount: mangCount,
    drainCount: drnCount,
    tick: S.tick,
  };
}

/**
 * Stamp a brush over the grid. Moved out of handlePaint unchanged.
 *
 * `onCell` is called for every cell the brush actually affects, which is how the
 * React version keeps firing a sound cue per cell — the reference does that
 * inside this loop, and quietly turning it into one cue per stroke would have
 * changed the thing everything else is verified against. The fingerprint passes
 * no callback.
 *
 * The house tool is NOT here: it needs the houses list, it only acts on the
 * centre cell, and it picks a style from Math.random. It stays in the component.
 */
export function paint(S, tool, x, y, R = 3, onCell) {
  for (let dy = -R; dy <= R; dy++) {
    for (let dx = -R; dx <= R; dx++) {
      const px = x + dx;
      const py = y + dy;
      if (px < 0 || px >= W || py < 0 || py >= H) continue;
      const d2 = dx * dx + dy * dy;
      if (d2 > R * R) continue;
      const i = py * W + px;
      const fall = 1 - Math.sqrt(d2) / R;

      if (tool === "raise") {
        S.elev[i] += 0.15 * fall;
      } else if (tool === "lower") {
        S.elev[i] -= 0.15 * fall;
      } else if (tool === "water") {
        S.water[i] += 0.25 * fall;
      } else if (tool === "mangrove" && S.elev[i] > SEA_LEVEL) {
        S.mang[i] = 1;
        S.absorb[i] = 0.008;
      } else if (tool === "drain" && S.elev[i] > SEA_LEVEL) {
        S.drn[i] = 1;
        S.drain[i] = 0.025;
      } else if (tool === "clear") {
        S.mang[i] = 0;
        S.drn[i] = 0;
        S.absorb[i] = 0;
        S.drain[i] = 0;
        S.water[i] = 0;
      } else {
        continue;
      }
      if (onCell) onCell(tool, i, dx, dy);
    }
  }
}

export { W, H, FLOW_RATE, MIN_WATER, SEA_LEVEL, makeTerrainPreset, PRESETS };
