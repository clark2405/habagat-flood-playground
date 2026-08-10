/* ─────────────────────────────────────────────────────────────
   The reference fingerprint, from the JavaScript implementation.

   This is the other half of unity/HabagatSim.Verify. That harness prints the
   same 52 lines from the C# port; the two are supposed to be byte-identical,
   and until now the JS side could not be produced at all — the simulation lived
   inside a React component, so the only way to run it was to render the app.
   The claim rested on a diff somebody did once by hand and did not keep.

     node scripts/fingerprint.mjs

   Emitted to stdout so it can be redirected or piped into diff. The committed
   copy at unity/HabagatSim.Verify/fingerprint.txt is what the C# harness now
   checks itself against.
   ───────────────────────────────────────────────────────────── */

import { createSim, step, makeTerrainPreset } from "../src/sim.js";

// F6 in C#, toFixed(6) here. Both round half away from zero on these values;
// where they would not, the fingerprint would disagree and that is the point.
const F = (v) => v.toFixed(6);

const presets = [
  ["coastal", "coastal", 1337],
  ["river", "river", 4040],
  ["urban", "urban", 9999],
  ["island", "island", 8888],
];

const out = [];

for (const [name, type, seed] of presets) {
  const elev = makeTerrainPreset(type, seed);

  let sum = 0, min = Infinity, max = -Infinity;
  for (const e of elev) {
    sum += e;
    if (e < min) min = e;
    if (e > max) max = e;
  }

  out.push(`TERRAIN ${name} sum=${F(sum)} min=${F(min)} max=${F(max)}`);

  // Spot samples spread across the map, so a localised divergence cannot hide
  // inside an aggregate that happens to match.
  for (const p of [0, 1, 95, 96, 3000, 3071, 4096, 5000, 6143])
    out.push(`  ELEV ${name}[${p}]=${F(elev[p])}`);

  // The same fixed weather script the C# harness runs.
  const S = createSim(name);
  for (let t = 0; t < 400; t++) {
    const storm = t >= 100 && t < 300;
    const rain = t < 100 ? 4 : 0;
    if (t === 150) S.surgeTicks = 200;
    const st = step(S, rain, storm, []);

    if (t === 99 || t === 299 || t === 399)
      out.push(
        `  SIM ${name} t=${st.tick} water=${F(st.water)} flooded=${st.flooded} ` +
          `mang=${st.mangroveCount} drn=${st.drainCount}`
      );
  }
}

process.stdout.write(out.join("\n") + "\n");
