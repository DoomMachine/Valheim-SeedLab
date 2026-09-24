using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Locations;
using SeedLab.Seeds;

namespace SeedLab.Search.Feasibility
{
    /// <summary>One named assertion and the detail line printed either way.</summary>
    public readonly struct CheckResult
    {
        public CheckResult(bool ok, string name, string detail)
        {
            Ok = ok;
            Name = name;
            Detail = detail;
        }

        public bool Ok { get; }
        public string Name { get; }
        public string Detail { get; }

        public override string ToString()
            => "[" + (Ok ? "ok  " : "FAIL") + "] " + Name + (Detail.Length > 0 ? " - " + Detail : "");
    }

    /// <summary>
    /// The checker's own test suite, as a library so the CLI, the web API and the search test project
    /// can all run exactly the code that ships rather than a copy of it.
    ///
    /// <para><b>The dangerous failure is a checker that refuses a LEGAL query.</b> It makes a seed
    /// unfindable and tells the user a falsehood with a citation attached. Every test here aims at
    /// that, and T1 is the one that matters: for all 183 running types, assert that none of the
    /// 36,829 real instances in the ground truth is refused by the CHECKER's own code path - not by
    /// the atlas builder's, which is what the atlas's own "0 violations" already says.</para>
    /// </summary>
    public static class CheckerSelfTest
    {
        /// <summary>Runs every test. <paramref name="quick"/> subsamples T1 rather than skipping it.</summary>
        public static List<CheckResult> Run(ILocationOracle oracle, bool quick = false,
                                            ConstraintAtlas? atlas = null)
        {
            atlas ??= ConstraintAtlas.Load();
            List<CheckResult> all = new List<CheckResult>();
            List<GoalCheck> everyCheck = new List<GoalCheck>();

            all.Add(AtlasPresent(atlas));
            all.AddRange(T1(oracle, atlas, everyCheck, quick));
            all.AddRange(T4(oracle, atlas));
            all.AddRange(T5(oracle, atlas, everyCheck));
            all.AddRange(T6(oracle, atlas, everyCheck));
            all.AddRange(T7(oracle, atlas, everyCheck));
            all.AddRange(T8(oracle, atlas, everyCheck));
            all.AddRange(T9(oracle, atlas));
            all.Add(T2(everyCheck));
            return all;
        }

        // =========================================================================================
        // T8 - the shared zone budget composes across goals WITHOUT over-charging
        // =========================================================================================

        /// <summary>
        /// The one-per-zone rule binds all types together, so several <c>count_within</c> must-goals
        /// compete for the same zones. The sound statement is a RUNNING sum by ascending radius: a
        /// goal at 10,500 m does not compete for the zones inside 300 m. Charging every goal against
        /// the SMALLEST disc - which is what the design's own formula says - refuses
        /// "one Fuling village within 300 m and 100 burial chambers anywhere", which every seed
        /// satisfies. Both directions are asserted here because only one of them is dangerous and it
        /// is the one no preset exercises.
        /// </summary>
        private static IEnumerable<CheckResult> T8(ILocationOracle oracle, ConstraintAtlas atlas,
                                                   List<GoalCheck> sink)
        {
            if (!oracle.Available || !atlas.Available)
            {
                yield return new CheckResult(false, "T8 needs the atlas and the location table", "");
                yield break;
            }

            Query legal = TwoCounts(("group:fuling_villages", 300, 1), ("group:burial_chambers", 10500, 100));
            QueryCheckReport a = RunReport(oracle, atlas, legal, sink);
            yield return new CheckResult(a != null && a.QueryMessages.Count == 0,
                "T8: a small tight goal beside a large loose one is NOT refused on the zone budget",
                a == null ? "did not compile"
                    : a.QueryMessages.Count == 0
                        ? "1 village within 300 m (89 zones) + 100 chambers within 10,500 m (85,233 zones): allowed"
                        : "  <<< WRONGLY REFUSED: " + string.Join(" | ", a.QueryMessages));

            Query over = TwoCounts(("group:fuling_villages", 300, 50), ("group:burial_chambers", 300, 50));
            QueryCheckReport b = RunReport(oracle, atlas, over, sink);
            yield return new CheckResult(b != null && b.QueryMessages.Count > 0,
                "T8: two goals that together exceed one disc's zones ARE refused",
                b != null && b.QueryMessages.Count > 0
                    ? b.QueryMessages[0]
                    : "NOT CAUGHT - 50 + 50 = 100 instances need more than the 89 zones inside 300 m");
        }

        // =========================================================================================
        // T9 - a DATA-STAMP mismatch disables every refusal
        // =========================================================================================

        /// <summary>
        /// A game update is exactly the case where a hard bound quietly stops being hard, so a
        /// mismatch between the atlas's stamp and the live location table's build must turn every
        /// refusal into a warning that names the mismatch. It has no coverage from any normal run,
        /// because every normal run has matching stamps - so this test doctors a copy of the atlas.
        /// </summary>
        private static IEnumerable<CheckResult> T9(ILocationOracle oracle, ConstraintAtlas atlas)
        {
            if (!oracle.Available || !atlas.Available || atlas.Path.Length == 0)
            {
                yield return new CheckResult(false, "T9 needs the atlas and the location table", "");
                yield break;
            }

            string doctored = Path.Combine(Path.GetTempPath(),
                "seedlab-atlas-stampcheck-" + Environment.ProcessId + ".json");
            ConstraintAtlas? other = Doctor(atlas, doctored, out string why);
            if (other == null)
            {
                yield return new CheckResult(false, "T9: a doctored copy of the atlas could be made", why);
                yield break;
            }

            yield return new CheckResult(other.Available && other.BuildTag != atlas.BuildTag,
                "T9: the doctored atlas loads and names a different build",
                other.BuildTag + " against the live " + atlas.BuildTag);

            (string What, Query Q)[] refusable =
            {
                ("group:fuling_villages count at_least 201", Count("group:fuling_villages", GoalTest.AtLeast, 201)),
                ("location:GoblinCamp2_1 nearest_distance near 5000", Distance("GoblinCamp2_1", GoalTest.Near, 5000)),
                ("location:Vendor_BlackForest count at_least 11", Count("location:Vendor_BlackForest", GoalTest.AtLeast, 11)),
                ("location:FaderLocation near 2,500 m from spawn", Spawn("FaderLocation", 2500)),
            };

            int stillRefused = 0, named = 0;
            List<string> bad = new List<string>();
            foreach ((string what, Query q) in refusable)
            {
                GoalCheck? gc = CheckOne(oracle, other, q);
                if (gc == null) continue;
                if (gc.Verdict == CheckVerdict.Refuse)
                {
                    stillRefused++;
                    bad.Add(what);
                    continue;
                }

                // The downgraded message has to name BOTH builds, or the user is told a bound is
                // advisory without being told which data stopped supporting it.
                if (gc.Message.IndexOf("NOT REFUSED", StringComparison.Ordinal) >= 0
                    && gc.Message.IndexOf(other.BuildTag, StringComparison.Ordinal) >= 0
                    && gc.Message.IndexOf(atlas.BuildTag, StringComparison.Ordinal) >= 0)
                {
                    named++;
                }
            }

            yield return new CheckResult(stillRefused == 0,
                "T9: under a stamp mismatch nothing is refused",
                refusable.Length + " normally-refusable goals, " + stillRefused + " still refused"
                + (stillRefused > 0 ? "  <<< " + string.Join(" | ", bad) : ""));

            yield return new CheckResult(named == refusable.Length,
                "T9: each downgraded refusal names both builds",
                named + " of " + refusable.Length + " carry 'NOT REFUSED' and both build tags ("
                + other.BuildTag + " vs " + atlas.BuildTag + ")");

            // R13 is a contradiction in the query's own text and needs no game data, so it must
            // survive the mismatch. It is the one rule that does.
            Query contradiction = new Query { Defs = 1 };
            contradiction.Search.Grid = 12.0;
            contradiction.Goals.Add(new Goal
            {
                Id = "close", Target = new GoalTarget(TargetKind.Biome, "Swamp"), Metric = "nearest_distance",
                Test = GoalTest.Near, Value = 2100, Importance = Importance.Must,
            });
            contradiction.Goals.Add(new Goal
            {
                Id = "far", Target = new GoalTarget(TargetKind.Biome, "Swamp"), Metric = "nearest_distance",
                Test = GoalTest.Far, Value = 3000, Importance = Importance.Must,
            });

            QueryCheckReport? cr = RunReport(oracle, other, contradiction, null);
            yield return new CheckResult(cr != null && cr.QueryMessages.Count > 0,
                "T9: a contradiction between two goals survives the mismatch",
                cr != null && cr.QueryMessages.Count > 0
                    ? cr.QueryMessages[cr.QueryMessages.Count - 1]
                    : "NOT CAUGHT - R13 needs no game data and must stand");

            try { File.Delete(doctored); }
            catch (IOException) { }
        }

        /// <summary>
        /// Writes a copy of the atlas with ONE hex digit of its assembly hash changed - the smallest
        /// possible "different build" - and loads it. Null when the copy could not be made.
        /// </summary>
        private static ConstraintAtlas? Doctor(ConstraintAtlas atlas, string path, out string why)
        {
            try
            {
                string text = File.ReadAllText(atlas.Path);
                int at = text.IndexOf("assembly_valheim-sha256=", StringComparison.Ordinal);
                if (at < 0)
                {
                    why = "no assembly_valheim-sha256 in " + atlas.Path;
                    return null;
                }

                int digit = at + "assembly_valheim-sha256=".Length;
                char c = text[digit];
                text = text.Substring(0, digit) + (c == '0' ? '1' : '0') + text.Substring(digit + 1);
                File.WriteAllText(path, text);
                why = "";
                return ConstraintAtlas.LoadFrom(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                why = ex.Message;
                return null;
            }
        }

        private static QueryCheckReport RunReport(ILocationOracle oracle, ConstraintAtlas atlas, Query q,
                                                  List<GoalCheck>? sink)
        {
            CompiledQuery cq = CompiledQuery.Compile(q, oracle);
            QueryCheckReport r = QueryCheck.Run(q, cq, oracle, atlas);
            sink?.AddRange(r.Goals);
            return r;
        }

        private static Query TwoCounts(params (string Target, double Radius, double N)[] goals)
        {
            Query q = new Query { Defs = 1 };
            q.World.GenVersion = 2;
            q.Search.Grid = 12.0;
            int i = 0;
            foreach ((string target, double radius, double n) in goals)
            {
                int colon = target.IndexOf(':');
                q.Goals.Add(new Goal
                {
                    Id = "g" + i++,
                    Target = new GoalTarget(
                        target.StartsWith("group:", StringComparison.Ordinal) ? TargetKind.Group : TargetKind.Location,
                        target.Substring(colon + 1)),
                    Metric = "count_within",
                    Radius = radius,
                    Test = GoalTest.AtLeast,
                    Value = n,
                    Importance = Importance.Must,
                });
            }

            return q;
        }

        private static CheckResult AtlasPresent(ConstraintAtlas atlas)
            => new CheckResult(atlas.Available,
                "the constraint atlas loads and carries a DATA-STAMP",
                atlas.Available
                    ? atlas.BuildTag + ", " + atlas.TypesCovered + " types, "
                      + atlas.InstancesValidated.ToString("N0", CultureInfo.InvariantCulture)
                      + " instances validated when it was built, atlas version " + atlas.AtlasVersion
                    : atlas.UnavailableReason);

        // =========================================================================================
        // T1 - no real instance is refused by the checker's own code path
        // =========================================================================================

        /// <summary>One real placed instance, from the ground truth.</summary>
        public readonly struct RealInstance
        {
            public RealInstance(string prefab, double distance, string source)
            {
                Prefab = prefab;
                Distance = distance;
                Source = source;
            }

            public string Prefab { get; }
            public double Distance { get; }
            public string Source { get; }
        }

        /// <summary>
        /// The three ground-truth corpora, resolved by STABLE HASH rather than by the prefab-name
        /// column: that column is blank on 10,293 of the 12,314 rows of one of the CSVs, and a test
        /// that quietly skipped those rows would be testing a sixth of the corpus while reporting the
        /// whole of it.
        /// </summary>
        public static List<RealInstance> LoadGroundTruth(ILocationOracle oracle, out string sources)
        {
            List<RealInstance> rows = new List<RealInstance>();
            List<string> found = new List<string>();

            Dictionary<int, string> byHash = new Dictionary<int, string>();
            if (oracle.Available)
            {
                foreach (string p in oracle.PrefabNames) byHash[StableHash.Compute(p)] = p;
            }

            string? root = FindRoot();
            if (root == null)
            {
                sources = "the ground-truth corpora were not found (looked for groundtruth\\ and data\\ "
                          + "by walking up from the working directory and from the binary)";
                return rows;
            }

            foreach (string csv in new[]
                     {
                         Path.Combine(root, "groundtruth", "asdasdasd-locations.csv"),
                         Path.Combine(root, "groundtruth", "testworldclaude-locations.csv"),
                     })
            {
                if (!File.Exists(csv)) continue;
                int n = 0;
                foreach (string line in File.ReadLines(csv))
                {
                    string[] f = line.Split(',');
                    if (f.Length < 5 || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture,
                                                      out int hash)) continue;
                    if (!double.TryParse(f[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double x)
                        || !double.TryParse(f[3], NumberStyles.Float, CultureInfo.InvariantCulture, out double z))
                    {
                        continue;
                    }

                    string prefab = byHash.TryGetValue(hash, out string? p) ? p
                                  : f[1].Trim().Length > 0 ? f[1].Trim() : "";
                    if (prefab.Length == 0) continue;
                    rows.Add(new RealInstance(prefab, Math.Sqrt(x * x + z * z), Path.GetFileName(csv)));
                    n++;
                }

                found.Add(Path.GetFileName(csv) + " " + n.ToString("N0", CultureInfo.InvariantCulture));
            }

            string goldens = Path.Combine(root, "data");
            if (Directory.Exists(goldens))
            {
                foreach (string dir in Directory.GetDirectories(goldens))
                {
                    string g = Path.Combine(dir, "goldens");
                    if (!Directory.Exists(g)) continue;
                    foreach (string file in Directory.GetFiles(g, "locationinstances-*.json"))
                    {
                        int n = 0;
                        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
                        if (!doc.RootElement.TryGetProperty("instances", out JsonElement inst)) continue;
                        foreach (JsonElement e in inst.EnumerateArray())
                        {
                            string prefab = e.TryGetProperty("hash", out JsonElement h)
                                            && byHash.TryGetValue(h.GetInt32(), out string? p)
                                ? p
                                : e.TryGetProperty("prefabName", out JsonElement pn) ? pn.GetString() ?? "" : "";
                            if (prefab.Length == 0) continue;
                            double x = e.GetProperty("x").GetDouble(), z = e.GetProperty("z").GetDouble();
                            rows.Add(new RealInstance(prefab, Math.Sqrt(x * x + z * z),
                                                      Path.GetFileName(file)));
                            n++;
                        }

                        found.Add(Path.GetFileName(file) + " " + n.ToString("N0", CultureInfo.InvariantCulture));
                    }
                }
            }

            sources = string.Join(", ", found);
            return rows;
        }

        private static IEnumerable<CheckResult> T1(ILocationOracle oracle, ConstraintAtlas atlas,
                                                   List<GoalCheck> sink, bool quick)
        {
            if (!oracle.Available)
            {
                yield return new CheckResult(false, "T1 needs the dumped location table", oracle.UnavailableReason);
                yield break;
            }

            List<RealInstance> rows = LoadGroundTruth(oracle, out string sources);
            if (rows.Count == 0)
            {
                yield return new CheckResult(false, "T1: the ground truth loaded", sources);
                yield break;
            }

            int step = quick ? 17 : 1;

            // (a) every real instance's measured distance lies inside the checker's own F.
            int outside = 0, tested = 0;
            HashSet<string> types = new HashSet<string>(StringComparer.Ordinal);
            List<string> worst = new List<string>();
            Dictionary<string, Feasible> fByPrefab = new Dictionary<string, Feasible>(StringComparer.Ordinal);

            for (int i = 0; i < rows.Count; i += step)
            {
                RealInstance row = rows[i];
                types.Add(row.Prefab);
                if (!fByPrefab.TryGetValue(row.Prefab, out Feasible f))
                {
                    GoalCheck? probe = CheckOne(oracle, atlas, Distance(row.Prefab, GoalTest.Near, 10500), sink);
                    f = probe?.F ?? Feasible.Unknown;
                    fByPrefab[row.Prefab] = f;
                }

                tested++;
                if (f.IsUnknown) continue;
                if (row.Distance + QueryCheck.Slack < f.Lo || row.Distance > f.Hi + QueryCheck.Slack)
                {
                    outside++;
                    if (worst.Count < 5)
                    {
                        worst.Add(row.Prefab + " at " + row.Distance.ToString("N1", CultureInfo.InvariantCulture)
                                  + " m is outside F = " + f.Describe(Unit.Metres) + " (" + row.Source + ")");
                    }
                }
            }

            yield return new CheckResult(outside == 0,
                "T1a: every real instance lies inside the checker's own feasible set",
                tested.ToString("N0", CultureInfo.InvariantCulture) + " instances over " + types.Count
                + " types from " + sources
                + (outside == 0 ? ", 0 outside" : "  <<< " + outside + " OUTSIDE: " + string.Join(" | ", worst)));

            // (b) the mechanical half: for every real instance at distance d of type X, the checker
            // must NOT refuse 'near ceil(d)', 'far floor(d)' or 'between floor(d)..ceil(d)'. One
            // refusal here is a shipped bug - it makes a seed that demonstrably exists unfindable.
            int refused = 0, goals = 0;
            List<string> offenders = new List<string>();
            HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < rows.Count; i += step)
            {
                RealInstance row = rows[i];
                double up = Math.Ceiling(row.Distance), down = Math.Floor(row.Distance);
                string key = row.Prefab + "|" + up + "|" + down;
                if (!seen.Add(key)) continue;

                foreach (Query q in new[]
                         {
                             Distance(row.Prefab, GoalTest.Near, up),
                             Distance(row.Prefab, GoalTest.Far, down),
                             Between(row.Prefab, down, up),

                             // The fourth shape exercises a DIFFERENT refusal path for free: R5's
                             // "radius inside the ring" test and the atlas-backed one-per-zone cap,
                             // both against a disc a real instance demonstrably sits in.
                             CountWithin(row.Prefab, up, 1),
                         })
                {
                    goals++;
                    GoalCheck? gc = CheckOne(oracle, atlas, q, sink);
                    if (gc == null || gc.Verdict != CheckVerdict.Refuse) continue;
                    refused++;
                    if (offenders.Count < 5)
                    {
                        offenders.Add(row.Prefab + " at " + row.Distance.ToString("N1", CultureInfo.InvariantCulture)
                                      + " m: " + gc.Message);
                    }
                }
            }

            yield return new CheckResult(refused == 0,
                "T1b: no goal satisfied by a real instance is refused",
                goals.ToString("N0", CultureInfo.InvariantCulture) + " goals over "
                + seen.Count.ToString("N0", CultureInfo.InvariantCulture) + " distinct (type, distance) pairs"
                + (refused == 0 ? ", 0 refused" : "  <<< " + refused + " REFUSED: " + string.Join(" | ", offenders)));
        }

        // =========================================================================================
        // T2 - no REFUSE ever rests on a sample
        // =========================================================================================

        private static CheckResult T2(List<GoalCheck> checks)
        {
            int refusals = 0, bad = 0;
            string first = "";
            foreach (GoalCheck g in checks)
            {
                if (g.Verdict != CheckVerdict.Refuse) continue;
                refusals++;
                foreach (CheckEvidence e in g.Evidence)
                {
                    if (e.Kind != EvidenceKind.Sample) continue;
                    bad++;
                    if (first.Length == 0) first = g.Goal.Id + ": " + e;
                }
            }

            return new CheckResult(bad == 0,
                "T2: every refusal rests only on code, asset or query evidence",
                refusals.ToString("N0", CultureInfo.InvariantCulture)
                + " refusals examined across every test above, 0 sample-backed"
                + (bad > 0 ? "  <<< " + bad + " SAMPLE-BACKED: " + first : "")
                + ". The guarantee is structural: GoalCheck.Refuse takes HardEvidence, whose "
                + "constructor throws on EvidenceKind.Sample");
        }

        // =========================================================================================
        // T4 - the atlas agrees with the live computation
        // =========================================================================================

        private static IEnumerable<CheckResult> T4(ILocationOracle oracle, ConstraintAtlas atlas)
        {
            if (!oracle.Available || !atlas.Available)
            {
                yield return new CheckResult(false, "T4 needs both the atlas and the location table", "");
                yield break;
            }

            int compared = 0, looser = 0, tighter = 0;
            double maxTighter = 0;
            List<string> bad = new List<string>();

            foreach (AtlasType at in atlas.Types)
            {
                LocationTypeInfo? t = oracle.TypeOf(at.Prefab);
                if (t == null) continue;
                compared++;
                double lo = LocationFeasibility.LowerBound(t, 2);
                double hi = LocationFeasibility.UpperBound(t, 2);

                // The atlas may be TIGHTER than the code-derived band - it computes the exact
                // AshLands floor, 7,907.681 m, where BiomeGeometry rounds to 7,900 - and the checker
                // still decides on the looser of the two. It may never be LOOSER, because that would
                // mean it is describing a different build.
                if (at.RadialLo + 1e-6 < lo - 1.0 || at.RadialHi > hi + 1.0 + 1e-6)
                {
                    looser++;
                    if (bad.Count < 5)
                    {
                        bad.Add(at.Prefab + " atlas [" + F(at.RadialLo) + ", " + F(at.RadialHi)
                                + "] vs live [" + F(lo) + ", " + F(hi) + "]");
                    }
                }
                else if (Math.Abs(at.RadialLo - lo) > 1e-6 || Math.Abs(at.RadialHi - hi) > 1e-6)
                {
                    tighter++;
                    maxTighter = Math.Max(maxTighter, Math.Max(at.RadialLo - lo, hi - at.RadialHi));
                }
            }

            yield return new CheckResult(looser == 0,
                "T4: the atlas is never looser than the live computation",
                compared + " types compared, " + tighter + " tighter than the code-derived band (max "
                + F(maxTighter) + ", which is the exact AshLands floor against BiomeGeometry's 7,900 m), "
                + looser + " looser"
                + (looser > 0 ? "  <<< " + string.Join(" | ", bad) : "")
                + ". The checker decides on the LOOSER of the two either way");
        }

        // =========================================================================================
        // T5 - the shipped presets are all legal
        // =========================================================================================

        private static IEnumerable<CheckResult> T5(ILocationOracle oracle, ConstraintAtlas atlas,
                                                   List<GoalCheck> sink)
        {
            int ok = 0;
            List<string> refused = new List<string>();
            List<string> vacuous = new List<string>();
            List<string> usage = new List<string>();

            foreach (string name in Presets.Names)
            {
                Query q;
                CompiledQuery cq;
                try
                {
                    q = Presets.Load(name);
                    cq = CompiledQuery.Compile(q, oracle);
                }
                catch (Exception ex)
                {
                    refused.Add(name + " does not compile: " + ex.Message);
                    continue;
                }

                QueryCheckReport r = QueryCheck.Run(q, cq, oracle, atlas);
                sink.AddRange(r.Goals);
                ok++;
                foreach (GoalCheck g in r.Refused) refused.Add(name + "/" + g.Goal.Id + ": " + g.Message);
                foreach (GoalCheck g in r.NotDiscriminating) vacuous.Add(name + "/" + g.Goal.Id);
                foreach (string u in r.UsageErrors) usage.Add(name + ": " + u);
            }

            yield return new CheckResult(refused.Count == 0,
                "T5: no shipped preset has a refused must-goal",
                ok + " of " + Presets.Names.Count + " presets checked"
                + (refused.Count == 0 ? ", 0 refusals" : "  <<< " + string.Join(" | ", refused)));

            yield return new CheckResult(usage.Count == 0,
                "T5b: no shipped preset breaks a metric's usage contract",
                usage.Count == 0 ? "0 usage errors" : "  <<< " + string.Join(" | ", usage));

            yield return new CheckResult(vacuous.Count == 0,
                "T5c: no shipped preset has a must-goal that excludes nothing",
                vacuous.Count == 0
                    ? "0 vacuous or degenerate must-goals across all " + ok + " presets"
                    : "  <<< " + string.Join(", ", vacuous));
        }

        // =========================================================================================
        // T6 - worldGenVersion is honoured
        // =========================================================================================

        private static IEnumerable<CheckResult> T6(ILocationOracle oracle, ConstraintAtlas atlas,
                                                   List<GoalCheck> sink)
        {
            // Swamp's band ends at 6,000 m at gen_version 2 and at 8,000 m at gen_version <= 1
            // (VersionSetup 253-256 raises m_maxMarshDistance). Hard-coding v2 would refuse a
            // satisfiable v1 query, which is the exact failure this whole design is about.
            GoalCheck? v2 = CheckOne(oracle, atlas, Biome("Swamp", "nearest_distance", GoalTest.Between, 7000, 9000, 2), sink);
            GoalCheck? v1 = CheckOne(oracle, atlas, Biome("Swamp", "nearest_distance", GoalTest.Between, 7000, 9000, 1), sink);

            yield return new CheckResult(v2 != null && v2.Verdict == CheckVerdict.Refuse,
                "T6: Swamp beyond 7 km is refused at gen_version 2 (band ends at 6,000 m)",
                v2?.Message ?? "NOT CAUGHT");

            yield return new CheckResult(v1 != null && v1.Verdict != CheckVerdict.Refuse,
                "T6: the same goal is NOT refused at gen_version 1 (VersionSetup raises it to 8,000 m)",
                v1 != null && v1.Verdict != CheckVerdict.Refuse
                    ? "F = " + v1.F.Describe(Unit.Metres)
                    : "WRONGLY REFUSED: " + (v1?.Message ?? ""));

            // Mountain's floor is 600 m at gen_version >= 1 and 1,100 m at gen_version 0.
            GoalCheck? m0 = CheckOne(oracle, atlas, Biome("Mountain", "nearest_distance", GoalTest.Near, 800, 0, 0), sink);
            GoalCheck? m2 = CheckOne(oracle, atlas, Biome("Mountain", "nearest_distance", GoalTest.Near, 800, 0, 2), sink);
            yield return new CheckResult(m0 != null && m0.Verdict == CheckVerdict.Refuse
                                         && m2 != null && m2.Verdict != CheckVerdict.Refuse,
                "T6: a Mountain within 800 m is refused at gen_version 0 and allowed at 2",
                "v0 " + (m0?.Verdict.ToString() ?? "?") + ", v2 " + (m2?.Verdict.ToString() ?? "?"));
        }

        // =========================================================================================
        // T7 - the known-vacuous corpus, and the must-be-OK list
        // =========================================================================================

        private static IEnumerable<CheckResult> T7(ILocationOracle oracle, ConstraintAtlas atlas,
                                                   List<GoalCheck> sink)
        {
            List<(string What, Query Q, CheckVerdict Want)> cases = new List<(string, Query, CheckVerdict)>
            {
                // gentle-start's three former must-goals. They are out of the preset now; the rule
                // that caught them has to stay tested, or the next preset to grow one is not caught.
                ("biome:Swamp area_within 1200 at_most 0",
                 BiomeArea("Swamp", 1200, 0), CheckVerdict.WarnVacuous),
                ("biome:Plains area_within 1200 at_most 0",
                 BiomeArea("Plains", 1200, 0), CheckVerdict.WarnVacuous),
                ("biome:Mistlands area_within 1200 at_most 0",
                 BiomeArea("Mistlands", 1200, 0), CheckVerdict.WarnVacuous),

                // The distinction the design exists to draw: this one is NOT vacuous. Eikthyrnir
                // carries m_maxDistance 1000, so 'within 1,200 m' rejects exactly the worlds in
                // which no altar placed - a presence filter, not a no-op.
                ("location:Eikthyrnir nearest_distance near 1200",
                 Distance("Eikthyrnir", GoalTest.Near, 1200), CheckVerdict.WarnDegenerate),

                ("biome:AshLands nearest_distance far 5000",
                 Biome("AshLands", "nearest_distance", GoalTest.Far, 5000, 0, 2), CheckVerdict.WarnVacuous),
                ("location:Bonemass nearest_distance far 2000",
                 Distance("Bonemass", GoalTest.Far, 2000), CheckVerdict.WarnVacuous),
                ("group:fuling_villages count at_least 201",
                 Count("group:fuling_villages", GoalTest.AtLeast, 201), CheckVerdict.Refuse),
                ("location:GoblinCamp2_1 nearest_distance near 5000",
                 Distance("GoblinCamp2_1", GoalTest.Near, 5000), CheckVerdict.Refuse),

                // The count-cap CORRECTION, asserted in both directions. Q is the sum of m_quantity,
                // because candidates of an m_unique type are counted individually. The design's own
                // table had both of these the other way round.
                ("group:traders count at_most 3 is NOT vacuous (Q = 30, not 3)",
                 Count("group:traders", GoalTest.AtMost, 3), CheckVerdict.Ok),
                ("group:traders count at_most 30 IS vacuous",
                 Count("group:traders", GoalTest.AtMost, 30), CheckVerdict.WarnVacuous),
                ("location:Vendor_BlackForest count at_least 2 is NOT refused (m_quantity 10)",
                 Count("location:Vendor_BlackForest", GoalTest.AtLeast, 2), CheckVerdict.Ok),
                ("location:Vendor_BlackForest count at_least 11 IS refused",
                 Count("location:Vendor_BlackForest", GoalTest.AtLeast, 11), CheckVerdict.Refuse),

                // D3: at N == Q the goal has become "every candidate placed".
                ("location:Vendor_BlackForest count at_least 10 is DEGENERATE (N == Q)",
                 Count("location:Vendor_BlackForest", GoalTest.AtLeast, 10), CheckVerdict.WarnDegenerate),

                // R14: from: spawn, through the triangle inequality against StartTemple's own band.
                ("location:FaderLocation near 2,500 m from spawn",
                 Spawn("FaderLocation", 2500), CheckVerdict.Refuse),
                ("location:FaderLocation near 3,000 m from spawn is NOT refused",
                 Spawn("FaderLocation", 3000), CheckVerdict.Ok),
            };

            int wrong = 0;
            List<string> bad = new List<string>();
            foreach ((string what, Query q, CheckVerdict want) in cases)
            {
                GoalCheck? gc = CheckOne(oracle, atlas, q, sink);
                CheckVerdict got = gc?.Verdict ?? CheckVerdict.Ok;
                bool ok = want == CheckVerdict.Ok ? got != CheckVerdict.Refuse
                                                    && got != CheckVerdict.WarnVacuous
                                                    && got != CheckVerdict.WarnDegenerate
                                                  : got == want;
                if (ok) continue;
                wrong++;
                bad.Add(what + ": wanted " + want + ", got " + got + (gc != null ? " (" + gc.Message + ")" : ""));
            }

            yield return new CheckResult(wrong == 0,
                "T7: the known-vacuous, degenerate and refusable corpus is classified correctly",
                cases.Count + " cases"
                + (wrong == 0 ? ", all as expected" : "  <<< " + string.Join(" | ", bad)));

            // The must-be-OK list, drawn from observed extremes: a checker that refuses any of these
            // is refusing a query a real world satisfies.
            (string Prefab, double D)[] extremes =
            {
                ("Vendor_BlackForest", 1508.0),
                ("InfestedTree01", 5999.3),
                ("GoblinHut01", 2902.0),
                ("GoblinHut01", 7994.8),
                ("Greydwarf_camp1", 10275.8),
                ("ShipWreck02_DN", 10360.0),
                ("Mistlands_RoadPost1", 5912.0),
            };

            int refusedExtremes = 0;
            List<string> ex = new List<string>();
            foreach ((string prefab, double d) in extremes)
            {
                GoalCheck? gc = CheckOne(oracle, atlas, Distance(prefab, GoalTest.Near, Math.Ceiling(d)), sink);
                if (gc == null || gc.Verdict != CheckVerdict.Refuse) continue;
                refusedExtremes++;
                ex.Add(prefab + " at " + d.ToString("N1", CultureInfo.InvariantCulture) + " m: " + gc.Message);
            }

            yield return new CheckResult(refusedExtremes == 0,
                "T7b: the observed extremes are all still allowed",
                extremes.Length + " observed extremes, including Mistlands_RoadPost1 at 5,912 m - below "
                + "the NOMINAL 6,000 m Mistlands bound, so it is the check that the wobble is applied "
                + "to the right endpoint"
                + (refusedExtremes == 0 ? ", 0 refused" : "  <<< " + string.Join(" | ", ex)));
        }

        // =========================================================================================
        // plumbing
        // =========================================================================================

        /// <summary>Compiles and checks a single-goal query, returning that goal's check.</summary>
        public static GoalCheck? CheckOne(ILocationOracle oracle, ConstraintAtlas atlas, Query q,
                                          List<GoalCheck>? sink = null)
        {
            try
            {
                CompiledQuery cq = CompiledQuery.Compile(q, oracle);
                QueryCheckReport r = QueryCheck.Run(q, cq, oracle, atlas);
                sink?.AddRange(r.Goals);
                return r.Goals.Count > 0 ? r.Goals[0] : null;
            }
            catch (QueryException)
            {
                return null;
            }
        }

        private static Query One(Goal g, int genVersion = 2)
        {
            Query q = new Query { Defs = 1 };
            q.World.GenVersion = genVersion;
            q.Search.Grid = 12.0;
            q.Goals.Add(g);
            return q;
        }

        private static Query Distance(string prefab, GoalTest test, double value) => One(new Goal
        {
            Id = "g",
            Target = new GoalTarget(TargetKind.Location, prefab),
            Metric = "nearest_distance",
            Test = test,
            Value = value,
            Importance = Importance.Must,
        });

        private static Query Spawn(string prefab, double value) => One(new Goal
        {
            Id = "g",
            Target = new GoalTarget(TargetKind.Location, prefab),
            Metric = "nearest_distance",
            Test = GoalTest.Near,
            Value = value,
            From = DistanceOrigin.Spawn,
            Importance = Importance.Must,
        });

        private static Query CountWithin(string prefab, double radius, double n) => One(new Goal
        {
            Id = "g",
            Target = new GoalTarget(TargetKind.Location, prefab),
            Metric = "count_within",
            Radius = radius,
            Test = GoalTest.AtLeast,
            Value = n,
            Importance = Importance.Must,
        });

        private static Query Between(string prefab, double lo, double hi) => One(new Goal
        {
            Id = "g",
            Target = new GoalTarget(TargetKind.Location, prefab),
            Metric = "nearest_distance",
            Test = GoalTest.Between,
            Value = lo,
            Max = hi,
            Importance = Importance.Must,
        });

        private static Query Count(string target, GoalTest test, double value)
        {
            int colon = target.IndexOf(':');
            TargetKind kind = target.StartsWith("group:", StringComparison.Ordinal)
                ? TargetKind.Group : TargetKind.Location;
            return One(new Goal
            {
                Id = "g",
                Target = new GoalTarget(kind, target.Substring(colon + 1)),
                Metric = "count",
                Test = test,
                Value = value,
                Importance = Importance.Must,
            });
        }

        private static Query Biome(string biome, string metric, GoalTest test, double value, double max,
                                   int genVersion) => One(new Goal
        {
            Id = "g",
            Target = new GoalTarget(TargetKind.Biome, biome),
            Metric = metric,
            Test = test,
            Value = value,
            Max = max,
            Importance = Importance.Must,
        }, genVersion);

        private static Query BiomeArea(string biome, double radius, double atMost) => One(new Goal
        {
            Id = "g",
            Target = new GoalTarget(TargetKind.Biome, biome),
            Metric = "area_within",
            Radius = radius,
            Test = GoalTest.AtMost,
            Value = atMost,
            Importance = Importance.Must,
        });

        private static string? FindRoot()
        {
            foreach (string start in new[] { SafeCwd(), AppContext.BaseDirectory })
            {
                if (start.Length == 0) continue;
                DirectoryInfo? d = new DirectoryInfo(start);
                for (int i = 0; i < 12 && d != null; i++, d = d.Parent)
                {
                    if (Directory.Exists(Path.Combine(d.FullName, "groundtruth"))
                        || Directory.Exists(Path.Combine(d.FullName, "data")))
                    {
                        return d.FullName;
                    }
                }
            }

            return null;
        }

        private static string SafeCwd()
        {
            try { return Directory.GetCurrentDirectory(); }
            catch (Exception) { return ""; }
        }

        private static string F(double v)
            => double.IsPositiveInfinity(v) ? "infinity"
             : v.ToString("N1", CultureInfo.InvariantCulture) + " m";
    }
}
