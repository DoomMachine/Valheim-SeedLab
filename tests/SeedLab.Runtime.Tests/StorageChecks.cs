using System;
using System.IO;
using System.Text;
using SeedLab.Runtime.Hardware;
using SeedLab.Runtime.Storage;

namespace SeedLab.RuntimeTests
{
    public static class StorageChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            string tempRoot = Path.Combine(Path.GetTempPath(),
                "seedlab-runtime-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));

            try
            {
                CacheRoot root = CacheRoot.Open(new CacheRootOptions { Override = tempRoot });

                check(Directory.Exists(root.Path) && Directory.Exists(root.Checkpoints)
                      && Directory.Exists(root.Maps) && Directory.Exists(root.ScratchParent),
                    "the cache root and its categories are created", root.Path);
                check(root.Source == CacheRootSource.Override, "an explicit --cache-dir wins", root.SourceDetail);

                CacheRoot natural = CacheRoot.Open(new CacheRootOptions { Create = false, ReadEnvironment = false });
                bool right = OperatingSystem.IsWindows()
                    ? natural.Source == CacheRootSource.LocalApplicationData
                    : OperatingSystem.IsMacOS()
                        ? natural.Source == CacheRootSource.MacCaches
                        : natural.Source == CacheRootSource.XdgCacheHome;
                check(right, "the per-OS cache location is used by default",
                    natural.Path + " (" + natural.SourceDetail + ")");

                // --- durable writes -------------------------------------------------------------
                string file = Path.Combine(root.Runs, "results.jsonl");
                DurableWrite.Text(file, "first\n");
                check(File.ReadAllText(file) == "first\n", "temp+rename writes the file", file);

                byte[] raw = File.ReadAllBytes(file);
                check(raw.Length == 6 && raw[0] == (byte)'f', "UTF-8 with no BOM", raw.Length + " bytes");

                check(Directory.GetFiles(root.Runs, DurableWrite.TempPrefix + "*").Length == 0,
                    "no temp file is left behind", "");

                try
                {
                    DurableWrite.Stream(file, s => throw new InvalidOperationException("writer blew up"));
                }
                catch (InvalidOperationException) { }
                check(File.ReadAllText(file) == "first\n",
                    "a writer that throws leaves the previous file intact", "still 'first'");
                check(Directory.GetFiles(root.Runs, DurableWrite.TempPrefix + "*").Length == 0,
                    "and cleans up its own temp file", "");

                DurableWrite.Text(file, "second\n");
                check(File.ReadAllText(file) == "second\n", "a rewrite replaces it atomically", "");

                // An orphan from a process that is gone, and one from a process that is not.
                string deadTemp = Path.Combine(root.Runs, DurableWrite.TempPrefix + "999999-aaaaaaaa-results.jsonl");
                string liveTemp = Path.Combine(root.Runs,
                    DurableWrite.TempPrefix + Environment.ProcessId + "-bbbbbbbb-results.jsonl");
                File.WriteAllText(deadTemp, "junk");
                File.WriteAllText(liveTemp, "in flight");
                int removed = DurableWrite.CleanOrphans(root.Runs, pid => pid == Environment.ProcessId);
                check(removed == 1 && !File.Exists(deadTemp) && File.Exists(liveTemp),
                    "orphaned temp files of dead processes are reaped, live ones are not",
                    removed + " removed");
                File.Delete(liveTemp);

                // --- scratch ---------------------------------------------------------------------
                string scratchPath;
                using (ScratchDirectory scratch = root.CreateScratch("unit-test"))
                {
                    scratchPath = scratch.Path;
                    check(Directory.Exists(scratchPath) && Path.GetFileName(scratchPath)
                            .StartsWith("pid-" + Environment.ProcessId + "-", StringComparison.Ordinal),
                        "the scratch directory is named with pid and process start time",
                        Path.GetFileName(scratchPath));

                    ScratchOwner? owner = ScratchOwner.TryRead(scratchPath);
                    check(owner != null && owner.Pid == Environment.ProcessId && owner.Purpose == "unit-test"
                          && owner.StartedUtc > DateTime.UtcNow.AddDays(-2),
                        "the owner stamp records pid, start time and purpose",
                        owner == null ? "missing" : owner.Pid + " @ " + owner.StartedUtc.ToString("o"));

                    File.WriteAllText(scratch.File("slice-0001.bin"), new string('x', 4096));
                    check(scratch.SizeBytes > 4000, "scratch usage is measurable", Bytes.Human(scratch.SizeBytes));
                }
                check(!Directory.Exists(scratchPath), "dispose deletes the scratch directory", scratchPath);

                // --- the reaper ------------------------------------------------------------------
                string dead = Path.Combine(root.ScratchParent, "pid-999999-20200101T000000Z");
                Directory.CreateDirectory(dead);
                File.WriteAllText(Path.Combine(dead, ScratchOwner.FileName),
                    new ScratchOwner(999999, new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                     "killed run", new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Serialise());
                File.WriteAllText(Path.Combine(dead, "slice.bin"), new string('y', 2048));

                ScratchDirectory mine = root.CreateScratch("live");
                ReapReport reap = root.ReapAbandoned();
                check(!Directory.Exists(dead) && Directory.Exists(mine.Path),
                    "the reaper deletes what a killed process left and keeps a live run's",
                    reap.Line());
                check(reap.BytesReclaimed >= 2048, "it reports what it reclaimed", Bytes.Human(reap.BytesReclaimed));

                // Pid reuse: a live pid whose start time does not match the stamp is NOT this owner.
                string reused = Path.Combine(root.ScratchParent, "pid-" + Environment.ProcessId + "-19990101T000000Z");
                Directory.CreateDirectory(reused);
                File.WriteAllText(Path.Combine(reused, ScratchOwner.FileName),
                    new ScratchOwner(Environment.ProcessId, new DateTime(1999, 1, 1, 0, 0, 0, DateTimeKind.Utc),
                                     "long gone", DateTime.UtcNow).Serialise());
                ScratchOwner? stale = ScratchOwner.TryRead(reused);
                check(stale != null && !ProcessLiveness.IsSameProcess(stale.Pid, stale.StartedUtc),
                    "pid reuse is caught by the recorded start time",
                    "pid " + Environment.ProcessId + " is alive but did not start in 1999");
                check(ProcessLiveness.IsSameProcess(Environment.ProcessId, ProcessLiveness.CurrentStartTimeUtc),
                    "this process recognises itself", "");
                check(!ProcessLiveness.IsAlive(999999), "a dead pid reads as dead", "");
                mine.Dispose();
                Directory.Delete(reused, true);

                // --- the usage report --------------------------------------------------------------
                DiskUsageReport usage = root.MeasureUsage();
                check(usage.TotalBytes > 0 && usage.Entries.Count >= 6,
                    "'what am I using on disk' answers by category",
                    Bytes.Human(usage.TotalBytes) + " over " + usage.Entries.Count + " categories");
                check(usage.Volume.IsReady && usage.Volume.FreeBytes > 0,
                    "and names the volume and its free space", usage.Volume.ToString());

                string extra = Path.Combine(tempRoot, "outside.jsonl");
                File.WriteAllText(extra, new string('z', 1000));
                DiskUsageReport withOut = root.MeasureUsage(extra);
                check(withOut.TotalBytes >= usage.TotalBytes + 1000,
                    "an output file outside the cache can be included", Bytes.Human(withOut.TotalBytes));
            }
            finally
            {
                try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch (Exception) { }
            }
        }
    }
}
