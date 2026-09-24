using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SeedLab.Runtime.Storage;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Execution;
using SeedLab.Search.Locations;
using SeedLab.Search.Output;

namespace SeedLab.SearchTests
{
    /// <summary>
    /// A run whose checkpoint another program holds - the "Access to the path is denied." of
    /// 2026-09-24 - reproduced in process and run to its end.
    ///
    /// <para><b>What used to happen</b> (measured that day against the real SeedLab.Search): the first
    /// checkpoint save that met a reader threw out of the collector, the run died with a message naming
    /// no file, its workers stayed parked for the life of a <c>vseed serve</c>, and - for a bounded run -
    /// the snapshot had already been replaced when the checkpoint's rename failed, so the resume that
    /// followed wrote 12 duplicated and 12 missing records of 50 and reported "top 50 of 60,000 matches;
    /// 71,022 were not kept".</para>
    ///
    /// <para><b>What is checked here:</b> a save that fails is a warning and the run goes on; a run
    /// that finishes while its checkpoint is held says it finished; the last save of an unfinished run
    /// fails into <see cref="SearchOutcome.CheckpointError"/> and can be retried; the SAME recipe that
    /// corrupted the results then resumes to the uninterrupted run's bytes; a snapshot from another
    /// moment than its checkpoint is refused; and whatever ends the collector, the workers are joined.</para>
    ///
    /// <para>The "other program" is a FileStream in this process that shares read only, like the
    /// reader in the measurement: the rename collides with the handle whoever owns it. A gate in the
    /// evaluator parks the scan at one block, so the checkpoint is saved - and fails - on the clock
    /// while the test holds it, with no timing guesswork about where the scan is. Retry schedules are
    /// shortened to milliseconds; the retry itself is <c>SeedLab.Runtime.Tests</c>' business.</para>
    /// </summary>
    public static class AccessDeniedChecks
    {
        private const string Engine = "tests";
        private const int Seeds = 400;
        private const int BlockSize = 8;
        private const int GateBlock = 6;
        private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(100);
        private static readonly RetrySchedule Short = new RetrySchedule("test", 5, 5);

        // Every seed passes (a nice-to-have only), so a bounded keep of 10 drops 390 of 400 - the set is
        // full from block 2 on, which is when a duplicated re-offer starts evicting a real record.
        private const string Query =
            @"{""version"":1,""defs"":1,""name"":""access-denied"",
               ""search"":{""order"":""shuffled"",""key"":""0x5EEDF00D1234ABCD"",""grid"":384,""screen"":""off"",""keep"":10,""block_size"":8},
               ""goals"":[
                 {""id"":""meadows"",""target"":""biome:Meadows"",""metric"":""area_within"",""radius"":2000,""test"":""at_least"",""value"":1,""importance"":""nice""}]}";

        public static void Run(Action<bool, string, string> check)
        {
            string dir = Path.Combine(Path.GetTempPath(), "seedlab-access-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                (SearchOutcome refOutcome, byte[] refBytes, string refLine) = Reference(Path.Combine(dir, "reference"));
                check(refOutcome.Complete && refBytes.Length > 0 && refOutcome.ResultsWritten == 10 && refOutcome.Passed == Seeds,
                      "an uninterrupted reference run (GUARD)",
                      refLine + ", " + refBytes.Length + " B");
                if (!refOutcome.Complete) return;

                WorkersJoined(check, Path.Combine(dir, "throws"));
                HeldAcrossIntervals(check, Path.Combine(dir, "recover"), refOutcome, refBytes, refLine);
                FinishedWhileHeld(check, Path.Combine(dir, "finished"), refBytes);
                HeldThroughTheEnd(check, Path.Combine(dir, "retry"), refOutcome, refBytes, refLine, retry: true);
                HeldThroughTheEnd(check, Path.Combine(dir, "pair"), refOutcome, refBytes, refLine, retry: false);
                OutOfStep(check, Path.Combine(dir, "out-of-step"), refBytes);
                ResumedWhileUnreadable(check, Path.Combine(dir, "unreadable"), refOutcome, refBytes, refLine);
                CommittedSnapshotRead(check, Path.Combine(dir, "committed"));
                ManifestPending(check, Path.Combine(dir, "manifest"));
            }
            finally
            {
                DeleteTree(dir);
            }
        }

        // =========================================================================================
        // Whatever ends the collector, the workers are stopped and joined.

        private static void WorkersJoined(Action<bool, string, string> check, string dir)
        {
            SearchSession s = Session(dir, null);
            SearchRun run = new SearchRun(s.Compiled, s.Plan, Oracle, 4);
            Gate g = new Gate { Seed = int.MinValue };
            run.EvaluatorFactory = () =>
            {
                g.Seen();
                return new Probe(new SeedEvaluator(s.Compiled, Oracle), g);
            };

            Exception? thrown = null;
            try
            {
                run.Run(0, new RunOptions { Sink = new ThrowingSink(20) });
            }
            catch (Exception ex)
            {
                thrown = ex;
            }

            check(thrown is InvalidOperationException && thrown.Message == ThrowingSink.Text,
                  "an exception in the collector (a sink that fails) still leaves the run as that exception",
                  thrown == null ? "nothing thrown" : thrown.GetType().Name + ": " + thrown.Message);
            check(g.Threads.Count == 4 && g.AllExited(),
                  "THE LEAK: and every worker has been stopped and joined by the time it does",
                  g.Threads.Count + " workers seen, " + g.Alive() + " still alive");
        }

        // =========================================================================================
        // Held across several checkpoints, then released: warnings, and the run finishes unharmed.

        private static void HeldAcrossIntervals(Action<bool, string, string> check, string dir, SearchOutcome refOutcome,
                                                byte[] refBytes, string refLine)
        {
            Leg leg = Launch(dir, stopOnRelease: false);
            FileStream? holder = null;
            try
            {
                holder = HoldWhenSaved(leg.Checkpoint);
                bool warned = WaitFor(leg, w => w.Contains("could not be saved at", StringComparison.Ordinal));
                Thread.Sleep(450);          // four intervals more: several failed saves in a row
                holder?.Dispose();
                holder = null;
                bool recovered = WaitFor(leg, w => w.Contains("was saved again", StringComparison.Ordinal));
                check(holder == null && warned && recovered, "the checkpoint was held after its first save, then released (GUARD)",
                      (warned ? "warned" : "NO WARNING") + ", " + (recovered ? "recovered" : "NOT RECOVERED"));
            }
            finally
            {
                holder?.Dispose();
                leg.Gate.Open.Set();
            }

            SearchOutcome? o = leg.Finish();
            if (o == null)
            {
                check(false, "the run finished", "it threw: " + leg.Error);
                return;
            }

            List<string> failedWarnings = leg.Live.FindAll(w => w.Contains("could not be saved at", StringComparison.Ordinal));
            string first = failedWarnings.Count > 0 ? failedWarnings[0] : "";
            string last = o.Warnings.Count > 0 ? o.Warnings[o.Warnings.Count - 1] : "";
            check(o.Complete && o.FailedSaves >= 2,
                  "a checkpoint save that fails is a warning, and the run goes on to the end",
                  o.FailedSaves + " saves failed; complete " + o.Complete);
            check(first.Contains(Path.GetFullPath(leg.Checkpoint), StringComparison.Ordinal)
                  && first.Contains("another program has it open", StringComparison.Ordinal)
                  && first.Contains("The run goes on", StringComparison.Ordinal),
                  "the warning names the checkpoint, says another program has it open, and that the run goes on",
                  first);
            check(failedWarnings.Count == 1 && last.Contains("after " + o.FailedSaves + " failed saves", StringComparison.Ordinal),
                  "failures in a row are said once, and the recovery says how many saves failed",
                  failedWarnings.Count + " failure warning(s); last: " + last);
            check(leg.Live.Count == o.Warnings.Count && o.CheckpointError == null && o.RetireWarning == null,
                  "every warning reached the live callback and the outcome alike, and nothing else went wrong",
                  leg.Live.Count + " live, " + o.Warnings.Count + " in the outcome");
            check(Same(File.ReadAllBytes(leg.Results), refBytes) && leg.Session.ResultLine(o) == refLine,
                  "the results are byte-identical to the uninterrupted run, and so is its 'top N of M' line",
                  leg.Session.ResultLine(o));
            check(leg.Gate.AllExited(), "the workers were joined", leg.Gate.Threads.Count + " seen, " + leg.Gate.Alive() + " alive");
            check(NoneLeft(leg.Checkpoint), "and the finished run retired its checkpoint and both snapshot generations", "");
        }

        // =========================================================================================
        // A run that FINISHES while its checkpoint is held says so, truthfully.

        private static void FinishedWhileHeld(Action<bool, string, string> check, string dir, byte[] refBytes)
        {
            Leg leg = Launch(dir, stopOnRelease: false);
            FileStream? holder = null;
            SearchOutcome? o;
            try
            {
                holder = HoldWhenSaved(leg.Checkpoint);
                WaitFor(leg, w => w.Contains("could not be saved at", StringComparison.Ordinal));
                leg.Gate.Open.Set();
                o = leg.Finish();
            }
            finally
            {
                holder?.Dispose();
                leg.Gate.Open.Set();
            }

            if (o == null)
            {
                check(false, "the run finished", "it threw: " + leg.Error);
                return;
            }

            string ckpt = Path.GetFullPath(leg.Checkpoint);
            string w = o.RetireWarning ?? "";
            check(o.Complete && o.CheckpointPath == null && o.CheckpointError == null
                  && o.CheckpointLeftovers.Contains(ckpt) && o.Warnings.Contains(w),
                  "a finished run whose checkpoint cannot be deleted is still reported as finished, not resumable",
                  "complete " + o.Complete + ", checkpoint path " + (o.CheckpointPath ?? "null") + ", "
                  + o.CheckpointLeftovers.Count + " file(s) left");
            check(w.Contains("the run finished", StringComparison.Ordinal) && w.Contains("safe to delete", StringComparison.Ordinal)
                  && w.Contains(ckpt, StringComparison.Ordinal) && w.Contains("another program has it open", StringComparison.Ordinal)
                  && !w.Contains("has not finished", StringComparison.Ordinal),
                  "and it says why and that the leftover is safe to delete, naming it", w);
            check(Same(File.ReadAllBytes(leg.Results), refBytes), "its results are the uninterrupted run's bytes", "");
            foreach (string p in o.CheckpointLeftovers) TryDelete(p);
        }

        // =========================================================================================
        // Held through the end of a run that stops: the last save fails, is reported, and the resume
        // point on disk - with or without a retry - resumes to the uninterrupted run's bytes.

        private static void HeldThroughTheEnd(Action<bool, string, string> check, string dir, SearchOutcome refOutcome,
                                              byte[] refBytes, string refLine, bool retry)
        {
            string tag = retry ? "RETRY" : "THE PAIR";
            Leg leg = Launch(dir, stopOnRelease: true);
            string ckpt = Path.GetFullPath(leg.Checkpoint);
            FileStream? holder = null;
            SearchOutcome? o = null;
            long heldAt = -1;
            try
            {
                holder = HoldWhenSaved(leg.Checkpoint);
                heldAt = holder == null ? -1 : Checkpoint.Load(ckpt).NextBlock;
                WaitFor(leg, w => w.Contains("could not be saved at", StringComparison.Ordinal));
                leg.Gate.Open.Set();              // the gated worker presses Stop as it goes on
                o = leg.Finish();

                if (o != null && retry)
                {
                    bool again = leg.Session.RetryFinalSave(Short);
                    check(!again && o.CheckpointError != null && o.CheckpointError.Attempts == 2,
                          tag + ": a retry while the file is still held fails again, with a fresh diagnosis",
                          "attempt " + (o.CheckpointError?.Attempts ?? 0));
                }
            }
            finally
            {
                holder?.Dispose();
                leg.Gate.Open.Set();
            }

            if (o == null)
            {
                check(false, tag + ": the run returned rather than throwing", "it threw: " + leg.Error);
                return;
            }

            CheckpointError? e = o.CheckpointError;
            check(!o.Complete && e != null && e.Problem == FileProblem.InUse && e.Path == ckpt
                  && e.Diagnosis.Message.Contains(ckpt, StringComparison.Ordinal),
                  tag + ": the last save of a stopped run fails into CheckpointError, naming the held checkpoint - no exception",
                  e == null ? "no error" : e.Problem + " " + e.Path);
            if (e == null) return;

            check(e.OnDiskBlock == heldAt && e.RunBlock == o.BlocksDone && o.CheckpointPath == ckpt
                  && e.Message.Contains("resuming starts there", StringComparison.Ordinal),
                  tag + ": and says what is on disk instead: the older resume point, and the blocks a resume repeats",
                  "on disk block " + e.OnDiskBlock + ", the run reached " + e.RunBlock + ": " + e.Message);
            check(leg.Gate.AllExited(), tag + ": the workers were joined", "");

            Checkpoint onDisk = Checkpoint.Load(ckpt);
            if (retry)
            {
                bool saved = leg.Session.RetryFinalSave(Short);
                Checkpoint after = Checkpoint.Load(ckpt);
                string? other = onDisk.KeptSnapshot;
                check(saved && o.CheckpointError == null && o.CheckpointPath == ckpt && after.NextBlock == o.BlocksDone
                      && after.KeptSnapshot != null && File.Exists(after.KeptSnapshot)
                      && HeaderNextBlock(after.KeptSnapshot) == after.NextBlock
                      && other != null && !File.Exists(other),
                      tag + ": once released, RetryFinalSave writes the identical save - the checkpoint at the block the run reached, "
                      + "naming a snapshot of that block - and the older generation goes",
                      "next block " + after.NextBlock + ", snapshot " + Path.GetFileName(after.KeptSnapshot ?? "?"));
            }
            else
            {
                // The recipe that corrupted 24 of 50 records: a snapshot written for the moment the run
                // reached, beside a checkpoint still at an older block. The newer snapshot is under the
                // generation the checkpoint does not name, so the checkpoint's own pair is untouched.
                string newer = CheckpointStore.NextSnapshotPath(ckpt, onDisk.KeptSnapshot);
                long newerAt = File.Exists(newer) ? HeaderNextBlock(newer) : -1;
                check(onDisk.NextBlock == heldAt && newerAt == o.BlocksDone && newerAt > onDisk.NextBlock
                      && onDisk.KeptSnapshot != null && HeaderNextBlock(onDisk.KeptSnapshot) == onDisk.NextBlock,
                      tag + ": the failed save left a NEWER snapshot beside an OLDER checkpoint - the corrupting state - but under the other name",
                      "checkpoint at block " + onDisk.NextBlock + " names " + Path.GetFileName(onDisk.KeptSnapshot ?? "?")
                      + " (block " + (onDisk.KeptSnapshot != null ? HeaderNextBlock(onDisk.KeptSnapshot) : -1) + "); "
                      + Path.GetFileName(newer) + " is at block " + newerAt);
            }

            (SearchOutcome r, SearchSession rs) = Resume(dir, leg.Results);
            bool identical = r.Complete && Same(File.ReadAllBytes(leg.Results), refBytes);
            check(identical,
                  tag + ": the resume finishes with the uninterrupted run's bytes"
                  + (retry ? "" : " (from the state that, before the fix, resumed into 12 duplicated and 12 lost of 50)"),
                  (identical ? "IDENTICAL" : "DIFFERENT") + ", " + r.ResultsWritten + " records");
            check(r.Passed == refOutcome.Passed && r.ResultsWritten + r.ResultsDropped == r.Passed
                  && r.ResultsDropped == refOutcome.ResultsDropped && rs.ResultLine(r) == refLine,
                  tag + ": and its 'top N of M' is consistent: kept + not kept = matches = the reference's",
                  rs.ResultLine(r));
            check(NoneLeft(leg.Checkpoint), tag + ": and the finished resume retired every checkpoint file", "");
        }

        // =========================================================================================
        // A snapshot and a checkpoint from different moments are refused; an older snapshot that
        // agrees with its checkpoint is still accepted.

        private static void OutOfStep(Action<bool, string, string> check, string dir, byte[] refBytes)
        {
            Leg leg = Launch(dir, stopOnRelease: true);
            leg.Gate.Reached.Wait(TimeSpan.FromSeconds(60));
            leg.Gate.Open.Set();
            SearchOutcome? o = leg.Finish();
            string ckpt = Path.GetFullPath(leg.Checkpoint);
            Checkpoint? c = File.Exists(ckpt) ? Checkpoint.Load(ckpt) : null;
            string snapshot = c?.KeptSnapshot ?? "";
            bool made = o != null && !o.Complete && o.CheckpointError == null && c != null && File.Exists(snapshot)
                        && HeaderNextBlock(snapshot) == c.NextBlock;
            check(made, "a stopped run left a checkpoint and a snapshot recording the same next_block (GUARD)",
                  c == null ? "no checkpoint" : "block " + c.NextBlock + ", snapshot " + Path.GetFileName(snapshot));
            if (!made || c == null) return;

            string[] lines = File.ReadAllLines(snapshot);
            string head = lines[0];
            string field = ",\"next_block\":" + c.NextBlock;

            Rewrite(snapshot, lines, head.Replace(field, ",\"next_block\":" + (c.NextBlock + 1), StringComparison.Ordinal));
            string? refused = StartRefusal(dir, leg.Results);
            check(refused != null && refused.Contains("different moments", StringComparison.Ordinal)
                  && refused.Contains(snapshot, StringComparison.Ordinal),
                  "a snapshot whose next_block differs from its checkpoint's is refused, naming it",
                  refused ?? "ACCEPTED");

            string old = head.Replace(field, "", StringComparison.Ordinal);
            string total = "\"total\":" + c.SeedsPassed;
            Rewrite(snapshot, lines, old.Replace(total, "\"total\":" + (c.SeedsPassed + 5), StringComparison.Ordinal));
            string? refusedOld = StartRefusal(dir, leg.Results);
            check(refusedOld != null && refusedOld.Contains("different moments", StringComparison.Ordinal),
                  "a snapshot from before the field whose match count is not its checkpoint's is refused too",
                  refusedOld ?? "ACCEPTED");

            Rewrite(snapshot, lines, old);
            (SearchOutcome r, SearchSession _) = Resume(dir, leg.Results);
            check(r.Complete && Same(File.ReadAllBytes(leg.Results), refBytes),
                  "a snapshot from before the field that agrees with its checkpoint is accepted as before, and resumes exactly",
                  r.Complete ? "resumed and identical" : "did not finish");
        }

        // =========================================================================================
        // A resumed run whose checkpoint another program holds so that it cannot even be read, from
        // before the run's first save to after its last: the checkpoint on disk and the snapshot it
        // names are left exactly as they were, the last-save error says the checkpoint is still a
        // resume point, and the resume after release gives the uninterrupted run's bytes.
        //
        // Review of 2026-09-24, measured against the built vseed: the first save of such a run could not
        // read the checkpoint, took that for "it names no snapshot", and wrote the first generation -
        // which was the one the checkpoint named - before the checkpoint's own rename failed. The pair
        // was left out of step (a checkpoint at block 617 naming a snapshot now at block 722) and the
        // next --resume was refused with "delete the checkpoint and its snapshots". Both legs of this
        // test fail against that build: the snapshot's bytes change, and the resume is refused.

        private static void ResumedWhileUnreadable(Action<bool, string, string> check, string dir, SearchOutcome refOutcome,
                                                   byte[] refBytes, string refLine)
        {
            // Leg 1 saves only when it stops, so its checkpoint names the FIRST generation, .top: the
            // one a save that guessed would write.
            Leg first = Launch(dir, stopOnRelease: true, interval: TimeSpan.FromHours(1));
            first.Gate.Reached.Wait(TimeSpan.FromSeconds(60));
            first.Gate.Open.Set();
            SearchOutcome? o1 = first.Finish();
            string ckpt = Path.GetFullPath(first.Checkpoint);
            string top = CheckpointStore.SnapshotPathFor(ckpt);
            Checkpoint? c1 = File.Exists(ckpt) ? Checkpoint.Load(ckpt) : null;
            bool made = o1 != null && !o1.Complete && c1 != null && c1.KeptSnapshot != null
                        && string.Equals(Path.GetFullPath(c1.KeptSnapshot), top, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(top)
                        && HeaderNextBlock(top) == c1.NextBlock && c1.NextBlock + 3 < Seeds / BlockSize;
            check(made, "a stopped run left a checkpoint naming the first snapshot generation, .top (GUARD)",
                  c1 == null ? "no checkpoint" : "block " + c1.NextBlock + ", names " + Path.GetFileName(c1.KeptSnapshot ?? "(none)"));
            if (!made || c1 == null) return;

            byte[] ckptBefore = File.ReadAllBytes(ckpt);
            byte[] topBefore = File.ReadAllBytes(top);

            // Leg 2 resumes it. The holder takes the checkpoint as soon as Start has read it, shares
            // nothing - so SeedLab can neither replace nor read it - and lets go only after the run and
            // its last save have ended.
            FileStream? holder = null;
            Leg second = Launch(dir, stopOnRelease: true, resume: true, gateBlock: c1.NextBlock + 3,
                                afterStart: () => holder = new FileStream(ckpt, FileMode.Open, FileAccess.Read, FileShare.None));
            SearchOutcome? o2;
            try
            {
                bool warned = WaitFor(second, w => w.Contains("could not be saved at", StringComparison.Ordinal));
                check(holder != null && warned, "the resumed run's periodic saves fail against a holder that shares nothing (GUARD)",
                      warned ? "warned" : "NO WARNING");
                second.Gate.Open.Set();
                o2 = second.Finish();
            }
            finally
            {
                holder?.Dispose();
                second.Gate.Open.Set();
            }

            if (o2 == null)
            {
                check(false, "the resumed run returned rather than throwing", "it threw: " + second.Error);
                return;
            }

            bool pairUntouched = Same(File.ReadAllBytes(ckpt), ckptBefore) && Same(File.ReadAllBytes(top), topBefore);
            check(pairUntouched,
                  "THE PAIR: while the checkpoint could not be read, no save wrote the snapshot it names - checkpoint and .top are "
                  + "byte for byte what leg 1 left",
                  pairUntouched ? "untouched" : "CHANGED: .top now at block " + HeaderNextBlock(top) + ", checkpoint at "
                                                + Checkpoint.Load(ckpt).NextBlock);

            CheckpointError? e = o2.CheckpointError;
            string m = e?.Message ?? "";
            check(e != null && e.OnDiskUnreadable && e.Resumable && e.OnDiskBlock < 0 && e.OnDiskWritten.HasValue
                  && o2.CheckpointPath == ckpt
                  && m.Contains("could not be read just now either", StringComparison.Ordinal)
                  && m.Contains("once it can be read again, resuming starts from it", StringComparison.Ordinal)
                  && !m.Contains("start the run from the beginning", StringComparison.Ordinal),
                  "THE LAST SAVE: a checkpoint that is there but cannot be read is still named as the resume point - never "
                  + "'there is no checkpoint ... from the beginning'",
                  e == null ? "no error" : m);

            SearchOutcome r;
            SearchSession rs;
            try
            {
                (r, rs) = Resume(dir, second.Results);
            }
            catch (InvalidOperationException ex)
            {
                // What the guessing save led to: a pair from two moments, refused.
                check(false, "and once it is let go, --resume continues from it", "REFUSED: " + ex.Message);
                return;
            }

            bool identical = r.Complete && Same(File.ReadAllBytes(second.Results), refBytes);
            check(identical && r.Passed == refOutcome.Passed && rs.ResultLine(r) == refLine,
                  "and once it is let go, --resume continues from it to the uninterrupted run's bytes and 'top N of M'",
                  (identical ? "IDENTICAL" : "DIFFERENT") + ", " + rs.ResultLine(r));
            check(NoneLeft(second.Checkpoint), "and the finished resume retired every checkpoint file", "");
        }

        // =========================================================================================
        // The read a fresh run makes of a stale checkpoint before its first save: missing or not a
        // checkpoint names nothing (either generation may be written); held, it fails by name.

        private static void CommittedSnapshotRead(Action<bool, string, string> check, string dir)
        {
            Directory.CreateDirectory(dir);
            string ckpt = Path.Combine(dir, "stale.ckpt");
            string? missing = CheckpointStore.CommittedSnapshot(ckpt, Short);
            File.WriteAllText(ckpt, "{}");
            string? notOne = CheckpointStore.CommittedSnapshot(ckpt, Short);

            FileAccessException? held = null;
            using (new FileStream(ckpt, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                try
                {
                    CheckpointStore.CommittedSnapshot(ckpt, Short);
                }
                catch (FileAccessException ex)
                {
                    held = ex;
                }
            }

            check(missing == null && notOne == null && held != null && held.Problem == FileProblem.InUse
                  && held.Path == Path.GetFullPath(ckpt),
                  "a stale checkpoint a fresh run replaces: missing or not a checkpoint names nothing; held so it cannot be read, "
                  + "the save fails naming it instead of guessing",
                  held?.Message ?? "no failure for the held file");
        }

        // =========================================================================================
        // A rotated run whose manifest another program holds at every rotation: no rotation waits for
        // it, and once it is let go, the next flush writes it with every closed segment - even with no
        // segment open, which is when nothing else would until the run's finish.
        //
        // Review of 2026-09-24: each rotation spent the quick schedule's ~1.6 s on the held manifest
        // with the collector standing still (31.3 s for a 6,000-seed run against 2.1 s unheld), and a
        // flush with no segment open returned before writing it, so a kill after the last rotation left
        // a manifest missing closed segments. Rotating at every record here keeps no segment open.

        private static void ManifestPending(Action<bool, string, string> check, string dir)
        {
            Directory.CreateDirectory(dir);
            string outPath = Path.Combine(dir, "rot.jsonl");
            Query q = QueryReader.Parse(Query.Replace("\"keep\":10", "\"keep\":\"all\"", StringComparison.Ordinal), "manifest-pending");
            q.Output.RotateBytes = 1;           // every record closes its segment: none is left open
            q.Output.Compress = false;
            SearchSession s = SearchSession.Create(q, Oracle, Engine, 40, 2, outPath, acceptScanOrder: true, allowVacuous: true,
                                                   checkpointDirectory: Path.Combine(dir, "ckpt"));
            s.PeriodicRetry = Short;
            s.FinalRetry = Short;
            IResultSink? sink = s.Start(resume: false);
            SegmentedResultSink? seg = sink as SegmentedResultSink;
            if (seg == null)
            {
                check(false, "a keep-all run rotating at every record builds a segmented sink (GUARD)", sink?.GetType().Name ?? "no sink");
                sink?.Dispose();
                return;
            }

            File.WriteAllText(seg.ManifestPath, "{\"an earlier run's manifest\": true}\n");
            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            SearchOutcome o;
            using (new FileStream(seg.ManifestPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                o = s.Run(sink, null, TimeSpan.Zero, TimeSpan.FromHours(1));
            }

            sw.Stop();
            string stale = File.ReadAllText(seg.ManifestPath);
            seg.Flush();
            string after = File.ReadAllText(seg.ManifestPath);
            int listed = 0;
            bool complete = true;
            try
            {
                using System.Text.Json.JsonDocument d = System.Text.Json.JsonDocument.Parse(after);
                listed = d.RootElement.GetProperty("segments").GetArrayLength();
                complete = d.RootElement.GetProperty("complete").GetBoolean();
            }
            catch (Exception)
            {
                listed = -1;
            }

            check(o.Passed == 40 && seg.SegmentCount == 40 && sw.Elapsed < TimeSpan.FromSeconds(20)
                  && stale.Contains("an earlier run's manifest", StringComparison.Ordinal),
                  "40 rotations against a held manifest do not wait for it (one attempt each; the next flush retries)",
                  seg.SegmentCount + " segments in " + sw.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture)
                  + " s (the quick schedule at each would be about 64 s)");
            check(listed == 40 && !complete,
                  "once it is let go, a flush with NO segment open writes the pending manifest, listing every closed segment",
                  "manifest lists " + listed + " of " + seg.SegmentCount + " segments, complete " + complete);
            try
            {
                sink!.Finish();
            }
            finally
            {
                sink!.Dispose();
            }
        }

        // =========================================================================================
        // Helpers.

        private static ILocationOracle Oracle => UnavailableLocationOracle.Instance;

        private static SearchSession Session(string checkpointDirectory, string? outPath) =>
            SearchSession.Create(QueryReader.Parse(Query, "access-denied"), Oracle, Engine, Seeds, 2, outPath,
                                 acceptScanOrder: true, allowVacuous: true, checkpointDirectory: checkpointDirectory);

        private static (SearchOutcome, byte[], string) Reference(string dir)
        {
            string results = Path.Combine(dir, "results.jsonl");
            SearchSession s = Session(Path.Combine(dir, "ckpt"), results);
            IResultSink? sink = s.Start(resume: false);
            SearchOutcome o = s.Run(sink, null, TimeSpan.Zero, TimeSpan.FromHours(1));
            sink?.Finish();
            sink?.Dispose();
            return (o, File.Exists(results) ? File.ReadAllBytes(results) : Array.Empty<byte>(), s.ResultLine(o));
        }

        private static (SearchOutcome, SearchSession) Resume(string dir, string results)
        {
            SearchSession s = Session(Path.Combine(dir, "ckpt"), results);
            IResultSink? sink = s.Start(resume: true);
            SearchOutcome o = s.Run(sink, null, TimeSpan.Zero, TimeSpan.FromHours(1));
            sink?.Finish();
            sink?.Dispose();
            return (o, s);
        }

        /// <summary>The message Start refuses with, or null when it accepted the pair.</summary>
        private static string? StartRefusal(string dir, string results)
        {
            SearchSession s = Session(Path.Combine(dir, "ckpt"), results);
            try
            {
                IResultSink? sink = s.Start(resume: true);
                sink?.Dispose();
                return null;
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// A run parked at <paramref name="gateBlock"/> (<see cref="GateBlock"/> when not given), saving
        /// its checkpoint every 100 ms (or <paramref name="interval"/>) on the short schedule - resuming the
        /// checkpoint in <paramref name="dir"/> when asked, with <paramref name="afterStart"/> run between
        /// the session's Start and the scan.
        /// </summary>
        private static Leg Launch(string dir, bool stopOnRelease, bool resume = false, long gateBlock = GateBlock,
                                  TimeSpan? interval = null, Action? afterStart = null)
        {
            string results = Path.Combine(dir, "results.jsonl");
            SearchSession s = Session(Path.Combine(dir, "ckpt"), results);
            s.PeriodicRetry = Short;
            s.FinalRetry = Short;
            Gate g = new Gate { Seed = s.Plan.SeedAt(gateBlock * BlockSize), StopOnRelease = stopOnRelease };
            Leg leg = new Leg { Session = s, Gate = g, Results = results, Checkpoint = s.CheckpointPath };
            IResultSink? sink = s.Start(resume: resume);
            leg.Sink = sink;
            afterStart?.Invoke();
            leg.Task = Task.Run(() => s.Run(sink, null, TimeSpan.Zero, interval ?? Interval,
                run =>
                {
                    g.Run = run;
                    run.EvaluatorFactory = () =>
                    {
                        g.Seen();
                        return new Probe(new SeedEvaluator(s.Compiled, Oracle), g);
                    };
                },
                w =>
                {
                    lock (leg.Live) leg.Live.Add(w);
                }));
            return leg;
        }

        /// <summary>Opens the checkpoint the way a reader does, as soon as its first save lands.</summary>
        private static FileStream? HoldWhenSaved(string ckpt)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < until)
            {
                if (File.Exists(ckpt))
                {
                    try
                    {
                        return new FileStream(ckpt, FileMode.Open, FileAccess.Read, FileShare.Read);
                    }
                    catch (IOException)
                    {
                        // Caught mid-rename; the next save is 100 ms away.
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                Thread.Sleep(5);
            }

            return null;
        }

        private static bool WaitFor(Leg leg, Predicate<string> match)
        {
            DateTime until = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < until)
            {
                lock (leg.Live)
                {
                    if (leg.Live.Exists(match)) return true;
                }

                if (leg.Task != null && leg.Task.IsCompleted) return false;
                Thread.Sleep(10);
            }

            return false;
        }

        private static long HeaderNextBlock(string snapshot)
        {
            string head = SharedRead.AllLines(snapshot)[0];
            int i = head.IndexOf("\"next_block\":", StringComparison.Ordinal);
            if (i < 0) return -1;
            int j = i + "\"next_block\":".Length, k = j;
            while (k < head.Length && char.IsDigit(head[k])) k++;
            return long.Parse(head.Substring(j, k - j), System.Globalization.CultureInfo.InvariantCulture);
        }

        private static void Rewrite(string path, string[] lines, string head)
        {
            string[] copy = (string[])lines.Clone();
            copy[0] = head;
            File.WriteAllText(path, string.Join("\n", copy) + "\n");
        }

        private static bool NoneLeft(string ckpt)
        {
            foreach (string p in CheckpointStore.FilesOf(ckpt))
            {
                if (File.Exists(p)) return false;
            }

            return true;
        }

        private static bool Same(byte[] a, byte[] b) => a.AsSpan().SequenceEqual(b);

        private static void TryDelete(string p)
        {
            try
            {
                if (File.Exists(p)) File.Delete(p);
            }
            catch (Exception)
            {
            }
        }

        private static void DeleteTree(string dir)
        {
            for (int i = 0; i < 10 && Directory.Exists(dir); i++)
            {
                try
                {
                    Directory.Delete(dir, true);
                }
                catch (Exception) when (i < 9)
                {
                    Thread.Sleep(300);
                }
                catch (Exception)
                {
                }
            }
        }

        private sealed class Leg
        {
            public SearchSession Session = null!;
            public Gate Gate = null!;
            public string Results = "";
            public string Checkpoint = "";
            public IResultSink? Sink;
            public Task<SearchOutcome>? Task;
            public readonly List<string> Live = new List<string>();
            public Exception? Error;

            /// <summary>Waits for the run, then finishes and closes its sink. Null when the run threw.</summary>
            public SearchOutcome? Finish()
            {
                SearchOutcome? o = null;
                try
                {
                    if (Task != null && Task.Wait(TimeSpan.FromSeconds(120))) o = Task.Result;
                    else Error = new TimeoutException("the run did not return within 120 s");
                }
                catch (AggregateException ex)
                {
                    Error = ex.InnerException ?? ex;
                }
                finally
                {
                    try
                    {
                        Sink?.Finish();
                    }
                    catch (Exception ex)
                    {
                        Error ??= ex;
                    }

                    Sink?.Dispose();
                }

                return Error == null ? o : null;
            }
        }

        /// <summary>
        /// Parks the worker that evaluates <see cref="Seed"/> until <see cref="Open"/> is set (pressing
        /// Stop as it goes on, when asked), and records every worker thread so the test can see that
        /// each one ended.
        /// </summary>
        private sealed class Gate
        {
            public int Seed;
            public bool StopOnRelease;
            public SearchRun? Run;
            public readonly ManualResetEventSlim Reached = new ManualResetEventSlim(false);
            public readonly ManualResetEventSlim Open = new ManualResetEventSlim(false);
            public readonly ConcurrentDictionary<int, Thread> Threads = new ConcurrentDictionary<int, Thread>();

            public void Seen() => Threads.TryAdd(Environment.CurrentManagedThreadId, Thread.CurrentThread);

            public void Hold()
            {
                Reached.Set();
                Open.Wait(TimeSpan.FromSeconds(120));
                if (StopOnRelease) Run?.RequestStop();
            }

            public int Alive()
            {
                int n = 0;
                foreach (Thread t in Threads.Values) n += t.IsAlive ? 1 : 0;
                return n;
            }

            public bool AllExited() => Threads.Count > 0 && Alive() == 0;
        }

        private sealed class Probe : ISeedEvaluator
        {
            private readonly ISeedEvaluator _inner;
            private readonly Gate _gate;

            public Probe(ISeedEvaluator inner, Gate gate)
            {
                _inner = inner;
                _gate = gate;
            }

            public SeedResult Evaluate(int seed, bool full = false)
            {
                if (seed == _gate.Seed) _gate.Hold();
                return _inner.Evaluate(seed, full);
            }

            public double ConstructSeconds => _inner.ConstructSeconds;
            public double SampleSeconds => _inner.SampleSeconds;
            public double PregenSeconds => _inner.PregenSeconds;
            public double LocationSeconds => _inner.LocationSeconds;
            public long Pregenerated => _inner.Pregenerated;
            public long ProbeAccepts => _inner.ProbeAccepts;
            public long EarlyExits => _inner.EarlyExits;
            public long LocationAborts => _inner.LocationAborts;
            public long LocationSkips => _inner.LocationSkips;
            public long Placed => _inner.Placed;
        }

        /// <summary>A sink that fails on a chosen record, standing in for any exception inside the collector.</summary>
        private sealed class ThrowingSink : IResultSink
        {
            public const string Text = "the sink failed on purpose";
            private readonly int _failAt;

            public ThrowingSink(int failAt) => _failAt = failAt;

            public string Path => "(nowhere)";

            public void Add(SeedResult r)
            {
                if (++Count > _failAt) throw new InvalidOperationException(Text);
            }

            public void Flush()
            {
            }

            public void Finish()
            {
            }

            public long Length => -1;
            public long FileBytes => 0;
            public long Count { get; private set; }
            public long TotalMatches => Count;
            public long Kept => Count;
            public long Dropped => 0;
            public bool IsBounded => false;

            public void Dispose()
            {
            }
        }
    }
}
