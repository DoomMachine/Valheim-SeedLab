using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using SeedLab.Contracts.Dump;
using SeedLab.Locations;
using SeedLab.Saves;
using SeedLab.WorldGen;

namespace SeedLab.LocationLab
{
    /// <summary>
    /// THE reconstruction harness. Given a dumped location table, it regenerates a world's location
    /// instances from the seed alone and compares them, instance by instance, with what the game wrote
    /// into that world's <c>.db2</c>.
    ///
    /// <para><b>Why the .db2 is an exact oracle, not a fuzzy one.</b> <c>ZoneSystem.PlaceLocations</c>
    /// modifies a LOCAL copy of the position when a zone is finally generated; the stored
    /// <c>m_position</c> is never updated. So the saved (x, y, z) is exactly the triple that
    /// <c>GenerateLocationsTimeSliced</c> produced, and y doubles as a check on the height port.</para>
    ///
    /// <para><b>Where disagreement is expected.</b> Both ground-truth worlds have been PLAYED. Any zone
    /// in <c>m_generatedZones</c> was excluded from placement on a re-run, and any <c>m_unique</c> type
    /// whose winner was found has had its other candidates deleted by
    /// <c>RemoveUnplacedLocations</c>. The report separates those from real mismatches.</para>
    /// </summary>
    public static class Reconstruct
    {
        public static int Run(string[] args)
        {
            string tablePath = Args.Str(args, "--table", "");
            if (tablePath.Length == 0)
            {
                Console.WriteLine("reconstruct needs --table <locations.json> from the dumper.");
                Console.WriteLine("Run 'requirements' to see exactly what that file has to contain.");
                return 2;
            }
            if (!File.Exists(tablePath))
            {
                Console.WriteLine("No such file: " + tablePath);
                return 2;
            }

            JsonSerializerOptions jo = new JsonSerializerOptions
            {
                IncludeFields = true,
                PropertyNameCaseInsensitive = true,
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };

            LocationTableFile? file = JsonSerializer.Deserialize<LocationTableFile>(File.ReadAllText(tablePath), jo);
            if (file?.locations == null || file.locations.Length == 0)
            {
                Console.WriteLine(tablePath + " has no locations[].");
                return 2;
            }
            LocationTable table = LocationTable.FromDump(file.locations);
            Console.WriteLine("table        " + table.All.Count + " entries, " + table.Ordered.Count
                              + " in the ordered run list (dump says enabledCount=" + file.enabledCount + ")");
            if (file.enabledCount != 0 && file.enabledCount != table.Ordered.Count)
                Console.WriteLine("  WARNING: the dump's enabledCount disagrees with this port's ordering.");
            foreach (string n in table.Notes) Console.WriteLine("  note: " + n);

            List<AltBiomeRuntime>? alts = null;
            string altPath = Args.Str(args, "--altbiomes", "");
            if (altPath.Length > 0 && File.Exists(altPath))
            {
                AltBiomeTableFile? af = JsonSerializer.Deserialize<AltBiomeTableFile>(File.ReadAllText(altPath), jo);
                if (af?.altBiomes != null)
                {
                    alts = new List<AltBiomeRuntime>(af.altBiomes.Length);
                    foreach (AltBiomeDef d in af.altBiomes) alts.Add(AltBiomeRuntime.FromDump(d));
                    Console.WriteLine("alt-biomes   " + alts.Count + " entries");
                }
            }
            else if (altPath.Length > 0) Console.WriteLine("alt-biomes   " + altPath + " not found - skipping");
            else Console.WriteLine("alt-biomes   not supplied (--altbiomes); filters 10a/10b will see empty sectors");

            string only = Args.Str(args, "--world", "");
            int workers = Args.Int(args, "--workers", -1);
            int bad = 0;
            foreach (WorldRef w in WorldRef.All)
            {
                if (only.Length > 0 && !string.Equals(only, w.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (!One(w, table, alts, workers)) bad++;
            }
            return bad == 0 ? 0 : 1;
        }

        private static bool One(WorldRef w, LocationTable table, List<AltBiomeRuntime>? alts, int workers)
        {
            Console.WriteLine();
            Console.WriteLine("== " + w.Name + "  seed " + w.Seed + (w.IsHoldOut ? "  (HOLD-OUT)" : ""));

            WorldSave save = WorldSaveReader.OpenWorldDirectory(GroundTruthPaths.WorldDirectory(w),
                                                                new WorldSaveOptions { ReadDb = true });
            if (save.Db == null) { Console.WriteLine("  no .db2"); return false; }
            if (save.Meta.Seed != w.Seed)
                Console.WriteLine("  WARNING: .fwl2 seed " + save.Meta.Seed + " != expected " + w.Seed);

            ZoneSystemData zs = save.Db.ZoneSystem;
            Console.WriteLine("  save         " + zs.Locations.Count.ToString("N0") + " instances, "
                              + zs.GeneratedZones.Count.ToString("N0") + " generated zones, locationVersion "
                              + zs.LocationVersion + ", locationsGenerated " + zs.LocationsGenerated);

            WorldLocations wl = WorldLocations.Build(w.Seed, 2, alts, workers);
            PlacementResult res = wl.PlaceAll(table);
            Console.WriteLine("  engine       " + res.Instances.Count.ToString("N0") + " instances in "
                              + res.Milliseconds.ToString("F0") + " ms (world build "
                              + wl.TotalMilliseconds.ToString("F0") + " ms)");
            foreach (string wn in res.Warnings) Console.WriteLine("  warning: " + wn);

            // ---- group both sides by prefab hash --------------------------------------------------
            Dictionary<int, List<SeedLab.Saves.LocationInstance>> game = new Dictionary<int, List<SeedLab.Saves.LocationInstance>>();
            foreach (SeedLab.Saves.LocationInstance i in zs.Locations)
            {
                if (!game.TryGetValue(i.PrefabHash, out List<SeedLab.Saves.LocationInstance>? l))
                { l = new List<SeedLab.Saves.LocationInstance>(); game[i.PrefabHash] = l; }
                l.Add(i);
            }

            Dictionary<int, List<LocationInstanceResult>> ours = new Dictionary<int, List<LocationInstanceResult>>();
            foreach (LocationInstanceResult i in res.Instances)
            {
                if (!ours.TryGetValue(i.PrefabHash, out List<LocationInstanceResult>? l))
                { l = new List<LocationInstanceResult>(); ours[i.PrefabHash] = l; }
                l.Add(i);
            }

            Dictionary<int, string> nameOf = new Dictionary<int, string>();
            foreach (ZoneLocationEntry e in table.All) nameOf[e.NameHash] = e.PrefabName;
            foreach (GroundTruthPaths.NameRow r in GroundTruthPaths.ReadNames()) if (!nameOf.ContainsKey(r.Hash)) nameOf[r.Hash] = r.Name;

            HashSet<int> hashes = new HashSet<int>(game.Keys);
            foreach (int h in ours.Keys) hashes.Add(h);
            List<int> sorted = new List<int>(hashes);
            sorted.Sort((x, y) => string.CompareOrdinal(Name(nameOf, x), Name(nameOf, y)));

            int exactTotal = 0, gameTotal = 0, oursTotal = 0, perfectPrefabs = 0, imperfectPrefabs = 0;
            Console.WriteLine("  per prefab (exact = x, y and z bit-identical)");
            foreach (int h in sorted)
            {
                game.TryGetValue(h, out List<SeedLab.Saves.LocationInstance>? g);
                ours.TryGetValue(h, out List<LocationInstanceResult>? o);
                int gn = g?.Count ?? 0, on = o?.Count ?? 0;
                gameTotal += gn; oursTotal += on;

                int exact = 0;
                if (g != null && o != null)
                {
                    HashSet<(int, int, int)> ob = new HashSet<(int, int, int)>();
                    foreach (LocationInstanceResult i in o)
                        ob.Add((BitConverter.SingleToInt32Bits(i.X), BitConverter.SingleToInt32Bits(i.Y),
                                BitConverter.SingleToInt32Bits(i.Z)));
                    foreach (SeedLab.Saves.LocationInstance i in g)
                        if (ob.Contains((BitConverter.SingleToInt32Bits(i.X), BitConverter.SingleToInt32Bits(i.Y),
                                         BitConverter.SingleToInt32Bits(i.Z)))) exact++;
                }
                exactTotal += exact;
                bool perfect = exact == gn && gn == on;
                if (perfect) perfectPrefabs++; else imperfectPrefabs++;

                Console.WriteLine("    " + (perfect ? "ok   " : "DIFF ") + Name(nameOf, h).PadRight(30)
                                  + " game " + gn.ToString().PadLeft(5)
                                  + "  engine " + on.ToString().PadLeft(5)
                                  + "  exact " + exact.ToString().PadLeft(5));
            }

            Console.WriteLine("  totals       game " + gameTotal.ToString("N0") + ", engine "
                              + oursTotal.ToString("N0") + ", exact position matches " + exactTotal.ToString("N0")
                              + "  (" + (gameTotal == 0 ? 0 : 100.0 * exactTotal / gameTotal).ToString("F3") + " % of the save)");
            Console.WriteLine("  prefabs      " + perfectPrefabs + " reproduced exactly, " + imperfectPrefabs + " not");
            Console.WriteLine("  expected disagreement: " + zs.GeneratedZones.Count.ToString("N0")
                              + " generated zones and every m_unique type whose losers were deleted.");
            return imperfectPrefabs == 0;
        }

        private static string Name(Dictionary<int, string> map, int hash)
            => map.TryGetValue(hash, out string? n) ? n : "hash:" + hash;
    }
}
