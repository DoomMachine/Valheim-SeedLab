using System;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
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
        /// resume needs, and it is written in the same atomic step as this file.
        /// </summary>
        public string? KeptSnapshot;

        public long ProbeAccepts;
        public long EarlyExits;

        public void Save(string path)
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

            string tmp = path + ".tmp";
            string? dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                byte[] b = Encoding.UTF8.GetBytes(sb.ToString());
                fs.Write(b, 0, b.Length);
                fs.Flush(true);
            }

            File.Move(tmp, path, overwrite: true);
        }

        public static Checkpoint Load(string path)
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
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
        /// </summary>
        public void MustMatch(Query q, ScanPlan plan, string queryHash)
        {
            Check(QueryHash == queryHash, "the query file has changed since the checkpoint was written");
            Check(Defs == q.Defs, "the metric definitions version has changed");
            Check(Grid == q.Search.Grid, "the grid has changed (" + Grid + " m -> " + q.Search.Grid + " m)");
            Check(Order == plan.Order.ToString().ToLowerInvariant(), "the scan order has changed");
            Check(Key == plan.Key, "the Feistel key has changed, so the seed order is different");
            Check(From == plan.From && To == plan.To, "the seed range has changed");
            Check(BlockSize == plan.BlockSize, "the block size has changed, so the block boundaries have moved");
            Check(Limit == plan.Limit, "the seed budget has changed (" + Limit + " -> " + plan.Limit + ")");
        }

        private static void Check(bool ok, string why)
        {
            if (!ok) throw new InvalidOperationException("cannot resume: " + why);
        }

        private static string J(string s) => JsonSerializer.Serialize(s);

        private static string D(double v) => v.ToString("R", CultureInfo.InvariantCulture);
    }
}
