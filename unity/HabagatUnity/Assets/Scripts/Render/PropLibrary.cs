using UnityEngine;
using Rng = Habagat.JsMath.Rng;

namespace Habagat.Render
{
    /// <summary>
    /// The barangay's buildings and set dressing, ported from section 14 of
    /// ThreeCanvas.jsx.
    ///
    /// Everything here is built to the project's world scale: one terrain cell is
    /// one world unit and reads as roughly 4 m, so a house is ~2.5u and a coconut
    /// palm ~4.4u. The numbers below are not arbitrary — they are the fix for the
    /// original version, where props were ~0.75u, SMALLER than the cell they stood
    /// on, and the whole map read as confetti. Anything added here must be built to
    /// the same scale.
    ///
    /// Where a creator takes an <see cref="Rng"/> the order and COUNT of draws
    /// matters: it is the same stream the scatter placement draws from, so an extra
    /// call here shifts every prop placed afterwards.
    /// </summary>
    public static class PropLibrary
    {
        private static readonly Color DrainBox = PropPalette.Hex(0x5c6770);
        private static readonly Color WarnFlag = PropPalette.Hex(0xd9442b);
        private const float Pi = Mathf.PI;

        private static Quaternion RotY(float y) => Prim.EulerXYZ(0, y, 0);

        // ── Nipa hut ─────────────────────────────────────────────────────────
        public static Mesh NipaHut(int variant)
        {
            var b = new PropBuilder();
            Color thatch = (variant % 2) != 0 ? PropPalette.NipaDark : PropPalette.Nipa;

            // Stilts with cross-bracing. Flood water running UNDER a house is the
            // entire point of the building type, so the gap beneath the floor has
            // to be visible from the camera.
            foreach (float sx in new[] { -0.72f, 0.72f })
                foreach (float sz in new[] { -0.72f, 0.72f })
                    b.Add(Prim.Cylinder(0.1f, 0.13f, 1.0f, 6), PropPalette.Bamboo, new Vector3(sx, 0.5f, sz));
            foreach (float sz in new[] { -0.72f, 0.72f })
                b.Add(Prim.Box(1.5f, 0.07f, 0.07f), PropPalette.Bamboo, new Vector3(0, 0.72f, sz));

            // Floor platform, deliberately wider than the walls so it reads as a deck.
            b.Add(Prim.Box(1.95f, 0.12f, 1.95f), PropPalette.Bamboo, new Vector3(0, 1.03f, 0));

            // Sawali-walled body with corner posts.
            b.Add(Prim.Box(1.65f, 1.15f, 1.65f), PropPalette.Sawali, new Vector3(0, 1.68f, 0));
            foreach (float sx in new[] { -0.8f, 0.8f })
                foreach (float sz in new[] { -0.8f, 0.8f })
                    b.Add(Prim.Box(0.11f, 1.2f, 0.11f), PropPalette.Bamboo, new Vector3(sx, 1.68f, sz));

            // Shuttered window on two sides, propped open the way a real kubo's is.
            foreach (var (wx, wz, ry) in new[] { (0f, 0.84f, 0f), (0.84f, 0f, Pi / 2f) })
            {
                b.Add(Prim.Box(0.6f, 0.5f, 0.06f), PropPalette.Glass,
                      new Vector3(wx, 1.78f, wz), RotY(ry), Vector3.one, PropPalette.GlassEmissive);
                b.Add(Prim.Box(0.66f, 0.05f, 0.42f), PropPalette.Bamboo,
                      new Vector3(wx * 1.22f, 2.06f, wz * 1.22f),
                      Prim.EulerXYZ(ry != 0f ? 0f : -0.5f, ry, ry != 0f ? -0.5f : 0f));
            }

            // Two stacked cones: a steep thatch cap over a shallower flared eave.
            // One cone alone reads as a party hat; the break in the slope is what
            // makes it look like layered nipa shingles. The π/4 yaw squares up the
            // 4-segment cone, whose base corners sit on the axes.
            b.Add(Prim.Cone(1.62f, 0.5f, 4), thatch, new Vector3(0, 2.44f, 0), RotY(Pi / 4f));
            b.Add(Prim.Cone(1.2f, 1.0f, 4), thatch, new Vector3(0, 2.95f, 0), RotY(Pi / 4f));
            b.Add(Prim.Box(0.16f, 0.16f, 0.16f), PropPalette.Bamboo, new Vector3(0, 3.45f, 0));

            // Ladder up to the deck — the detail that sells the height of the stilts.
            foreach (float lx in new[] { -0.22f, 0.22f })
                b.Add(Prim.Cylinder(0.05f, 0.05f, 1.3f, 5), PropPalette.Bamboo,
                      new Vector3(lx, 0.55f, 1.28f), Prim.EulerXYZ(0.28f, 0, 0));
            for (int r = 0; r < 3; r++)
                b.Add(Prim.Box(0.44f, 0.05f, 0.05f), PropPalette.Bamboo,
                      new Vector3(0, 0.25f + r * 0.34f, 1.4f - r * 0.1f));

            return b.Build("NipaHut" + variant);
        }

        // ── Townhouse ────────────────────────────────────────────────────────
        public static Mesh Townhouse(int id)
        {
            var b = new PropBuilder();
            Color wall = PropPalette.WallColors[id % PropPalette.WallColors.Length];
            Color roofC = PropPalette.RoofColors[id % PropPalette.RoofColors.Length];

            // Low plinth: concrete houses here sit on a raised slab, and it gives
            // the silhouette a base instead of a box floating on the grass.
            b.Add(Prim.Box(2.35f, 0.22f, 2.35f), PropPalette.Concrete, new Vector3(0, 0.11f, 0));
            b.Add(Prim.Box(2.1f, 1.5f, 2.1f), wall, new Vector3(0, 0.97f, 0));

            // Roof with a real overhang plus a ridge cap — the overhang is what
            // makes a hipped roof read as a roof rather than a cone on a cube.
            b.Add(Prim.Cone(1.85f, 0.95f, 4), roofC, new Vector3(0, 2.18f, 0), RotY(Pi / 4f));
            b.Add(Prim.Box(2.3f, 0.1f, 2.3f), roofC, new Vector3(0, 1.75f, 0));

            b.Add(Prim.Box(0.5f, 0.9f, 0.08f), PropPalette.Wood, new Vector3(0, 0.67f, 1.06f));
            b.Add(Prim.Box(0.64f, 1.02f, 0.05f), PropPalette.Trim, new Vector3(0, 0.7f, 1.03f));
            b.Add(Prim.Box(0.7f, 0.1f, 0.3f), PropPalette.Concrete, new Vector3(0, 0.16f, 1.28f));

            // Windows on three faces, each with a sill. Three lit rectangles is the
            // cheapest thing that turns a blank cube into an inhabited house.
            foreach (var (a, c, ry) in new[]
            {
                (-0.62f, 1.06f, 0f), (0.62f, 1.06f, 0f),
                (1.06f, 0f, Pi / 2f), (-1.06f, 0f, Pi / 2f),
            })
            {
                b.Add(Prim.Box(0.52f, 0.5f, 0.06f), PropPalette.Glass,
                      new Vector3(a, 1.15f, c), RotY(ry), Vector3.one, PropPalette.GlassEmissive);
                b.Add(Prim.Box(0.62f, 0.07f, 0.12f), PropPalette.Trim, new Vector3(a, 0.87f, c), RotY(ry));
            }

            // Rooftop water drum — near-universal on Philippine houses and a good
            // little silhouette-breaker against the sky.
            b.Add(Prim.Cylinder(0.22f, 0.22f, 0.36f, 8), PropPalette.Tarp, new Vector3(0.62f, 2.0f, -0.55f));
            return b.Build("Townhouse" + id);
        }

        // ── Urban apartment ──────────────────────────────────────────────────
        public static Mesh UrbanApartment(int variant)
        {
            var b = new PropBuilder();
            Color[] tones =
            {
                PropPalette.Hex(0x8d97a1), PropPalette.Hex(0xa8a294),
                PropPalette.Hex(0x9fb0ad), PropPalette.Hex(0xb0a6a0),
            };
            Color wall = tones[variant % tones.Length];
            Color shopC = PropPalette.Hex(0xd9663a);

            // Three storeys rather than one cube. The old apartment was a 0.9u box
            // with a near-black roof slab, which at map scale rendered as a black
            // speck — height and banded floors are what make it read as a building.
            int storeys = 3 + (variant % 2);
            const float sh = 1.05f;

            // Ground floor is a shopfront: mixed-use ground level is what a Metro
            // Manila street actually looks like, and the warm colour lifts the whole
            // grey palette off the grey ground.
            b.Add(Prim.Box(2.4f, sh, 2.4f), shopC, new Vector3(0, sh / 2f, 0));
            b.Add(Prim.Box(1.7f, 0.6f, 0.06f), PropPalette.Glass,
                  new Vector3(0, 0.55f, 1.22f), Quaternion.identity, Vector3.one, PropPalette.GlassEmissive);
            b.Add(Prim.Box(2.5f, 0.08f, 0.55f), PropPalette.Tarp,
                  new Vector3(0, 1.0f, 1.35f), Prim.EulerXYZ(0.22f, 0, 0));

            for (int s = 1; s < storeys; s++)
            {
                float y = sh * s + sh / 2f;
                b.Add(Prim.Box(2.4f, sh, 2.4f), wall, new Vector3(0, y, 0));
                // Slab edge between floors — a horizontal line per storey is the
                // single clearest cue for "this is a multi-storey building".
                b.Add(Prim.Box(2.56f, 0.1f, 2.56f), PropPalette.Concrete, new Vector3(0, sh * s, 0));
                foreach (var (ox, oz, ry) in new[]
                {
                    (0f, 1.22f, 0f), (1.22f, 0f, Pi / 2f), (-1.22f, 0f, Pi / 2f), (0f, -1.22f, 0f),
                })
                {
                    foreach (float off in new[] { -0.52f, 0.52f })
                        b.Add(Prim.Box(0.62f, 0.55f, 0.06f), PropPalette.Glass,
                              new Vector3(ox != 0f ? ox : off, y + 0.06f, oz != 0f ? oz : off),
                              RotY(ry), Vector3.one, PropPalette.GlassEmissive);
                }
                b.Add(Prim.Box(2.4f, 0.32f, 0.08f), PropPalette.Concrete, new Vector3(0, y - 0.32f, 1.26f));
            }

            // Rooftop clutter is what stops a stack of boxes ending in a dead plane.
            float roofTop = sh * storeys;
            b.Add(Prim.Box(2.56f, 0.26f, 2.56f), PropPalette.Concrete, new Vector3(0, roofTop + 0.13f, 0));
            foreach (float tx in new[] { -0.6f, 0.1f })
                b.Add(Prim.Cylinder(0.26f, 0.26f, 0.5f, 8), PropPalette.Tarp, new Vector3(tx, roofTop + 0.5f, -0.5f));
            b.Add(Prim.Cylinder(0.03f, 0.03f, 1.1f, 4), DrainBox, new Vector3(0.8f, roofTop + 0.8f, 0.7f));

            return b.Build("Apartment" + variant);
        }

        // ── Sari-sari store ──────────────────────────────────────────────────
        public static Mesh SariSariStore()
        {
            var b = new PropBuilder();
            Color wall = PropPalette.Hex(0xf4a261);
            Color awningC = PropPalette.Hex(0xe76f51);

            b.Add(Prim.Box(2.3f, 0.18f, 2.3f), PropPalette.Concrete, new Vector3(0, 0.09f, 0));
            b.Add(Prim.Box(2.05f, 1.5f, 2.05f), wall, new Vector3(0, 0.93f, 0));
            // Corrugated GI roof, slightly pitched — the flat-topped version read
            // as an unfinished box.
            b.Add(Prim.Box(2.4f, 0.12f, 2.4f), PropPalette.Rust, new Vector3(0, 1.72f, 0), Prim.EulerXYZ(0, 0, 0.09f));

            // The counter grille: a sari-sari store IS its serving window, so it
            // gets the biggest single detail on the building.
            b.Add(Prim.Box(1.3f, 0.72f, 0.08f), PropPalette.Glass,
                  new Vector3(0, 1.1f, 1.04f), Quaternion.identity, Vector3.one, PropPalette.GlassEmissive);
            b.Add(Prim.Box(1.5f, 0.14f, 0.36f), PropPalette.Wood, new Vector3(0, 0.72f, 1.14f));
            for (int i = 0; i < 5; i++)
                b.Add(Prim.Box(0.05f, 0.72f, 0.05f), DrainBox, new Vector3(-0.52f + i * 0.26f, 1.1f, 1.09f));

            // Deep awning on posts + hanging sachet strips, the signature of the shop.
            b.Add(Prim.Box(2.5f, 0.09f, 1.15f), awningC, new Vector3(0, 1.66f, 1.62f), Prim.EulerXYZ(0.2f, 0, 0));
            foreach (float px in new[] { -1.1f, 1.1f })
                b.Add(Prim.Cylinder(0.06f, 0.06f, 1.5f, 5), PropPalette.Bamboo, new Vector3(px, 0.75f, 2.08f));
            for (int s = 0; s < 6; s++)
                b.Add(Prim.Box(0.14f, 0.5f, 0.03f), PropPalette.Trim, new Vector3(-0.85f + s * 0.34f, 1.32f, 1.12f));
            b.Add(Prim.Box(1.9f, 0.4f, 0.07f), PropPalette.Trim, new Vector3(0, 1.58f, 1.06f));

            for (int c = 0; c < 3; c++)
                b.Add(Prim.Box(0.34f, 0.3f, 0.34f), PropPalette.Wood,
                      new Vector3(-0.9f + c * 0.36f, 0.24f + (c == 1 ? 0.3f : 0f), 1.75f));
            b.Add(Prim.Box(0.44f, 0.62f, 0.4f), PropPalette.Tarp, new Vector3(0.85f, 0.4f, 1.72f));

            return b.Build("SariSariStore");
        }

        // ── Barangay hall ────────────────────────────────────────────────────
        public static Mesh BarangayHall()
        {
            var b = new PropBuilder();
            Color wall = PropPalette.Hex(0x4a90e2);
            Color roofC = PropPalette.Hex(0x2b3a4b);
            Color sandbag = PropPalette.Hex(0xd9c5a0);

            // The civic building is the landmark of the barangay: widest footprint,
            // a portico, and a flag. If everything is the same size there is nothing
            // for the eye to anchor on.
            b.Add(Prim.Box(4.0f, 0.3f, 3.0f), PropPalette.Concrete, new Vector3(0, 0.15f, 0));
            b.Add(Prim.Box(3.5f, 1.7f, 2.6f), wall, new Vector3(0, 1.15f, 0));
            b.Add(Prim.Cone(2.75f, 1.05f, 4), roofC, new Vector3(0, 2.5f, 0), RotY(Pi / 4f));
            b.Add(Prim.Box(3.9f, 0.12f, 3.0f), roofC, new Vector3(0, 2.02f, 0));

            b.Add(Prim.Box(2.4f, 0.14f, 1.1f), PropPalette.Trim, new Vector3(0, 1.75f, 1.75f));
            foreach (float cx in new[] { -1.0f, -0.34f, 0.34f, 1.0f })
                b.Add(Prim.Cylinder(0.1f, 0.12f, 1.6f, 8), PropPalette.Trim, new Vector3(cx, 0.95f, 2.15f));
            b.Add(Prim.Box(1.0f, 1.15f, 0.08f), PropPalette.Glass,
                  new Vector3(0, 0.9f, 1.32f), Quaternion.identity, Vector3.one, PropPalette.GlassEmissive);
            for (int s = 0; s < 2; s++)
                b.Add(Prim.Box(2.2f, 0.12f, 0.3f + s * 0.2f), PropPalette.Concrete,
                      new Vector3(0, 0.24f - s * 0.12f, 2.5f + s * 0.28f));

            foreach (float wx in new[] { -1.2f, -0.4f, 0.4f, 1.2f })
                b.Add(Prim.Box(0.5f, 0.72f, 0.06f), PropPalette.Glass,
                      new Vector3(wx, 1.3f, 1.32f), Quaternion.identity, Vector3.one, PropPalette.GlassEmissive);
            foreach (float wz in new[] { -0.7f, 0.4f })
                foreach (float sx in new[] { -1.77f, 1.77f })
                    b.Add(Prim.Box(0.5f, 0.72f, 0.06f), PropPalette.Glass,
                          new Vector3(sx, 1.3f, wz), RotY(Pi / 2f), Vector3.one, PropPalette.GlassEmissive);

            // Flagpole — the one vertical accent in the whole village.
            b.Add(Prim.Cylinder(0.045f, 0.055f, 3.4f, 6), PropPalette.Trim, new Vector3(-2.35f, 1.7f, 1.9f));
            b.Add(Prim.Box(0.75f, 0.45f, 0.03f), WarnFlag, new Vector3(-1.95f, 3.15f, 1.9f));

            // Sandbag line across the front — the flood-defence read, at a size
            // where the individual bags are actually visible.
            for (int row = 0; row < 2; row++)
                for (float bx = -1.6f; bx <= 1.6f; bx += 0.42f)
                    b.Add(Prim.Box(0.38f, 0.17f, 0.22f), sandbag,
                          new Vector3(bx + (row != 0 ? 0.2f : 0f), 0.09f + row * 0.17f, 2.95f));

            return b.Build("BarangayHall");
        }

        // ── Coconut palm ─────────────────────────────────────────────────────
        public static Mesh CoconutPalm(ref Rng rnd)
        {
            var b = new PropBuilder();
            float lean = (rnd.NextF() - 0.5f) * 0.34f;
            const float TH = 4.4f; // a palm towers over a one-storey hut — it should here too

            // Segmented trunk: a single tapered cylinder is a stick, whereas a stack
            // of slightly offset segments curves the way a palm actually leans.
            const int segs = 5;
            for (int s = 0; s < segs; s++)
            {
                float t0 = s / (float)segs, t1 = (s + 1) / (float)segs;
                float h = TH / segs;
                b.Add(Prim.Cylinder(0.15f - t1 * 0.07f, 0.15f - t0 * 0.07f, h * 1.04f, 6), PropPalette.Wood,
                      new Vector3(Mathf.Sin(lean * t0) * TH * t0 * 0.55f, h * (s + 0.5f), 0),
                      Prim.EulerXYZ(0, 0, lean * (0.4f + t0)));
            }

            // Crown of drooping fronds: three rings of tapered blades rather than
            // one ring of flat slabs, which from any distance just reads as an
            // asterisk. Nested like the original's THREE.Group.
            b.Push(new Vector3(Mathf.Sin(lean) * TH * 0.5f, TH, 0));
            for (int ring = 0; ring < 3; ring++)
            {
                int n = ring == 0 ? 6 : ring == 1 ? 5 : 4;
                for (int a = 0; a < n; a++)
                {
                    float angle = (a / (float)n) * Pi * 2f + ring * 0.55f + rnd.NextF() * 0.25f;
                    float len = ring == 0 ? 2.0f : ring == 1 ? 1.55f : 1.05f;
                    Color mat = ring == 2 ? PropPalette.LeafLight : PropPalette.PalmLeaf;
                    b.Add(Prim.Cylinder(0.26f, 0.03f, len, 3), mat,
                          new Vector3(Mathf.Cos(angle) * len * 0.42f,
                                      ring == 0 ? -0.16f : ring == 1 ? 0.12f : 0.34f,
                                      Mathf.Sin(angle) * len * 0.42f),
                          Prim.EulerXYZ(Pi / 2f, -angle, ring == 0 ? 0.62f : ring == 1 ? 0.95f : 1.25f),
                          new Vector3(1, 1, 0.3f));
                }
            }
            for (int c = 0; c < 4; c++)
            {
                float a = (c / 4f) * Pi * 2f;
                b.Add(Prim.Dodecahedron(0.12f), PropPalette.Banana,
                      new Vector3(Mathf.Cos(a) * 0.2f, -0.2f, Mathf.Sin(a) * 0.2f));
            }
            b.Pop();

            // The original applies a random yaw to the whole group. Here that would
            // bake a fixed rotation into a shared prototype, so it is dropped: the
            // scatter already gives every instance its own yaw.
            rnd.Next();
            return b.Build("CoconutPalm");
        }

        // ── Set dressing ─────────────────────────────────────────────────────
        // The map was 6144 cells carrying 30 palms and a dozen huts, so most of the
        // frame was unbroken flat colour. These are the small, cheap, repeatable
        // things that fill the space between the landform and the buildings.

        public static Mesh Bush(ref Rng rnd)
        {
            var b = new PropBuilder();
            int n = 2 + rnd.NextInt(2);
            for (int i = 0; i < n; i++)
            {
                float r = 0.32f + rnd.NextF() * 0.24f;
                Color c = rnd.Next() > 0.5 ? PropPalette.LeafMid : PropPalette.LeafLight;
                b.Add(Prim.Dodecahedron(r), c,
                      new Vector3((rnd.NextF() - 0.5f) * 0.5f, r * 0.75f, (rnd.NextF() - 0.5f) * 0.5f),
                      Quaternion.identity, new Vector3(1, 0.78f, 1));
            }
            return b.Build("Bush");
        }

        public static Mesh BananaPlant(ref Rng rnd)
        {
            var b = new PropBuilder();
            b.Add(Prim.Cylinder(0.09f, 0.14f, 1.0f, 5), PropPalette.Banana, new Vector3(0, 0.5f, 0));
            for (int l = 0; l < 6; l++)
            {
                float a = (l / 6f) * Pi * 2f + rnd.NextF() * 0.4f;
                Color c = (l % 2) != 0 ? PropPalette.Banana : PropPalette.LeafMid;
                b.Add(Prim.Box(1.25f, 0.05f, 0.42f), c,
                      new Vector3(Mathf.Cos(a) * 0.55f, 1.05f + rnd.NextF() * 0.2f, Mathf.Sin(a) * 0.55f),
                      Prim.EulerXYZ(0, -a, -0.45f - rnd.NextF() * 0.25f));
            }
            return b.Build("BananaPlant");
        }

        public static Mesh Rock(ref Rng rnd)
        {
            var b = new PropBuilder();
            float r = 0.28f + rnd.NextF() * 0.3f;
            b.Add(Prim.Dodecahedron(r), PropPalette.Rock, new Vector3(0, r * 0.55f, 0),
                  Prim.EulerXYZ(rnd.NextF() * 3f, rnd.NextF() * 3f, rnd.NextF() * 3f),
                  new Vector3(1, 0.7f, 0.85f));
            if (rnd.Next() > 0.5)
                b.Add(Prim.Dodecahedron(r * 0.45f), PropPalette.Rock, new Vector3(r * 1.1f, r * 0.25f, r * 0.5f));
            return b.Build("Rock");
        }

        /// <summary>
        /// Reed/grass clump — a handful of thin tapered blades. They catch the light
        /// at grazing angles and break up the big empty greens without costing
        /// anything, which is exactly what "barren" was asking for.
        /// </summary>
        public static Mesh GrassTuft(ref Rng rnd)
        {
            var b = new PropBuilder();
            int n = 5 + rnd.NextInt(4);
            for (int i = 0; i < n; i++)
            {
                float h = 0.35f + rnd.NextF() * 0.4f;
                Color c = rnd.Next() > 0.4 ? PropPalette.LeafLight : PropPalette.LeafMid;
                b.Add(Prim.Cylinder(0.012f, 0.05f, h, 3), c,
                      new Vector3((rnd.NextF() - 0.5f) * 0.38f, h * 0.5f, (rnd.NextF() - 0.5f) * 0.38f),
                      Prim.EulerXYZ((rnd.NextF() - 0.5f) * 0.5f, rnd.NextF() * 3f, (rnd.NextF() - 0.5f) * 0.5f));
            }
            return b.Build("GrassTuft");
        }

        /// <summary>
        /// A short run of bamboo fence. Placed in lines beside the houses, these are
        /// what turn loose scattered buildings into something that reads as plots.
        /// </summary>
        public static Mesh Fence()
        {
            var b = new PropBuilder();
            for (int p = 0; p < 4; p++)
                b.Add(Prim.Cylinder(0.045f, 0.045f, 0.75f, 4), PropPalette.Bamboo,
                      new Vector3(-0.75f + p * 0.5f, 0.37f, 0));
            foreach (float y in new[] { 0.28f, 0.58f })
                b.Add(Prim.Box(2.05f, 0.05f, 0.05f), PropPalette.Bamboo, new Vector3(-0.05f, y, 0));
            return b.Build("Fence");
        }

        /// <summary>
        /// Laundry strung between two poles — pure cozy-village texture, and the
        /// brightest small colour accents on the map.
        /// </summary>
        public static Mesh LaundryLine(ref Rng rnd)
        {
            var b = new PropBuilder();
            foreach (float px in new[] { -1.1f, 1.1f })
                b.Add(Prim.Cylinder(0.05f, 0.06f, 1.7f, 5), PropPalette.Bamboo, new Vector3(px, 0.85f, 0));
            b.Add(Prim.Box(2.2f, 0.02f, 0.02f), PropPalette.Trim, new Vector3(0, 1.6f, 0));
            for (int c = 0; c < 5; c++)
            {
                Color cloth = PropPalette.ClothColors[rnd.NextInt(PropPalette.ClothColors.Length)];
                b.Add(Prim.Box(0.3f, 0.45f, 0.03f), cloth, new Vector3(-0.85f + c * 0.42f, 1.35f, 0));
            }
            return b.Build("LaundryLine");
        }

        // ── Background dressing ──────────────────────────────────────────────
        // These live outside the sandbox and are seen from hundreds of units away
        // through fog, so they only have to hold a silhouette. Detail spent here
        // would be invisible; what matters is that SOMETHING keeps standing out
        // there, because the alternative is a smooth wash of one colour running to
        // the horizon — which is what made the background read as a flat backdrop
        // detached from the play area.

        /// <summary>Cheap two-blob canopy tree.</summary>
        public static Mesh DistantTree(ref Rng r)
        {
            var b = new PropBuilder();
            b.Add(Prim.Cylinder(0.1f, 0.16f, 1.1f, 4), PropPalette.Wood, new Vector3(0, 0.55f, 0));
            b.Add(Prim.Dodecahedron(0.85f), PropPalette.LeafMid, new Vector3(0, 1.6f, 0),
                  Quaternion.identity, new Vector3(1, 0.85f, 1));
            b.Add(Prim.Dodecahedron(0.6f), PropPalette.PalmLeaf,
                  new Vector3(r.NextF() * 0.5f - 0.25f, 2.25f, r.NextF() * 0.5f - 0.25f));
            return b.Build("DistantTree");
        }

        /// <summary>
        /// A far-off house: a coloured roof on a pale box is all that survives haze,
        /// and it is enough to say "the barangay keeps going out there".
        /// </summary>
        public static Mesh FarHouse(ref Rng r)
        {
            var b = new PropBuilder();
            Color wall = PropPalette.WallColors[r.NextInt(PropPalette.WallColors.Length)];
            Color roof = PropPalette.RoofColors[r.NextInt(PropPalette.RoofColors.Length)];
            b.Add(Prim.Box(2.0f, 1.3f, 2.0f), wall, new Vector3(0, 0.65f, 0));
            b.Add(Prim.Cone(1.7f, 0.85f, 4), roof, new Vector3(0, 1.72f, 0), RotY(Pi / 4f));
            return b.Build("FarHouse");
        }

        public static Mesh FarBlock(ref Rng r)
        {
            var b = new PropBuilder();
            Color[] tones =
            {
                PropPalette.Hex(0x8d97a1), PropPalette.Hex(0xa8a294), PropPalette.Hex(0x9fb0ad),
                PropPalette.Hex(0xb0a6a0), PropPalette.Hex(0xc0b6a8), PropPalette.Hex(0xb9a48f),
            };
            Color body = tones[r.NextInt(tones.Length)];
            int st = 2 + r.NextInt(3);
            b.Add(Prim.Box(2.1f, st * 1.0f, 2.1f), body, new Vector3(0, st * 1.0f / 2f, 0));

            // Roofs carry the colour. From an overhead camera the roof IS most of
            // what you see of a distant building, so a grey cap on every one of them
            // turned the sprawl into a field of headstones. Real Manila roofs are
            // rust red, faded teal and blue GI sheet — that variety is the whole
            // difference between a drab backdrop and a living city.
            Color[] roofs =
            {
                PropPalette.Hex(0xa4523a), PropPalette.Hex(0x8a6f4e), PropPalette.Hex(0x3f7f7a),
                PropPalette.Hex(0x4a6b93), PropPalette.Hex(0x9c5b3c), PropPalette.Hex(0x6b6a66),
                PropPalette.Hex(0x7c8a6a),
            };
            Color roofC = roofs[r.NextInt(roofs.Length)];

            if (r.Next() > 0.45)
            {
                b.Add(Prim.Cone(1.75f, 0.8f, 4), roofC, new Vector3(0, st * 1.0f + 0.4f, 0), RotY(Pi / 4f));
            }
            else
            {
                b.Add(Prim.Box(2.3f, 0.2f, 2.3f), roofC, new Vector3(0, st * 1.0f, 0));
                // Roof clutter, just enough to break the flat plane.
                b.Add(Prim.Box(0.5f, 0.4f, 0.5f), body,
                      new Vector3(r.NextF() - 0.5f, st * 1.0f + 0.3f, r.NextF() - 0.5f));
            }
            return b.Build("FarBlock");
        }

        /// <summary>
        /// A single beached log at the tideline. Exactly three draws — length, yaw,
        /// roll — and that count is load-bearing: the fence and laundry placements
        /// come later in the same stream, so a creator that draws a different number
        /// of values silently moves them.
        /// </summary>
        public static Mesh Driftwood(ref Rng rnd)
        {
            var b = new PropBuilder();
            float len = 1.1f + rnd.NextF() * 0.6f;
            float ry = rnd.NextF() * 3f;
            float rz = Pi / 2f + (rnd.NextF() - 0.5f) * 0.3f;
            b.Add(Prim.Cylinder(0.11f, 0.14f, len, 5), PropPalette.Wood,
                  new Vector3(0, 0.12f, 0), Prim.EulerXYZ(0, ry, rz));
            return b.Build("Driftwood");
        }

        // ── Street furniture ─────────────────────────────────────────────────

        /// <summary>
        /// Street lamp and power pole. Verticals are what a flat urban map is
        /// missing — without them the city preset is a grey plane with specks on it.
        /// </summary>
        public static Mesh StreetLamp()
        {
            var b = new PropBuilder();
            b.Add(Prim.Cylinder(0.07f, 0.1f, 3.2f, 6), DrainBox, new Vector3(0, 1.6f, 0));
            b.Add(Prim.Box(0.7f, 0.07f, 0.07f), DrainBox, new Vector3(0.33f, 3.15f, 0));
            b.Add(Prim.Box(0.34f, 0.12f, 0.2f), PropPalette.Trim, new Vector3(0.66f, 3.06f, 0));
            // Crossarm + insulators: the tangle of overhead wiring is a Manila signature.
            b.Add(Prim.Box(1.15f, 0.06f, 0.06f), PropPalette.Wood, new Vector3(0, 2.6f, 0));
            foreach (float ix in new[] { -0.45f, 0f, 0.45f })
                b.Add(Prim.Cylinder(0.05f, 0.05f, 0.14f, 5), PropPalette.Tarp, new Vector3(ix, 2.72f, 0));
            return b.Build("StreetLamp");
        }

        /// <summary>Roadside market stall under a tarp.</summary>
        public static Mesh MarketStall(ref Rng rnd)
        {
            var b = new PropBuilder();
            Color[] tarps =
            {
                PropPalette.Hex(0x3f8f86), PropPalette.Hex(0xd9663a),
                PropPalette.Hex(0x4a90e2), PropPalette.Hex(0xe8a33d),
            };
            Color tarp = tarps[rnd.NextInt(tarps.Length)];

            foreach (float px in new[] { -0.7f, 0.7f })
                foreach (float pz in new[] { -0.55f, 0.55f })
                    b.Add(Prim.Cylinder(0.045f, 0.045f, 1.5f, 4), PropPalette.Bamboo, new Vector3(px, 0.75f, pz));
            b.Add(Prim.Box(1.8f, 0.08f, 1.5f), tarp, new Vector3(0, 1.5f, 0), Prim.EulerXYZ(0.1f, 0, 0));
            b.Add(Prim.Box(1.5f, 0.1f, 0.7f), PropPalette.Wood, new Vector3(0, 0.72f, 0.2f));
            for (int c = 0; c < 3; c++)
            {
                Color crate = rnd.Next() > 0.5 ? PropPalette.Banana : PropPalette.Rust;
                b.Add(Prim.Box(0.28f, 0.22f, 0.28f), crate, new Vector3(-0.45f + c * 0.45f, 0.88f, 0.2f));
            }
            return b.Build("MarketStall");
        }

        /// <summary>
        /// Parked tricycle — the single most recognisable object on a barangay street.
        /// </summary>
        public static Mesh Tricycle(ref Rng rnd)
        {
            var b = new PropBuilder();
            Color[] cols =
            {
                PropPalette.Hex(0xd9442b), PropPalette.Hex(0x3f8f86),
                PropPalette.Hex(0x4a90e2), PropPalette.Hex(0xe8a33d),
            };
            Color body = cols[rnd.NextInt(cols.Length)];

            b.Add(Prim.Box(0.75f, 0.55f, 0.62f), body, new Vector3(0.1f, 0.42f, 0.3f));
            b.Add(Prim.Box(0.85f, 0.07f, 0.72f), PropPalette.Rust, new Vector3(0.1f, 0.74f, 0.3f));
            b.Add(Prim.Box(0.85f, 0.22f, 0.2f), DrainBox, new Vector3(-0.05f, 0.33f, -0.22f));
            foreach (var (wx, wz) in new[] { (-0.42f, -0.22f), (0.42f, -0.22f), (0.3f, 0.55f) })
                b.Add(Prim.Cylinder(0.19f, 0.19f, 0.09f, 8), PropPalette.Wood,
                      new Vector3(wx, 0.19f, wz), Prim.EulerXYZ(0, 0, Pi / 2f));
            return b.Build("Tricycle");
        }

        /// <summary>
        /// The covered court, which is the social centre of a barangay and so the
        /// largest flat man-made thing on the map.
        ///
        /// A slab, not a plane: a flat plane laid on a heightmap gets sliced by any
        /// slope, which is why the court used to read as a red rag half-buried in the
        /// ground. The caller sits it on the HIGHEST point of its footprint so
        /// nothing can poke through.
        /// </summary>
        public static Mesh BasketballCourt()
        {
            var b = new PropBuilder();
            Color court = PropPalette.Hex(0xc23d27);
            Color line = PropPalette.Hex(0xf0e6d2);
            // A real court is 28x15 m; at ~4 m per cell that is about 7x3.8 units.
            const float CW = 7.6f, CD = 4.6f;

            b.Add(Prim.Box(CW, 0.22f, CD), court, new Vector3(0, -0.03f, 0));
            foreach (float z in new[] { -CD / 2f + 0.35f, CD / 2f - 0.35f })
                b.Add(Prim.Box(CW - 0.5f, 0.03f, 0.09f), line, new Vector3(0, 0.09f, z));
            b.Add(Prim.Box(0.1f, 0.03f, CD - 0.7f), line, new Vector3(0, 0.09f, 0));
            b.Add(Prim.Torus(0.95f, 0.05f, 4, 20), line, new Vector3(0, 0.09f, 0), Prim.EulerXYZ(-Pi / 2f, 0, 0));
            foreach (float kx in new[] { -CW / 2f + 1.15f, CW / 2f - 1.15f })
                b.Add(Prim.Torus(0.7f, 0.045f, 4, 16), line, new Vector3(kx, 0.09f, 0), Prim.EulerXYZ(-Pi / 2f, 0, 0));

            foreach (float side in new[] { -CW / 2f - 0.15f, CW / 2f + 0.15f })
            {
                float inward = side > 0 ? -1f : 1f;
                b.Add(Prim.Cylinder(0.09f, 0.13f, 2.9f, 6), DrainBox, new Vector3(side, 1.45f, 0));
                b.Add(Prim.Box(0.5f, 0.09f, 0.09f), DrainBox, new Vector3(side + inward * 0.25f, 2.55f, 0));
                b.Add(Prim.Box(0.09f, 0.72f, 1.05f), PropPalette.Trim, new Vector3(side + inward * 0.5f, 2.5f, 0));
                b.Add(Prim.Torus(0.24f, 0.035f, 4, 12), WarnFlag,
                      new Vector3(side + inward * 0.78f, 2.2f, 0), Prim.EulerXYZ(-Pi / 2f, 0, 0));
            }

            // Perimeter benches: cheap, and they stop the slab reading as a bare red
            // rectangle dropped on the grass.
            foreach (float bz in new[] { -CD / 2f - 0.5f, CD / 2f + 0.5f })
                foreach (float bx in new[] { -1.8f, 1.8f })
                {
                    b.Add(Prim.Box(1.5f, 0.1f, 0.32f), PropPalette.Wood, new Vector3(bx, 0.34f, bz));
                    foreach (float lx in new[] { -0.6f, 0.6f })
                        b.Add(Prim.Box(0.09f, 0.34f, 0.09f), DrainBox, new Vector3(bx + lx, 0.17f, bz));
                }
            return b.Build("BasketballCourt");
        }

        /// <summary>
        /// Outrigger fishing boat. A bangka is ~8-10 m long, about 2.5 units here —
        /// tapered hull plus a real bow so it reads as a boat from above rather than
        /// as a brick.
        /// </summary>
        public static Mesh BangkaBoat(ref Rng rnd)
        {
            var b = new PropBuilder();
            Color[] hulls =
            {
                PropPalette.Hex(0x4a90e2), PropPalette.Hex(0xd9442b),
                PropPalette.Hex(0x3f8f86), PropPalette.Hex(0xe8a33d),
            };
            Color hull = hulls[rnd.NextInt(hulls.Length)];

            b.Add(Prim.Box(2.5f, 0.34f, 0.56f), hull, new Vector3(0, 0.13f, 0));
            b.Add(Prim.Cone(0.33f, 0.9f, 4), hull, new Vector3(1.6f, 0.13f, 0), Prim.EulerXYZ(0, Pi / 4f, -Pi / 2f));
            b.Add(Prim.Cone(0.28f, 0.5f, 4), hull, new Vector3(-1.4f, 0.13f, 0), Prim.EulerXYZ(0, Pi / 4f, Pi / 2f));
            b.Add(Prim.Box(2.5f, 0.1f, 0.66f), PropPalette.Sawali, new Vector3(0, 0.33f, 0));
            foreach (float tx in new[] { -0.6f, 0.3f })
                b.Add(Prim.Box(0.14f, 0.07f, 0.56f), PropPalette.Wood, new Vector3(tx, 0.4f, 0));
            b.Add(Prim.Box(0.95f, 0.06f, 0.7f), PropPalette.Tarp, new Vector3(-0.15f, 0.95f, 0));
            foreach (float px in new[] { -0.55f, 0.25f })
                foreach (float pz in new[] { 0.28f, -0.28f })
                    b.Add(Prim.Cylinder(0.03f, 0.03f, 0.6f, 4), PropPalette.Bamboo, new Vector3(px, 0.63f, pz));

            // Outriggers: floats running PARALLEL to the hull on cross-booms. The old
            // version put two long boxes across the hull, which looked like a hammer.
            foreach (float side in new[] { -1.0f, 1.0f })
            {
                b.Add(Prim.Cylinder(0.08f, 0.08f, 2.1f, 5), PropPalette.Bamboo,
                      new Vector3(0, 0.11f, side), Prim.EulerXYZ(0, 0, Pi / 2f));
                foreach (float bx in new[] { -0.7f, 0.7f })
                    b.Add(Prim.Box(0.09f, 0.07f, Mathf.Abs(side) + 0.1f), PropPalette.Bamboo,
                          new Vector3(bx, 0.36f, side / 2f));
            }
            return b.Build("BangkaBoat");
        }

        /// <summary>
        /// Mangroves are defined by their stilt roots standing clear of the water —
        /// at the old 0.5u height that detail was invisible and they just looked like
        /// green dots.
        /// </summary>
        public static Mesh MangroveTree()
        {
            var b = new PropBuilder();
            for (int r = 0; r < 5; r++)
            {
                float a = (r / 5f) * Pi * 2f;
                b.Add(Prim.Cylinder(0.045f, 0.075f, 0.75f, 4), PropPalette.Bamboo,
                      new Vector3(Mathf.Cos(a) * 0.22f, 0.34f, Mathf.Sin(a) * 0.22f),
                      Prim.EulerXYZ(Mathf.Cos(a) * 0.42f, 0, -Mathf.Sin(a) * 0.42f));
            }
            b.Add(Prim.Cylinder(0.11f, 0.17f, 0.85f, 5), PropPalette.Bamboo, new Vector3(0, 0.9f, 0));
            b.Add(Prim.Dodecahedron(0.72f), PropPalette.PalmLeaf, new Vector3(0, 1.6f, 0),
                  Quaternion.identity, new Vector3(1, 0.8f, 1));
            b.Add(Prim.Dodecahedron(0.46f), PropPalette.LeafMid, new Vector3(0.4f, 1.3f, 0.28f),
                  Quaternion.identity, new Vector3(1, 0.8f, 1));
            b.Add(Prim.Dodecahedron(0.4f), PropPalette.LeafLight, new Vector3(-0.35f, 1.42f, -0.3f),
                  Quaternion.identity, new Vector3(1, 0.8f, 1));
            return b.Build("MangroveTree");
        }

        /// <summary>
        /// Kerb inlet and pump housing, with a visible outfall pipe so it reads as
        /// drainage infrastructure rather than a grey box.
        /// </summary>
        public static Mesh Drain()
        {
            var b = new PropBuilder();
            Color grate = PropPalette.Hex(0x3ba99c);

            b.Add(Prim.Box(1.35f, 0.42f, 1.35f), DrainBox, new Vector3(0, 0.21f, 0));
            b.Add(Prim.Box(1.55f, 0.14f, 1.55f), PropPalette.Concrete, new Vector3(0, 0.07f, 0));
            b.Add(Prim.Plane(0.95f, 0.95f), grate, new Vector3(0, 0.43f, 0), Prim.EulerXYZ(-Pi / 2f, 0, 0));
            for (int i = 0; i < 4; i++)
                b.Add(Prim.Box(0.95f, 0.05f, 0.07f), DrainBox, new Vector3(0, 0.45f, -0.34f + i * 0.23f));
            b.Add(Prim.Box(0.5f, 0.55f, 0.5f), PropPalette.Rust, new Vector3(0.75f, 0.5f, -0.5f));
            b.Add(Prim.Cylinder(0.1f, 0.1f, 1.0f, 6), DrainBox,
                  new Vector3(1.2f, 0.62f, -0.5f), Prim.EulerXYZ(0, 0, Pi / 2f));
            return b.Build("Drain");
        }
    }
}
