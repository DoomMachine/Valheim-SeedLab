using System;
using System.Collections.Generic;
using System.Globalization;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Output;

namespace SeedLab.Search.Execution
{
    /// <summary>
    /// Everything that must be decided, said or refused <b>before a seed is touched</b>, in one place
    /// so that the CLI and the web UI cannot disagree about any of it (decision 12: every flag, mode,
    /// bound, estimate, warning and confirmation must be reachable from both).
    ///
    /// <para>Three lists, with three different meanings, kept apart on purpose:</para>
    /// <list type="bullet">
    /// <item><see cref="Refusals"/> - the run must not start. Each one names the fix.</item>
    /// <item><see cref="Confirmations"/> - the run may start, but only after the user says yes.</item>
    /// <item><see cref="Warnings"/> - the run starts; the user needs to know something.</item>
    /// </list>
    /// </summary>
    public sealed class SearchPreflight
    {
        public List<string> Refusals = new List<string>();
        public List<string> Confirmations = new List<string>();
        public List<string> Warnings = new List<string>();

        /// <summary>The grid decision and the measured reasons for it.</summary>
        public GridPlan Grid = new GridPlan();

        /// <summary>The resolved output policy, including what <c>keep</c> now really means.</summary>
        public OutputPolicy Output = new OutputPolicy();

        /// <summary>The projected result size from the measured per-record law, before any real work.</summary>
        public long ProjectedBytesIfAllMatch;

        public long FreeBytes = -1;

        public bool Ok => Refusals.Count == 0;

        /// <summary>The plan lines every front end prints, in order.</summary>
        public List<string> Plan = new List<string>();

        /// <summary>
        /// The feasibility, vacuity and degeneracy check, decided before a seed is touched.
        ///
        /// <para>It is a separate object rather than more strings because the CLI and the web API
        /// both render it and must not word the same fact differently - and because a refusal's
        /// evidence (which member, which asset field) has to survive into
        /// <c>/api/search/check</c> as data rather than as a sentence to be re-parsed.</para>
        /// </summary>
        public Feasibility.QueryCheckReport? Check;

        /// <summary>
        /// True when a must-goal excludes nothing and the run would therefore be a full scan wearing
        /// a filter's name. The front end blocks on this unless the user passes <c>--allow-vacuous</c>,
        /// which <see cref="SearchPreflightCheck.Check"/> takes as <c>allowVacuous</c>.
        /// </summary>
        public bool BlockedByVacuity;
    }

    /// <summary>
    /// The pre-run checks. Every number here comes from a measurement recorded in the code, and every
    /// refusal names what to change.
    /// </summary>
    public static class SearchPreflightCheck
    {
        /// <summary>The whole space, for the coverage line that must never be left implicit.</summary>
        public const double SeedSpace = 4294967296.0;

        public static SearchPreflight Check(Query q, CompiledQuery cq, ScanPlan plan, OutputPolicy output,
                                            int threads, bool acceptScanOrder = false,
                                            SeedLab.Search.Locations.ILocationOracle? oracle = null,
                                            bool allowVacuous = false)
        {
            SearchPreflight pf = new SearchPreflight { Output = output };

            // ---- 0. the feasibility / vacuity / degeneracy check, before anything is priced ---------
            //
            // It runs first because its verdict can make every number below irrelevant: there is no
            // point pricing a week of CPU for a query the generator's own branch conditions say no
            // seed satisfies. It touches no seed and costs a few hundred microseconds plus one grid
            // pass per new grid size.
            Feasibility.QueryCheckReport check = Feasibility.QueryCheck.Run(
                q, cq, oracle ?? SeedLab.Search.Locations.UnavailableLocationOracle.Instance);
            pf.Check = check;
            AppendCheck(pf, check, allowVacuous);

            // ---- 1. the output bound, which is now a real one -------------------------------------
            try
            {
                output.Validate();
            }
            catch (QueryException ex)
            {
                pf.Refusals.Add(ex.Message + (ex.Hint != null ? " - " + ex.Hint : ""));
            }

            if (output.Warning != null) pf.Warnings.Add(output.Warning);

            int goals = cq.Goals.Count;

            // The WORST case, formatted by the real writer, not the fitted law. The law was fitted on
            // one eleven-goal query and under-reads everywhere else; multiplying it by `keep` and
            // printing "a REAL cap on the file: at most 167 KB" produced a run that wrote 398,214 B,
            // 2.33x the stated ceiling - and the same number is the free-disk refusal's threshold, so
            // a refusal quoting 818 GB was describing 1.47 TB. A ceiling that is exceeded is not a
            // ceiling, and this one is the headline safety claim of the whole output layer.
            RecordFormatter shape = new RecordFormatter(q, cq, "preflight");
            double perRecord = shape.WorstCaseBytesPerRecord(output.Format);

            // A record carries optional FREE-TEXT fields - grid_note, unique_semantics, contents_note,
            // censored, error_source - whose length nothing bounds, so no arithmetic here can be a guaranteed
            // byte ceiling. Two consequences, and both are honoured rather than papered over:
            //
            //   1. the sentence below no longer claims the file cannot exceed a size. What IS a real
            //      cap is the RECORD COUNT, and QA confirmed that one holds exactly (10/100/1000
            //      requested gave 10/100/1000 written, in all three formats);
            //   2. the free-disk REFUSAL must not under-state, because under-stating is the direction
            //      that fills a disk. It uses a deliberately pessimistic multiple of the worst case.
            //      Measured 2026-09-23: the fitted law said 818 GB where the truth was 1.47 TB.
            const double DiskSafety = 2.0;
            double perRecordForRefusal = perRecord * DiskSafety;
            pf.ProjectedBytesIfAllMatch = (long)(plan.Limit * perRecordForRefusal);
            if (output.Path != null) pf.FreeBytes = CheckpointStore.FreeBytes(output.Path);

            pf.Plan.Add("results      " + (output.Path ?? "(none - nothing is kept)"));
            if (output.Path != null)
            {
                pf.Plan.Add("keep         " + (output.KeepAll
                    ? "all matches" + (output.IsRotating
                        ? ", rotated into " + CheckpointStore.Bytes(output.RotateBytes) + " segments"
                          + (output.Compress ? " (gzipped)" : "")
                          + (output.Reduce.IsNone ? "" : ", reduced " + output.Reduce.Text + " and the raw segment deleted")
                        : " in one file")
                    : "the best " + output.Keep.ToString("N0", CultureInfo.InvariantCulture)
                      + " - a REAL cap on the RECORD COUNT: the file holds at most "
                      + output.Keep.ToString("N0", CultureInfo.InvariantCulture)
                      + " records whatever the scan finds, and the true match count is reported beside "
                      + "it. Around " + CheckpointStore.Bytes((long)(output.Keep * perRecord))
                      + " of them, which is an ESTIMATE - a record carries free-text notes that nothing "
                      + "bounds, and the finished run reports the real size"));
                // NOT called "at most": a real record also carries free-text notes (grid_note,
                // unique_semantics, contents_note, censored) that this synthetic one has no way to
                // fill, and measured
                // on balanced-biomes the real average is 3,982 B against this 2,556 B. It is the
                // structural size - what the fields themselves cost - and saying otherwise would be
                // the same false ceiling in smaller print.
                pf.Plan.Add("record size  " + perRecord.ToString("0", CultureInfo.InvariantCulture)
                            + " B of structure for " + goals + (goals == 1 ? " goal" : " goals")
                            + " in " + output.Format.ToString().ToLowerInvariant()
                            + " - one record from the real writer with every NUMERIC field at its"
                            + " widest; free-text notes are extra and are why this is not a ceiling");
                pf.Plan.Add("free space   " + CheckpointStore.Bytes(pf.FreeBytes));
            }

            // ---- 2. a query with no must-have matches everything ----------------------------------
            int musts = 0;
            foreach (CompiledGoal g in cq.Goals)
            {
                if (g.Available && g.Goal.Importance == Importance.Must) musts++;
            }

            if (musts == 0)
            {
                string size = CheckpointStore.Bytes(pf.ProjectedBytesIfAllMatch);
                pf.Warnings.Add("this query has no must-have goal, so EVERY seed matches it: "
                                + plan.Limit.ToString("N0", CultureInfo.InvariantCulture) + " of "
                                + plan.Limit.ToString("N0", CultureInfo.InvariantCulture)
                                + ". It ranks, it does not filter"
                                + (output.KeepAll
                                    ? " - and with keep: all that is " + size + " of output"
                                    : " - the bounded file keeps the best "
                                      + output.Keep.ToString("N0", CultureInfo.InvariantCulture)
                                      + ", so it is " + CheckpointStore.Bytes((long)(output.Keep * perRecord))
                                      + " rather than " + size));
            }

            // ---- 3. the refusal rule (decision 9) --------------------------------------------------
            //
            // All must-haves, nothing to rank by, and a bounded output: "the best 1000" is then just
            // "the first 1000 the scan happened to reach", and a whole-space run would be spent
            // producing an answer whose order means nothing. The user asked for this to be refused
            // with the fixes named rather than answered.
            if (!output.KeepAll && !cq.HasNiceGoals && musts > 0 && !acceptScanOrder)
            {
                pf.Refusals.Add(
                    "every goal in this query is a must-have, so there is nothing to rank by, and a "
                    + "bounded run would return whichever " + output.Keep.ToString("N0", CultureInfo.InvariantCulture)
                    + " matches the scan reached first - an arbitrary sample wearing the name 'top "
                    + output.Keep.ToString("N0", CultureInfo.InvariantCulture) + "'. Fixes, any one of them: "
                    + "add a nice-to-have goal to rank by; change one goal's importance to 'nice'; "
                    + "pass --accept-scan-order to accept an arbitrary first-N; or --keep all to write "
                    + "every match (with --rotate for a large range)");
            }

            // ---- 4. the grid, from the measured policy ---------------------------------------------
            pf.Grid = q.Search.Screen == ScreenMode.Off
                ? new GridPlan { VerifyGrid = q.Search.Grid }
                : GridPolicy.AutoPick(cq, q.Search.ScreenGrid > 0 ? q.Search.ScreenGrid : 24.0);

            if (q.Search.Screen == ScreenMode.Off) pf.Grid.Notes.Add("screening is off: measured once at this grid");
            pf.Plan.Add("grid         " + pf.Grid.Describe());
            if (pf.Grid.RaisedFrom > 0)
            {
                pf.Confirmations.Add("the grid was raised from G"
                                     + pf.Grid.RaisedFrom.ToString("0.###", CultureInfo.InvariantCulture)
                                     + " to G12 because " + pf.Grid.UnsafeMusts.Count
                                     + (pf.Grid.UnsafeMusts.Count == 1 ? " must-have goal is" : " must-have goals are")
                                     + " measured wrongly at a coarse grid; a height query costs about 6.8x more "
                                     + "at 12 m. Run it with \"screen\": \"off\" to keep the coarse grid and the "
                                     + "measured loss");
            }
            foreach (string n in pf.Grid.Notes) pf.Plan.Add("             " + n);
            foreach (string g in pf.Grid.FineOnlyGoals) pf.Plan.Add("             " + g);

            if (!pf.Grid.ScreenThenVerify && q.Search.Grid > GridPolicy.CoarsestScreen
                && pf.Grid.FineOnlyGoals.Count == 0 && musts > 0)
            {
                pf.Warnings.Add("grid = " + q.Search.Grid.ToString("0.###", CultureInfo.InvariantCulture)
                                + " m is coarser than G96, where the measured margin needed to lose no true "
                                + "match passes 90 % of all seeds: a must-have at this grid is not a filter, "
                                + "and its reported number is a different measurement, not an approximation "
                                + "of the game's own grid");
            }

            // ---- 5. the region, the biggest free lever ---------------------------------------------
            if (cq.Plan.Radius < SeedLab.Search.Metrics.SeedSampler.WaterEdge)
            {
                double speedup = RunEstimator.RegionSpeedup(cq.Plan.Radius);
                pf.Plan.Add("region       " + cq.Plan.Radius.ToString("N0", CultureInfo.InvariantCulture)
                            + " m disc - " + speedup.ToString("0", CultureInfo.InvariantCulture)
                            + "x less sampling than the whole 10,500 m world, and EXACT for what it measures "
                            + "(a metric bounded by a disc cannot be changed by a cell outside it)");
            }
            else
            {
                pf.Plan.Add("region       the whole world (10,500 m). A radius-bounded goal is the cheapest "
                            + "thing in the tool and it loses nothing: measured on this machine at the game's "
                            + "own 12 m grid, one biome goal costs 7.90 ms/seed inside a 1 km disc against "
                            + "314.40 ms/seed over the whole world - 39.8x - and the disc decides the goal "
                            + "exactly, because a metric bounded by a disc cannot be changed by a cell "
                            + "outside it. Set search.region, or give the goal a radius");
            }

            // ---- 6. coverage, always out loud ------------------------------------------------------
            double f = plan.Limit / SeedSpace;
            pf.Plan.Add("coverage     " + (f >= 0.01 ? (f * 100).ToString("0.##", CultureInfo.InvariantCulture) + " %"
                                                     : (f * 100).ToString("0.###e+00", CultureInfo.InvariantCulture) + " %")
                        + " of all 4,294,967,296 worlds"
                        + (plan.Limit >= 4294967296L ? " (the whole space)" : ""));
            pf.Plan.Add("threads      " + threads.ToString(CultureInfo.InvariantCulture));
            pf.Plan.Add("checkpoint   one block of " + plan.BlockSize.ToString(CultureInfo.InvariantCulture)
                        + " seeds is the finest resume point there is. The checkpoint is written on a clock, "
                        + "but it can only record blocks that have FINISHED, and ONE worker computes a whole "
                        + "block - so a block's wall time is block size x per-seed cost and the thread count "
                        + "does not divide it");

            // Measured, and the reason this warning is worth its space: at T3 on the game's own 12 m
            // grid a seed is about 4.2 s, so a 32-seed block is ~134 s of one worker's time. Sixteen
            // threads do not help: they are working on blocks 1..15 while block 0 decides the resume
            // point. A kill therefore costs up to one block however often the checkpoint is written.
            if (cq.MaxTier >= Tier.T3 && plan.BlockSize > 16)
            {
                pf.Warnings.Add("this query reaches " + cq.MaxTier + " - where a seed costs a large fraction of a "
                                + "second, and about 4.2 s at 12 m over the whole world - and a block is "
                                + plan.BlockSize + " seeds computed by ONE worker. A kill can cost up to one "
                                + "block whatever --checkpoint-every says, so for a fine resume point size the "
                                + "block to about a minute of work: --block-size 16 at this tier, or 4 if the "
                                + "query also places locations");
            }

            // ---- 7. the confirmations decision 10 asks for -----------------------------------------
            if (plan.Limit >= 4294967296L)
            {
                pf.Confirmations.Add("this is the WHOLE 4,294,967,296-seed space");
            }
            else if (plan.Limit > 100_000_000L)
            {
                pf.Confirmations.Add(plan.Limit.ToString("N0", CultureInfo.InvariantCulture)
                                     + " seeds is over 100 million");
            }

            if (output.Path != null && output.KeepAll)
            {
                long projected = pf.ProjectedBytesIfAllMatch;
                if (projected > (1L << 30))
                {
                    pf.Confirmations.Add("keep: all could write up to " + CheckpointStore.Bytes(projected)
                                         + " if every seed matched");
                }

                if (pf.FreeBytes > 0 && projected > pf.FreeBytes / 10)
                {
                    pf.Confirmations.Add("that is more than 10 % of the "
                                         + CheckpointStore.Bytes(pf.FreeBytes) + " free on this volume");
                }

                if (pf.FreeBytes > 0 && projected > pf.FreeBytes)
                {
                    // Refuse only when the worst case is also the KNOWN case. With no must-have goal
                    // every seed matches, so "it could write this much" is "it will"; with a filter in
                    // place nobody knows the hit rate yet, and refusing on an assumption would block a
                    // run that writes 2 GB. Then it is a confirmation plus a ceiling the run stops at,
                    // which is what actually protects the disk.
                    if (musts == 0)
                    {
                        pf.Refusals.Add("keep: all on a query with no must-have goal matches every seed, so it "
                                        + "WILL write about " + CheckpointStore.Bytes(projected)
                                        + " and the volume has " + CheckpointStore.Bytes(pf.FreeBytes)
                                        + " free. Use --keep N (bounded: at most "
                                        + CheckpointStore.Bytes((long)(output.Keep * perRecord))
                                        + "), add a must-have goal, or point --out at a bigger volume");
                    }
                    else
                    {
                        pf.Confirmations.Add("keep: all could write up to " + CheckpointStore.Bytes(projected)
                                             + " if every seed matched, against "
                                             + CheckpointStore.Bytes(pf.FreeBytes) + " free. The real number "
                                             + "depends on a hit rate nothing has measured yet, so the run "
                                             + "carries a ceiling of "
                                             + CheckpointStore.Bytes(output.MaxBytes)
                                             + " and stops cleanly there with a resume command");
                    }
                }
            }

            if (output.OnLimit == OnLimit.Evict)
            {
                pf.Confirmations.Add("--on-limit evict keeps running at the ceiling and DROPS the "
                                     + "lowest-scoring records; the report will say how many and from which score");
            }

            return pf;
        }

        /// <summary>
        /// Turns the check into the three lists every front end already prints, without losing the
        /// structured report: <see cref="SearchPreflight.Check"/> still carries the evidence, the
        /// feasible set and the repair for each goal, which is what <c>/api/search/check</c> and
        /// <c>vseed search --json</c> serialise.
        /// </summary>
        private static void AppendCheck(SearchPreflight pf, Feasibility.QueryCheckReport check,
                                        bool allowVacuous)
        {
            foreach (string u in check.UsageErrors)
            {
                pf.Refusals.Add(u);
            }

            if (check.StampMismatch != null) pf.Warnings.Add(check.StampMismatch);

            foreach (string m in check.QueryMessages)
            {
                if (m == check.StampMismatch) continue;
                pf.Refusals.Add(m);
            }

            int refused = 0;
            foreach (Feasibility.GoalCheck g in check.Refused)
            {
                refused++;
                pf.Refusals.Add("goal '" + g.Goal.Id + "' " + g.Goal.Target + "." + g.Def.Name
                                + ": " + g.Message
                                + (g.Repair != null ? "  " + g.Repair + "." : "")
                                + "  Evidence: " + Feasibility.EvidenceText.Join(g.Evidence));
            }

            if (refused > 0)
            {
                // There is no --force and no --allow-impossible, by decision: a provable
                // impossibility cannot be overridden into existence, and a flag that pretended
                // otherwise would burn a week of CPU on a proof the tool already holds.
                pf.Refusals.Add("nothing was scanned. These refusals have no override: fix the goals "
                                + "above and run again. (--no-prefilter is an AUDIT mode and does not "
                                + "silence this check; it only disables rejection during the scan.)");
            }

            List<string> inert = new List<string>();
            foreach (Feasibility.GoalCheck g in check.NotDiscriminating)
            {
                inert.Add(g.Goal.Id);
                string line = "goal '" + g.Goal.Id + "' " + g.Goal.Target + "." + g.Def.Name
                              + " does not exclude any seed: " + g.Message
                              + (g.Repair != null ? "  " + g.Repair + "." : "");
                if (allowVacuous) pf.Warnings.Add(line);
                else pf.Refusals.Add(line);
            }

            if (inert.Count > 0)
            {
                pf.BlockedByVacuity = !allowVacuous;
                string tail = inert.Count + (inert.Count == 1 ? " must-have goal (" : " must-have goals (")
                              + string.Join(", ", inert)
                              + (inert.Count == 1 ? ") rejects nothing" : ") reject nothing")
                              + ", so this run is a "
                              + "full scan that returns whichever seeds the permutation visits first, "
                              + "ranked by your nice-to-have score.";
                if (allowVacuous) pf.Warnings.Add(tail + " --allow-vacuous was passed, so it runs anyway.");
                else pf.Refusals.Add(tail + " Fix them, or pass --allow-vacuous to scan anyway.");
            }

            foreach (Feasibility.GoalCheck g in check.Goals)
            {
                if (g.Verdict == Feasibility.CheckVerdict.Refuse
                    || g.Verdict == Feasibility.CheckVerdict.WarnVacuous
                    || g.Verdict == Feasibility.CheckVerdict.WarnDegenerate)
                {
                    continue;
                }

                // A refusal DOWNGRADED by a stamp mismatch arrives here as WarnRare. It is a
                // warning, not a plan line: the tool is saying "this would have been refused and the
                // data no longer proves it", which is the single most important thing on the screen
                // when the game has been updated. Plan lines are indented notes nobody reads twice.
                if (g.Verdict == Feasibility.CheckVerdict.WarnRare && g.Message.Length > 0)
                {
                    pf.Warnings.Add("goal '" + g.Goal.Id + "' " + g.Goal.Target + "." + g.Def.Name
                                    + ": " + g.Message
                                    + (g.Repair != null ? "  " + g.Repair + "." : ""));
                    if (g.AbsenceLine != null)
                    {
                        pf.Plan.Add("             " + g.Goal.Id + " absence: " + g.AbsenceLine);
                    }

                    continue;
                }

                if (g.Message.Length > 0) pf.Plan.Add("             " + g.Goal.Id + ": " + g.Message);
                if (g.AbsenceLine != null) pf.Plan.Add("             " + g.Goal.Id + " absence: " + g.AbsenceLine);
            }
        }
    }
}
