using System;
using System.Collections.Generic;
using System.Diagnostics;
using SeedLab.Locations;
using SeedLab.Seeds;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLab.LocationLab
{
    /// <summary>
    /// What can be proved about the engine WITHOUT the asset table: how much RNG every step consumes,
    /// that the documented <c>RandomBiomeFromBiomes</c> bugs are reproduced, that the run is
    /// deterministic, that a target prefix gives the same answer as a full run, and that the world-level
    /// invariants hold on a synthetic table.
    ///
    /// <para>A synthetic table cannot validate POSITIONS - those need the real
    /// <c>ZoneSystem.m_locations</c>. It validates the machinery that turns a table into positions.</para>
    /// </summary>
    public static class SyntheticCheck
    {
        private static int s_fail;

        private static void Check(bool ok, string what, string detail = "")
        {
            if (!ok) s_fail++;
            Console.WriteLine("  " + (ok ? "ok   " : "FAIL ") + what + (detail.Length > 0 ? "  [" + detail + "]" : ""));
        }

        /// <summary>Number of xorshift steps between two generator states, or -1 if more than <paramref name="max"/>.</summary>
        private static int DrawsBetween(UnityRandom before, UnityRandom after, int max = 128)
        {
            UnityRandom probe = new UnityRandom();
            probe.SetState(before.GetState());
            (int, int, int, int) target = after.GetState();
            for (int n = 0; n <= max; n++)
            {
                if (probe.GetState() == target) return n;
                probe.Next();
            }
            return -1;
        }

        private static UnityRandom At(int seed)
        {
            UnityRandom r = new UnityRandom();
            r.InitState(seed);
            return r;
        }

        private static UnityRandom Copy(UnityRandom r)
        {
            UnityRandom c = new UnityRandom();
            c.SetState(r.GetState());
            return c;
        }

        public static int Run(string[] args)
        {
            s_fail = 0;
            int seed = Args.Int(args, "--seed", WorldRef.Development.Seed);
            int workers = Args.Int(args, "--workers", -1);

            Console.WriteLine("RNG accounting (what each step costs the stream)");
            AccountingTests();

            Console.WriteLine();
            Console.WriteLine("RandomBiomeFromBiomes - the documented bugs");
            BiomeDrawTests();

            Console.WriteLine();
            Console.WriteLine("Building the world for seed " + seed + " ...");
            WorldLocations wl = WorldLocations.Build(seed, 2, null, workers);
            Console.WriteLine("  grid " + wl.GridMilliseconds.ToString("F0") + " ms, sectors "
                              + wl.SectorMilliseconds.ToString("F0") + " ms, "
                              + wl.Field.Sectors.Count.ToString("N0") + " sectors");

            Console.WriteLine();
            Console.WriteLine("Biome point draws against the grid");
            PointDrawTests(wl);

            Console.WriteLine();
            Console.WriteLine("Engine on a synthetic table");
            EngineTests(wl);

            Console.WriteLine();
            Console.WriteLine("Cost of a synthetic full-table run");
            CostRun(wl);

            Console.WriteLine();
            Console.WriteLine("verdict     " + (s_fail == 0 ? "PASS" : s_fail + " FAILURES"));
            return s_fail == 0 ? 0 : 1;
        }

        private static void AccountingTests()
        {
            // GetRandomPointInZone: exactly two float draws, whatever the radius does.
            foreach (float radius in new[] { 0f, 10f, 40f })
            {
                UnityRandom r = At(12345);
                UnityRandom b = Copy(r);
                ZoneMath.GetRandomPointInZone(r, new Vec2s(3, -4), radius);
                Check(DrawsBetween(b, r) == 2, "GetRandomPointInZone(radius " + radius + ") costs 2 draws",
                      DrawsBetween(b, r).ToString());
            }

            // Range(float,float) with min > max: the lerp spans the interval either way round, so the
            // offset lands in +/-(r - 32). That is INSIDE the zone until r > 64 - the spec's "> 32"
            // threshold for escaping the zone is wrong.
            {
                bool inBand = true, escaped32 = false, escaped64 = false;
                for (int i = 0; i < 20000; i++)
                {
                    (float x, float _, float z) = ZoneMath.GetRandomPointInZone(At(i), new Vec2s(0, 0), 40f);
                    if (Math.Abs(x) > 8.0001f || Math.Abs(z) > 8.0001f) inBand = false;
                    if (Math.Abs(x) > 32f || Math.Abs(z) > 32f) escaped32 = true;
                    (float x2, float _, float z2) = ZoneMath.GetRandomPointInZone(At(i), new Vec2s(0, 0), 70f);
                    if (Math.Abs(x2) > 32f || Math.Abs(z2) > 32f) escaped64 = true;
                }
                Check(inBand && !escaped32,
                      "radius 40 inverts the bounds but keeps the point inside +/-8 m of the zone centre");
                Check(escaped64, "radius 70 (> 64) really does put points outside the 64 m zone");
            }

            // GetRandomZone: num == 0 costs nothing; num > 0 costs 2 per do-while iteration.
            {
                UnityRandom r = At(99);
                UnityRandom b = Copy(r);
                Vec2s z = ZoneMath.GetRandomZone(r, 0f);
                Check(DrawsBetween(b, r) == 0 && z.x == 0 && z.y == 0,
                      "GetRandomZone(range < 64) costs 0 draws and returns (0,0)",
                      DrawsBetween(b, r) + " draws, zone " + z);
            }
            {
                UnityRandom r = At(99);
                UnityRandom b = Copy(r);
                ZoneMath.GetRandomZone(r, 6400f);                  // num = 100, always inside 10000 m
                int d = DrawsBetween(b, r);
                Check(d == 2, "GetRandomZone(6400) costs 2 draws (no rejection possible at num=100)", d.ToString());
            }
            {
                // num = 200 -> |zone| can exceed 156, so rejections happen and each costs 2 more.
                UnityRandom r = At(4242);
                UnityRandom b = Copy(r);
                ZoneMath.GetRandomZone(r, 12800f);
                int d = DrawsBetween(b, r, 512);
                Check(d > 0 && d % 2 == 0, "GetRandomZone(12800) costs a multiple of 2 draws", d.ToString());
            }

            // GetTerrainDelta: 10 insideUnitCircle, 2 draws each, unconditionally - even at radius 0.
            {
                WorldGeneratorPort gen = new WorldGeneratorPort(1, 2);
                foreach (float radius in new[] { 0f, 16f })
                {
                    UnityRandom r = At(5150);
                    UnityRandom b = Copy(r);
                    gen.GetTerrainDelta(r, 100f, 30f, 100f, radius, out float delta, out _);
                    int d = DrawsBetween(b, r, 64);
                    Check(d == 20, "GetTerrainDelta(radius " + radius + ") costs 20 draws", d + " draws, delta " + delta);
                    if (radius == 0f) Check(delta == 0f, "radius 0 collapses all 10 samples: delta == 0", delta.ToString());
                }
            }
        }

        private static void BiomeDrawTests()
        {
            UnityRandom r = At(1);
            UnityRandom b = Copy(r);
            BiomeField.RandomBiomeFromBiomes(r, Biome.Meadows);
            Check(DrawsBetween(b, r) == 0, "single-bit mask draws nothing");

            r = At(1); b = Copy(r);
            BiomeField.RandomBiomeFromBiomes(r, Biome.Meadows | Biome.Swamp);
            Check(DrawsBetween(b, r) == 1, "multi-bit mask draws exactly one int");

            // Two bits -> Range(0, 1) -> always 0 -> the first tested biome always wins.
            bool allMeadows = true;
            for (int i = 0; i < 64; i++)
                if (BiomeField.RandomBiomeFromBiomes(At(i), Biome.Meadows | Biome.BlackForest) != Biome.Meadows)
                    allMeadows = false;
            Check(allMeadows, "Meadows|BlackForest always returns Meadows (Range(0,1) can only be 0)");

            // Mistlands requested but unreachable.
            bool anyMist = false;
            for (int i = 0; i < 512; i++)
            {
                Biome got = BiomeField.RandomBiomeFromBiomes(At(i), Biome.Swamp | Biome.Mountain | Biome.Mistlands);
                if (got == Biome.Mistlands) anyMist = true;
            }
            Check(!anyMist, "Swamp|Mountain|Mistlands never returns Mistlands (the last slot is unreachable)");

            // The Plains slot returns BlackForest.
            bool sawBf = false, sawOcean = false;
            for (int i = 0; i < 4096; i++)
            {
                Biome got = BiomeField.RandomBiomeFromBiomes(At(i), Biome.Mountain | Biome.Plains | Biome.Ocean | Biome.Mistlands);
                if (got == Biome.BlackForest) sawBf = true;
                if (got == Biome.Ocean) sawOcean = true;
            }
            Check(sawBf, "the Plains slot returns BlackForest, a biome that was never requested");
            Check(!sawOcean, "the Ocean bit is never tested, so Ocean cannot come back without Meadows");

            bool sawOcean2 = false;
            for (int i = 0; i < 4096; i++)
                if (BiomeField.RandomBiomeFromBiomes(At(i), Biome.Meadows | Biome.Ocean | Biome.Mistlands) == Biome.Ocean)
                    sawOcean2 = true;
            Check(sawOcean2, "Meadows|Ocean|Mistlands CAN return Ocean - the slot tests the Meadows bit");
        }

        private static void PointDrawTests(WorldLocations wl)
        {
            UnityRandom r = At(31337);
            UnityRandom b = Copy(r);
            int p = wl.Field.GetRandomPointByBiomes(r, Biome.Meadows);
            Check(DrawsBetween(b, r) == 1, "GetRandomPointByBiomes(single bit) costs 1 draw");
            Check(wl.Grid.PointBiomes[p] == (byte)BiomeIndex.Meadows,
                  "the drawn point really is Meadows in the grid",
                  ((BiomeIndex)wl.Grid.PointBiomes[p]).ToString());

            r = At(31337); b = Copy(r);
            wl.Field.GetRandomPointByBiomes(r, Biome.Meadows | Biome.Swamp);
            Check(DrawsBetween(b, r) == 2, "GetRandomPointByBiomes(multi bit) costs 2 draws");

            r = At(31337); b = Copy(r);
            int q = wl.Field.GetRandomPointByBiomesAboveSeaLevel(r, Biome.Mountain);
            Check(DrawsBetween(b, r) == 1, "GetRandomPointByBiomesAboveSeaLevel costs 1 draw");
            Check(wl.Grid.PointHeights[q] >= 30f, "...and the point is above sea level (>= 30 m raw height)",
                  wl.Grid.PointHeights[q].ToString("F2"));

            // Every listed point of a biome really has that biome, and the above-sea list is a subset.
            foreach (BiomeTypeInfo t in wl.Field.BiomesInKeyOrder)
            {
                if (t.AllPoints.Count == 0) continue;
                byte want = (byte)t.Biome.ToBiomeIndex();
                bool clean = true;
                for (int i = 0; i < t.AllPoints.Count; i += 997)
                    if (wl.Grid.PointBiomes[t.AllPoints[i]] != want) clean = false;
                bool aboveOk = true;
                for (int i = 0; i < t.AllPointsAboveSeaLevel.Count; i += 997)
                    if (wl.Grid.PointHeights[t.AllPointsAboveSeaLevel[i]] < 30f) aboveOk = false;
                Check(clean && aboveOk, t.Biome + " point lists are internally consistent",
                      t.AllPoints.Count.ToString("N0") + " / " + t.AllPointsAboveSeaLevel.Count.ToString("N0"));
            }
        }

        /// <summary>
        /// A synthetic table. Every field here is INVENTED - it exists to exercise the machinery, not to
        /// predict anything. The real values come from the dumper.
        /// </summary>
        private static LocationTable SyntheticTable()
        {
            List<ZoneLocationEntry> e = new List<ZoneLocationEntry>();

            e.Add(Make("SyntheticAltar", Biome.Meadows, 1, prioritized: true, centerFirst: true,
                       minDistance: 500f, unique: true));
            e.Add(Make("SyntheticTemple", Biome.Meadows, 1, prioritized: true, centerFirst: true,
                       minDistance: 0f, unique: true));
            e.Add(Make("SyntheticTraderCandidate", Biome.BlackForest, 4, unique: true,
                       minDistanceFromSimilar: 512f, group: "Trader"));
            e.Add(Make("SyntheticCrypt", Biome.BlackForest, 200, minDistanceFromSimilar: 128f, group: "Crypt",
                       exteriorRadius: 12f, maxTerrainDelta: 5f));
            e.Add(Make("SyntheticCamp", Biome.Plains, 120, exteriorRadius: 20f, maxTerrainDelta: 4f));
            e.Add(Make("SyntheticMountainCave", Biome.Mountain, 60, minAltitude: 100f, maxAltitude: 1000f,
                       exteriorRadius: 10f, maxTerrainDelta: 8f));
            e.Add(Make("SyntheticSwampHut", Biome.Swamp, 50, maxTerrainDelta: 3f));
            e.Add(Make("SyntheticMulti", Biome.Meadows | Biome.BlackForest | Biome.Plains, 80));
            e.Add(Make("SyntheticEdge", Biome.Meadows, 30, biomeArea: BiomeArea.Edge));
            e.Add(Make("SyntheticDisabled", Biome.Meadows, 10, enable: false));
            e.Add(Make("SyntheticZeroQuantity", Biome.Meadows, 0));
            e.Add(Make("SyntheticBigRadius", Biome.Meadows, 20, exteriorRadius: 70f, maxTerrainDelta: 100f));
            e.Add(Make("SyntheticSurround", Biome.Mistlands, 25, surroundCheck: true));
            return LocationTable.FromEntries(e);
        }

        private static ZoneLocationEntry Make(string prefab, Biome biome, int quantity,
                                              bool prioritized = false, bool centerFirst = false,
                                              bool unique = false, bool enable = true,
                                              float minDistance = 0f, float minDistanceFromSimilar = 0f,
                                              string group = "", float exteriorRadius = 0f,
                                              float maxTerrainDelta = 2f, float minAltitude = -1000f,
                                              float maxAltitude = 1000f, BiomeArea biomeArea = BiomeArea.Everything,
                                              bool surroundCheck = false)
        {
            return new ZoneLocationEntry
            {
                Index = 0,
                Name = prefab,
                PrefabName = prefab,
                NameHash = StableHash.Compute(prefab),
                AssetId = AssetId.FromPrefabNameFallback(prefab),
                Enable = enable,
                Biome = biome,
                BiomeArea = biomeArea,
                Quantity = quantity,
                Prioritized = prioritized,
                CenterFirst = centerFirst,
                Unique = unique,
                MinDistance = minDistance,
                MinDistanceFromSimilar = minDistanceFromSimilar,
                Group = group,
                ExteriorRadius = exteriorRadius,
                MaxTerrainDelta = maxTerrainDelta,
                MinAltitude = minAltitude,
                MaxAltitude = maxAltitude,
                SurroundCheckVegetation = surroundCheck,
            };
        }

        private static void EngineTests(WorldLocations wl)
        {
            LocationTable table = SyntheticTable();
            Check(table.Ordered.Count == 11, "disabled and quantity-0 entries are dropped from the ordered list",
                  table.All.Count + " -> " + table.Ordered.Count);
            Check(table.Ordered[0].PrefabName == "SyntheticAltar" && table.Ordered[1].PrefabName == "SyntheticTemple",
                  "OrderByDescending(prioritized) is a stable partition: prioritized first, order kept");

            PlacementResult a = wl.PlaceAll(table);
            PlacementResult b = wl.PlaceAll(table);

            Check(a.Instances.Count == b.Instances.Count, "run is deterministic (instance count)",
                  a.Instances.Count + " vs " + b.Instances.Count);
            bool identical = a.Instances.Count == b.Instances.Count;
            for (int i = 0; identical && i < a.Instances.Count; i++)
                identical = a.Instances[i].PrefabHash == b.Instances[i].PrefabHash
                            && BitConverter.SingleToInt32Bits(a.Instances[i].X) == BitConverter.SingleToInt32Bits(b.Instances[i].X)
                            && BitConverter.SingleToInt32Bits(a.Instances[i].Y) == BitConverter.SingleToInt32Bits(b.Instances[i].Y)
                            && BitConverter.SingleToInt32Bits(a.Instances[i].Z) == BitConverter.SingleToInt32Bits(b.Instances[i].Z);
            Check(identical, "run is deterministic (every instance bit for bit)");

            Check(a.ByZone.Count == a.Instances.Count, "one location per zone, globally");

            // Every accepted point satisfies the filters that do not depend on the stream.
            int biomeBad = 0, altBad = 0, distBad = 0;
            foreach (LocationInstanceResult inst in a.Instances)
            {
                Biome got = wl.Generator.GetBiome(inst.X, inst.Z);
                if (((int)inst.Location.Biome & (int)got) == 0) biomeBad++;
                float alt = (float)((double)inst.Y - 30.0);
                if (alt < inst.Location.MinAltitude || alt > inst.Location.MaxAltitude) altBad++;
                float mag = ZoneMath.Magnitude3(inst.X, 0f, inst.Z);
                if (inst.Location.MinDistance != 0f && mag < inst.Location.MinDistance) distBad++;
            }
            Check(biomeBad == 0, "every placed instance satisfies its biome mask", biomeBad + " bad");
            Check(altBad == 0, "every placed instance satisfies its altitude window", altBad + " bad");
            Check(distBad == 0, "every placed instance satisfies m_minDistance", distBad + " bad");

            // The height stored on the instance is GetHeight at its own XZ - the .db2's value.
            int yBad = 0;
            foreach (LocationInstanceResult inst in a.Instances)
                if (BitConverter.SingleToInt32Bits(inst.Y) != BitConverter.SingleToInt32Bits(wl.Generator.GetHeight(inst.X, inst.Z)))
                    yBad++;
            Check(yBad == 0, "instance Y is exactly WorldGenerator.GetHeight(x, z)", yBad + " bad");

            // Target prefix: everything up to and including the target is bit-identical to the full run.
            string target = "SyntheticCrypt";
            int prefix = table.PrefixLengthForTarget(target);
            PlacementResult p = wl.PlaceForTarget(table, target);
            Check(p.IsPartial && p.OrderedCountRun == prefix, "PlaceForTarget runs exactly the prefix",
                  prefix + " of " + table.Ordered.Count);
            List<LocationInstanceResult> fullPrefix = new List<LocationInstanceResult>();
            foreach (LocationInstanceResult i in a.Instances) if (i.OrderedIndex < prefix) fullPrefix.Add(i);
            bool same = fullPrefix.Count == p.Instances.Count;
            for (int i = 0; same && i < fullPrefix.Count; i++)
                same = BitConverter.SingleToInt32Bits(fullPrefix[i].X) == BitConverter.SingleToInt32Bits(p.Instances[i].X)
                       && BitConverter.SingleToInt32Bits(fullPrefix[i].Z) == BitConverter.SingleToInt32Bits(p.Instances[i].Z)
                       && fullPrefix[i].PrefabHash == p.Instances[i].PrefabHash;
            Check(same, "the prefix run reproduces the full run's prefix exactly",
                  fullPrefix.Count + " vs " + p.Instances.Count);

            // Unique entries are reported as candidate sets, never as a single answer.
            bool uniqueReported = false;
            foreach (UniqueCandidateSet u in a.UniqueCandidates)
                if (u.Location.PrefabName == "SyntheticTraderCandidate")
                {
                    uniqueReported = true;
                    Check(!u.WinnerIsPredictable || u.Candidates.Count == 1,
                          "a unique entry with several candidates is flagged unpredictable",
                          u.Candidates.Count + " candidates");
                }
            Check(uniqueReported, "unique entries appear in PlacementResult.UniqueCandidates");

            // The escape-the-zone path is exercised by the big-radius entry.
            foreach (LocationTypeResult t in a.Types)
                if (t.Location.PrefabName == "SyntheticBigRadius")
                    Check(t.Location.CanEscapeZone && t.PointsOutsideCandidateZone > 0,
                          "an entry with MaxRadius > 64 really registers in a zone the filters never vetted",
                          t.PointsOutsideCandidateZone + " of its points landed outside the candidate zone, "
                          + t.RegisterCollisions + " registrations collided while placed++ still counted them");

            // The surround check throws away its first nine qualifying points, always.
            foreach (LocationTypeResult t in a.Types)
                if (t.Location.PrefabName == "SyntheticSurround")
                    Check(t[RejectReason.SurroundBaseline] <= 9,
                          "the surround check rejects at most 9 points for the baseline",
                          t[RejectReason.SurroundBaseline] + " baseline rejections, "
                          + t[RejectReason.SurroundBelowCutoff] + " below cutoff");

            Console.WriteLine("  synthetic run: " + a.Instances.Count + " instances over "
                              + a.OrderedCountRun + " types in " + a.Milliseconds.ToString("F0") + " ms");
            foreach (LocationTypeResult t in a.Types)
                Console.WriteLine("    " + t.Location.PrefabName.PadRight(26) + t.Placed + "/" + t.Quantity
                                  + "  attempts " + t.Attempts.ToString().PadLeft(6)
                                  + "  tries " + t.Iterations.ToString().PadLeft(7)
                                  + "  " + t.Milliseconds.ToString("F0").PadLeft(6) + " ms"
                                  + (t.Shortfall ? "  SHORTFALL" : ""));
        }

        private static void CostRun(WorldLocations wl)
        {
            // A synthetic workload whose SHAPE (183 entries, the quantities the game's own log reports
            // for the ones we know) is realistic even though every other field is invented.
            List<GroundTruthPaths.NameRow> names = GroundTruthPaths.ReadNames();
            List<ZoneLocationEntry> e = new List<ZoneLocationEntry>();
            Biome[] cycle = { Biome.Meadows, Biome.BlackForest, Biome.Swamp, Biome.Mountain, Biome.Plains, Biome.Mistlands };
            int n = 0;
            foreach (GroundTruthPaths.NameRow row in names)
            {
                e.Add(Make(row.Name, cycle[n % cycle.Length], row.QuantityInLog ?? 20,
                           exteriorRadius: 8f, maxTerrainDelta: 4f));
                n++;
            }
            for (int i = names.Count; i < 183; i++)
                e.Add(Make("SyntheticFiller" + i, cycle[i % cycle.Length], 20, exteriorRadius: 8f, maxTerrainDelta: 4f));

            LocationTable t = LocationTable.FromEntries(e);
            Stopwatch sw = Stopwatch.StartNew();
            PlacementResult r = wl.PlaceAll(t);
            sw.Stop();
            Console.WriteLine("  " + t.Ordered.Count + " synthetic entries, " + r.Instances.Count.ToString("N0")
                              + " instances, " + sw.Elapsed.TotalMilliseconds.ToString("F0") + " ms");
            Console.WriteLine("  per seed, this shape of workload: grid " + wl.GridMilliseconds.ToString("F0")
                              + " ms + sectors " + wl.SectorMilliseconds.ToString("F0") + " ms + placement "
                              + sw.Elapsed.TotalMilliseconds.ToString("F0") + " ms = "
                              + (wl.TotalMilliseconds + sw.Elapsed.TotalMilliseconds).ToString("F0") + " ms");
            Console.WriteLine("  NOTE: the quantities are the game's, every other field is invented. Treat this "
                              + "as an order of magnitude for tier design, not as the real cost.");
        }
    }
}
