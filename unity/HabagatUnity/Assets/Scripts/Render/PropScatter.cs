using System;
using System.Collections.Generic;
using UnityEngine;
using Rng = Habagat.JsMath.Rng;

namespace Habagat.Render
{
    /// <summary>One prop kind plus every transform it is drawn at.</summary>
    public sealed class PropBatch
    {
        public Mesh Mesh;
        public List<Matrix4x4> Instances = new();
        public bool CastShadow = true;

        /// <summary>
        /// Flatten every instance of this kind into a single mesh.
        ///
        /// The web build uses one THREE.InstancedMesh per kind, and the density only
        /// works because of it: ~1500 scatter objects cost one draw call each *kind*
        /// rather than each object. Baking gets to the same draw-call count by a
        /// different route, and unlike <c>Graphics.DrawMeshInstanced</c> it survives
        /// a manual <c>Camera.Render()</c> in batch mode, which is how every
        /// screenshot in this project is taken. Props never move, so nothing is lost
        /// by giving up per-instance transforms — but a runtime scene that wants to
        /// stream them in and out should instance rather than bake.
        /// </summary>
        public Mesh Bake(string name)
        {
            var ci = new CombineInstance[Instances.Count];
            for (int i = 0; i < Instances.Count; i++)
                ci[i] = new CombineInstance { mesh = Mesh, transform = Instances[i] };

            var baked = new Mesh
            {
                name = name,
                // 260 grass tufts of ~100 verts each blow past the 16-bit index
                // limit immediately; without this the combine silently truncates.
                indexFormat = UnityEngine.Rendering.IndexFormat.UInt32,
            };
            baked.CombineMeshes(ci, true, true);
            baked.RecalculateBounds();
            return baked;
        }
    }

    /// <summary>
    /// Deterministic placement of props across the map, ported from the "Populate
    /// Props" block of ThreeCanvas.jsx.
    ///
    /// Determinism is not a nicety. In the web build, placing a single house re-runs
    /// the whole scene effect, so an unseeded scatter reshuffles every bush and palm
    /// on the map each time the user clicks. Everything here draws from one
    /// <see cref="Rng"/> seeded on the preset name, and because that generator is
    /// bit-exact with the browser's (see <see cref="JsMath.Rng"/>) the props land in
    /// the SAME cells in both builds — which is what makes a side-by-side screenshot
    /// a real comparison.
    ///
    /// The draw order therefore matters as much as the draw count. The original
    /// relies on JavaScript's argument evaluation order: in
    /// <c>addScatter(key, () =&gt; createPalm(prnd), gather(40, ...))</c> the arrow
    /// function is only *created* at that point, so <c>gather</c> runs first and the
    /// prop is built afterwards, inside <c>getProto</c>. Each block below keeps that
    /// order explicitly.
    /// </summary>
    public static class PropScatter
    {
        private const int W = FloodSim.W;
        private const int H = FloodSim.H;

        /// <summary>
        /// World position of a grid cell's centre. X matches the web build; Z is
        /// negated for the same reason <see cref="TerrainMeshBuilder.Vz"/> negates
        /// it — the map was mirrored into Unity's left-handed space, so anything
        /// placed in world coordinates has to be mirrored with it or the props end
        /// up on the wrong side of the river from the terrain they belong to.
        /// </summary>
        private static float Cx(double px) => (float)(px - W / 2.0 + 0.5);
        private static float Cz(double py) => -(float)(py - H / 2.0 + 0.5);

        private struct Placement
        {
            public float X, Y, Z, Yaw, S;
        }

        private static Matrix4x4 ToMatrix(in Placement p) =>
            Matrix4x4.TRS(new Vector3(p.X, p.Y, p.Z),
                          // Yaw negated along with Z: reflecting the world reverses
                          // the sense of rotation about the vertical axis.
                          Quaternion.Euler(0f, -p.Yaw * Mathf.Rad2Deg, 0f),
                          new Vector3(p.S, p.S, p.S));

        public static List<PropBatch> Build(FloodSim sim, PresetType type)
        {
            var elev = sim.Elev;
            var houses = Barangay.For(type);
            var batches = new List<PropBatch>();

            // Seeded on the preset name exactly as the web build is, so both
            // implementations walk the same stream.
            string presetName = PresetName(type);
            var rnd = new Rng(JsMath.StrSeed("props:" + presetName));

            // Keep the scatter out of people's yards: anything within this many
            // cells of a house is reserved, so a bush never grows through a wall.
            var occupied = new HashSet<int>();
            void Reserve(int cx, int cy, int r)
            {
                for (int dy = -r; dy <= r; dy++)
                    for (int dx = -r; dx <= r; dx++)
                        occupied.Add((cy + dy) * W + (cx + dx));
            }
            foreach (var h in houses) Reserve(h.X, h.Y, 2);

            // Generic scatter: sample random cells, keep the ones that pass `ok`.
            List<Placement> Gather(int want, Func<double, int, int, bool> ok,
                                   double minS = 0.85, double maxS = 1.2, double jitter = 0.42,
                                   int reserveR = 0, double yOff = 0)
            {
                var outp = new List<Placement>();
                int tries = want * 24;
                for (int a = 0; a < tries && outp.Count < want; a++)
                {
                    int px = 3 + (int)(rnd.Next() * (W - 6));
                    int py = 3 + (int)(rnd.Next() * (H - 6));
                    int i = py * W + px;
                    if (occupied.Contains(i)) continue;
                    if (!ok(elev[i], px, py)) continue;
                    if (reserveR != 0) Reserve(px, py, reserveR);
                    outp.Add(new Placement
                    {
                        X = Cx(px + (rnd.Next() - 0.5) * jitter),
                        Y = (float)(elev[i] + yOff),
                        Z = Cz(py + (rnd.Next() - 0.5) * jitter),
                        Yaw = (float)(rnd.Next() * Math.PI * 2),
                        S = (float)(minS + rnd.Next() * (maxS - minS)),
                    });
                }
                return outp;
            }

            void Add(Mesh mesh, List<Placement> spots, bool shadow = true)
            {
                if (mesh == null || spots.Count == 0) return;
                var batch = new PropBatch { Mesh = mesh, CastShadow = shadow };
                foreach (var p in spots) batch.Instances.Add(ToMatrix(p));
                batches.Add(batch);
            }

            // ── Buildings ────────────────────────────────────────────────────
            // Merged per style variant and cached: nineteen detailed apartments as
            // individual meshes would have cost a thousand draw calls.
            var houseProtos = new Dictionary<string, Mesh>();
            var houseBatches = new Dictionary<string, PropBatch>();
            for (int idx = 0; idx < houses.Length; idx++)
            {
                var h = houses[idx];
                string key;
                Mesh proto;
                switch (h.Style)
                {
                    case HouseStyle.Nipa:      key = "nipa" + (idx % 2); break;
                    case HouseStyle.Apartment: key = "apt" + (idx % 4); break;
                    case HouseStyle.Store:     key = "store"; break;
                    case HouseStyle.Hall:      key = "hall"; break;
                    default:                   key = "town" + (idx % PropPalette.WallColors.Length); break;
                }
                if (!houseProtos.TryGetValue(key, out proto))
                {
                    proto = h.Style switch
                    {
                        HouseStyle.Nipa => PropLibrary.NipaHut(idx),
                        HouseStyle.Apartment => PropLibrary.UrbanApartment(idx),
                        HouseStyle.Store => PropLibrary.SariSariStore(),
                        HouseStyle.Hall => PropLibrary.BarangayHall(),
                        _ => PropLibrary.Townhouse(idx),
                    };
                    houseProtos[key] = proto;
                }
                if (!houseBatches.TryGetValue(key, out var hb))
                {
                    hb = new PropBatch { Mesh = proto, CastShadow = true };
                    houseBatches[key] = hb;
                    batches.Add(hb);
                }

                double e = elev[h.Y * W + h.X];
                // A small deterministic yaw stops a row of identical houses from
                // reading as a spreadsheet. Drawn from the cell coordinates rather
                // than the scatter stream, so it does not shift anything else.
                float yaw = ((JsMath.StrSeed($"{h.X},{h.Y}") % 1000) / 1000f - 0.5f) * 0.5f;
                hb.Instances.Add(Matrix4x4.TRS(
                    new Vector3(Cx(h.X), (float)e, Cz(h.Y)),
                    Quaternion.Euler(0f, -yaw * Mathf.Rad2Deg, 0f), Vector3.one));
            }

            // ── Scatter ──────────────────────────────────────────────────────
            // Basin counts as built-up too, not just Urban — it shares the grey
            // palette and gets streets rather than groves.
            bool isVillage = type != PresetType.Urban && type != PresetType.Basin;
            // Dry, walkable ground: the band everything vegetal and man-made lives in.
            static bool DryLand(double e, int x, int y) => e > 0.35 && e < 3.4;

            if (isVillage)
            {
                // Palms are the signature silhouette of the coast, so there are
                // enough to form groves rather than a dozen lonely sticks — but each
                // reserves its own cell, because a canopy dense enough to hide the
                // village is no better than an empty field.
                var palmSpots = Gather(40, (e, x, y) => e > 0.4 && e < 3.0, 0.8, 1.25, reserveR: 2);
                Add(PropLibrary.CoconutPalm(ref rnd), palmSpots);

                var bananaSpots = Gather(34, (e, x, y) => e > 0.5 && e < 2.6, 0.85, 1.3, reserveR: 1);
                Add(PropLibrary.BananaPlant(ref rnd), bananaSpots);

                // The bulk of the fill. Bushes and tufts are what stop the big greens
                // from rendering as unbroken sheets of one colour, and being low they
                // add texture without ever occluding a building.
                var bushSpots = Gather(140, DryLand, 0.8, 1.4);
                Add(PropLibrary.Bush(ref rnd), bushSpots, shadow: false);

                var tuftSpots = Gather(260, (e, x, y) => e > 0.2 && e < 3.6, 0.8, 1.6, jitter: 0.7);
                Add(PropLibrary.GrassTuft(ref rnd), tuftSpots, shadow: false);

                var rockSpots = Gather(70, (e, x, y) => e > 0.1 && e < 3.8, 0.8, 1.7);
                Add(PropLibrary.Rock(ref rnd), rockSpots, shadow: false);

                var driftSpots = Gather(34, (e, x, y) => e > -0.35 && e < 0.55, 0.9, 1.5);
                Add(PropLibrary.Driftwood(ref rnd), driftSpots, shadow: false);

                // Yards: a fence and a laundry line beside some of the homes, placed
                // relative to the houses rather than at random.
                var fenceSpots = new List<Placement>();
                var laundrySpots = new List<Placement>();
                for (int hi = 0; hi < houses.Length; hi++)
                {
                    var h = houses[hi];
                    double e = elev[h.Y * W + h.X];
                    if (e < 0.45) continue;
                    if (hi % 2 == 0)
                    {
                        double a = rnd.Next() * Math.PI * 2;
                        fenceSpots.Add(new Placement
                        {
                            X = Cx(h.X + Math.Cos(a) * 2.3), Y = (float)e, Z = Cz(h.Y + Math.Sin(a) * 2.3),
                            Yaw = (float)(a + Math.PI / 2), S = 1f,
                        });
                    }
                    if (hi % 3 == 1)
                    {
                        double a = rnd.Next() * Math.PI * 2;
                        laundrySpots.Add(new Placement
                        {
                            X = Cx(h.X + Math.Cos(a) * 2.6), Y = (float)e, Z = Cz(h.Y + Math.Sin(a) * 2.6),
                            Yaw = (float)(rnd.Next() * Math.PI), S = 1f,
                        });
                    }
                }
                Add(PropLibrary.Fence(), fenceSpots, shadow: false);
                Add(PropLibrary.LaundryLine(ref rnd), laundrySpots, shadow: false);
            }

            return batches;
        }

        private static string PresetName(PresetType t) => t switch
        {
            PresetType.River => "river",
            PresetType.Urban => "urban",
            PresetType.Island => "island",
            _ => "coastal",
        };
    }
}
