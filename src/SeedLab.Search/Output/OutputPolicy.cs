using System;
using System.Globalization;
using SeedLab.Search.Criteria;

namespace SeedLab.Search.Output
{
    /// <summary>What a run does when its result budget is reached.</summary>
    public enum OnLimit
    {
        /// <summary>DEFAULT. Stop cleanly at the ceiling, flush, checkpoint, print the resume command.</summary>
        Stop,

        /// <summary>Keep running and drop the lowest-scoring records. Requires an explicit confirmation.</summary>
        Evict,
    }

    /// <summary>
    /// A per-segment reduction. Only reductions that MERGE EXACTLY across segments are allowed: a
    /// segment is reduced and its raw records are deleted, so anything whose whole-run answer cannot
    /// be rebuilt from the per-segment answers would be silently wrong afterwards.
    ///
    /// <para>Mergeable: top-N by score (the top N of the union is the top N of the per-segment top
    /// Ns), counts, sums, minima and maxima. NOT mergeable: medians, percentiles, "most diverse N",
    /// and anything else that needs the whole multiset. <see cref="ReduceSpec.Parse"/> refuses those
    /// by name rather than computing a number that looks fine and is not.</para>
    /// </summary>
    public sealed class ReduceSpec
    {
        private ReduceSpec(string kind, int n, string text)
        {
            Kind = kind;
            N = n;
            Text = text;
        }

        /// <summary>"none", "top", "count".</summary>
        public string Kind { get; }

        public int N { get; }

        /// <summary>The spelling the user wrote, for the manifest and the report.</summary>
        public string Text { get; }

        public bool IsNone => Kind == "none";

        public static readonly ReduceSpec None = new ReduceSpec("none", 0, "none");

        public static ReduceSpec Parse(string text)
        {
            string t = (text ?? "").Trim().ToLowerInvariant();
            if (t.Length == 0 || t == "none") return None;

            int colon = t.IndexOf(':');
            string kind = colon < 0 ? t : t.Substring(0, colon);
            string arg = colon < 0 ? "" : t.Substring(colon + 1);

            switch (kind)
            {
                case "top":
                    if (!int.TryParse(arg, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) || n < 1)
                    {
                        throw new QueryException("reduce", "'" + text + "' needs a positive N, as in 'top:1000'");
                    }

                    return new ReduceSpec("top", n, "top:" + n.ToString(CultureInfo.InvariantCulture));

                case "count":
                    return new ReduceSpec("count", 0, "count");

                // Named and refused, with the reason, because the failure would otherwise be silent.
                case "median":
                case "percentile":
                case "p50":
                case "p90":
                case "p95":
                case "p99":
                case "quantile":
                case "diverse":
                case "histogram":
                    throw new QueryException("reduce",
                        "'" + text + "' cannot be computed per segment and merged",
                        "a segment is reduced and its raw records are then DELETED, so only reductions that "
                        + "merge exactly are allowed: top:N, count, sum, min and max. A median or a percentile "
                        + "of the whole run cannot be rebuilt from per-segment medians - the answer would be "
                        + "wrong and nothing would say so. Reduce with top:N and compute the quantile from the "
                        + "kept records, or run without --reduce and keep the segments.");

                default:
                    throw new QueryException("reduce", "'" + text + "' is not a reduction",
                        "mergeable reductions: top:N, count");
            }
        }
    }

    /// <summary>
    /// Everything about where a run's results go, resolved once, before a seed is touched.
    ///
    /// <para>The defaults are the user's decisions (docs\studies\decisions-1.md sections 1, 2 and 5):
    /// <b>bounded at 1000 by default</b>, <b>stop at the ceiling</b> rather than evict, and rotation
    /// with gzip whenever the run is unbounded.</para>
    /// </summary>
    public sealed class OutputPolicy
    {
        /// <summary>The results file, or null for a run that keeps nothing on disk.</summary>
        public string? Path;

        public ResultFormat Format = ResultFormat.Jsonl;

        /// <summary>False: keep the best <see cref="Keep"/>. True: <c>--keep all</c>, stream everything.</summary>
        public bool KeepAll;

        /// <summary>The real cap on the file when <see cref="KeepAll"/> is false.</summary>
        public int Keep = 1000;

        public OnLimit OnLimit = OnLimit.Stop;

        /// <summary>Uncompressed bytes per segment before rotating. 0 means "do not rotate".</summary>
        public long RotateBytes;

        /// <summary>gzip each closed segment. On by default whenever rotation is on (JSONL gzips 8-15x).</summary>
        public bool Compress = true;

        public ReduceSpec Reduce = ReduceSpec.None;

        /// <summary>A hard ceiling on total result bytes, 0 for none. <see cref="OnLimit"/> decides what happens at it.</summary>
        public long MaxBytes;

        public bool IsRotating => KeepAll && RotateBytes > 0;

        /// <summary>1 GB, the decided segment size.</summary>
        public const long DefaultRotateBytes = 1L << 30;

        /// <summary>
        /// Checks the combinations that cannot be honoured, before anything is written. Each refusal
        /// names the fix; none of them is a warning, because each would produce a file that looks
        /// right and is not.
        /// </summary>
        public void Validate()
        {
            if (Keep < 1) throw new QueryException("keep", "must be at least 1, or 'all'");

            if (IsRotating && Format == ResultFormat.Json)
            {
                throw new QueryException("rotate",
                    "a rotated JSON array is not valid JSON",
                    "segments are line-oriented: write .jsonl (or .csv) instead of .json, or drop --rotate");
            }

            if (OnLimit == OnLimit.Evict && KeepAll)
            {
                // Eviction means "drop the worst record to make room for a better one", which only
                // means anything when there is a ranked set to drop from. A streaming run has already
                // written its records in scan order; the only thing a ceiling can do to it is stop it.
                // Accepting the flag here would leave a run that ignores its ceiling and fills the
                // disk, which is the one behaviour that was never an option.
                throw new QueryException("on_limit",
                    "'evict' has nothing to evict from in a keep: all run",
                    "a streaming run's records are already on disk in scan order, so at the ceiling it can "
                    + "only stop (on_limit: stop, the default) - or use a bounded run (keep: N), where "
                    + "'evict' means 'keep going and drop the lowest-scoring records'");
            }

            if (!Reduce.IsNone && !IsRotating)
            {
                throw new QueryException("reduce",
                    "--reduce reduces a segment when it closes, and this run has no segments",
                    "use --keep all --rotate <size> to segment the output, or --keep N for a bounded run "
                    + "(a bounded run is already a top-N reduction)");
            }

            if (KeepAll && RotateBytes <= 0 && MaxBytes <= 0)
            {
                // Not refused - the user may genuinely want one flat file - but it is the one
                // configuration that can fill a disk, so it must be said out loud by the caller.
                Warning = "keep: all without rotation writes one unbounded file; the audit measured "
                          + "7.36 TB for a whole-space run of a query with no must-have goal";
            }
        }

        /// <summary>Set by <see cref="Validate"/> when the configuration is legal but dangerous.</summary>
        public string? Warning;

        /// <summary>
        /// The size this run's output will reach, from the MEASURED per-record law
        /// (<see cref="RecordFormatter.EstimatedBytesPerRecord"/>) and a match count.
        /// </summary>
        public long EstimatedBytes(long matches, int goals)
        {
            long records = KeepAll ? matches : Math.Min(matches, Keep);
            double per = RecordFormatter.EstimatedBytesPerRecord(Format, goals);
            double bytes = records * per;
            if (IsRotating && Compress) bytes /= 10.0;   // measured JSONL gzip ratio is 8-15x; 10x is the middle
            return (long)Math.Min(bytes, long.MaxValue);
        }
    }
}
