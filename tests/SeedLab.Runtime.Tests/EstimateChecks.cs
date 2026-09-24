using System;
using SeedLab.Runtime.Estimation;
using SeedLab.Runtime.Execution;
using SeedLab.Runtime.Hardware;

namespace SeedLab.RuntimeTests
{
    public static class EstimateChecks
    {
        private static EstimateInputs Query(long seeds, WorkTier tier, double spacing, int threads,
                                            long? keep, OutputFormat format = OutputFormat.Jsonl,
                                            int goals = 11) =>
            new EstimateInputs
            {
                Seeds = seeds,
                Tier = tier,
                GridCells = WorkerFootprints.CellsForSpacing(spacing),
                Threads = threads,
                Format = format,
                GoalCount = goals,
                KeepN = keep,
                FreeSpaceBytes = 3L * 1024 * Bytes.GiB,
                AvailableMemoryBytes = 32L * Bytes.GiB
            };

        public static void Run(Action<bool, string, string> check)
        {
            Estimator est = new Estimator();

            // --- the model reproduces the measurements it was built from --------------------------
            double biome384 = CostModel.SeedsPerSecond(WorkTier.BiomeGrid, WorkerFootprints.CellsForSpacing(384), 16);
            double biome12 = CostModel.SeedsPerSecond(WorkTier.BiomeGrid, WorkerFootprints.CellsForSpacing(12), 16);
            double heights384 = CostModel.SeedsPerSecond(WorkTier.HeightsRivers, WorkerFootprints.CellsForSpacing(384), 16);

            check(Math.Abs(biome384 - 10_717) / 10_717 < 0.01,
                "the cost model reproduces the measured 10,717 seeds/s at G384, 16 threads",
                biome384.ToString("N0") + " seeds/s");
            check(Math.Abs(biome12 - 18.2) / 18.2 < 0.01,
                "and the measured 18.2 seeds/s at G12", biome12.ToString("N2") + " seeds/s");
            check(Math.Abs(heights384 - 28.6) / 28.6 < 0.01,
                "and the measured 28.6 seeds/s for heights and rivers at G384",
                heights384.ToString("N2") + " seeds/s");

            double k = CostModel.GridExponent();
            check(k > 0.5 && k < 1.0,
                "cost grows sub-linearly in grid cells, as the two anchors measured", "exponent " + k.ToString("0.0000"));

            double loc = CostModel.SeedsPerSecond(WorkTier.LocationsAll, WorkerFootprints.CellsForSpacing(384), 16);
            check(loc > 1 && loc < 2.5,
                "one location goal drags a query into the seconds-per-seed tier",
                loc.ToString("0.00") + " seeds/s at 16 threads for all 183 types");

            // --- threads --------------------------------------------------------------------------
            double t1 = CostModel.SeedsPerSecond(WorkTier.BiomeGrid, WorkerFootprints.CellsForSpacing(384), 1);
            double t8 = CostModel.SeedsPerSecond(WorkTier.BiomeGrid, WorkerFootprints.CellsForSpacing(384), 8);
            check(t8 > t1 && t8 < t1 * 8,
                "more threads are faster, but never linearly - the measured scaling says so",
                t1.ToString("N0") + " -> " + t8.ToString("N0") + " seeds/s (x"
                + (t8 / t1).ToString("0.00") + " on 8 threads)");

            // --- workers with nothing to compute ---------------------------------------------------
            //
            // One worker computes a whole block, so a run cut into 2 blocks on 8 workers runs at 2
            // workers' rate - but every worker still starts and builds its buffers, so memory is 8's.
            // BusyWorkers carries the first fact without disturbing the second (2026-09-24).
            EstimateInputs starved = Query(512, WorkTier.HeightsRivers, 96, 8, 1000);
            starved.BusyWorkers = 2;
            Estimate es2 = est.Project(starved);
            Estimate full8 = est.Project(Query(512, WorkTier.HeightsRivers, 96, 8, 1000));
            Estimate only2 = est.Project(Query(512, WorkTier.HeightsRivers, 96, 2, 1000));
            check(Math.Abs(es2.SeedsPerSecond - only2.SeedsPerSecond) < 1e-9 && es2.SeedsPerSecond < full8.SeedsPerSecond,
                "busy workers set the rate: 2 of 8 with blocks project 2 workers' rate",
                es2.SeedsPerSecond.ToString("N2") + " seeds/s, against " + full8.SeedsPerSecond.ToString("N2")
                + " for 8 busy and " + only2.SeedsPerSecond.ToString("N2") + " for 2 threads");
            check(Math.Abs(es2.Memory.High - full8.Memory.High) < 1 && es2.Memory.High > only2.Memory.High,
                "and memory stays every worker's, busy or not",
                es2.Memory.HumanBytes() + " against " + only2.Memory.HumanBytes() + " for 2 threads");
            bool saidIdle = false;
            foreach (string n in es2.Notes) if (n.Contains("only 2 of the 8 workers have a block")) saidIdle = true;
            check(saidIdle, "and the estimate says why its rate is below the machine's", string.Join("; ", es2.Notes));

            // --- ranges and constants ---------------------------------------------------------------
            Estimate e = est.Project(Query(200_000, WorkTier.BiomeGrid, 384, 8, 1000));
            check(e.Time.Low <= e.Time.Mid && e.Time.Mid <= e.Time.High && e.Time.Low > 0,
                "time is a range, not a number", e.Time.HumanTime());
            check(e.Memory.Low > 0 && e.Disk.High >= e.Disk.Low,
                "so are memory and disk", e.Memory.HumanBytes() + " / " + e.Disk.HumanBytes());
            check(e.Constants.Count >= 4, "the constants used are reported with the estimate",
                e.Constants.Count + " constants");
            bool labelled = false;
            foreach (ConstantUse c in e.Constants) if (!c.Measured) labelled = true;
            check(labelled || e.Notes.Count > 0, "assumptions are labelled as assumptions",
                e.Notes.Count > 0 ? e.Notes[0] : "");
            check(e.Verdict == EstimateVerdict.Ok, "a small bounded run needs no confirmation", e.ToString());

            // --- bounded output is a real cap --------------------------------------------------------
            EstimateInputs small = Query(1_000_000, WorkTier.BiomeGrid, 384, 8, 1000);
            small.ExpectedHitRate = 0.01;
            EstimateInputs huge = Query(1_000_000_000, WorkTier.BiomeGrid, 384, 8, 1000);
            huge.ExpectedHitRate = 0.01;
            Estimate es = est.Project(small), eh = est.Project(huge);
            check(Math.Abs(es.Disk.High - eh.Disk.High) < 1,
                "--keep N caps the FILE: disk does not grow with the seeds scanned",
                Bytes.Human(es.Disk.High) + " for 1 M seeds, " + Bytes.Human(eh.Disk.High) + " for 1 G seeds");
            check(eh.Disk.High < 2L * Bytes.MiB,
                "1,000 kept records is under 2 MiB at 11 goals, as the audit measured",
                Bytes.Human(eh.Disk.High));

            // --- unbounded --------------------------------------------------------------------------
            EstimateInputs unbounded = Query(10_000_000, WorkTier.BiomeGrid, 384, 8, null);
            unbounded.ExpectedHitRate = 1.0;
            Estimate eu = est.Project(unbounded);
            check(eu.Disk.High > 10L * Bytes.GiB,
                "--keep all with a query that matches everything is the 17 GB case the audit found",
                eu.Disk.HumanBytes());
            check(eu.Verdict != EstimateVerdict.Ok, "and it does not start without a word", eu.Verdict.ToString());

            EstimateInputs gz = unbounded.Clone();
            gz.Format = OutputFormat.JsonlGz;
            check(est.Project(gz).Disk.High < eu.Disk.High / 5,
                "gzip is applied as the measured 8-15x, as a range", est.Project(gz).Disk.HumanBytes());

            // --- verdict thresholds -------------------------------------------------------------------
            Estimate slow = est.Project(Query(4_294_967_296L, WorkTier.BiomeGrid, 384, 16, 1000));
            check(slow.NeedsConfirmation, "a 4.6-day whole-space run needs confirmation",
                slow.Time.HumanTime() + " - " + string.Join("; ", slow.Reasons));

            EstimateInputs all = Query(4_294_967_296L, WorkTier.BiomeGrid, 384, 16, 1000);
            all.AllSeedsFlag = true;
            bool saidAll = false;
            foreach (string r in est.Project(all).Reasons) if (r.Contains("--all")) saidAll = true;
            check(saidAll, "--all always says so", "");

            EstimateInputs tooBig = Query(1_000_000_000, WorkTier.BiomeGrid, 384, 8, null);
            tooBig.ExpectedHitRate = 1.0;
            tooBig.FreeSpaceBytes = 10L * Bytes.GiB;
            Estimate refused = est.Project(tooBig);
            check(refused.Refused, "a run that cannot fit on the volume is refused, with the arithmetic",
                string.Join("; ", refused.Reasons));

            EstimateInputs noRam = Query(1000, WorkTier.LocationsAll, 384, 64, 1000);
            noRam.AvailableMemoryBytes = 1L * Bytes.GiB;
            check(est.Project(noRam).Refused, "so is one that cannot fit in RAM",
                est.Project(noRam).Memory.HumanBytes() + " needed, 1 GiB available");

            EstimateInputs everything = Query(1_000_000, WorkTier.BiomeGrid, 384, 8, 1000);
            everything.ExpectedHitRate = 0.99;
            bool saidNoFilter = false;
            foreach (string r in est.Project(everything).Reasons)
                if (r.Contains("not filtering")) saidNoFilter = true;
            check(saidNoFilter, "a query matching ~100 % of seeds is flagged BEFORE the run", "");

            // --- live re-calibration --------------------------------------------------------------------
            EstimateInputs run = Query(100_000_000, WorkTier.BiomeGrid, 384, 16, 1000);
            Estimate before = est.Project(run);

            Calibration cal = new Calibration();
            double half = CostModel.SeedsPerSecond(WorkTier.BiomeGrid, run.GridCells, 16) / 2.0;
            cal.ObserveSlice(1_000_000, TimeSpan.FromSeconds(1_000_000 / half), 12, 20_000);
            cal.ObserveSlice(1_000_000, TimeSpan.FromSeconds(1_000_000 / (half * 0.9)), 8, 13_000);
            Estimate after = est.Project(run, cal.Snapshot());

            check(after.Calibrated && !before.Calibrated,
                "an estimate switches to live measurements after the first slice",
                after.SeedsPerSecond.ToString("N0") + " measured vs " + before.SeedsPerSecond.ToString("N0") + " projected");
            check(after.Time.Mid > before.Time.Mid * 1.5,
                "a machine that is half as fast is re-projected as half as fast",
                before.Time.HumanTime() + " -> " + after.Time.HumanTime());
            bool fromSpread = false;
            foreach (string n in after.Notes) if (n.Contains("spread of 2 measured slices")) fromSpread = true;
            check(fromSpread, "and the range becomes the observed spread, not an assumed band", after.Time.HumanTime());

            Estimate remaining = est.Reproject(run, cal, 90_000_000);
            check(remaining.Time.Mid < after.Time.Mid / 5,
                "mid-run, only the REMAINING seeds are projected",
                after.Time.HumanTime() + " for all of it, " + remaining.Time.HumanTime() + " for the last 10 %");

            CalibrationSnapshot snap = cal.Snapshot();
            check(snap.HitRate.HasValue && Math.Abs(snap.HitRate!.Value - 1e-5) < 1e-6,
                "the hit rate is measured too", (snap.HitRate!.Value * 100).ToString("0.00000") + " %");
            check(snap.BytesPerMatch.HasValue && snap.BytesPerMatch!.Value > 1000,
                "and so is the real record size", snap.BytesPerMatch!.Value.ToString("0") + " B per match");

            // --- record sizes match the audit ------------------------------------------------------------
            check(Math.Abs(CostModel.RecordBytes(OutputFormat.Jsonl, 11) - 1709.5) < 1.0,
                "the JSONL record law reproduces the audited 1,709.6 B at 11 goals",
                CostModel.RecordBytes(OutputFormat.Jsonl, 11).ToString("0.0") + " B");
            check(Math.Abs(CostModel.RecordBytes(OutputFormat.Csv, 11) - 383.0) < 1.0,
                "and the CSV law the audited 383.0 B",
                CostModel.RecordBytes(OutputFormat.Csv, 11).ToString("0.0") + " B");

            // --- the printed block ------------------------------------------------------------------------
            string text = slow.Format();
            check(text.Contains("% of all 4,294,967,296 worlds") && text.Contains("verdict")
                  && text.Contains("constants"),
                "the pre-run block carries seeds, % of the space, the verdict and the constants",
                text.Split('\n').Length + " lines");
        }
    }
}
