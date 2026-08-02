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
        /// Baking flattens every instance into one static mesh, which is why the
        /// scatter is affordable — but it also destroys per-instance transforms.
        /// Anything that has to MOVE at runtime must opt out. Boats ride the swell,
        /// so they do; everything else stands still.
        /// </summary>
        public bool Dynamic;

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

        /// <summary>
        /// Build a placement from REFERENCE world coordinates — the raw
        /// <c>x - W/2</c> the web build uses, with no cell-centre offset — mirroring
        /// Z on the way in.
        /// </summary>
        private static Placement RefPlace(double wx, double y, double wz, double yaw, double s) =>
            new Placement { X = (float)wx, Y = (float)y, Z = -(float)wz, Yaw = (float)yaw, S = (float)s };

        private static Matrix4x4 ToMatrix(in Placement p) =>
            Matrix4x4.TRS(new Vector3(p.X, p.Y, p.Z),
                          // Yaw negated along with Z: reflecting the world reverses
                          // the sense of rotation about the vertical axis.
                          Quaternion.Euler(0f, -p.Yaw * Mathf.Rad2Deg, 0f),
                          new Vector3(p.S, p.S, p.S));

        /// <summary>
        /// <paramref name="houses"/> defaults to the preset's own barangay. Passing a
        /// longer list — because the player has placed one — deliberately shifts the
        /// RNG stream: the yard loop draws per house, so everything after it moves,
        /// and the boats end up somewhere new. The reference behaves identically,
        /// which is why placing one house there "re-runs the whole scene effect".
        /// </summary>
        public static List<PropBatch> Build(FloodSim sim, PresetType type, House[] houses = null)
        {
            var elev = sim.Elev;
            houses ??= Barangay.For(type);
            var batches = new List<PropBatch>();

            // Seeded on the preset name exactly as the web build is, so both
            // implementations walk the same stream.
            string presetName = PresetName(type);
            var rnd = new Rng(JsMath.StrSeed("props:" + presetName));

            void Add(Mesh mesh, List<Placement> spots, bool shadow = true)
            {
                if (mesh == null || spots.Count == 0) return;
                var batch = new PropBatch { Mesh = mesh, CastShadow = shadow };
                foreach (var p in spots) batch.Instances.Add(ToMatrix(p));
                batches.Add(batch);
            }

            // ── Basketball court ─────────────────────────────────────────────
            // A rigid 4x3-cell slab, so it needs genuinely flat, dry ground. It used
            // to be hard-coded to x = 0.48W — which on the urban map is exactly where
            // the canal runs, so it spawned in the water. Draws nothing from the RNG,
            // and runs first, matching the original.
            {
                int bgx = -1, bgy = -1; double bhi = 0, bestScore = 0; bool found = false;
                for (int gy = 6; gy < H - 6; gy++)
                    for (int gx = 6; gx < W - 6; gx++)
                    {
                        double lo = double.MaxValue, hi = double.MinValue;
                        for (int dy = -2; dy <= 2; dy++)
                            for (int dx = -2; dx <= 2; dx++)
                            {
                                double e = elev[(gy + dy) * W + gx + dx];
                                if (e < lo) lo = e;
                                if (e > hi) hi = e;
                            }
                        if (lo < 0.45) continue; // must be dry, with freeboard
                        // Prefer flat, and prefer sitting near the middle of the barangay.
                        double score = (hi - lo) + Math.Sqrt(Math.Pow(gx - W * 0.5, 2) + Math.Pow(gy - H * 0.5, 2)) * 0.012;
                        if (!found || score < bestScore) { found = true; bestScore = score; bgx = gx; bgy = gy; bhi = hi; }
                    }
                if (found)
                {
                    Add(PropLibrary.BasketballCourt(),
                        new List<Placement> { RefPlace(bgx - W / 2.0, bhi + 0.08, bgy - H / 2.0, 0, 1) });
                }
            }

            // Bilinear height lookup in REFERENCE world space — roads and paths have
            // to follow the ground exactly or they slice through every rise they cross.
            double ElevAt(double wx, double wz)
            {
                double fx = Math.Min(Math.Max(wx + W / 2.0, 0), W - 1.001);
                double fz = Math.Min(Math.Max(wz + H / 2.0, 0), H - 1.001);
                int x0 = (int)fx, z0 = (int)fz;
                double tx = fx - x0, tz = fz - z0;
                double e00 = elev[z0 * W + x0], e10 = elev[z0 * W + x0 + 1];
                double e01 = elev[(z0 + 1) * W + x0], e11 = elev[(z0 + 1) * W + x0 + 1];
                return (e00 * (1 - tx) + e10 * tx) * (1 - tz) + (e01 * (1 - tx) + e11 * tx) * tz;
            }

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
            else
            {
                // ── Urban: streets, poles and street furniture ───────────────
                // The city preset was the worst offender — a flat grey plane with
                // dark specks on it. What a barangay street actually has is asphalt,
                // a forest of utility poles, and clutter along the kerb.
                double canalX = W * 0.48;
                int[] roadRows = { 15, 25, 35, 45, 55 };

                var road = new PropBuilder();
                var marks = new PropBuilder();
                Color roadCol = PropPalette.Hex(0x4a4a4d);
                Color markCol = PropPalette.Hex(0xe8e2cf);

                // Ribbon geometry that samples the heightmap, so the road drapes over
                // the ground instead of guillotining it. Built in REFERENCE space and
                // handed to PropBuilder, whose mirror does the rest — which is also
                // what keeps the winding right without thinking about it.
                MeshData Ribbon(List<(double x, double z)> pts, double halfW, double y0)
                {
                    int n = pts.Count;
                    var verts = new Vector3[n * 2];
                    var norms = new Vector3[n * 2];
                    for (int s = 0; s < n; s++)
                    {
                        var p = pts[s];
                        var q = pts[Math.Min(s + 1, n - 1)];
                        var r = pts[Math.Max(s - 1, 0)];
                        double dx = q.x - r.x, dz = q.z - r.z;
                        double l = Math.Sqrt(dx * dx + dz * dz);
                        if (l == 0) l = 1;
                        dx /= l; dz /= l;
                        double nx = -dz, nz = dx;
                        int k = 0;
                        foreach (int sgn in new[] { -1, 1 })
                        {
                            double wx = p.x + nx * halfW * sgn;
                            double wz = p.z + nz * halfW * sgn;
                            verts[s * 2 + k] = new Vector3((float)wx, (float)(ElevAt(wx, wz) + y0), (float)wz);
                            norms[s * 2 + k] = Vector3.up;
                            k++;
                        }
                    }
                    var tris = new int[(n - 1) * 6];
                    int t = 0;
                    for (int s = 0; s < n - 1; s++)
                    {
                        int a = s * 2, b = s * 2 + 1, c = s * 2 + 2, d = s * 2 + 3;
                        // See the note in ThreeCanvas.jsx: the reference wound these
                        // (a,c,b)/(b,c,d), which points an east-west road's normal at
                        // the ground and culls it. Corrected in both builds.
                        tris[t++] = a; tris[t++] = b; tris[t++] = c;
                        tris[t++] = b; tris[t++] = d; tris[t++] = c;
                    }
                    return new MeshData { Verts = verts, Normals = norms, Tris = tris };
                }

                foreach (int gy in roadRows)
                {
                    // Two carriageways so the canal is spanned by a gap, not paved over.
                    foreach (var (x0, x1) in new[] { (6.0, canalX - 3.2), (canalX + 3.2, W - 6.0) })
                    {
                        var pts = new List<(double, double)>();
                        for (double x = x0; x <= x1; x += 2) pts.Add((x - W / 2.0, gy - H / 2.0));
                        if (pts.Count < 2) continue;
                        road.Add(Ribbon(pts, 1.5, 0.07), roadCol);
                        // Dashed centre line.
                        for (double x = x0 + 1; x < x1 - 1; x += 5)
                        {
                            var seg = new List<(double, double)>
                            {
                                (x - W / 2.0, gy - H / 2.0), (x + 2 - W / 2.0, gy - H / 2.0),
                            };
                            marks.Add(Ribbon(seg, 0.09, 0.1), markCol);
                        }
                    }
                }
                // One cross street on the dry side of the canal.
                foreach (double cx in new[] { 20.0, 72.0 })
                {
                    var pts = new List<(double, double)>();
                    for (double y = 8; y <= H - 8; y += 2) pts.Add((cx - W / 2.0, y - H / 2.0));
                    road.Add(Ribbon(pts, 1.4, 0.07), roadCol);
                }

                // Footbridges over the canal on every road line.
                foreach (int gy in roadRows)
                {
                    double bx = canalX - W / 2.0, bz = gy - H / 2.0;
                    double y = Math.Max(ElevAt(bx, bz), 0);
                    road.Add(Prim.Box(8.0f, 0.22f, 3.0f), PropPalette.Concrete,
                             new Vector3((float)bx, (float)(y + 0.75), (float)bz));
                    foreach (double rz in new[] { -1.4, 1.4 })
                        road.Add(Prim.Box(8.0f, 0.5f, 0.14f), PropPalette.Concrete,
                                 new Vector3((float)bx, (float)(y + 1.1), (float)(bz + rz)));
                }

                // A road is already in world space, so it is placed at the origin.
                var atOrigin = new List<Placement> { RefPlace(0, 0, 0, 0, 1) };
                Add(road.Build("Roads"), atOrigin, shadow: false);
                Add(marks.Build("RoadMarks"), atOrigin, shadow: false);

                // Utility poles marching down both sides of every street. These are
                // the verticals the flat city was completely missing.
                var lampSpots = new List<Placement>();
                foreach (int gy in roadRows)
                    for (int x = 8; x < W - 8; x += 7)
                    {
                        if (Math.Abs(x - canalX) < 5) continue;
                        int side = (x / 7) % 2 < 1 ? -1 : 1;
                        double wx = x - W / 2.0, wz = gy - H / 2.0 + side * 2.4;
                        lampSpots.Add(RefPlace(wx, ElevAt(wx, wz), wz, side > 0 ? Math.PI : 0, 0.95 + rnd.Next() * 0.2));
                    }
                Add(PropLibrary.StreetLamp(), lampSpots);

                // Kerbside life: stalls, parked tricycles, planters.
                var stallSpots = new List<Placement>();
                var trikeSpots = new List<Placement>();
                foreach (int gy in roadRows)
                    for (int x = 12; x < W - 12; x += 9)
                    {
                        if (Math.Abs(x - canalX) < 6) continue;
                        if (rnd.Next() > 0.55)
                        {
                            double wx = x - W / 2.0 + rnd.Next() * 2;
                            double wz = gy - H / 2.0 + (rnd.Next() > 0.5 ? 2.7 : -2.7);
                            stallSpots.Add(RefPlace(wx, ElevAt(wx, wz), wz, rnd.Next() * Math.PI * 2, 1));
                        }
                        if (rnd.Next() > 0.45)
                        {
                            double wx = x - W / 2.0 + rnd.Next() * 3;
                            double wz = gy - H / 2.0 + (rnd.Next() > 0.5 ? 1.9 : -1.9);
                            double yaw = rnd.Next() * 0.5 + (rnd.Next() > 0.5 ? 0 : Math.PI);
                            trikeSpots.Add(RefPlace(wx, ElevAt(wx, wz), wz, yaw, 1));
                        }
                    }
                Add(PropLibrary.MarketStall(ref rnd), stallSpots);
                Add(PropLibrary.Tricycle(ref rnd), trikeSpots);

                // Even a paved barangay has weeds, potted plants and rubble.
                var uBush = Gather(110, DryLand, 0.7, 1.2);
                Add(PropLibrary.Bush(ref rnd), uBush, shadow: false);
                var uTuft = Gather(160, (e, x, y) => e > 0.25 && e < 3.6, 0.7, 1.3, jitter: 0.7);
                Add(PropLibrary.GrassTuft(ref rnd), uTuft, shadow: false);
                var uRock = Gather(50, (e, x, y) => e > 0.2 && e < 3.8, 0.7, 1.3);
                Add(PropLibrary.Rock(ref rnd), uRock, shadow: false);
                // A few palms survive along the canal, as they do in real Metro Manila.
                var uPalm = Gather(16, (e, px, py) => e > 0.45 && e < 2.4 && Math.Abs(px - canalX) < 14,
                                   0.8, 1.1, reserveR: 1);
                Add(PropLibrary.CoconutPalm(ref rnd), uPalm);
            }

            // ── Boats ────────────────────────────────────────────────────────
            // Boats belong in water. They used to be dropped at fixed columns on one
            // fixed row, which on both water maps left them beached on dry grass. The
            // hull is 0.34 deep and the outriggers sit lower still, so a boat needs
            // real draught under it, and they are spread out — eight boats in one
            // huddle is not a fishing village.
            {
                int placed = 0;
                int want = isVillage ? 8 : 3;
                var taken = new List<(int x, int y)>();
                var boatProtos = new Dictionary<string, Mesh>();
                var boatBatches = new Dictionary<string, PropBatch>();

                for (int attempt = 0; attempt < 900 && placed < want; attempt++)
                {
                    int px = 6 + (int)(rnd.Next() * (W - 12));
                    int py = 6 + (int)(rnd.Next() * (H - 12));
                    double e = elev[py * W + px];
                    bool clear = true;
                    foreach (var t in taken)
                        if (Math.Sqrt(Math.Pow(t.x - px, 2) + Math.Pow(t.y - py, 2)) <= 7) { clear = false; break; }

                    if (e > -2.6 && e < -0.85 && clear)
                    {
                        taken.Add((px, py));
                        string key = "boat" + (placed % 4);
                        if (!boatProtos.TryGetValue(key, out var proto))
                        {
                            proto = PropLibrary.BangkaBoat(ref rnd);
                            boatProtos[key] = proto;
                        }
                        if (!boatBatches.TryGetValue(key, out var bb))
                        {
                            bb = new PropBatch { Mesh = proto, CastShadow = true, Dynamic = true };
                            boatBatches[key] = bb;
                            batches.Add(bb);
                        }
                        double yaw = rnd.Next() * Math.PI * 2;
                        // Sits at sea level, not on the seabed — it floats.
                        bb.Instances.Add(ToMatrix(RefPlace(px - W / 2.0, FloodSim.SeaLevel, py - H / 2.0, yaw, 1)));
                        placed++;
                    }
                }
            }

            return batches;
        }

        /// <summary>
        /// Mangroves and drain pumps, which are not scattered but PAINTED — they come
        /// from simulation state the player edits, so they are rebuilt whenever the
        /// counts change rather than once per preset, and they draw nothing from the
        /// RNG. They also sit on cell centres, unlike the raw reference coordinates
        /// the roads use.
        /// </summary>
        public static List<PropBatch> BuildSimProps(FloodSim sim)
        {
            var batches = new List<PropBatch>();
            var elev = sim.Elev;

            PropBatch mang = null, drn = null;
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = y * W + x;
                    var m = Matrix4x4.TRS(new Vector3(Cx(x), elev[i], Cz(y)), Quaternion.identity, Vector3.one);
                    if (sim.Mang[i] != 0)
                    {
                        mang ??= new PropBatch { Mesh = PropLibrary.MangroveTree(), CastShadow = true };
                        mang.Instances.Add(m);
                    }
                    if (sim.Drn[i] != 0)
                    {
                        drn ??= new PropBatch { Mesh = PropLibrary.Drain(), CastShadow = true };
                        drn.Instances.Add(m);
                    }
                }

            if (mang != null) batches.Add(mang);
            if (drn != null) batches.Add(drn);
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
