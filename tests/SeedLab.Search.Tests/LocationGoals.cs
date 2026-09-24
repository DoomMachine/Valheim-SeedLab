using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Feasibility;
using SeedLab.Search.Locations;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// The location half of the search engine: the ordered-prefix property it rests on, the early
    /// abort it uses, the T0 rings it refuses queries with, and the m_unique candidate semantics.
    ///
    /// <para>These are slow - every seed builds a 2048^2 biome-and-height point grid and then places
    /// locations - so the counts are small and deliberate. They are correctness checks, not a
    /// benchmark; <c>vseed search --dry-run</c> is the benchmark.</para>
    /// </summary>
    public static class LocationGoals
    {
        public static void Run(Action<bool, string, string> check, bool quick)
        {
            ILocationOracle oracle = SeedLab.LocationOracle.DumpedLocationOracle.Create(out string? problem);
            if (!oracle.Available)
            {
                check(false, "the location oracle opens",
                      problem ?? oracle.UnavailableReason);
                return;
            }

            check(true, "the location oracle opens", oracle.Provenance);

            PresetsAgainstTheRealTable(check, oracle);
            StaticRings(check, oracle);
            CandidateSemantics(check, oracle);
            PrefixIsSound(check, oracle, quick ? 2 : 4);
            GateIsExact(check, oracle, quick ? 8 : 24);
        }

        /// <summary>
        /// Every shipped preset, compiled against the REAL location table rather than the refusing
        /// oracle: nothing may be unavailable, and no <c>must</c> goal may be one T0 can prove no seed
        /// satisfies. <c>QueryChecks</c> runs the same loop with the unavailable oracle, which cannot
        /// see a location goal's rings at all - so a preset asking for "all seven bosses within 6 km"
        /// would pass there and be refused the moment a user ran it. This is the check that catches it.
        /// </summary>
        private static void PresetsAgainstTheRealTable(Action<bool, string, string> check, ILocationOracle oracle)
        {
            List<string> broken = new List<string>();
            int withLocations = 0;
            foreach (string name in Presets.Names)
            {
                try
                {
                    CompiledQuery c = CompiledQuery.Compile(Presets.Load(name), oracle);
                    if (c.Locations != null) withLocations++;
                    if (c.Unavailable.Count > 0)
                    {
                        broken.Add(name + ": " + c.Unavailable[0].Goal.Id + " is unavailable");
                    }

                    foreach (CompiledGoal g in c.Unsatisfiable)
                    {
                        if (g.Goal.Importance == Importance.Must)
                        {
                            broken.Add(name + ": no seed can satisfy '" + g.Goal.Id + "' - " + g.Unsatisfiable);
                        }
                    }
                }
                catch (Exception ex)
                {
                    broken.Add(name + ": " + ex.Message);
                }
            }

            check(broken.Count == 0, "every shipped preset compiles against the real location table",
                  Presets.Names.Count + " presets, " + withLocations + " place locations"
                  + (broken.Count > 0 ? "  <<< " + string.Join(" | ", broken) : ""));
        }

        // -----------------------------------------------------------------------------------------
        // T0: what the dumped ZoneLocation parameters make impossible for every seed
        // -----------------------------------------------------------------------------------------

        private static void StaticRings(Action<bool, string, string> check, ILocationOracle oracle)
        {
            // Hildir's camp has m_minDistance 3000, so filter 1 rejects any point nearer than that.
            Refused(check, oracle, "a goal inside a type's own m_minDistance ring",
                @"{""version"":1,""goals"":[{""id"":""g"",""target"":""location:Hildir_camp"",
                   ""metric"":""nearest_distance"",""test"":""near"",""value"":1000,""importance"":""must""}]}");

            // FaderLocation is AshLands-only, and IsAshlands cannot be true within 7,900 m of the
            // centre for any seed - so "every boss altar within 5 km" is impossible.
            Refused(check, oracle, "'every boss within 5 km' - Fader's biome cannot reach that far in",
                @"{""version"":1,""goals"":[{""id"":""g"",""target"":""group:bosses"",
                   ""metric"":""all_types_distance"",""test"":""near"",""value"":5000,""importance"":""must""}]}");

            // Crypt2/3/4 are m_quantity 200 each: 601 of them cannot exist.
            Refused(check, oracle, "a count above the sum of the types' m_quantity",
                @"{""version"":1,""goals"":[{""id"":""g"",""target"":""group:burial_chambers"",
                   ""metric"":""count"",""test"":""at_least"",""value"":601,""importance"":""must""}]}");

            // Nothing in the group can be inside 500 m, so a disc of 500 m holds none of it.
            Refused(check, oracle, "a count_within whose radius is inside every type's ring",
                @"{""version"":1,""goals"":[{""id"":""g"",""target"":""group:traders"",
                   ""metric"":""count_within"",""radius"":500,""test"":""at_least"",""value"":1,""importance"":""must""}]}");

            // ... and the ones that must NOT be refused.
            Allowed(check, oracle, "600 burial chambers is exactly the cap, so it is allowed to run",
                @"{""version"":1,""goals"":[{""id"":""g"",""target"":""group:burial_chambers"",
                   ""metric"":""count"",""test"":""at_least"",""value"":600,""importance"":""must""}]}");

            Allowed(check, oracle, "'keep the traders far away' is satisfiable - absence measures infinity",
                @"{""version"":1,""goals"":[{""id"":""g"",""target"":""group:traders"",
                   ""metric"":""nearest_distance"",""test"":""far"",""value"":9000,""importance"":""must""}]}");

            Allowed(check, oracle, "Eikthyr within 400 m is tight but reachable",
                @"{""version"":1,""goals"":[{""id"":""g"",""target"":""location:Eikthyrnir"",
                   ""metric"":""nearest_distance"",""test"":""near"",""value"":400,""importance"":""must""}]}");

            // An unknown prefab is a query error with a suggestion, not a silent zero.
            //
            // The probe used to be "Eikthyr", which is now a NAME the oracle resolves to Eikthyrnir -
            // this check would have gone on passing for the wrong reason the day names stopped
            // resolving, and failing for the right one the day they started. "Eikthyrnirr" is a
            // genuine non-name: not a prefab, not a display name, not an alias, not a fold of one.
            bool refusedUnknown = false;
            string message = "";
            try
            {
                CompiledQuery.Compile(QueryReader.Parse(
                    @"{""version"":1,""goals"":[{""id"":""g"",""target"":""location:Eikthyrnirr"",
                       ""metric"":""count"",""test"":""at_least"",""value"":1,""importance"":""must""}]}", "x"), oracle);
            }
            catch (QueryException ex)
            {
                refusedUnknown = true;
                message = ex.Message + (ex.Hint != null ? " | " + ex.Hint : "");
            }

            check(refusedUnknown, "an unknown location prefab is a query error with a suggestion", message);

            // ... and the positive half: the player-facing name compiles to the SAME plan as the
            // prefab. Not merely "does not throw" - the same last prefab and the same ordered index,
            // which is what decides the placement prefix and therefore the cost and the early abort.
            CompiledGoal byName = CompiledQuery.Compile(QueryReader.Parse(
                @"{""version"":1,""goals"":[{""id"":""g"",""target"":""location:Eikthyr"",
                   ""metric"":""count"",""test"":""at_least"",""value"":1,""importance"":""must""}]}",
                "x"), oracle).Goals[0];
            CompiledGoal byPrefab = CompiledQuery.Compile(QueryReader.Parse(
                @"{""version"":1,""goals"":[{""id"":""g"",""target"":""location:Eikthyrnir"",
                   ""metric"":""count"",""test"":""at_least"",""value"":1,""importance"":""must""}]}",
                "x"), oracle).Goals[0];

            check(byName.LastPrefabInOrder == byPrefab.LastPrefabInOrder
                  && byName.LastOrderedIndex == byPrefab.LastOrderedIndex
                  && byName.Prefabs.Count == 1 && byName.Prefabs[0] == "Eikthyrnir",
                  "'location:Eikthyr' compiles to exactly the plan 'location:Eikthyrnir' compiles to",
                  "name -> " + (byName.LastPrefabInOrder ?? "(none)") + " @" + byName.LastOrderedIndex
                  + ", prefab -> " + (byPrefab.LastPrefabInOrder ?? "(none)") + " @" + byPrefab.LastOrderedIndex);

            // 'from: spawn' on a grid metric would quietly answer from the world centre. Refused.
            bool refusedSpawn = false;
            string spawnMessage = "";
            try
            {
                CompiledQuery.Compile(QueryReader.Parse(
                    @"{""version"":1,""goals"":[{""id"":""g"",""target"":""biome:Swamp"",
                       ""metric"":""nearest_distance"",""from"":""spawn"",""test"":""near"",
                       ""value"":2000,""importance"":""must""}]}", "x"), oracle);
            }
            catch (QueryException ex)
            {
                refusedSpawn = true;
                spawnMessage = ex.Message;
            }

            check(refusedSpawn, "'from: spawn' on a grid metric is refused, not answered from the centre",
                  spawnMessage);

            // ... and it IS honoured on a location metric, where the origin is a placed position.
            CompiledQuery spawnOk = CompiledQuery.Compile(QueryReader.Parse(
                @"{""version"":1,""goals"":[{""id"":""g"",""target"":""group:burial_chambers"",
                   ""metric"":""nearest_distance"",""from"":""spawn"",""test"":""near"",
                   ""value"":800,""importance"":""must""}]}", "x"), oracle);
            check(spawnOk.Locations != null && spawnOk.Locations.NeedSpawn
                  && spawnOk.Locations.Prefabs.Contains("StartTemple"),
                  "  a 'from: spawn' location goal pulls StartTemple into the plan",
                  spawnOk.Locations != null
                      ? "prefix " + spawnOk.Locations.PrefixLength + ", prefabs "
                        + string.Join(", ", spawnOk.Locations.Prefabs)
                      : "no plan built");

            // The rings themselves, printed so a reader can check them against the table.
            LocationTypeInfo fader = oracle.TypeOf("FaderLocation")!;
            LocationTypeInfo eik = oracle.TypeOf("Eikthyrnir")!;
            check(LocationFeasibility.LowerBound(fader, 2) > 7000 && LocationFeasibility.UpperBound(eik, 2) <= 1000,
                  "the static rings match the table",
                  "FaderLocation >= " + F(LocationFeasibility.LowerBound(fader, 2))
                  + " m (AshLands band), Eikthyrnir <= " + F(LocationFeasibility.UpperBound(eik, 2))
                  + " m (m_maxDistance 1000)");
        }

        private static void Refused(Action<bool, string, string> check, ILocationOracle oracle,
                                    string name, string json)
        {
            CompiledQuery c = CompiledQuery.Compile(QueryReader.Parse(json, name), oracle);
            check(c.Unsatisfiable.Count == 1, "T0 locations: " + name,
                  c.Unsatisfiable.Count == 1 ? c.Unsatisfiable[0].Unsatisfiable ?? "" : "NOT CAUGHT");
        }

        private static void Allowed(Action<bool, string, string> check, ILocationOracle oracle,
                                    string name, string json)
        {
            CompiledQuery c = CompiledQuery.Compile(QueryReader.Parse(json, name), oracle);
            check(c.Unsatisfiable.Count == 0, "T0 locations: " + name,
                  c.Unsatisfiable.Count == 0 ? "compiles" : "WRONGLY REFUSED: " + c.Unsatisfiable[0].Unsatisfiable);
        }

        // -----------------------------------------------------------------------------------------
        // m_unique: the candidate set, and the three questions a query can ask about it
        // -----------------------------------------------------------------------------------------

        private static void CandidateSemantics(Action<bool, string, string> check, ILocationOracle oracle)
        {
            const int seed = 12345;
            Query q = QueryReader.Parse(@"{""version"":1,""search"":{""grid"":384},""goals"":[
                {""id"":""near"",""target"":""location:Vendor_BlackForest"",""metric"":""nearest_distance"",""test"":""near"",""value"":10500,""importance"":""nice""},
                {""id"":""all"",""target"":""location:Vendor_BlackForest"",""metric"":""all_candidates_distance"",""test"":""near"",""value"":10500,""importance"":""nice""},
                {""id"":""count"",""target"":""location:Vendor_BlackForest"",""metric"":""count"",""test"":""at_least"",""value"":1,""importance"":""nice""},
                {""id"":""types"",""target"":""group:traders"",""metric"":""types_within"",""radius"":10500,""test"":""at_least"",""value"":1,""importance"":""nice""}
            ]}", "unique");

            CompiledQuery cq = CompiledQuery.Compile(q, oracle, noPrefilter: true);
            SeedResult r = new SeedEvaluator(cq, oracle).Evaluate(seed, full: true);

            double near = Value(r, "near"), all = Value(r, "all"), count = Value(r, "count"), types = Value(r, "types");

            check(count == 10, "a m_unique trader contributes all ten CANDIDATES to count",
                  "Vendor_BlackForest count = " + count + " on seed " + seed
                  + " (m_quantity 10; the game keeps exactly one of them, chosen by exploration order)");

            check(near <= all && near > 0 && double.IsFinite(all),
                  "nearest_distance <= all_candidates_distance, and both are finite",
                  "nearest " + F(near) + " m, furthest candidate " + F(all) + " m");

            check(types == 3, "types_within counts the three trader TYPES, not their thirty candidates",
                  "types_within(10,500 m) = " + types);

            bool semantics = true;
            foreach (GoalOutcome g in r.Goals)
            {
                if (g.UniqueSemantics == null || g.UniqueSemantics.Length == 0) semantics = false;
            }

            check(semantics, "every goal on a m_unique target carries the semantics it used",
                  semantics ? "all four goals name their candidate-set semantics for 'vseed explain'"
                            : "a goal reported no semantics, so a report could imply a position the seed does not decide");

            // The one number this suite pins to the placement engine itself: the same value the
            // location gate in tools\SeedLab.LocationLab reproduces from the game's own dumps.
            check(Math.Abs(near - 2464.1) < 0.1,
                  "the measured distance agrees with the placement engine's own output",
                  "seed 12345 nearest Vendor_BlackForest candidate = " + F(near)
                  + " m (expected 2464.1 m)");
        }

        private static double Value(SeedResult r, string id)
        {
            foreach (GoalOutcome g in r.Goals)
            {
                if (g.Id == id) return g.Value;
            }

            return double.NaN;
        }

        // -----------------------------------------------------------------------------------------
        // The ordered-prefix property, checked against the real vanilla table
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// <b>The claim the whole location tier rests on.</b> Running only the first
        /// <c>OrderedIndexOf(target) + 1</c> entries must give the target type exactly the instances
        /// the full 183-entry run gives it - same count, same float32 coordinates, same order. If that
        /// were false, every boss search would be fast and wrong.
        /// </summary>
        private static void PrefixIsSound(Action<bool, string, string> check, ILocationOracle oracle, int seeds)
        {
            // GoblinCamp2 is in the list on purpose: two enabled alt-biomes ("Lox Plains" and "Death
            // Plains") carry it in m_blockLocationNames, so its placement depends on the per-world
            // alt-biome assignment that the oracle's thread-local runtime objects are RESET for on
            // every seed. If that reuse leaked state between seeds, this type would drift and the
            // coordinate comparison below would catch it.
            string[] targets = { "Eikthyrnir", "Vendor_BlackForest", "FaderLocation", "Crypt2", "GoblinCamp2" };
            LocationPlan shortPlan = oracle.Plan(targets, needSpawn: false);

            // The reference run has to ASK for the same prefabs - the oracle only ever copies out the
            // ones its plan names - and it is dragged to the full 183 entries by TarPit3_1, which is
            // the last entry of the ordered list.
            List<string> everything = new List<string>(targets) { "TarPit3_1" };
            LocationPlan whole = oracle.Plan(everything, needSpawn: false);

            check(shortPlan.PrefixLength == 38 && whole.PrefixLength == whole.OrderedCount,
                  "the plan takes the maximum of the per-type prefixes",
                  "{" + string.Join(", ", targets) + "} needs " + shortPlan.PrefixLength + " of "
                  + shortPlan.OrderedCount + " entries (GoblinCamp2 is ordered 37); adding TarPit3_1 "
                  + "needs all " + whole.PrefixLength);

            int mismatches = 0;
            string first = "";
            int compared = 0;
            foreach (int seed in new[] { 12345, 777, -5, 918273645, 42, 99991 })
            {
                if (compared >= seeds) break;
                compared++;

                LocationWorld shortRun = oracle.Run(shortPlan, seed, 2, null);
                LocationWorld fullRun = oracle.Run(whole, seed, 2, null);

                foreach (string t in targets)
                {
                    List<string> a = Coords(shortRun, t), b = Coords(fullRun, t);
                    if (a.Count == b.Count)
                    {
                        bool same = true;
                        for (int i = 0; i < a.Count; i++)
                        {
                            if (a[i] != b[i]) same = false;
                        }

                        if (same) continue;
                    }

                    mismatches++;
                    if (first.Length == 0)
                    {
                        first = "seed " + seed + " " + t + ": prefix gave " + a.Count
                                + ", full run gave " + b.Count;
                    }
                }
            }

            check(mismatches == 0,
                  "the ordered prefix reproduces the full run exactly, coordinate for coordinate",
                  compared + " seeds x " + targets.Length + " types, prefix "
                  + shortPlan.PrefixLength + " vs " + whole.PrefixLength + " entries, "
                  + mismatches + " mismatches" + (first.Length > 0 ? "  <<< " + first : ""));
        }

        private static List<string> Coords(LocationWorld w, string prefab)
        {
            List<string> l = new List<string>();
            foreach (LocationHit h in w.Hits)
            {
                if (!string.Equals(h.Prefab, prefab, StringComparison.Ordinal)) continue;
                l.Add(BitConverter.SingleToInt32Bits(h.X).ToString("X8") + ":"
                      + BitConverter.SingleToInt32Bits(h.Z).ToString("X8"));
            }

            return l;
        }

        // -----------------------------------------------------------------------------------------
        // The early abort
        // -----------------------------------------------------------------------------------------

        /// <summary>
        /// The location gate stops the placement the moment a must-goal's own last prefab has finished
        /// and failed. It is EXACT - an entry's instances are final once its loop returns - so a gated
        /// run and an ungated run must agree on every seed of a fixed range, and the gate must
        /// actually fire, or the check proves nothing.
        /// </summary>
        private static void GateIsExact(Action<bool, string, string> check, ILocationOracle oracle, int seeds)
        {
            const string json = @"{""version"":1,""search"":{""grid"":384},""goals"":[
                {""id"":""eik"",""target"":""location:Eikthyrnir"",""metric"":""nearest_distance"",""test"":""near"",""value"":320,""importance"":""must""},
                {""id"":""chambers"",""target"":""group:burial_chambers"",""metric"":""count_within"",""radius"":2000,""test"":""at_least"",""value"":8,""importance"":""must""}
            ]}";

            CompiledQuery gated = CompiledQuery.Compile(QueryReader.Parse(json, "gate"), oracle, noPrefilter: false);
            CompiledQuery audit = CompiledQuery.Compile(QueryReader.Parse(json, "gate"), oracle, noPrefilter: true);
            SeedEvaluator evGated = new SeedEvaluator(gated, oracle);
            SeedEvaluator evAudit = new SeedEvaluator(audit, oracle);

            List<int> a = new List<int>(), b = new List<int>();
            int disagree = 0;
            string firstBad = "";
            for (int i = 0; i < seeds; i++)
            {
                int seed = 7_000_000 + i;
                SeedResult ra = evGated.Evaluate(seed);
                SeedResult rb = evAudit.Evaluate(seed, full: true);
                if (ra.Pass) a.Add(seed);
                if (rb.Pass) b.Add(seed);
                if (ra.Pass != rb.Pass && disagree++ == 0)
                {
                    firstBad = "seed " + seed + ": gated=" + ra.Pass + " audit=" + rb.Pass
                               + " (gated failed on '" + (ra.FailedGoal ?? "-") + "')";
                }
            }

            check(disagree == 0, "the location gate never changes a verdict",
                  seeds + " seeds [7,000,000 ..], " + a.Count + " hits gated, " + b.Count + " hits audited, "
                  + disagree + " disagreements" + (firstBad.Length > 0 ? "  <<< " + firstBad : ""));

            check(evGated.LocationAborts > 0,
                  "  ... and it actually fired, so the check is not vacuous",
                  evGated.LocationAborts + " of " + evGated.Placed
                  + " placements stopped after Eikthyrnir instead of running on to Crypt4 (ordered 66)");
        }

        private static string F(double v)
            => double.IsPositiveInfinity(v) ? "infinity" : v.ToString("F1", CultureInfo.InvariantCulture);
    }
}
