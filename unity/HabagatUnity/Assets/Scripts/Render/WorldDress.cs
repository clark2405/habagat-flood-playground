using System;
using System.Collections.Generic;
using UnityEngine;
using Rng = Habagat.JsMath.Rng;

namespace Habagat.Render
{
    /// <summary>
    /// Dressing the world OUTSIDE the sandbox — section 14b of ThreeCanvas.jsx.
    ///
    /// The surrounding land is geometrically continuous with the play area, but on
    /// its own it is completely empty: a smooth wash of one colour running to the
    /// fog. That emptiness is what makes the background read as a separate thing
    /// from the sandbox — inside the border there are trees and houses, outside
    /// there is nothing at all, and the eye finds the line instantly. The fix is to
    /// keep scattering the SAME kinds of object past the border, thinning with
    /// distance and letting the fog do the blending.
    ///
    /// It scatters onto the outerland's own ring lattice, so every prop stands on
    /// the exact vertex height it was placed at.
    /// </summary>
    public static class WorldDress
    {
        private struct Spot
        {
            public double X, Y, Z, Yaw, S;
        }

        private static Matrix4x4 ToMatrix(in Spot s) =>
            Matrix4x4.TRS(new Vector3((float)s.X, (float)s.Y, (float)s.Z),
                          Quaternion.Euler(0f, -(float)s.Yaw * Mathf.Rad2Deg, 0f),
                          Vector3.one * (float)s.S);

        public static List<PropBatch> Build(OuterlandBuilder outer, PresetType type)
        {
            var batches = new List<PropBatch>();
            var rnd = new Rng(JsMath.StrSeed("worlddress:" + PresetName(type)));
            bool isCity = type == PresetType.Urban || type == PresetType.Basin;

            int OR = outer.RingCount, P = outer.PerimeterCount;
            var ringD = outer.RingDistances;
            var baseY = outer.BaseY;
            var verts = outer.LandVerts;

            var treeSpots = new List<Spot>();
            var houseSpots = new List<Spot>();
            var blockSpots = new List<Spot>();
            var scrubSpots = new List<Spot>();

            // Reject anything at or below the waterline, and anything too close in.
            bool Sample(out double d, out double h, out double x, out double z)
            {
                d = h = x = z = 0;
                int j = 2 + (int)(rnd.Next() * (OR - 3));
                int p = (int)(rnd.Next() * P);
                d = ringD[j];
                // Hold the dressing back from the border. Packed right up against
                // the sandbox it built a hedge around the play area — the outside
                // world should start just past the edge of attention, not on top
                // of it.
                if (d < 7.0 || d > 215) return false;
                if (d < 18 && rnd.Next() > (d - 7) / 11) return false;
                int k = j * P + p;
                h = baseY[k];
                if (h < 0.6) return false;
                x = verts[k].x;
                z = verts[k].z;
                return true;
            }

            for (int a = 0; a < 9000; a++)
            {
                if (!Sample(out double d, out double h, out double sx, out double sz)) continue;

                // Props grow with distance so a far-off stand of trees still covers
                // a few pixels; without this the outer world silts up into
                // featureless mush. Capped, though — ungoverned it made the near
                // background bigger than the village it is supposed to sit behind.
                double grow = 1 + Math.Min(d, 200) * 0.011;
                double by = h - 0.1, yaw = rnd.Next() * Math.PI * 2;

                if (isCity)
                {
                    // The city has to keep being a city right up to the skyline, or
                    // the barangay ends abruptly in open scrubland with towers
                    // behind it.
                    if (d < 130 && rnd.Next() < 0.62)
                    {
                        if (blockSpots.Count < 420)
                            blockSpots.Add(new Spot { X = sx, Y = by, Z = sz, Yaw = yaw, S = grow * (0.9 + rnd.Next() * 0.5) });
                    }
                    else if (rnd.Next() < 0.5)
                    {
                        if (treeSpots.Count < 300)
                            treeSpots.Add(new Spot { X = sx, Y = by, Z = sz, Yaw = yaw, S = grow * (0.7 + rnd.Next() * 0.5) });
                    }
                    else if (scrubSpots.Count < 300)
                    {
                        scrubSpots.Add(new Spot { X = sx, Y = by, Z = sz, Yaw = yaw, S = grow * (0.9 + rnd.Next() * 0.8) });
                    }
                }
                else
                {
                    // Village: forest thinning to scrub, with occasional hamlets.
                    // Groves rather than an even sprinkle — clumping is what makes
                    // scatter read as landscape instead of as wallpaper.
                    double rr = rnd.Next();
                    if (rr < 0.52)
                    {
                        if (treeSpots.Count < 620)
                        {
                            treeSpots.Add(new Spot { X = sx, Y = by, Z = sz, Yaw = yaw, S = grow * (0.75 + rnd.Next() * 0.6) });
                            int mates = rnd.NextInt(3);
                            for (int m = 0; m < mates && treeSpots.Count < 620; m++)
                            {
                                double rad = (1.5 + rnd.Next() * 3.5) * grow;
                                double ang = rnd.Next() * Math.PI * 2;
                                treeSpots.Add(new Spot
                                {
                                    X = sx + Math.Cos(ang) * rad, Y = by, Z = sz + Math.Sin(ang) * rad,
                                    Yaw = rnd.Next() * Math.PI * 2, S = grow * (0.7 + rnd.Next() * 0.55),
                                });
                            }
                        }
                    }
                    else if (rr < 0.62 && d > 12 && d < 150)
                    {
                        if (houseSpots.Count < 150)
                        {
                            int cluster = 2 + rnd.NextInt(4);
                            for (int m = 0; m < cluster && houseSpots.Count < 150; m++)
                            {
                                double rad = (m == 0 ? 0 : 2.5 + rnd.Next() * 5) * grow;
                                double ang = rnd.Next() * Math.PI * 2;
                                houseSpots.Add(new Spot
                                {
                                    X = sx + Math.Cos(ang) * rad, Y = by, Z = sz + Math.Sin(ang) * rad,
                                    Yaw = rnd.Next() * Math.PI * 2, S = grow * (0.85 + rnd.Next() * 0.3),
                                });
                            }
                        }
                    }
                    else if (scrubSpots.Count < 520)
                    {
                        scrubSpots.Add(new Spot { X = sx, Y = by, Z = sz, Yaw = yaw, S = grow * (1.0 + rnd.Next() * 1.1) });
                    }
                }
            }

            // Prototypes are built here, AFTER sampling, matching the original's
            // order — they draw from the same stream, so building one earlier would
            // shift every placement above.
            void Dress(Mesh mesh, List<Spot> spots)
            {
                if (spots.Count == 0) return;
                // Background only: it must never cast into the play area's shadow
                // map, whose frustum edge would draw a hard line across the world.
                var batch = new PropBatch { Mesh = mesh, CastShadow = false };
                foreach (var s in spots) batch.Instances.Add(ToMatrix(s));
                batches.Add(batch);
            }

            // Logged because these four counts are the cheapest way to tell whether
            // this walked the same RNG stream as the browser: the web build's
            // worldDressGroup exposes the identical numbers as InstancedMesh.count.
            Debug.Log($"[WorldDress] tree={treeSpots.Count} scrub={scrubSpots.Count} " +
                      $"house={houseSpots.Count} block={blockSpots.Count}");

            Dress(PropLibrary.DistantTree(ref rnd), treeSpots);
            Dress(PropLibrary.Bush(ref rnd), scrubSpots);
            if (houseSpots.Count > 0) Dress(PropLibrary.FarHouse(ref rnd), houseSpots);
            if (blockSpots.Count > 0) Dress(PropLibrary.FarBlock(ref rnd), blockSpots);

            return batches;
        }

        private static string PresetName(PresetType t) => t switch
        {
            PresetType.River => "river",
            PresetType.Urban => "urban",
            PresetType.Island => "island",
            PresetType.Basin => "basin",
            _ => "coastal",
        };
    }
}
