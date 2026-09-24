using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using SeedLab.Contracts.Dump;
using SeedLab.Data;
using SeedLab.Locations;
using SeedLab.Saves;
using SeedLab.WorldGen;

namespace SeedLab.LocationLab
{
    /// <summary>
    /// The two PLAYED worlds, against their <c>.db2</c> and - for <c>testworldclaude</c> - against the
    /// game's own worldgen log.
    ///
    /// <para><b>What "played" does and does not change.</b> <c>GenerateLocationsTimeSliced</c> runs once,
    /// at world creation, before anything is explored (the log proves it: "missing
    /// /worlds/testworldclaude/_main.0.db2" immediately precedes "Loading: Generating locations"). It is
    /// never re-run while <c>LocationsGenerated</c> is true and <c>m_locationVersion</c> is unchanged. So
    /// the save's instance list is the ORIGINAL output minus whatever was deleted afterwards, and the
    /// engine's fresh run should be a SUPERSET of it. <c>m_generatedZones</c> is therefore irrelevant to
    /// a reproduction of these two worlds - it would only matter if genloc ran again.</para>
    ///
    /// <para>The one deletion is <c>ZoneSystem.RemoveUnplacedLocations</c>, which a
    /// <c>m_unique</c> location's <c>Location.Awake</c> triggers when its zone is finally generated: every
    /// other candidate of that prefab is dropped. That is exploration order, not seed - see
    /// <see cref="PlacementResult.NotPredictable"/>.</para>
    /// </summary>
    public static class PlayedCheck
    {
        public static int Run(string[] args)
        {
            GameData data = GameData.Load();
            LocationTable table = LocationTable.FromDump(data.Locations);
            List<AltBiomeRuntime> alts = new List<AltBiomeRuntime>();
            foreach (AltBiomeDef d in data.AltBiomes) alts.Add(AltBiomeRuntime.FromDump(d));

            s_verbose = Args.Has(args, "--verbose");
            string only = Args.Str(args, "--world", "");
            int workers = Args.Int(args, "--workers", -1);

            Console.WriteLine("table        " + table.All.Count + " entries, " + table.Ordered.Count
                              + " ordered; alt-biomes " + alts.Count);

            int rc = 0;
            foreach (WorldRef w in WorldRef.All)
            {
                if (only.Length > 0 && !string.Equals(only, w.Name, StringComparison.OrdinalIgnoreCase)) continue;
                rc |= One(w, table, alts, workers);
            }
            Console.WriteLine();
            Console.WriteLine(rc == 0 ? "VERDICT: PASS" : "VERDICT: FAIL");
            return rc;
        }

        private static int One(WorldRef w, LocationTable table, List<AltBiomeRuntime> alts, int workers)
        {
            Console.WriteLine();
            Console.WriteLine("== " + w.Name + "  seed " + w.Seed + (w.IsHoldOut ? "  (HOLD-OUT)" : ""));

            WorldSave save = WorldSaveReader.OpenWorldDirectory(GroundTruthPaths.WorldDirectory(w),
                                                                new WorldSaveOptions { ReadDb = true });
            if (save.Db == null) { Console.WriteLine("  no .db2"); return 1; }
            ZoneSystemData zs = save.Db.ZoneSystem;
            Console.WriteLine("  save         " + zs.Locations.Count.ToString("N0") + " instances ("
                              + CountPlaced(zs) + " placed), " + zs.GeneratedZones.Count
                              + " generated zones, locationVersion " + zs.LocationVersion);

            // A fresh run: genloc ran before any zone existed, so no GeneratedZones are passed.
            WorldLocations wl = WorldLocations.Build(w.Seed, 2, alts, workers);
            PlacementResult res = wl.PlaceAll(table);
            Console.WriteLine("  engine       " + res.Instances.Count.ToString("N0") + " instances ("
                              + res.Milliseconds.ToString("F0") + " ms)");

            // ---- instance-for-instance, by (hash, x, y, z) bits -----------------------------------
            Dictionary<long, List<LocationInstanceResult>> ours = new Dictionary<long, List<LocationInstanceResult>>();
            Dictionary<int, int> oursByHash = new Dictionary<int, int>();
            foreach (LocationInstanceResult i in res.Instances)
            {
                long k = Key(i.X, i.Z);
                if (!ours.TryGetValue(k, out List<LocationInstanceResult>? l)) { l = new List<LocationInstanceResult>(); ours[k] = l; }
                l.Add(i);
                oursByHash.TryGetValue(i.PrefabHash, out int c); oursByHash[i.PrefabHash] = c + 1;
            }

            Dictionary<int, string> nameOf = new Dictionary<int, string>();
            foreach (ZoneLocationEntry e in table.All) nameOf[e.NameHash] = e.PrefabName;

            HashSet<LocationInstanceResult> used = new HashSet<LocationInstanceResult>();
            int exact = 0, hashDiff = 0, yDiff = 0, absent = 0;
            Dictionary<int, int> saveByHash = new Dictionary<int, int>();
            Dictionary<int, int> matchedByHash = new Dictionary<int, int>();
            List<string> firstBad = new List<string>();

            foreach (SeedLab.Saves.LocationInstance g in zs.Locations)
            {
                saveByHash.TryGetValue(g.PrefabHash, out int sc); saveByHash[g.PrefabHash] = sc + 1;

                LocationInstanceResult? hit = null;
                if (ours.TryGetValue(Key(g.X, g.Z), out List<LocationInstanceResult>? cand))
                    foreach (LocationInstanceResult o in cand)
                        if (!used.Contains(o) && o.PrefabHash == g.PrefabHash
                            && Bits(o.Y) == Bits(g.Y)) { hit = o; break; }

                if (hit != null)
                {
                    used.Add(hit); exact++;
                    matchedByHash.TryGetValue(g.PrefabHash, out int mc); matchedByHash[g.PrefabHash] = mc + 1;
                    continue;
                }

                // Diagnose the miss.
                if (cand != null)
                {
                    bool sameXZdiffHash = false, sameXZdiffY = false;
                    foreach (LocationInstanceResult o in cand)
                    {
                        if (o.PrefabHash != g.PrefabHash) sameXZdiffHash = true;
                        else if (Bits(o.Y) != Bits(g.Y)) sameXZdiffY = true;
                    }
                    if (sameXZdiffY) { yDiff++; Add(firstBad, "Y     " + Name(nameOf, g.PrefabHash) + " at (" + g.X + ", " + g.Z + ")"); continue; }
                    if (sameXZdiffHash) { hashDiff++; Add(firstBad, "HASH  (" + g.X + ", " + g.Z + ") save " + Name(nameOf, g.PrefabHash)); continue; }
                }
                absent++;
                Add(firstBad, "ABSENT " + Name(nameOf, g.PrefabHash) + " at (" + g.X + ", " + g.Y + ", " + g.Z + ")");
            }

            int extra = res.Instances.Count - used.Count;

            Console.WriteLine("  MATCH        " + exact.ToString("N0") + " / " + zs.Locations.Count.ToString("N0")
                              + " save instances reproduced with x, y and z bit-identical = "
                              + (100.0 * exact / zs.Locations.Count).ToString("F4") + " %");
            if (yDiff + hashDiff + absent > 0)
                Console.WriteLine("  unmatched    y differs " + yDiff + ", prefab differs " + hashDiff
                                  + ", position absent " + absent);
            foreach (string s in firstBad) Console.WriteLine("    " + s);
            Console.WriteLine("  engine-only  " + extra.ToString("N0")
                              + " instances the engine produced that the save no longer holds");

            // ---- categorise the engine-only instances ---------------------------------------------
            Dictionary<string, int> extraByPrefab = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (LocationInstanceResult o in res.Instances)
                if (!used.Contains(o))
                {
                    extraByPrefab.TryGetValue(o.PrefabName, out int c);
                    extraByPrefab[o.PrefabName] = c + 1;
                }

            int uniqueExtra = 0, otherExtra = 0;
            List<string> cats = new List<string>();
            foreach (KeyValuePair<string, int> kv in extraByPrefab)
            {
                ZoneLocationEntry? e = Find(table, kv.Key);
                bool uni = e != null && e.Unique;
                if (uni) uniqueExtra += kv.Value; else otherExtra += kv.Value;
                cats.Add((uni ? "  m_unique  " : "  OTHER     ") + kv.Key.PadRight(30) + " engine "
                         + (oursByHash.TryGetValue(e?.NameHash ?? 0, out int on) ? on : 0).ToString().PadLeft(4)
                         + "  save " + (saveByHash.TryGetValue(e?.NameHash ?? 0, out int sn) ? sn : 0).ToString().PadLeft(4)
                         + "  engine-only " + kv.Value.ToString().PadLeft(4)
                         + (uni ? "  (quantity " + e!.Quantity + ", candidates pruned by RemoveUnplacedLocations)" : ""));
            }
            cats.Sort(StringComparer.Ordinal);
            foreach (string s in cats) Console.WriteLine(s);
            Console.WriteLine("  engine-only breakdown: " + uniqueExtra + " from m_unique types, "
                              + otherExtra + " from everything else");

            // ---- per-type counts, against the game's own log ---------------------------------------
            int logRc = 0;
            if (w.IsHoldOut) { logRc = CompareAltBiomeLog(alts); logRc |= CompareLog(res); }

            bool ok = exact == zs.Locations.Count && otherExtra == 0 && logRc == 0;
            return ok ? 0 : 1;
        }

        private static bool s_verbose;

        private static void Add(List<string> l, string s) { if (l.Count < 15) l.Add(s); }

        private static int CountPlaced(ZoneSystemData zs)
        {
            int n = 0; foreach (SeedLab.Saves.LocationInstance i in zs.Locations) if (i.Placed) n++; return n;
        }

        private static ZoneLocationEntry? Find(LocationTable t, string prefab)
        {
            foreach (ZoneLocationEntry e in t.All) if (e.PrefabName == prefab) return e;
            return null;
        }

        private static string Name(Dictionary<int, string> m, int h) => m.TryGetValue(h, out string? n) ? n : "hash:" + h;

        private static long Key(float x, float z)
            => ((long)(uint)BitConverter.SingleToInt32Bits(x) << 32) | (uint)BitConverter.SingleToInt32Bits(z);

        private static int Bits(float f) => BitConverter.SingleToInt32Bits(f);

        // ------------------------------------------------------------------------------------------
        // The game's own per-type counters, from the worldgen log of testworldclaude's creation.
        // ------------------------------------------------------------------------------------------

        private static readonly Regex s_failed = new Regex(
            @"Failed to place all (?<n>[A-Za-z0-9_]+), placed (?<p>\d+) out of (?<q>\d+)", RegexOptions.Compiled);

        private static readonly Regex s_slow = new Regex(
            @"Location (?<n>[A-Za-z0-9_]+) took more than [\d.]+ seconds to place[^(]*\(placed (?<p>\d+) out of (?<q>\d+)",
            RegexOptions.Compiled);

        private static int CompareLog(PlacementResult res)
        {
            string path = Path.Combine(GroundTruthPaths.Root, "LogOutput-20260922-worldgen.log");
            if (!File.Exists(path)) { Console.WriteLine("  (no worldgen log at " + path + ")"); return 0; }

            Dictionary<string, (int p, int q)> log = new Dictionary<string, (int, int)>(StringComparer.Ordinal);
            foreach (string line in File.ReadLines(path))
            {
                foreach (Match m in s_failed.Matches(line)) Record(log, m);
                foreach (Match m in s_slow.Matches(line)) Record(log, m);
            }

            Dictionary<string, LocationTypeResult> mine = new Dictionary<string, LocationTypeResult>(StringComparer.Ordinal);
            foreach (LocationTypeResult t in res.Types) mine[t.Location.PrefabName] = t;

            int ok = 0, bad = 0, qBad = 0;
            List<string> diffs = new List<string>();
            foreach (KeyValuePair<string, (int p, int q)> kv in log)
            {
                if (!mine.TryGetValue(kv.Key, out LocationTypeResult? t))
                { diffs.Add("    MISSING TYPE " + kv.Key); bad++; continue; }
                if (t.Quantity != kv.Value.q) { diffs.Add("    QUANTITY " + kv.Key + " log " + kv.Value.q + " table " + t.Quantity); qBad++; }
                if (t.Placed == kv.Value.p)
                {
                    ok++;
                    if (s_verbose) diffs.Add("    ok  " + kv.Key.PadRight(30) + " log " + kv.Value.p + "/" + kv.Value.q
                                             + "  engine " + t.Placed + "/" + t.Quantity);
                }
                else { bad++; diffs.Add("    PLACED   " + kv.Key.PadRight(30) + " log " + kv.Value.p + "/" + kv.Value.q + "  engine " + t.Placed + "/" + t.Quantity); }
            }
            Console.WriteLine("  game log     " + log.Count + " types carry a 'placed N out of M' line; "
                              + ok + " reproduced exactly, " + bad + " differ, " + qBad + " quantity mismatches");
            foreach (string s in diffs) Console.WriteLine(s);
            return bad + qBad == 0 ? 0 : 1;
        }

        private static readonly Regex s_alt = new Regex(
            @"Placed (?<c>\d+)/(?<min>\d+)-(?<max>\d+) of '(?<n>[^']+)' altbiome\. \(Valid, sectors: (?<vs>\d+), combos: (?<vc>\d+)\)",
            RegexOptions.Compiled);

        /// <summary>
        /// The game logs one line per alt-biome that ended UNDER its <c>m_minAmountSpawned</c>
        /// (<c>ZLog.LogWarning</c>); the rest go to <c>ZLog.DevLog</c> and never reach a release log. So
        /// the log tells us exactly which alt-biomes under-placed and with what counters, and - by its
        /// silence - that every other alt-biome reached its minimum. Both halves are checked.
        /// </summary>
        private static int CompareAltBiomeLog(List<AltBiomeRuntime> alts)
        {
            string path = Path.Combine(GroundTruthPaths.Root, "LogOutput-20260922-worldgen.log");
            if (!File.Exists(path)) return 0;

            Dictionary<string, (int c, int min, int max, int vs, int vc)> log =
                new Dictionary<string, (int, int, int, int, int)>(StringComparer.Ordinal);
            foreach (string line in File.ReadLines(path))
                foreach (Match m in s_alt.Matches(line))
                    log[m.Groups["n"].Value] = (
                        int.Parse(m.Groups["c"].Value, CultureInfo.InvariantCulture),
                        int.Parse(m.Groups["min"].Value, CultureInfo.InvariantCulture),
                        int.Parse(m.Groups["max"].Value, CultureInfo.InvariantCulture),
                        int.Parse(m.Groups["vs"].Value, CultureInfo.InvariantCulture),
                        int.Parse(m.Groups["vc"].Value, CultureInfo.InvariantCulture));

            int bad = 0, under = 0;
            foreach (AltBiomeRuntime a in alts)
            {
                bool engineUnder = a.Sectors.Count < a.MinAmountSpawned;
                bool logged = log.TryGetValue(a.Name, out (int c, int min, int max, int vs, int vc) g);
                if (engineUnder) under++;
                if (engineUnder != logged)
                {
                    bad++;
                    Console.WriteLine("    ALTBIOME " + a.Name.PadRight(20)
                                      + (engineUnder ? " engine under-placed (" + a.Sectors.Count + "/"
                                         + a.MinAmountSpawned + ") but the game logged no warning"
                                         : " the game warned but the engine placed " + a.Sectors.Count
                                           + "/" + a.MinAmountSpawned));
                    continue;
                }
                if (!logged) continue;
                if (g.c != a.Sectors.Count || g.min != a.MinAmountSpawned || g.max != a.MaxAmountSpawned
                    || g.vs != a.ValidPlacementSectors || g.vc != a.ValidPlacementSectorCombos)
                {
                    bad++;
                    Console.WriteLine("    ALTBIOME " + a.Name.PadRight(20) + " log " + g.c + "/" + g.min + "-" + g.max
                                      + " (vs " + g.vs + ", vc " + g.vc + ")  engine " + a.Sectors.Count + "/"
                                      + a.MinAmountSpawned + "-" + a.MaxAmountSpawned + " (vs " + a.ValidPlacementSectors
                                      + ", vc " + a.ValidPlacementSectorCombos + ")");
                }
            }
            int slots = 0; foreach (AltBiomeRuntime a in alts) slots += a.Sectors.Count;
            Console.WriteLine("  alt-biomes   " + alts.Count + " assigned to " + slots + " sector slots; "
                              + log.Count + " under-min warnings in the game's log, " + under
                              + " in the engine, " + (bad == 0 ? "all counters equal" : bad + " MISMATCHES"));
            return bad == 0 ? 0 : 1;
        }

        private static void Record(Dictionary<string, (int, int)> log, Match m)
        {
            string n = m.Groups["n"].Value;
            int p = int.Parse(m.Groups["p"].Value, CultureInfo.InvariantCulture);
            int q = int.Parse(m.Groups["q"].Value, CultureInfo.InvariantCulture);
            log[n] = (p, q);
        }
    }
}
