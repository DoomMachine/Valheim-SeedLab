using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using HarmonyLib;
using SeedLab.Contracts.Dump;
using UnityEngine;

namespace SeedLab.Dumper
{
    /// <summary>
    /// Dense samples of <c>GetBiome</c> / <c>GetBiomeHeight</c> / <c>GetBaseHeight</c> as raw
    /// little-endian float32, never decimal text.
    ///
    /// All three are pure functions of the generator's state - they draw nothing from
    /// <c>UnityEngine.Random</c> - so sampling them cannot perturb anything. <c>GetBiomeHeight</c> even
    /// computes a <c>BiomeSector</c> and never uses it, which is why the height field is independent of
    /// alt-biome data and is clean ground truth.
    /// </summary>
    internal static class Grids
    {
        /// <summary>The <c>GetBaseHeight</c> delegate. The method is private, so it is reached through
        /// an open-instance delegate rather than <c>MethodInfo.Invoke</c>: at 4.2 million samples the
        /// difference between the two is minutes.</summary>
        private static Func<WorldGenerator, float, float, bool, float> _baseHeight;

        private static Func<WorldGenerator, float, float, bool, float> BaseHeight()
        {
            if (_baseHeight != null) return _baseHeight;
            var mi = AccessTools.Method(typeof(WorldGenerator), "GetBaseHeight",
                                        new[] { typeof(float), typeof(float), typeof(bool) });
            if (mi == null)
            {
                throw new MissingMethodException("WorldGenerator.GetBaseHeight(float,float,bool) not found.");
            }
            _baseHeight = AccessTools.MethodDelegate<Func<WorldGenerator, float, float, bool, float>>(mi);
            return _baseHeight;
        }

        public static bool IsKnown(string id)
        {
            switch (id)
            {
                case "none":
                case "coarse128":
                case "findlakes":
                case "full12":
                case "edges":
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>Writes the named grid for the CURRENT <c>WorldGenerator.instance</c>.
        /// Yields between rows so a long grid does not freeze the game for its whole duration.
        ///
        /// <paramref name="source"/> is <c>DumpFormat.SourceWorld</c> or <c>DumpFormat.SourceMenu</c>
        /// and goes in the file name: both modes accept <c>grid=</c>, both write with
        /// <c>FileMode.Create</c>, and the same seed can be sampled from a loaded world and from the
        /// menu - so without it the second run would overwrite the first.</summary>
        public static IEnumerator Write(DumpWriter w, WorldGenerator wg, WorldGenDumpFile d, string id,
                                        string source, List<string> written)
        {
            switch (id)
            {
                case "none":
                    yield break;

                // A cheap regression lattice. NOTE it is deliberately NOT the lattice FindLakes walks:
                // FindLakes runs -10000 + 128k and 10000 is not a multiple of 128, so the two never
                // coincide. Using this one to validate lake placement would compare nothing.
                case "coarse128":
                    yield return null;
                    foreach (object o in Lattice(w, wg, d, "coarse128", source, -10496f, -10496f, 128f, 165, 165, written))
                        yield return o;
                    yield break;

                // The exact FindLakes lattice: for (float y = -10000; y <= 10000; y += 128) with the
                // same for x, i.e. -10000 + 128k for k = 0..156 (last value 9968), y outer, x inner.
                // This is the only grid that can test lake placement, and lakes feed PlaceRivers.
                case "findlakes":
                    yield return null;
                    foreach (object o in Lattice(w, wg, d, "findlakes", source, -10000f, -10000f, 128f, 157, 157, written))
                        yield return o;
                    yield break;

                // The minimap-cache lattice: AltBiomeWorldData.MapSpaceToWorldSpace(x) = (x-1024)*12+6,
                // so j=0 is -12282 and j=2047 is +12282 - NOT +/-12288. A half-pixel offset on the
                // wrong lattice reports a whole-grid mismatch against the cache.
                case "full12":
                    yield return null;
                    foreach (object o in Lattice(w, wg, d, "full12", source, -12282f, -12282f, 12f, 2048, 2048, written))
                        yield return o;
                    yield break;

                case "edges":
                    yield return null;
                    foreach (object o in Edges(w, wg, d, source, written)) yield return o;
                    yield break;

                default:
                    Plugin.Log.LogWarning("Unknown grid '" + id + "', skipped.");
                    yield break;
            }
        }

        private static IEnumerable Lattice(DumpWriter w, WorldGenerator wg, WorldGenDumpFile d, string id,
                                           string source, float x0, float z0, float step, int nx, int nz,
                                           List<string> written)
        {
            string rel = DumpFormat.WorldGridFile(WorldGenReader.SeedHex(d.seed), id, source);
            Func<WorldGenerator, float, float, bool, float> baseHeight = BaseHeight();

            IEnumerator job = w.WriteBinaryStreaming(rel, bw =>
                LatticeBody(bw, wg, baseHeight, d, x0, z0, step, nx, nz));
            while (job.MoveNext()) yield return job.Current;

            written.Add(rel);
            Plugin.Log.LogInfo("grid " + id + ": " + ((long)nx * nz) + " samples -> " + rel);
        }

        private static IEnumerator LatticeBody(BinaryWriter bw, WorldGenerator wg,
                                               Func<WorldGenerator, float, float, bool, float> baseHeight,
                                               WorldGenDumpFile d, float x0, float z0, float step, int nx, int nz)
        {
            WriteHeader(bw, d, x0, z0, step, nx, nz, DumpFormat.GridFlagBaseHeight);
            for (int j = 0; j < nz; j++)
            {
                float wy = z0 + j * step;
                for (int i = 0; i < nx; i++)
                {
                    float wx = x0 + i * step;
                    WriteSample(bw, wg, baseHeight, wx, wy, false);
                }
                // One yield per row: 2048 rows is ~2048 frames at worst, which is a few seconds of
                // hitching rather than one multi-minute freeze.
                if ((j & 7) == 7) yield return null;
            }
        }

        /// <summary>
        /// The discontinuities a uniform grid will not hit and where a port breaks: the biome radius
        /// bands (5000 / 6000 / 8000 / 10000), the base-height ramps at 10490 and 10500, the mountain
        /// distance +/-600, the Ashlands and DeepNorth rings, and the river centre lines.
        /// </summary>
        private static IEnumerable Edges(DumpWriter w, WorldGenerator wg, WorldGenDumpFile d, string source,
                                         List<string> written)
        {
            var pts = new List<Vector2>(8192);

            float[] radii =
            {
                5000f, 6000f, 8000f, 10000f, 10490f, 10500f,
                d.minMountainDistance - 600f, d.minMountainDistance, d.minMountainDistance + 600f,
            };
            for (int r = 0; r < radii.Length; r++)
            {
                for (int a = 0; a < 360; a++)
                {
                    double ang = a * Math.PI / 180.0;
                    pts.Add(new Vector2((float)(radii[r] * Math.Cos(ang)), (float)(radii[r] * Math.Sin(ang))));
                }
            }

            // IsAshlands: Length(x, y + ashlandsYOffset) > ashlandsMinDistance + WorldAngle(x,y)*100,
            // with ashlandsYOffset = -4000 and ashlandsMinDistance = 12000. IsDeepnorth is the same
            // ring with +4000. Sampled either side of the boundary rather than on it.
            for (int a = 0; a < 720; a++)
            {
                double ang = a * Math.PI / 360.0;
                for (int k = -1; k <= 1; k++)
                {
                    float r = 12000f + k * 8f;
                    pts.Add(new Vector2((float)(r * Math.Cos(ang)), (float)(r * Math.Sin(ang)) + 4000f));
                    pts.Add(new Vector2((float)(r * Math.Cos(ang)), (float)(r * Math.Sin(ang)) - 4000f));
                }
            }

            if (d.rivers != null)
            {
                foreach (RiverDef r in d.rivers)
                {
                    if (r == null || r.p0 == null || r.p1 == null) continue;
                    for (int s = 0; s <= 8; s++)
                    {
                        float t = s / 8f;
                        pts.Add(new Vector2(r.p0.x + (r.p1.x - r.p0.x) * t, r.p0.y + (r.p1.y - r.p0.y) * t));
                    }
                }
            }

            string rel = DumpFormat.WorldGridFile(WorldGenReader.SeedHex(d.seed), "edges", source);
            Func<WorldGenerator, float, float, bool, float> baseHeight = BaseHeight();

            IEnumerator job = w.WriteBinaryStreaming(rel, bw => EdgesBody(bw, wg, baseHeight, d, pts));
            while (job.MoveNext()) yield return job.Current;

            written.Add(rel);
            Plugin.Log.LogInfo("grid edges: " + pts.Count + " explicit points -> " + rel);
        }

        private static IEnumerator EdgesBody(BinaryWriter bw, WorldGenerator wg,
                                             Func<WorldGenerator, float, float, bool, float> baseHeight,
                                             WorldGenDumpFile d, List<Vector2> pts)
        {
            WriteHeader(bw, d, 0f, 0f, 0f, pts.Count, 1,
                        DumpFormat.GridFlagBaseHeight | DumpFormat.GridFlagExplicitPoints);
            for (int i = 0; i < pts.Count; i++)
            {
                WriteSample(bw, wg, baseHeight, pts[i].x, pts[i].y, true);
                if ((i & 1023) == 1023) yield return null;
            }
        }

        private static void WriteHeader(BinaryWriter bw, WorldGenDumpFile d, float x0, float z0, float step,
                                        int nx, int nz, int flags)
        {
            foreach (char c in DumpFormat.GridMagic) bw.Write((byte)c);
            bw.Write(DumpFormat.Schema);
            bw.Write(d.seed);
            bw.Write(d.worldGenVersion);
            bw.Write(x0);
            bw.Write(z0);
            bw.Write(step);
            bw.Write(nx);
            bw.Write(nz);
            bw.Write(flags);
        }

        private static void WriteSample(BinaryWriter bw, WorldGenerator wg,
                                        Func<WorldGenerator, float, float, bool, float> baseHeight,
                                        float wx, float wy, bool explicitPoint)
        {
            if (explicitPoint) { bw.Write(wx); bw.Write(wy); }

            // Defaults matter: Minimap.GenerateWorldMap samples GetBiome with oceanLevel 0.02 and
            // waterAlwaysOcean false, which is what makes this comparable with the minimap cache.
            Heightmap.Biome biome = wg.GetBiome(wx, wy);
            Color mask;
            float h = wg.GetBiomeHeight(biome, wx, wy, out mask);

            bw.Write((ushort)biome);
            bw.Write(h);
            bw.Write(mask.r);
            bw.Write(mask.g);
            bw.Write(mask.b);
            bw.Write(mask.a);
            bw.Write(baseHeight(wg, wx, wy, false));
        }
    }
}
