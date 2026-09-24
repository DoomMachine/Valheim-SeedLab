using System;
using System.Diagnostics;
using SeedLab.Runtime.Execution;
using SeedLab.Runtime.Hardware;

namespace SeedLab.RuntimeTests
{
    public static class ResourceChecks
    {
        private static HardwareInfo Machine(int cores, long availableBytes) =>
            HardwareProbe.Probe(new HardwareProbeOptions
            {
                LogicalCoresOverride = cores,
                AvailableMemoryOverride = availableBytes
            });

        public static void Run(Action<bool, string, string> check)
        {
            long cells384 = WorkerFootprints.CellsForSpacing(384);
            long cells12 = WorkerFootprints.CellsForSpacing(12);

            check(cells12 == 2048L * 2048L, "G12 is the game's own 2048 x 2048 grid", cells12.ToString("N0"));
            check(cells384 == 56L * 56L, "G384 follows the engine's grid law (2 x ceil(10500/r))",
                cells384.ToString("N0") + " samples");

            // --- footprints ---------------------------------------------------------------------
            WorkerFootprint f384 = WorkerFootprints.For(WorkTier.BiomeGrid, cells384);
            WorkerFootprint f12 = WorkerFootprints.For(WorkTier.BiomeGrid, cells12);
            check(Math.Abs(f384.HighBytes - 30L * 1024) < 3L * 1024,
                "the biome footprint at G384 is the measured ~30 KiB", Bytes.Human(f384.HighBytes));
            check(Math.Abs(f12.HighBytes - 24L * 1024 * 1024) < 2L * 1024 * 1024,
                "the biome footprint at G12 is the measured ~24 MiB", Bytes.Human(f12.HighBytes));

            WorkerFootprint fh = WorkerFootprints.For(WorkTier.HeightsRivers, cells384);
            check(fh.IsRange && fh.LowBytes >= 50L * 1000 * 1000 && fh.HighBytes >= 60L * 1000 * 1000,
                "the height tier is the measured 50-60 MB range", fh.ToString());
            WorkerFootprint fl = WorkerFootprints.For(WorkTier.LocationsAll, cells384);
            check(fl.HighBytes == 65L * 1000 * 1000, "the location tier is the measured ~65 MB", fl.ToString());
            check(WorkerFootprints.For(WorkTier.LocationsAll, cells12).HighBytes == fl.HighBytes,
                "the location tier ignores the query's grid (placement uses the game's own)", fl.ToString());

            // --- mode shares --------------------------------------------------------------------
            HardwareInfo big = Machine(16, 32L * Bytes.GiB);
            WorkerPlan bg = WorkerPlanner.Plan(ResourceMode.Background, f384, big);
            WorkerPlan bal = WorkerPlanner.Plan(ResourceMode.Balanced, f384, big);
            WorkerPlan full = WorkerPlanner.Plan(ResourceMode.Full, f384, big);

            check(bg.Workers == 4 && bal.Workers == 8 && full.Workers == 16,
                "16 cores: background 4, balanced 8, full 16",
                bg.Workers + " / " + bal.Workers + " / " + full.Workers);
            check(bg.Priority == ProcessPriorityClass.BelowNormal
                  && bal.Priority == ProcessPriorityClass.Normal
                  && full.Priority == ProcessPriorityClass.Normal,
                "background is BelowNormal, nothing is ever above Normal",
                bg.Priority + " / " + bal.Priority + " / " + full.Priority);
            check(ResourceModes.Default == ResourceMode.Balanced, "balanced is the default", "");

            HardwareInfo tiny = Machine(2, 8L * Bytes.GiB);
            check(WorkerPlanner.Plan(ResourceMode.Background, f384, tiny).Workers == 1
                  && WorkerPlanner.Plan(ResourceMode.Balanced, f384, tiny).Workers == 1
                  && WorkerPlanner.Plan(ResourceMode.Full, f384, tiny).Workers == 2,
                "2 cores: never below one worker, full still uses both", "");

            HardwareInfo one = Machine(1, 4L * Bytes.GiB);
            check(WorkerPlanner.Plan(ResourceMode.Full, f384, one).Workers == 1, "1 core: one worker", "");

            // --- the memory guard ---------------------------------------------------------------
            // 4 GiB available, half of it usable, 65 MB per worker -> 31 workers; the mode asks for 16.
            HardwareInfo mem = Machine(32, 4L * Bytes.GiB);
            WorkerPlan locPlan = WorkerPlanner.Plan(ResourceMode.Balanced, fl, mem);
            check(locPlan.Workers == Math.Min(16, locPlan.MemoryWorkers),
                "worker count is min(mode share, memory)",
                "mode " + locPlan.ModeWorkers + ", memory " + locPlan.MemoryWorkers + " -> " + locPlan.Workers);

            // 1 GiB available: half of it is 512 MiB, 65 MB each -> 8 workers, below the mode's 16.
            HardwareInfo squeezed = Machine(32, 1L * Bytes.GiB);
            WorkerPlan sq = WorkerPlanner.Plan(ResourceMode.Balanced, fl, squeezed);
            check(sq.MemoryLimited && sq.Workers == 8 && sq.ModeWorkers == 16,
                "memory forces fewer workers than the mode asks",
                sq.Workers + " of the " + sq.ModeWorkers + " the mode wanted");

            bool bothNumbers = false;
            foreach (string line in sq.Lines())
                if (line.Contains("16 asked for") && line.Contains("8 will run")) bothNumbers = true;
            check(bothNumbers, "BOTH numbers are reported when memory is the limit", string.Join(" | ", sq.Lines())
                .Substring(0, Math.Min(110, string.Join(" | ", sq.Lines()).Length)) + "...");

            HardwareInfo starved = Machine(16, 64L * Bytes.MiB);
            WorkerPlan st = WorkerPlanner.Plan(ResourceMode.Full, fl, starved);
            check(st.Workers == 1 && st.InsufficientMemory,
                "one worker that does not fit is flagged, not silently rounded up",
                Bytes.Human(st.MemoryBudgetBytes) + " budget vs " + Bytes.Human(fl.HighBytes) + " per worker");

            WorkerPlan asked = WorkerPlanner.Plan(ResourceMode.Background, fl, squeezed,
                new WorkerPlanOptions { RequestedWorkers = 24 });
            check(asked.Workers == 8 && asked.RequestedWorkers == 24,
                "an explicit --threads still obeys the memory guard", asked.ToString());

            WorkerPlan reserved = WorkerPlanner.Plan(ResourceMode.Full, fl, squeezed,
                new WorkerPlanOptions { ReservedBytes = 400L * Bytes.MiB });
            check(reserved.MemoryWorkers < sq.MemoryWorkers,
                "a reservation (the bounded heap, buffers) reduces the budget",
                reserved.MemoryWorkers + " vs " + sq.MemoryWorkers);

            // --- priority -----------------------------------------------------------------------
            using (PriorityScope p = PriorityScope.Apply(ProcessPriorityClass.High))
            {
                check(p.Current != ProcessPriorityClass.High && p.Current != ProcessPriorityClass.RealTime,
                    "a request above Normal is clamped", p.Current.ToString());
            }
            ProcessPriorityClass before = Process.GetCurrentProcess().PriorityClass;
            using (PriorityScope p = PriorityScope.Apply(ProcessPriorityClass.BelowNormal))
            {
                check(Process.GetCurrentProcess().PriorityClass == ProcessPriorityClass.BelowNormal || p.Note.Length > 0,
                    "background lowers the process priority (or says why it could not)",
                    p.Note.Length > 0 ? p.Note : "now BelowNormal");
            }
            check(Process.GetCurrentProcess().PriorityClass == before,
                "the priority is restored on dispose", before.ToString());

            // --- parsing ------------------------------------------------------------------------
            check(ResourceModes.TryParse("", out ResourceMode m0, out _) && m0 == ResourceMode.Balanced,
                "no --mode means balanced", "");
            check(ResourceModes.TryParse("FULL", out ResourceMode m1, out _) && m1 == ResourceMode.Full,
                "--mode is case-insensitive", "");
            check(!ResourceModes.TryParse("turbo", out _, out string err) && err.Contains("background"),
                "an unknown mode is refused with the list", err);
        }
    }
}
