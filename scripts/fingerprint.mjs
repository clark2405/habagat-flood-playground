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

import { createSim, step, paint, makeTerrainPreset } from "../src/sim.js";

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
  //
  // t=160 and t=360 bracket the surge, which begins at 150 and lasts 200 ticks.
  // Without them the only sample after the surge ends is t=400, and that nearly
  // let a surge running 30% too long through — at t=100 neither side has begun
  // and at t=300 both are still going, so one line carried the whole check.
  const S = createSim(name);
  for (let t = 0; t < 400; t++) {
    const storm = t >= 100 && t < 300;
    const rain = t < 100 ? 4 : 0;
    if (t === 150) S.surgeTicks = 200;
    const st = step(S, rain, storm, []);

    if (t === 99 || t === 159 || t === 299 || t === 359 || t === 399)
      out.push(
        `  SIM ${name} t=${st.tick} water=${F(st.water)} flooded=${st.flooded} ` +
          `mang=${st.mangroveCount} drn=${st.drainCount}`
      );
  }

  // The brush tools, which nothing verified until now: six of them, ported cell
  // for cell into C#, and never once compared against this side.
  const P = createSim(name);
  paint(P, "raise", 30, 20);
  paint(P, "lower", 60, 40);
  paint(P, "water", 45, 30);
  paint(P, "mangrove", 20, 45);
  paint(P, "drain", 70, 25);
  paint(P, "clear", 20, 45); // over the mangroves, so clear has something to undo

  // Probed at the stamp centres, not summed over the grid. The first version of
  // this summed, and the sum was worthless: raise and lower cancel each other
  // exactly, so it came back equal to the untouched terrain to six decimals and
  // would have passed with both tools broken.
  const eR = P.elev[20 * 96 + 30], eL = P.elev[40 * 96 + 60];
  const wW = P.water[30 * 96 + 45], wEdge = P.water[30 * 96 + 47];
  let mang = 0, drn = 0;
  for (let i = 0; i < P.elev.length; i++) {
    if (P.mang[i]) mang++;
    if (P.drn[i]) drn++;
  }
  out.push(
    `  PAINT ${name} raise=${F(eR)} lower=${F(eL)} water=${F(wW)} ` +
      `falloff=${F(wEdge)} mang=${mang} drn=${drn}`
  );

  // And again after the water has had somewhere to go, so the painted state is
  // checked through the CA rather than only at the moment it was stamped.
  for (let t = 0; t < 50; t++) step(P, 0, false, []);
  const ps = step(P, 0, false, []);
  out.push(
    `  PAINT ${name} t=${ps.tick} water=${F(ps.water)} flooded=${ps.flooded} ` +
      `mang=${ps.mangroveCount} drn=${ps.drainCount}`
  );
}

process.stdout.write(out.join("\n") + "\n");
