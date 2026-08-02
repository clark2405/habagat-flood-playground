using System.Collections.Generic;
using UnityEngine;
using Rng = Habagat.JsMath.Rng;

namespace Habagat.Render
{
    /// <summary>Which silhouette a preset puts on its horizon.</summary>
    public readonly struct BackdropConfig
    {
        public readonly bool City;
        public readonly int Color;
        public readonly int Count;
        public readonly bool BehindOnly;
        public readonly float Radius, Spread, HeightLo, HeightHi;

        public BackdropConfig(bool city, int color, int count, bool behindOnly,
                              float radius, float spread, float heightLo, float heightHi)
        {
            City = city; Color = color; Count = count; BehindOnly = behindOnly;
            Radius = radius; Spread = spread; HeightLo = heightLo; HeightHi = heightHi;
        }

        public static BackdropConfig For(PresetType t) => t switch
        {
            PresetType.River => new BackdropConfig(false, 0x40624a, 42, false, 292f, 96f, 26f, 66f),
            PresetType.Urban => new BackdropConfig(true, 0x3f4852, 190, false, 200f, 150f, 0f, 0f),
            PresetType.Island => new BackdropConfig(false, 0x4f7a86, 22, false, 300f, 95f, 12f, 30f),
            _ => new BackdropConfig(false, 0x5b7b7a, 34, true, 300f, 95f, 20f, 50f),
        };
    }

    /// <summary>
    /// Distant silhouettes — mountains, forested peaks, or a city skyline depending
    /// on the preset. Ported from section 7 of ThreeCanvas.jsx.
    ///
    /// They sit 240+ units out so fog dissolves most of them, which is what sells
    /// them as distance rather than as props parked around the edge of a tile.
    ///
    /// Depth comes from LAYERS, not from one ring. A single band at one distance and
    /// one haze level reads as a painted wall standing around the map; three bands,
    /// each further out and hazed harder than the one in front, is what actually
    /// makes a horizon recede.
    ///
    /// The backdrop never moves, so each layer bakes into one mesh — 190 separate
    /// building meshes would be 190 draw calls for scenery nobody ever touches.
    /// Colour lives in the material rather than in vertex colours, because the haze
    /// is re-mixed toward the live sky colour every frame.
    /// </summary>
    public static class Backdrop
    {
        private readonly struct Layer
        {
            public readonly float Dr, Haze, Hs;
            public Layer(float dr, float haze, float hs) { Dr = dr; Haze = haze; Hs = hs; }
        }

        private static readonly Layer[] Layers =
        {
            new Layer(0.00f, 0.34f, 1.00f),
            new Layer(0.42f, 0.58f, 1.35f),
            new Layer(0.95f, 0.78f, 1.75f),
        };

        /// <summary>One baked layer plus what it needs to be re-hazed each frame.</summary>
        public sealed class LayerMat
        {
            public Material Mat;
            public Color Base;
            public float Haze;
        }

        public static List<LayerMat> Build(PresetType type, Transform parent, Material template)
        {
            var cfg = BackdropConfig.For(type);
            var env = EnvConfig.For(type);
            var rnd = new Rng(JsMath.StrSeed("backdrop:" + PresetName(type)));
            var mats = new List<LayerMat>();
            var baseCol = WorldBuilder.Hex(cfg.Color);
            var fog = WorldBuilder.Hex(env.Fog);

            for (int L = 0; L < Layers.Length; L++)
            {
                var lay = Layers[L];
                // Built in REFERENCE coordinates and mirrored by PropBuilder on the
                // way out, the same trick the roads use — it keeps the code readable
                // against the original and gets the handedness right without
                // reasoning about it twice.
                var b = new PropBuilder();
                bool any = false;

                if (cfg.City)
                {
                    int n = Mathf.RoundToInt(cfg.Count * (L == 0 ? 0.5f : L == 1 ? 0.32f : 0.28f));
                    for (int i = 0; i < n; i++)
                    {
                        double angle = rnd.Next() * Mathf.PI * 2;
                        double radius = cfg.Radius * (1 + lay.Dr) + rnd.Next() * cfg.Spread;
                        // Front layer is low-rise sprawl, back layers are towers.
                        // Reading a skyline depends on that gradient of heights, not
                        // on a field of random boxes.
                        double bh = (L == 0 ? 8 + rnd.Next() * 22 : 24 + rnd.Next() * 78) * lay.Hs;
                        double bw = L == 0 ? 9 + rnd.Next() * 14 : 12 + rnd.Next() * 22;
                        double cx = System.Math.Cos(angle) * radius, cz = System.Math.Sin(angle) * radius;

                        b.Add(Prim.Box((float)bw, (float)bh, (float)bw), Color.white,
                              new Vector3((float)cx, (float)(bh / 2 - 6), (float)cz));
                        any = true;

                        // A setback block on the taller towers, so the skyline is not
                        // a bar chart.
                        if (L > 0 && rnd.Next() > 0.55)
                        {
                            double th = bh * (0.2 + rnd.Next() * 0.3);
                            b.Add(Prim.Box((float)(bw * 0.55), (float)th, (float)(bw * 0.55)), Color.white,
                                  new Vector3((float)cx, (float)(bh - 6 + th / 2), (float)cz));
                        }
                    }
                }
                else
                {
                    int n = Mathf.RoundToInt(cfg.Count * (L == 0 ? 0.45f : L == 1 ? 0.32f : 0.3f));
                    for (int m = 0; m < n; m++)
                    {
                        double angle = (m / (double)n) * Mathf.PI * 2 + rnd.Next() * 0.55;
                        double radius = cfg.Radius * (1 + lay.Dr * 0.55) + rnd.Next() * cfg.Spread;
                        double mtx = System.Math.Cos(angle) * radius;
                        double mtz = System.Math.Sin(angle) * radius;
                        // Keep the bay open on the seaward side. Tested against the
                        // REFERENCE z, before the mirror.
                        if (cfg.BehindOnly && L == 0 && mtz > -40) continue;

                        double height = (cfg.HeightLo + rnd.Next() * (cfg.HeightHi - cfg.HeightLo)) * lay.Hs;
                        double widthM = 40 + rnd.Next() * 34;
                        b.Add(Prim.Flat(Prim.Cone((float)widthM, (float)height, 5)), Color.white,
                              new Vector3((float)mtx, (float)(height / 2 - 8), (float)mtz));
                        any = true;

                        // A subsidiary shoulder off each peak. A ridge of lone cones
                        // looks like a row of traffic bollards; overlapping masses
                        // look like mountains.
                        if (rnd.Next() > 0.35)
                        {
                            double sh = height * (0.45 + rnd.Next() * 0.3);
                            double off = widthM * (0.55 + rnd.Next() * 0.4) * (rnd.Next() > 0.5 ? 1 : -1);
                            b.Add(Prim.Flat(Prim.Cone((float)(widthM * 0.7), (float)sh, 5)), Color.white,
                                  new Vector3((float)(mtx + System.Math.Cos(angle + Mathf.PI / 2) * off),
                                              (float)(sh / 2 - 8),
                                              (float)(mtz + System.Math.Sin(angle + Mathf.PI / 2) * off)));
                        }
                    }
                }

                if (!any) continue;

                var lm = new LayerMat
                {
                    Mat = new Material(template),
                    Base = baseCol,
                    Haze = lay.Haze,
                };
                // Pre-hazed toward the fog colour by its own amount, so even the
                // un-fogged parts read as being at their own distance.
                lm.Mat.SetColor("_Tint", Color.Lerp(baseCol, fog, lay.Haze));
                mats.Add(lm);

                var go = new GameObject($"Backdrop{L}");
                go.transform.SetParent(parent, false);
                go.AddComponent<MeshFilter>().sharedMesh = b.Build($"Backdrop{L}");
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterial = lm.Mat;
                r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                r.receiveShadows = false;
            }

            return mats;
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
