using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using SeedLab.Runtime.Storage;
using SeedLab.Search.Criteria;

namespace SeedLab.Search.Execution
{
    /// <summary>
    /// The resume point of a long scan, written atomically (temp file, flush, rename) so that a reboot
    /// in the middle of a write cannot leave a half-parsed checkpoint.
    ///
    /// <para><b>Why a single block number is enough.</b> Blocks are <i>claimed</i> out of order by 16
    /// threads but are <i>emitted</i> strictly in order by one collector, so "every block below
    /// <see cref="NextBlock"/> is finished and written" is an invariant, not a hope. A claimed-but-
    /// unfinished block is simply re-run on resume: every tier is a pure function of the seed, so
    /// re-running has no side effect. Together with <see cref="ResultsLength"/> - the byte count the
    /// results file is truncated back to - this makes a resumed run produce the identical result
    /// file, which is the property the acceptance test checks.</para>
    ///
    /// <para>It is JSON on purpose: a user who has been scanning for two days should be able to open
    /// the file and see where they are.</para>
    /// </summary>
    public sealed class Checkpoint
    {
        public const int FormatVersion = 1;

        public int Version = FormatVersion;
        public string Engine = "";
        public string QueryHash = "";
        public int Defs;
        public double Grid;
        public string Order = "shuffled";
        public ulong Key;
        public long From;
        public long To;
        public int BlockSize;
        public long Limit;

        /// <summary>Every block below this index is finished and its results are on disk.</summary>
        public long NextBlock;

        public long SeedsEvaluated;
        public long SeedsPassed;
        public double ElapsedSeconds;
        public string? ResultsPath;

        /// <summary>
        /// Bytes of complete records in the results file at the moment this checkpoint was written,
        /// or -1 when the sink rewrites its file wholesale (a bounded run).
        ///
        /// <para>A hard kill leaves the file LONGER than this: the records written since the last
        /// checkpoint are re-produced by the resumed run, and the very last one is usually torn in
        /// half. Measured on this machine, 2026-09-23: a killed 180 s run left 1,249,280 bytes beyond
        /// this mark, ending mid-token. Truncating back to it on resume is what makes the resumed
        /// file identical to an uninterrupted one.</para>
        /// </summary>
        public long ResultsLength = -1;

        /// <summary>Records on disk at the checkpoint. For a bounded run this is at most <c>keep</c>.</summary>
        public long ResultsKept;

        /// <summary>
        /// The kept-set snapshot a bounded run resumes from, or null. A bounded run's results file is
        /// its best-N set rather than an append log, so the set - not a byte offset - is the state a
        /// resume needs.
        ///
        /// <para><b>This field is what makes the pair atomic</b> (2026-09-24). The snapshot is written
        /// first, under whichever of <c>&lt;ckpt&gt;.top</c> and <c>&lt;ckpt&gt;.top2</c> the checkpoint
        /// on disk does NOT name, and only then is this file renamed into place naming it - so the
        /// rename of this file is the one moment the pair changes, and a failure or a kill before it
        /// leaves the old checkpoint naming the old, untouched snapshot. See
        /// <see cref="CheckpointStore.NextSnapshotPath"/>.</para>
        /// </summary>
        public string? KeptSnapshot;

        public long ProbeAccepts;
        public long EarlyExits;

        /// <summary>
        /// The file <see cref="Load"/> read this from, or null for one built in memory. Not written:
        /// it is where the checkpoint IS, not part of it, and a sentence that tells the user which file
        /// to delete needs it (<see cref="BlockSizing.Decide"/>'s resume refusal).
        /// </summary>
        public string? LoadedFrom;

        /// <summary>
        /// Writes <c>&lt;path&gt;.tmp</c>, flushes it to the device and renames it over
        /// <paramref name="path"/> (<see cref="DurableWrite.Stream"/>), retrying while another program
        /// holds the file on <paramref name="retry"/> - <see cref="RetrySchedule.Quick"/> when not given.
        /// <paramref name="onAttempt"/> hears each attempt, so a caller can say it is waiting.
        /// </summary>
        /// <exception cref="FileAccessException">The file stayed unavailable for the whole schedule; the
        /// message names it and says why. The checkpoint already on disk is untouched.</exception>
        public void Save(string path, RetrySchedule? retry = null, Action<RetryAttempt>? onAttempt = null)
        {
            StringBuilder sb = new StringBuilder(1024);
            sb.Append("{\n");
            sb.Append("  \"version\": ").Append(Version).Append(",\n");
            sb.Append("  \"engine\": ").Append(J(Engine)).Append(",\n");
            sb.Append("  \"query_hash\": ").Append(J(QueryHash)).Append(",\n");
            sb.Append("  \"defs\": ").Append(Defs).Append(",\n");
            sb.Append("  \"grid\": ").Append(D(Grid)).Append(",\n");
            sb.Append("  \"order\": ").Append(J(Order)).Append(",\n");
            sb.Append("  \"key\": ").Append(J("0x" + Key.ToString("X16"))).Append(",\n");
            sb.Append("  \"from\": ").Append(From).Append(",\n");
            sb.Append("  \"to\": ").Append(To).Append(",\n");
            sb.Append("  \"block_size\": ").Append(BlockSize).Append(",\n");
            sb.Append("  \"limit\": ").Append(Limit).Append(",\n");
            sb.Append("  \"next_block\": ").Append(NextBlock).Append(",\n");
            sb.Append("  \"seeds_evaluated\": ").Append(SeedsEvaluated).Append(",\n");
            sb.Append("  \"seeds_passed\": ").Append(SeedsPassed).Append(",\n");
            sb.Append("  \"probe_accepts\": ").Append(ProbeAccepts).Append(",\n");
            sb.Append("  \"early_exits\": ").Append(EarlyExits).Append(",\n");
            sb.Append("  \"elapsed_seconds\": ").Append(D(ElapsedSeconds)).Append(",\n");
            sb.Append("  \"results_path\": ").Append(ResultsPath == null ? "null" : J(ResultsPath)).Append(",\n");
            sb.Append("  \"results_length\": ").Append(ResultsLength).Append(",\n");
            sb.Append("  \"results_kept\": ").Append(ResultsKept).Append(",\n");
            sb.Append("  \"kept_snapshot\": ").Append(KeptSnapshot == null ? "null" : J(KeptSnapshot)).Append('\n');
            sb.Append("}\n");

            // The temp keeps its <ckpt>.tmp name: CheckpointStore.CleanOrphans knows it.
            byte[] b = Encoding.UTF8.GetBytes(sb.ToString());
            DurableWrite.Stream(path, s => s.Write(b, 0, b.Length), retry, tempPath: path + ".tmp", onAttempt: onAttempt);
        }

        /// <summary>
        /// Reads a checkpoint, sharing read, write and delete (<see cref="SharedRead"/>) so that a run
        /// saving or retiring the same file is not stopped by the read.
        /// </summary>
        public static Checkpoint Load(string path)
        {
            using JsonDocument doc = JsonDocument.Parse(SharedRead.AllText(path));
            JsonElement r = doc.RootElement;
            Checkpoint c = new Checkpoint
            {
                Version = r.GetProperty("version").GetInt32(),
                Engine = r.GetProperty("engine").GetString() ?? "",
                QueryHash = r.GetProperty("query_hash").GetString() ?? "",
                Defs = r.GetProperty("defs").GetInt32(),
                Grid = r.GetProperty("grid").GetDouble(),
                Order = r.GetProperty("order").GetString() ?? "shuffled",
                Key = ulong.Parse((r.GetProperty("key").GetString() ?? "0x0").Substring(2), NumberStyles.HexNumber,
                                  CultureInfo.InvariantCulture),
                From = r.GetProperty("from").GetInt64(),
                To = r.GetProperty("to").GetInt64(),
                BlockSize = r.GetProperty("block_size").GetInt32(),
                Limit = r.GetProperty("limit").GetInt64(),
                NextBlock = r.GetProperty("next_block").GetInt64(),
                SeedsEvaluated = r.GetProperty("seeds_evaluated").GetInt64(),
                SeedsPassed = r.GetProperty("seeds_passed").GetInt64(),
                ElapsedSeconds = r.GetProperty("elapsed_seconds").GetDouble(),
                ResultsLength = r.GetProperty("results_length").GetInt64(),
                LoadedFrom = path,
            };

            if (r.TryGetProperty("probe_accepts", out JsonElement pa)) c.ProbeAccepts = pa.GetInt64();
            if (r.TryGetProperty("results_kept", out JsonElement rk)) c.ResultsKept = rk.GetInt64();
            if (r.TryGetProperty("kept_snapshot", out JsonElement ks) && ks.ValueKind == JsonValueKind.String)
            {
                c.KeptSnapshot = ks.GetString();
            }

            if (r.TryGetProperty("early_exits", out JsonElement ee)) c.EarlyExits = ee.GetInt64();
            if (r.TryGetProperty("results_path", out JsonElement rp) && rp.ValueKind == JsonValueKind.String)
            {
                c.ResultsPath = rp.GetString();
            }

            if (c.Version != FormatVersion)
            {
                throw new InvalidOperationException("checkpoint format " + c.Version + ", this build writes "
                                                    + FormatVersion + "; start the run again");
            }

            return c;
        }

        /// <summary>
        /// Refuses to resume a run that is not the same run. Silently continuing a different query into
        /// the same results file is the one failure that would be invisible in the output.
        ///
        /// <para><b>The seed budget is checked before the block size</b> (2026-09-24). A resumed run now
        /// takes its block size from this checkpoint (<see cref="MatchesExceptBlockSize"/>), but only
        /// from one that is the same run on everything else, the budget included - so a changed
        /// <c>--seeds</c> is not adopted, gets the automatic size, and with the old order was told to
        /// pass <c>--block-size</c> for what is really a <c>--seeds</c> problem.</para>
        /// </summary>
        public void MustMatch(Query q, ScanPlan plan, string queryHash)
        {
            Check(QueryHash == queryHash, "the query file has changed since the checkpoint was written");
            Check(Defs == q.Defs, "the metric definitions version has changed");
            Check(Grid == q.Search.Grid, "the grid has changed (" + G(Grid) + " m -> " + G(q.Search.Grid) + " m)");
            Check(Order == plan.Order.ToString().ToLowerInvariant(), "the scan order has changed");
            Check(Key == plan.Key, "the Feistel key has changed, so the seed order is different");
            Check(From == plan.From && To == plan.To, "the seed range has changed");
            Check(Limit == plan.Limit, "the seed budget has changed (" + Limit + " -> " + plan.Limit + ")");

            // A hand edit, never a run: a block holds at least one seed. Not adopted either
            // (BlockSizing.Decide ignores it), so this is the sentence the user gets.
            Check(BlockSize >= 1, "the checkpoint is not valid: its block_size is "
                                  + BlockSize.ToString(CultureInfo.InvariantCulture)
                                  + ", and a block holds at least one seed");
            Check(BlockSize == plan.BlockSize,
                  "the block size has changed (" + BlockSize.ToString(CultureInfo.InvariantCulture)
                  + " in the checkpoint, " + plan.BlockSize.ToString(CultureInfo.InvariantCulture)
                  + " now), so the block boundaries have moved - a resume point is a block number. Pass --block-size "
                  + BlockSize.ToString(CultureInfo.InvariantCulture)
                  + ", or leave the block size out, to resume it");
        }

        /// <summary>
        /// True when this checkpoint is the run <paramref name="plan"/> describes on every field
        /// <see cref="MustMatch"/> checks except the block size - the query hash, the definitions, the
        /// grid, the order, the key, the range and the seed budget - so its block size can be adopted.
        ///
        /// <para><b>Why every one of them.</b> One query hash names one checkpoint file, but not one
        /// run: a funnel's second stage checkpoints under the same hash (a sequential walk of the
        /// survivor list, keyed by a hash of that list, with the survivor count as its limit), and a
        /// sample run with a different <c>--seeds</c> leaves its own file. Adopting from either would
        /// print "from the checkpoint being resumed" for a plan nothing resumes, and a real stage-two
        /// file left by a QA run (sequential, blocks of 4, limit 2,308) would have cut a 6,000-seed
        /// stage one into 1,500 blocks of 4 (design review of 2026-09-24).</para>
        /// </summary>
        public bool MatchesExceptBlockSize(Query q, ScanPlan plan, string queryHash)
            => QueryHash == queryHash
               && Defs == q.Defs
               && Grid == q.Search.Grid
               && Order == plan.Order.ToString().ToLowerInvariant()
               && Key == plan.Key
               && From == plan.From && To == plan.To
               && Limit == plan.Limit;

        private static string G(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        private static void Check(bool ok, string why)
        {
            if (!ok) throw new InvalidOperationException("cannot resume: " + why);
        }

        private static string J(string s) => JsonSerializer.Serialize(s);

        private static string D(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
