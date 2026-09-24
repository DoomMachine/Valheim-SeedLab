using System;
using System.Collections.Generic;
using System.Globalization;
using HarmonyLib;
using SeedLab.Contracts.Dump;
using UnityEngine;
using URandom = UnityEngine.Random;

namespace SeedLab.Dumper
{
    /// <summary>
    /// Reads one <c>WorldGenerator</c>'s private state. Pure reflection over an instance that already
    /// exists: it never constructs a generator and never touches <c>WorldGenerator.instance</c>.
    ///
    /// The fields are private instance fields declared at WorldGenerator.cs:60-80. They are read with
    /// <c>AccessTools.Field(...).GetValue(instance)</c> rather than <c>FieldRef</c>, which keeps the
    /// pattern uniform and works under every compiler this project has had to fall back to.
    /// </summary>
    internal static class WorldGenReader
    {
        public static string SeedHex(int seed)
        {
            return unchecked((uint)seed).ToString("X8", CultureInfo.InvariantCulture);
        }

        public static WorldGenDumpFile Read(WorldGenerator wg, World world, string stamp)
        {
            var d = new WorldGenDumpFile
            {
                stamp = stamp,
                schema = DumpFormat.Schema,
                worldName = world != null ? world.m_name : null,
                seedText = world != null ? world.m_seedName : null,
                seed = world != null ? world.m_seed : 0,
                worldGenVersion = world != null ? world.m_worldGenVersion : 0,
                menu = world != null && world.m_menu,
            };

            d.version = GetInt(wg, "m_version");
            d.offset0 = GetFloat(wg, "m_offset0");
            d.offset1 = GetFloat(wg, "m_offset1");
            d.offset2 = GetFloat(wg, "m_offset2");
            d.offset3 = GetFloat(wg, "m_offset3");
            d.offset4 = GetFloat(wg, "m_offset4");
            d.riverSeed = GetInt(wg, "m_riverSeed");
            d.streamSeed = GetInt(wg, "m_streamSeed");

            d.minMountainDistance = GetFloat(wg, "m_minMountainDistance");
            d.minDarklandNoise = GetFloat(wg, "minDarklandNoise");
            d.maxMarshDistance = GetFloat(wg, "maxMarshDistance");

            // The cellular FastNoise is created once per process and then unconditionally SetSeed(0)
            // at WorldGenerator.cs:222, so the world seed never reaches it. Dumped as a tripwire:
            // anything but 0 means this build no longer matches the port's assumption.
            d.noiseGenSeed = ReadNoiseGenSeed();

            d.constructorTrace = ReplayConstructorDraws(d.seed);

            d.lakes = ReadVec2List(wg, "m_lakes");
            d.rivers = ReadRiverList(wg, "m_rivers");
            d.streams = ReadRiverList(wg, "m_streams");

            return d;
        }

        /// <summary>
        /// Replays the seven <c>WorldGenerator..ctor</c> draws on a scratch stream inside a guard, in
        /// the constructor's exact order: <c>m_offset0..3</c> as <c>Range(-10000, 10000)</c>, then
        /// <c>m_riverSeed</c> and <c>m_streamSeed</c> as <c>Range(int.MinValue, int.MaxValue)</c>, then
        /// <c>m_offset4</c> LAST (WorldGenerator.cs:223-229).
        ///
        /// This is not decoration. It is an independent check that the reflected offsets really are
        /// those draws and that nothing perturbed the constructor - and <c>m_offset4</c> being seventh
        /// rather than fifth is precisely the kind of error that survives a casual look, because
        /// getting it wrong misplaces the Mistlands and nothing else.
        /// </summary>
        public static RandomTraceDef ReplayConstructorDraws(int seed)
        {
            var draws = new List<RandomDrawDef>(7);
            int[] afterInit;
            int[] end;

            using (RandomGuard.Seeded(seed, "natives: constructor replay"))
            {
                afterInit = RandomStateUtil.Current();
                for (int i = 0; i < 4; i++)
                {
                    draws.Add(IntDraw("Range(-10000,10000)", URandom.Range(-10000, 10000)));
                }
                draws.Add(IntDraw("Range(int.MinValue,int.MaxValue)", URandom.Range(int.MinValue, int.MaxValue)));
                draws.Add(IntDraw("Range(int.MinValue,int.MaxValue)", URandom.Range(int.MinValue, int.MaxValue)));
                draws.Add(IntDraw("Range(-10000,10000)", URandom.Range(-10000, 10000)));
                end = RandomStateUtil.Current();
            }

            return new RandomTraceDef
            {
                id = "worldgen-ctor",
                note = "offset0, offset1, offset2, offset3, riverSeed, streamSeed, offset4 - offset4 is LAST",
                initState = seed,
                stateAfterInit = afterInit,
                draws = draws.ToArray(),
                stateAtEnd = end,
            };
        }

        private static RandomDrawDef IntDraw(string call, int value)
        {
            return new RandomDrawDef
            {
                call = call,
                kind = "int",
                resultInt = value,
                stateAfter = RandomStateUtil.Current(),
            };
        }

        /// <summary>
        /// Writes <c>m_riverPoints</c> as a raw little-endian sidecar. It is a
        /// <c>Dictionary&lt;Vector2i, RiverPoint[]&gt;</c> on a 64 m grid and it is large; more to the
        /// point, it is where the DeepNorth streams live, because <c>Pregenerate</c> discards the
        /// return value of <c>PlaceStreams(isDN: true)</c> and keeps only its side effect here.
        /// </summary>
        public static void WriteRiverPoints(DumpWriter w, WorldGenerator wg, WorldGenDumpFile d, string relPath)
        {
            var dict = Get(wg, "m_riverPoints") as Dictionary<Vector2i, WorldGenerator.RiverPoint[]>;
            if (dict == null || dict.Count == 0)
            {
                d.riverPointCellCount = 0;
                d.riverPointTotal = 0;
                d.riverPointsFile = null;
                return;
            }

            int total = 0;
            foreach (var kv in dict) total += kv.Value != null ? kv.Value.Length : 0;
            d.riverPointCellCount = dict.Count;
            d.riverPointTotal = total;
            d.riverPointsFile = relPath;

            w.WriteBinary(relPath, bw =>
            {
                foreach (char c in DumpFormat.RiverPointsMagic) bw.Write((byte)c);
                bw.Write(DumpFormat.Schema);
                bw.Write(d.seed);
                bw.Write(64f);                 // WorldGenerator.riverGridSize
                bw.Write(dict.Count);
                foreach (var kv in dict)
                {
                    WorldGenerator.RiverPoint[] pts = kv.Value ?? new WorldGenerator.RiverPoint[0];
                    bw.Write(kv.Key.x);
                    bw.Write(kv.Key.y);
                    bw.Write(pts.Length);
                    for (int i = 0; i < pts.Length; i++)
                    {
                        bw.Write(pts[i].p.x);
                        bw.Write(pts[i].p.y);
                        bw.Write(pts[i].w);
                        bw.Write(pts[i].w2);
                    }
                }
            });
        }

        // ---- reflection helpers ----------------------------------------------------------------

        private static object Get(WorldGenerator wg, string field)
        {
            var f = AccessTools.Field(typeof(WorldGenerator), field);
            if (f == null)
            {
                throw new MissingFieldException("WorldGenerator." + field + " no longer exists; the " +
                                                "dump would be silently wrong. Re-run preflight.ps1.");
            }
            return f.GetValue(wg);
        }

        private static float GetFloat(WorldGenerator wg, string field)
        {
            return Convert.ToSingle(Get(wg, field), CultureInfo.InvariantCulture);
        }

        private static int GetInt(WorldGenerator wg, string field)
        {
            return Convert.ToInt32(Get(wg, field), CultureInfo.InvariantCulture);
        }

        private static int ReadNoiseGenSeed()
        {
            try
            {
                var f = AccessTools.Field(typeof(WorldGenerator), "m_noiseGen");
                if (f == null) return int.MinValue;
                var noise = f.GetValue(null) as FastNoise;
                return noise != null ? noise.GetSeed() : int.MinValue;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("m_noiseGen unreadable: " + e.Message);
                return int.MinValue;
            }
        }

        private static Vec2Def[] ReadVec2List(WorldGenerator wg, string field)
        {
            var list = Get(wg, field) as List<Vector2>;
            if (list == null) return new Vec2Def[0];
            var result = new Vec2Def[list.Count];
            for (int i = 0; i < list.Count; i++) result[i] = Snapshot.Vec2(list[i]);
            return result;
        }

        private static RiverDef[] ReadRiverList(WorldGenerator wg, string field)
        {
            var list = Get(wg, field) as List<WorldGenerator.River>;
            if (list == null) return new RiverDef[0];
            var result = new RiverDef[list.Count];
            for (int i = 0; i < list.Count; i++)
            {
                WorldGenerator.River r = list[i];
                result[i] = new RiverDef
                {
                    p0 = Snapshot.Vec2(r.p0),
                    p1 = Snapshot.Vec2(r.p1),
                    center = Snapshot.Vec2(r.center),
                    widthMin = r.widthMin,
                    widthMax = r.widthMax,
                    curveWidth = r.curveWidth,
                    curveWavelength = r.curveWavelength,
                };
            }
            return result;
        }
    }
}
