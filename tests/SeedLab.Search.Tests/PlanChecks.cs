using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Execution;
using SeedLab.Search.Locations;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// The plan and its grid warnings say only true things, and each true thing once.
    ///
    /// <para><b>What went wrong, and why these checks exist.</b> Until 2026-09-24 a query whose goals
    /// were ALL location goals - boss-rush, dungeon-delver, any hand-written boss or dungeon query at
    /// a coarse grid - was told two false things: "grid = 384 m is coarser than G96 ... a must-have at
    /// this grid is not a filter", and "every metric in this run is DEFINED on that grid and is not
    /// comparable with a G12 result". Location placement never reads the query's grid: it builds its
    /// own 2048 x 2048 @ 12 m biome-point grid whatever the query says, and explain at G384 and at G12
    /// gives bit-identical distances and counts. The first warning counted every must-have, and the
    /// second never looked at a goal at all. The same defects hid a true warning (a bulk must-have
    /// beside a NICE fine-only goal got no G96 warning), invented one for river counts, dropped the
    /// only "not comparable" sentence at G10, and printed every compiled warning twice in explain.</para>
    ///
    /// <para><b>How.</b> Every case goes through <see cref="SearchSession.Create"/> - the path the CLI,
    /// explain and the web page share - with the REAL dumped location table. With the unavailable
    /// oracle every location goal is <c>Available = false</c> and the location checks would pass for
    /// the wrong reason, so they fail loudly instead when the table is missing; the checks with no
    /// location goal still run. Assertions match substrings that occur in exactly one warning each:
    /// "coarser than G96" (the G96 warning), "on that grid" (the run-level grid warning, old wording
    /// and new), "which is NOT comparable" (the per-goal warning), "is a MUST-HAVE on" (the unsafe
    /// must-have warning). "that grid's number" is deliberately NOT used: three different sentences
    /// contain it.</para>
    ///
    /// <para>Run against the library as it was before this change, every check here fails except the
    /// ones marked GUARD, which passed then and must keep passing (checked 2026-09-24: 40 of the 59
    /// checks that compile against that library failed, and the 19 that passed are the 18 GUARDs and
    /// the data-availability check; the ladder and explain-list checks use code that did not exist
    /// yet). A review of the first cut added eleven more - the records' side metrics when no goal is
    /// left to name, the wall-budget seed clause, the raise note's order, and the ladder's reasons -
    /// and ten of them failed against that cut; the eleventh is a GUARD (a sequential run with a
    /// wall budget gets no seed clause).</para>
    /// </summary>
    public static class PlanChecks
    {
        private const string Engine = "tests";

        private const string W1 = "coarser than G96";
        private const string W2 = "on that grid";
        private const string W3 = "which is NOT comparable";
        private const string UnsafeMust = "is a MUST-HAVE on";
        private const string SideMetrics = "side metrics (land_km2, ocean_share, highest_peak_m)";
        private const string SideMetricsOwnLine = "has its own warning, but the records' side metrics";
        private const string NoteB = "every goal in this query is answered by location placement";
        private const string SeedClause = "different seeds";

        public static void Run(Action<bool, string, string> check)
        {
            ILocationOracle oracle = SeedLab.LocationOracle.DumpedLocationOracle.Create(out string? problem);

            // ---- no location goal: these run whatever the data folder holds --------------------------
            BulkMusts(check, oracle);
            RiverCounts(check, oracle);
            AreaAboveHeight(check, oracle);
            FineOnlyMusts(check, oracle);
            FinerThanTheGame(check, oracle);
            LadderWithoutLocations(check, oracle);

            // ---- location goals: meaningless without the dumped table -------------------------------
            check(oracle.Available, "the dumped location table is available for the location plan checks",
                  oracle.Available ? "" : (problem ?? "unavailable") + " - every location check below is SKIPPED, not passed");
            if (!oracle.Available) return;

            LocationOnly(check, oracle);
            PlacementNoteSeedClause(check, oracle);
            AllTraders(check, oracle);
            MixedPresets(check, oracle);
            RaiseCarriesItsNotes(check, oracle);
            ExplainPrintsEachWarningOnce(check, oracle);
            LadderWithLocations(check, oracle);
        }

        // =========================================================================================
        // Bulk must-haves: the G96 warning is TRUE for them, so it must survive, and name them.

        private static void BulkMusts(Action<bool, string, string> check, ILocationOracle oracle)
        {
            const string goals =
                @"{""id"":""meadows"",""target"":""biome:Meadows"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":1000000,""importance"":""must""},
                  {""id"":""swamp-near"",""target"":""biome:Swamp"",""metric"":""nearest_distance"",""test"":""near"",""value"":2500,""importance"":""nice""}";

            // screen: auto is where the NICE fine-only goal silenced the true G96 warning.
            foreach (string screen in new[] { "auto", "off" })
            {
                SearchSession s = Session(Query("t6-" + screen, "\"grid\":384,\"screen\":\"" + screen + "\"", goals), oracle);
                string label = "a bulk must-have beside a NICE fine-only goal at G384, screen " + screen;
                check(Count(s, W1) == 1 && SameSet(Ids(s, W1, W1Names), "meadows"),
                      label + ": the G96 warning fires and names only the bulk must-have",
                      Show(s, W1));
                check(Count(s, W2) == 1 && SameSet(Ids(s, W2, W2Names), "meadows"),
                      label + ": the grid warning names the grid-measured goal the per-goal warning does not",
                      Show(s, W2));
                check(Count(s, W3) == 1 && Line(s, W3)!.Contains("'swamp-near'", StringComparison.Ordinal),
                      label + ": the per-goal NOT-comparable warning still names the nearest-biome goal (GUARD)",
                      Show(s, W3));
            }

            const string bulkOnly =
                @"{""id"":""meadows"",""target"":""biome:Meadows"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":1000000,""importance"":""must""}";
            foreach (string extra in new[] { "", ",\"screen\":\"off\"", ",\"screen_grid\":512" })
            {
                SearchSession s = Session(Query("t7", "\"grid\":384" + extra, bulkOnly), oracle);
                string label = "a lone bulk must-have at G384" + (extra.Length > 0 ? " (" + extra.Trim(',') + ")" : "");
                check(Count(s, W1) == 1 && SameSet(Ids(s, W1, W1Names), "meadows")
                      && Count(s, W2) == 1 && SameSet(Ids(s, W2, W2Names), "meadows"),
                      label + ": still gets the G96 warning and the grid warning, both naming it"
                      + (extra.Contains("screen_grid", StringComparison.Ordinal)
                          ? " - a G512 screen clamps to G96 and re-measures at G384, so the verdicts are G384's"
                          : " - narrowed to the goals it is about, not deleted"),
                      "plan: " + s.Grid.Describe() + " | " + Show(s, W1));
            }

            // The G96 warning's retired clause claimed the number is "not an approximation" - the
            // run's own records carry a measured relative error against G12 for a bulk metric.
            SearchSession one = Session(Query("t7", "\"grid\":384", bulkOnly), oracle);
            check(Line(one, W1) is string w1 && !w1.Contains("not an approximation", StringComparison.Ordinal)
                  && w1.Contains("goal 'meadows' is not a filter at this grid: its number is that grid's number",
                                 StringComparison.Ordinal),
                  "the G96 warning names its goal in the singular and drops the 'not an approximation' clause",
                  Line(one, W1) ?? "(none)");
        }

        // =========================================================================================
        // River, lake and stream counts come from the generator's own lists, not from the grid.

        private static void RiverCounts(Action<bool, string, string> check, ILocationOracle oracle)
        {
            const string rivers =
                @"{""id"":""rivers"",""target"":""world:river_count"",""metric"":""river_count"",""test"":""at_least"",""value"":5,""importance"":""must""}";
            foreach (string screen in new[] { "auto", "off" })
            {
                SearchSession s = Session(Query("t8-" + screen, "\"grid\":384,\"screen\":\"" + screen + "\"", rivers), oracle);
                string label = "a river-count must-have at G384, screen " + screen;
                check(Count(s, W1) == 0, label + ": no G96 warning (the count is not a grid measurement)", Show(s, W1));
                check(Count(s, W2) == 0 && Count(s, SideMetrics) == 1,
                      label + ": no grid-goal warning, but the side-metrics one - the record's land, ocean and peak ARE G384's",
                      Show(s, "grid = "));
                check(!PlanHas(s, NoteB),
                      label + ": no placement note (a river count is not placement) (GUARD)",
                      PlanLine(s, NoteB) ?? "none");
            }

            SearchSession gap = Session(Query("gap-river-nicebiome", "\"grid\":384",
                rivers + @",{""id"":""bf"",""target"":""biome:BlackForest"",""metric"":""nearest_distance"",""test"":""near"",""value"":1000,""importance"":""nice""}"), oracle);
            check(Count(gap, W3) == 1 && Count(gap, W2) == 0 && Count(gap, SideMetricsOwnLine) == 1
                  && Line(gap, SideMetricsOwnLine)!.Contains("the goal measured on it has its own warning", StringComparison.Ordinal),
                  "a river must-have beside a nice nearest-biome goal at G384: the per-goal warning, and the side metrics on their own line",
                  Show(gap, "grid = ") + Show(gap, W3));

            SearchSession mixed = Session(Query("river-nice", "\"grid\":384",
                rivers + @",{""id"":""lakes"",""target"":""world:lake_count"",""metric"":""lake_count"",""test"":""at_least"",""value"":5,""importance"":""nice""}"), oracle);
            check(Count(mixed, W1) == 0 && Count(mixed, W2) == 0 && Count(mixed, SideMetrics) == 1,
                  "a river must-have and a lake nice goal at G384: only the side-metrics warning",
                  Show(mixed, "grid = "));
        }

        // =========================================================================================
        // area_above_height: fine-only, but comparable across grids, so no per-goal warning covers it.

        private static void AreaAboveHeight(Action<bool, string, string> check, ILocationOracle oracle)
        {
            foreach ((string target, string id) in new[] { ("world:high", "high-ground"), ("biome:Mountain", "high-mountain") })
            {
                SearchSession s = Session(Query("aah", "\"grid\":384,\"screen\":\"off\",\"keep\":50",
                    "{\"id\":\"" + id + "\",\"target\":\"" + target + "\",\"metric\":\"area_above_height\",\"height\":200,"
                    + "\"test\":\"at_least\",\"value\":5000000,\"importance\":\"must\"},"
                    + @"{""id"":""meadows"",""target"":""biome:Meadows"",""metric"":""area"",""test"":""at_least"",""value"":1,""importance"":""nice""}"), oracle);
                string label = target.Substring(0, target.IndexOf(':')) + " area_above_height must-have at G384, screen off";
                check(Count(s, W1) == 0 && Count(s, W3) == 0,
                      label + ": no G96 warning (it is not a counting metric) and no NOT-comparable one (it is comparable)",
                      Show(s, W1) + Show(s, W3));
                check(Count(s, UnsafeMust) == 1 && Line(s, UnsafeMust)!.Contains("'" + id + "'", StringComparison.Ordinal)
                      && Line(s, UnsafeMust)!.Contains(". Screen is off, so nothing is raised", StringComparison.Ordinal),
                      label + ": the unsafe-must-have warning fires under screen: off, names it and says nothing is raised",
                      Show(s, UnsafeMust));
                check(s.Grid.RaisedFrom == 0 && s.Query.Search.Grid == 384.0 && s.Preflight.Confirmations.Count == 0,
                      label + ": and nothing IS raised or asked for (GUARD)",
                      "raised from " + s.Grid.RaisedFrom + ", grid " + s.Query.Search.Grid);
                check(Count(s, W2) == 1 && SameSet(Ids(s, W2, W2Names), id, "meadows"),
                      label + ": the grid warning names both grid-measured goals",
                      Show(s, W2));
                check(Count(s, SideMetrics) == 1 && Line(s, W2) is string w2
                      && w2.Contains("and so are the records' " + SideMetrics, StringComparison.Ordinal)
                      && w2.Contains("highest_peak_m is not comparable across grids", StringComparison.Ordinal),
                      label + ": and the same warning says the records' side metrics are G384's, the peak not comparable",
                      Show(s, "side metrics"));
            }
        }

        // =========================================================================================
        // Fine-only must-haves: the raise must still happen, and screen: off must not double-warn.

        private static void FineOnlyMusts(Action<bool, string, string> check, ILocationOracle oracle)
        {
            const string home =
                @"{""id"":""home"",""target"":""world:spawn_island_area"",""metric"":""spawn_island_area"",""test"":""at_least"",""value"":1000000,""importance"":""must""}";

            SearchSession off = Session(Query("sia384off", "\"grid\":384,\"screen\":\"off\"", home), oracle);
            check(Count(off, W3) == 1 && Count(off, W1) == 0 && Count(off, W2) == 0 && Count(off, UnsafeMust) == 0,
                  "spawn_island_area must-have at G384, screen off: the per-goal NOT-comparable warning, no goal-naming one",
                  Show(off, "grid") + Show(off, UnsafeMust) + Show(off, W3));
            check(Count(off, SideMetricsOwnLine) == 1
                  && Line(off, SideMetricsOwnLine)!.Contains("(land_km2, largest_island_km2, ocean_share, highest_peak_m)", StringComparison.Ordinal)
                  && Line(off, SideMetricsOwnLine)!.Contains("largest_island_km2 and highest_peak_m are not comparable", StringComparison.Ordinal),
                  "spawn_island_area must-have at G384, screen off: the records' side metrics, island and peak included, still get their sentence",
                  Show(off, "side metrics"));

            // GUARD: the unsafe-must-have set that drives the raise is the FULL set. Filtering it by
            // the per-goal warning would switch the raise off for every island must-have.
            SearchSession raised = Session(Query("sia96auto", "\"grid\":96", home), oracle);
            check(raised.Grid.RaisedFrom == 96.0 && raised.Query.Search.Grid == 12.0
                  && raised.Preflight.Confirmations.Count == 1,
                  "spawn_island_area must-have at G96, screen auto: still raised to G12, with one confirmation (GUARD)",
                  "raised from " + raised.Grid.RaisedFrom + ", grid " + raised.Query.Search.Grid + ", "
                  + raised.Preflight.Confirmations.Count + " confirmation(s)");
            check(PlanHas(raised, "grid raised from G96"),
                  "the raise's own note reaches the plan block, not only Grid.Notes",
                  PlanLine(raised, "grid raised") ?? "not in the plan");
            check(raised.Grid.Notes.Count > 0 && raised.Grid.Notes[0].StartsWith("grid raised from G96", StringComparison.Ordinal),
                  "and it leads Grid.Notes (what explain prints), in the plan block's order",
                  raised.Grid.Notes.Count > 0 ? raised.Grid.Notes[0] : "(no notes)");

            SearchSession islands = Session(Query("island384", "\"grid\":384",
                @"{""id"":""islands"",""target"":""world:islands"",""metric"":""island_count"",""test"":""at_least"",""value"":5,""importance"":""must""},
                  {""id"":""meadows"",""target"":""biome:Meadows"",""metric"":""area"",""test"":""at_least"",""value"":1,""importance"":""nice""}"), oracle);
            check(islands.Grid.RaisedFrom == 384.0 && islands.Query.Search.Grid == 12.0,
                  "island_count must-have at G384, screen auto: still raised to G12 (GUARD)",
                  "raised from " + islands.Grid.RaisedFrom + ", grid " + islands.Query.Search.Grid);

            Query lc = Presets.Load("large-continents");
            SearchSession continents = SearchSession.Create(lc, oracle, Engine, 512, 8, outPath: null,
                                                            acceptScanOrder: true, allowVacuous: true);
            check(continents.Grid.RaisedFrom == 24.0 && continents.Query.Search.Grid == 12.0,
                  "large-continents as shipped: still raised from G24 to G12 (GUARD)",
                  "raised from " + continents.Grid.RaisedFrom + ", grid " + continents.Query.Search.Grid);
        }

        // =========================================================================================
        // A grid FINER than the game's is still not the game's grid.

        private static void FinerThanTheGame(Action<bool, string, string> check, ILocationOracle oracle)
        {
            SearchSession s = Session(Query("g10", "\"grid\":10,\"keep\":50",
                @"{""id"":""islands"",""target"":""world:islands"",""metric"":""island_count"",""test"":""at_least"",""value"":5,""importance"":""must""},
                  {""id"":""meadows"",""target"":""biome:Meadows"",""metric"":""area"",""test"":""at_least"",""value"":1,""importance"":""nice""}"), oracle);
            check(Count(s, W3) == 1 && Line(s, W3)!.Contains("'islands'", StringComparison.Ordinal),
                  "island_count at G10: the per-goal NOT-comparable warning fires on a grid finer than the game's",
                  Show(s, W3));
            check(Count(s, W2) == 1 && SameSet(Ids(s, W2, W2Names), "meadows"),
                  "island_count at G10: the grid warning names only the goal the per-goal one does not",
                  Show(s, W2));
            check(s.Grid.RaisedFrom == 0,
                  "island_count at G10: nothing is 'raised' to the coarser G12 (GUARD)",
                  "raised from " + s.Grid.RaisedFrom);
        }

        // =========================================================================================
        // Location-only queries: no grid warning of any kind, and the placement note instead.

        private static void LocationOnly(Action<bool, string, string> check, ILocationOracle oracle)
        {
            const string t1Goals =
                @"{""id"":""eik"",""target"":""location:Eikthyrnir"",""metric"":""nearest_distance"",""test"":""near"",""value"":320,""importance"":""must""},
                  {""id"":""chambers"",""target"":""group:burial_chambers"",""metric"":""count_within"",""radius"":2000,""test"":""at_least"",""value"":8,""importance"":""must""},
                  {""id"":""spawn"",""target"":""group:burial_chambers"",""metric"":""nearest_distance"",""from"":""spawn"",""test"":""near"",""value"":800,""importance"":""must""}";

            foreach (string screen in new[] { "auto", "off" })
            {
                SearchSession s = Session(Query("t1-" + screen, "\"grid\":384,\"screen\":\"" + screen + "\"", t1Goals), oracle);
                string label = "location, group and from-spawn must-haves at G384, screen " + screen;
                check(Count(s, W1) == 0, label + ": no G96 warning", Show(s, W1));
                check(Count(s, W2) == 0 && Count(s, SideMetrics) == 0, label + ": no grid warning", Show(s, "grid = "));
                check(PlanHas(s, NoteB) && !PlanHas(s, "side metrics"),
                      label + ": the placement note is in the plan, without the false side-metrics clause",
                      PlanLine(s, NoteB) ?? "not in the plan");
            }

            foreach (string preset in new[] { "boss-rush", "dungeon-delver" })
            {
                SearchSession s = PresetSession(preset, oracle, null);
                check(Count(s, W1) == 0 && Count(s, W2) == 0 && PlanHas(s, NoteB),
                      preset + " as shipped (G" + s.Query.Search.Grid + "): no G96 warning, no grid warning, the placement note",
                      Show(s, W1) + Show(s, W2));
            }

            SearchSession axe = PresetSession("axe-heads", oracle, 384.0);
            check(Count(axe, W1) == 0 && Count(axe, W2) == 0 && PlanHas(axe, NoteB),
                  "axe-heads forced to G384: no G96 warning, no grid warning, the placement note",
                  Show(axe, W1) + Show(axe, W2));
        }

        /// <summary>
        /// "Another grid visits different seeds" is true only for a shuffled partial run whose key
        /// comes from the hash - not for a sequential run, an explicit key or the whole range.
        /// </summary>
        private static void PlacementNoteSeedClause(Action<bool, string, string> check, ILocationOracle oracle)
        {
            const string eik =
                @"{""id"":""eik"",""target"":""location:Eikthyrnir"",""metric"":""nearest_distance"",""test"":""near"",""value"":320,""importance"":""must""}";
            (string search, bool seeds, string what)[] cases =
            {
                ("\"grid\":384", true, "shuffled, no key, 512 of 2^32 seeds"),
                ("\"grid\":384,\"order\":\"sequential\"", false, "sequential order"),
                ("\"grid\":384,\"key\":\"0x1234\"", false, "an explicit search.key"),
                ("\"grid\":384,\"range\":[0,99]", false, "the whole of a 100-seed range"),
                ("\"grid\":384,\"range\":[0,99],\"budget\":{\"wall\":\"10m\"}", true,
                 "the whole of a 100-seed range, but a budget.wall can stop it part-way"),
                ("\"grid\":384,\"range\":[0,99],\"order\":\"sequential\",\"budget\":{\"wall\":\"10m\"}", false,
                 "a budget.wall on a sequential run"),
            };

            foreach ((string search, bool seeds, string what) in cases)
            {
                SearchSession s = Session(Query("seed-clause", search, eik), oracle);
                string? line = PlanLine(s, NoteB);
                check(line != null && line.Contains(SeedClause, StringComparison.Ordinal) == seeds,
                      "placement note, " + what + ": " + (seeds ? "says" : "does not say") + " another grid visits different seeds" + (seeds ? "" : " (GUARD)"),
                      line ?? "no placement note");
            }
        }

        // =========================================================================================
        // all-traders: three exact trader goals and one nice Black Forest distance at G384.

        private static void AllTraders(Action<bool, string, string> check, ILocationOracle oracle)
        {
            SearchSession s = PresetSession("all-traders", oracle, null);
            check(Count(s, W1) == 0, "all-traders: no G96 warning (GUARD)", Show(s, W1));
            check(Count(s, W3) == 1 && Line(s, W3)!.Contains("'blackforest-near'", StringComparison.Ordinal),
                  "all-traders: exactly one per-goal NOT-comparable warning, naming blackforest-near (GUARD)",
                  Show(s, W3));
            check(Count(s, W2) == 0 && Count(s, "side metrics") == 0,
                  "all-traders: no run-level grid warning - its one grid-measured goal already has its own",
                  Show(s, "grid = "));
            check(PlanHas(s, "goal 'blackforest-near' is one no coarse grid measures safely")
                  && !PlanHas(s, "the number it reports"),
                  "all-traders: the plan's coarse-grid note names blackforest-near instead of 'the number it reports'",
                  PlanLine(s, "no coarse grid measures safely") ?? "none");
            check(!PlanHas(s, NoteB), "all-traders: no placement note (it has a biome goal) (GUARD)", PlanLine(s, NoteB) ?? "none");

            SearchSession off = PresetSession("all-traders", oracle, null, "off");
            check(Count(off, W1) == 0 && Count(off, UnsafeMust) == 0 && Count(off, W3) == 1,
                  "all-traders under screen: off: still no G96 warning, and no unsafe-must-have one (the fine goal is nice)",
                  Show(off, W1) + Show(off, UnsafeMust));
        }

        // =========================================================================================
        // Mixed presets: the grid warning names exactly the grid-measured goals.

        private static void MixedPresets(Action<bool, string, string> check, ILocationOracle oracle)
        {
            SearchSession bal = PresetSession("balanced", oracle, null);
            check(Count(bal, W2) == 1 && SameSet(Ids(bal, W2, W2Names),
                      "meadows", "blackforest", "swamp", "mountain", "plains", "mistlands", "deepnorth", "ocean"),
                  "balanced (G192): the grid warning names exactly the eight biome-area goals",
                  Show(bal, W2));
            check(Count(bal, W1) == 0 && Count(bal, W3) == 3,
                  "balanced: no G96 warning, three per-goal warnings (home-island, big-home, swamp-near) (GUARD)",
                  Count(bal, W1) + " G96, " + Count(bal, W3) + " per-goal");

            SearchSession iron = PresetSession("iron-rich", oracle, null);
            check(Count(iron, W2) == 1 && SameSet(Ids(iron, W2, W2Names), "swamp-area"),
                  "iron-rich (G24): the grid warning names only swamp-area, not the crypt goals",
                  Show(iron, W2));

            SearchSession compact = PresetSession("compact-progression", oracle, null);
            check(Count(compact, W1) == 0 && Count(compact, W2) == 0 && Count(compact, W3) == 2,
                  "compact-progression (G384): two per-goal warnings and nothing else about the grid",
                  Show(compact, "grid = ") + Count(compact, W3) + " per-goal");
        }

        // =========================================================================================
        // The grid raise carries its notes into the plan and keeps the name notes.

        private static void RaiseCarriesItsNotes(Action<bool, string, string> check, ILocationOracle oracle)
        {
            SearchSession s = Session(Query("raise-name", "\"grid\":96",
                @"{""id"":""home"",""target"":""world:spawn_island_area"",""metric"":""spawn_island_area"",""test"":""at_least"",""value"":1000000,""importance"":""must""},
                  {""id"":""elder"",""target"":""location:The Elder"",""metric"":""nearest_distance"",""test"":""near"",""value"":4000,""importance"":""nice""}"), oracle);
            check(s.Grid.RaisedFrom == 96.0 && PlanHas(s, "grid raised from G96"),
                  "a raised query with a typed location name: the raise note is in the plan block",
                  PlanLine(s, "grid raised") ?? "not in the plan");
            check(Count(s, "is the display name of GDKing") == 1,
                  "and the name note survives the raise, once (GUARD: the search kept it; explain's rebuild lost it)",
                  Show(s, "display name"));
        }

        // =========================================================================================
        // explain prints SearchPreflight.WarningsForOneSeed() and nothing else.

        private static void ExplainPrintsEachWarningOnce(Action<bool, string, string> check, ILocationOracle oracle)
        {
            Query q = Presets.Load("all-traders");
            SearchSession s = SearchSession.Create(q, oracle, Engine, 1, 1, outPath: null, noPrefilter: true,
                                                   acceptScanOrder: true);
            List<string> shown = s.Preflight.WarningsForOneSeed();
            int w3 = 0, block = 0;
            foreach (string w in shown)
            {
                if (w.Contains(W3, StringComparison.Ordinal)) w3++;
                if (w.Contains("--block-size", StringComparison.Ordinal)) block++;
            }

            check(w3 == 1 && block == 0,
                  "explain's warning list for all-traders holds the per-goal warning once and no scan-only advice",
                  w3 + " per-goal, " + block + " block-size, of " + shown.Count);
        }

        // =========================================================================================
        // The web page's ladder, from the shared code.

        private static void LadderWithoutLocations(Action<bool, string, string> check, ILocationOracle oracle)
        {
            SearchSession rivers = Session(Query("t8", "\"grid\":384",
                @"{""id"":""rivers"",""target"":""world:river_count"",""metric"":""river_count"",""test"":""at_least"",""value"":5,""importance"":""must""}"), oracle);
            GridLadder lr = GridLadder.For(rivers.Compiled);
            GridRungVerdict r384 = lr.Rung(384);
            check(!lr.OnlyPlacement && r384.Safe && (r384.Why ?? "").Contains("the counts do not", StringComparison.Ordinal)
                  && lr.TableNote.Contains("The cost does vary", StringComparison.Ordinal),
                  "ladder, river-count must-have: G384 is not 'no longer a filter', and the cost is said to vary",
                  r384.Why ?? "(no reason)");
            check((r384.Why ?? "").Contains("gives each seed the same must-have verdict", StringComparison.Ordinal)
                  && !(r384.Why ?? "").Contains("decides the same seeds", StringComparison.Ordinal)
                  && !lr.TableNote.Contains("location placement", StringComparison.Ordinal),
                  "ladder, river-count must-have: 'the same verdict per seed' (not 'the same seeds'), and no word about placement it does not have",
                  r384.Why + " | " + lr.TableNote);

            GridLadder nice = GridLadder.For(Session(Query("niceonly", "\"grid\":384",
                @"{""id"":""bf"",""target"":""biome:BlackForest"",""metric"":""nearest_distance"",""test"":""near"",""value"":1000,""importance"":""nice""}"), oracle).Compiled);
            check(!nice.TableNote.Contains("must-haves", StringComparison.Ordinal)
                  && nice.TableNote.Contains("no must-have goal", StringComparison.Ordinal)
                  && (nice.Rung(384).Why ?? "").Contains("no must-have goal", StringComparison.Ordinal),
                  "ladder, a nice-only query: says it has no must-have, instead of talking about its must-haves",
                  nice.TableNote);

            SearchSession bulk = Session(Query("t6", "\"grid\":384",
                @"{""id"":""meadows"",""target"":""biome:Meadows"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":1000000,""importance"":""must""},
                  {""id"":""swamp-near"",""target"":""biome:Swamp"",""metric"":""nearest_distance"",""test"":""near"",""value"":2500,""importance"":""nice""}"), oracle);
            GridLadder lb = GridLadder.For(bulk.Compiled);
            GridRungVerdict b384 = lb.Rung(384), b96 = lb.Rung(96);
            check(!b384.Safe && (b384.Why ?? "").Contains(W1, StringComparison.Ordinal)
                  && (b384.Why ?? "").Contains("'meadows'", StringComparison.Ordinal)
                  && !(b384.Why ?? "").Contains("raises", StringComparison.Ordinal) && b96.Safe,
                  "ladder, bulk must-have beside a nice fine-only goal: G384 is past G96 for 'meadows', and nothing 'raises'",
                  b384.Why ?? "(no reason)");
            GridRungVerdict b12 = lb.Rung(12);
            check((b96.Why ?? "").Contains("'meadows' is a counting metric", StringComparison.Ordinal)
                  && (b96.Why ?? "").Contains("still a filter here", StringComparison.Ordinal)
                  && !(b96.Why ?? "").Contains("re-measured", StringComparison.Ordinal)
                  && b12.Safe && (b12.Why ?? "").Contains("the game's own grid", StringComparison.Ordinal),
                  "ladder: a safe rung has the server's own reason (the page no longer writes one), naming the counting must-have",
                  b96.Why + " | " + b12.Why);

            const string home =
                @"{""id"":""home"",""target"":""world:spawn_island_area"",""metric"":""spawn_island_area"",""test"":""at_least"",""value"":1000000,""importance"":""must""}";
            GridRungVerdict auto96 = GridLadder.For(Session(Query("sia96", "\"grid\":96", home), oracle).Compiled).Rung(96);
            GridRungVerdict off384 = GridLadder.For(Session(Query("sia384off", "\"grid\":384,\"screen\":\"off\"", home), oracle).Compiled).Rung(384);
            GridRungVerdict aah384 = GridLadder.For(Session(Query("aah384", "\"grid\":384",
                @"{""id"":""high-ground"",""target"":""world:high"",""metric"":""area_above_height"",""height"":200,""test"":""at_least"",""value"":5000000,""importance"":""must""}"), oracle).Compiled).Rung(384);
            check(!aah384.Safe && (aah384.Why ?? "").Contains("must-have 'high-ground' is one no coarse grid measures safely", StringComparison.Ordinal)
                  && !(aah384.Why ?? "").Contains("connectivity", StringComparison.Ordinal),
                  "ladder, area_above_height must-have: named as one no coarse grid measures safely, not called connectivity or an extremum",
                  aah384.Why ?? "");
            check(!auto96.Safe && (auto96.Why ?? "").Contains("raises the whole query to G12", StringComparison.Ordinal)
                  && !off384.Safe && (off384.Why ?? "").Contains("screen is off, so nothing is raised", StringComparison.Ordinal)
                  && !(off384.Why ?? "").Contains("raises", StringComparison.Ordinal),
                  "ladder, fine-only must-have: 'raises' under screen auto, 'nothing is raised' under screen off",
                  (auto96.Why ?? "") + " | " + (off384.Why ?? ""));
        }

        private static void LadderWithLocations(Action<bool, string, string> check, ILocationOracle oracle)
        {
            GridLadder boss = GridLadder.For(PresetSession("boss-rush", oracle, null).Compiled);
            check(boss.OnlyPlacement && boss.Rung(384).Safe
                  && boss.TableNote.Contains("every rung gives the same values", StringComparison.Ordinal)
                  && !boss.TableNote.Contains("decides nothing", StringComparison.Ordinal),
                  "ladder, boss-rush: the flat table, saying every rung gives the same values",
                  boss.TableNote);

            GridLadder traders = GridLadder.For(PresetSession("all-traders", oracle, null).Compiled);
            check(!traders.OnlyPlacement && !traders.GridDecides && traders.Rung(384).Safe
                  && traders.TableNote.Contains("nice goals' ranking depends on the grid", StringComparison.Ordinal)
                  && traders.TableNote.Contains("'blackforest-near'", StringComparison.Ordinal),
                  "ladder, all-traders: not flat - the must-haves are exact, the nice Black Forest goal's ranking is not",
                  traders.TableNote);
        }

        // =========================================================================================
        // helpers

        private static readonly Regex W1Names = new Regex("must-have goals? '(.+?)' (?:is|are) not", RegexOptions.CultureInvariant);
        private static readonly Regex W2Names = new Regex("goals? '(.+?)' (?:is|are) measured on that grid", RegexOptions.CultureInvariant);

        private static Query Query(string name, string search, string goals)
            => QueryReader.Parse("{\"version\":1,\"defs\":1,\"name\":\"" + name + "\",\"search\":{" + search
                                 + "},\"goals\":[" + goals + "]}", name);

        /// <summary>
        /// A dry session, the way the CLI builds one: 512 seeds on 8 workers, no output path, nothing
        /// on disk and no seed touched. The scan-order and vacuity rules are waived because neither is
        /// what these checks are about, and a refusal would otherwise hide the warnings.
        /// </summary>
        private static SearchSession Session(Query q, ILocationOracle oracle)
            => SearchSession.Create(q, oracle, Engine, 512, 8, outPath: null, acceptScanOrder: true,
                                    allowVacuous: true);

        private static SearchSession PresetSession(string name, ILocationOracle oracle, double? grid, string? screen = null)
        {
            Query q = Presets.Load(name);
            if (grid != null) q.Search.Grid = grid.Value;
            if (screen == "off") q.Search.Screen = ScreenMode.Off;
            if (grid != null || screen != null) q.CanonicalJson = QueryReader.Canonicalise(q);
            return Session(q, oracle);
        }

        private static int Count(SearchSession s, string needle)
        {
            int n = 0;
            foreach (string w in s.Preflight.Warnings)
            {
                if (w.Contains(needle, StringComparison.Ordinal)) n++;
            }

            return n;
        }

        private static string? Line(SearchSession s, string needle)
        {
            foreach (string w in s.Preflight.Warnings)
            {
                if (w.Contains(needle, StringComparison.Ordinal)) return w;
            }

            return null;
        }

        private static bool PlanHas(SearchSession s, string needle) => PlanLine(s, needle) != null;

        private static string? PlanLine(SearchSession s, string needle)
        {
            foreach (string l in s.Preflight.Plan)
            {
                if (l.Contains(needle, StringComparison.Ordinal)) return l.Trim();
            }

            return null;
        }

        /// <summary>The goal ids the first warning containing <paramref name="needle"/> names.</summary>
        private static HashSet<string> Ids(SearchSession s, string needle, Regex names)
        {
            HashSet<string> ids = new HashSet<string>(StringComparer.Ordinal);
            string? line = Line(s, needle);
            if (line == null) return ids;
            Match m = names.Match(line);
            if (!m.Success) return ids;
            foreach (string id in m.Groups[1].Value.Split(new[] { "', '" }, StringSplitOptions.None)) ids.Add(id);
            return ids;
        }

        private static bool SameSet(HashSet<string> got, params string[] want)
        {
            if (got.Count != want.Length) return false;
            foreach (string w in want)
            {
                if (!got.Contains(w)) return false;
            }

            return true;
        }

        private static string Show(SearchSession s, string needle)
        {
            List<string> hits = new List<string>();
            foreach (string w in s.Preflight.Warnings)
            {
                if (w.Contains(needle, StringComparison.Ordinal)) hits.Add(w.Length > 160 ? w.Substring(0, 160) + "..." : w);
            }

            return hits.Count == 0 ? "(none) " : "[" + string.Join("] [", hits) + "] ";
        }
    }
}
