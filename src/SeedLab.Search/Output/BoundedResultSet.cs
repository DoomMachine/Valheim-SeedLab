using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SeedLab.Search.Output
{
    /// <summary>
    /// The best <c>N</c> results seen so far, and nothing else.
    ///
    /// <para><b>Why a heap and not "write everything and sort later".</b> The disk audit measured what
    /// "later" costs: <c>vseed search custom --all --out results.json</c> would have written a single
    /// <b>7.36 TB</b> file, because <c>custom</c> has no must-have goal and therefore matches 100 % of
    /// seeds. With this set the same run writes <c>N</c> records - 1.7 MB at the default 1000 - and the
    /// memory is <c>N</c> entries, not 4.29e9. Disk is O(N), independent of how many seeds were
    /// scanned. That is the whole point.</para>
    ///
    /// <para><b>Order.</b> Score descending, then seed ascending - the same total order
    /// <c>SearchRun.CompareResults</c> uses, so the kept set and the run's own top list cannot
    /// disagree, and a re-run of the same query produces a byte-identical file.</para>
    ///
    /// <para><b>What it remembers per entry</b> is the formatted record text, not the
    /// <c>SeedResult</c>: the text is what the file needs, it is a fraction of the object's size, and
    /// it makes the snapshot below a plain file rather than a serialisation problem.</para>
    /// </summary>
    public sealed class BoundedResultSet
    {
        /// <summary>One kept result: enough to order it, write it and snapshot it.</summary>
        public readonly struct Entry
        {
            public Entry(int seed, double score, string text)
            {
                Seed = seed;
                Score = score;
                Text = text;
            }

            public int Seed { get; }
            public double Score { get; }

            /// <summary>The record exactly as the file holds it (including its newline for the line formats).</summary>
            public string Text { get; }
        }

        // A binary heap whose ROOT is the WORST kept entry, so the cut line is _heap[0] and an offer is
        // decided in O(1) and inserted in O(log N).
        private readonly List<Entry> _heap = new List<Entry>();
        private readonly int _keep;

        public BoundedResultSet(int keep)
        {
            if (keep < 1) throw new ArgumentOutOfRangeException(nameof(keep), keep, "keep must be at least 1");
            _keep = keep;
        }

        public int Keep => _keep;

        /// <summary>Every match offered, whether or not it was kept. "top 1000 of 131,076" - this is the 131,076.</summary>
        public long TotalMatches { get; private set; }

        /// <summary>Matches offered and not kept, including entries evicted by a better one.</summary>
        public long Dropped { get; private set; }

        public int Count => _heap.Count;

        /// <summary>The score of the worst kept entry: the cut line. NaN while the set is not yet full.</summary>
        public double CutScore => _heap.Count >= _keep && _heap.Count > 0 ? _heap[0].Score : double.NaN;

        /// <summary>The cut line at the moment the first record was dropped. NaN when nothing was dropped.</summary>
        public double FirstDropScore { get; private set; } = double.NaN;

        /// <summary>Matches seen when the first record was dropped, so a report can say "from match 1,001 on".</summary>
        public long FirstDropAtMatch { get; private set; } = -1;

        /// <summary>Offers one match. Returns true when it is now in the kept set.</summary>
        public bool Offer(int seed, double score, string text)
        {
            TotalMatches++;
            if (_heap.Count < _keep)
            {
                _heap.Add(new Entry(seed, score, text));
                SiftUp(_heap.Count - 1);
                return true;
            }

            if (Dropped == 0)
            {
                FirstDropScore = _heap[0].Score;
                FirstDropAtMatch = TotalMatches;
            }

            Dropped++;
            if (!Better(seed, score, _heap[0].Seed, _heap[0].Score)) return false;

            _heap[0] = new Entry(seed, score, text);
            SiftDown(0);
            return true;
        }

        /// <summary>The kept set in output order: score descending, then seed ascending.</summary>
        public List<Entry> Ordered()
        {
            List<Entry> all = new List<Entry>(_heap);
            all.Sort(static (a, b) =>
            {
                int c = b.Score.CompareTo(a.Score);
                return c != 0 ? c : a.Seed.CompareTo(b.Seed);
            });

            return all;
        }

        /// <summary>Is (seedA, scoreA) ahead of (seedB, scoreB) in the output order?</summary>
        public static bool Better(int seedA, double scoreA, int seedB, double scoreB)
        {
            if (scoreA != scoreB) return scoreA > scoreB;
            return seedA < seedB;
        }

        // ---- the snapshot ---------------------------------------------------------------------------
        //
        // A bounded run's results file is rewritten wholesale from this set, so the set IS the
        // resumable state. It is written beside the checkpoint, atomically, in the same step - a
        // checkpoint whose heap is missing would resume into a file holding only the tail of the run
        // and still report the full match count, which is exactly the silent data loss the audit
        // found in the streaming path.

        /// <summary>Writes the set (and its counters) to <paramref name="path"/>, temp file then rename.</summary>
        public void SaveSnapshot(string path)
        {
            StringBuilder sb = new StringBuilder(64 * 1024);
            sb.Append("{\"v\":1,\"keep\":").Append(_keep.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"total\":").Append(TotalMatches.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"dropped\":").Append(Dropped.ToString(CultureInfo.InvariantCulture));
            sb.Append(",\"first_drop_score\":").Append(RecordFormatter.N(FirstDropScore));
            sb.Append(",\"first_drop_at\":").Append(FirstDropAtMatch.ToString(CultureInfo.InvariantCulture));
            sb.Append("}\n");
            foreach (Entry e in Ordered())
            {
                sb.Append("{\"seed\":").Append(e.Seed.ToString(CultureInfo.InvariantCulture));
                sb.Append(",\"score\":").Append(RecordFormatter.N(e.Score));
                sb.Append(",\"text\":").Append(JsonSerializer.Serialize(e.Text));
                sb.Append("}\n");
            }

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

        /// <summary>Reads a snapshot back. Throws when the file is not a snapshot of this keep size.</summary>
        public static BoundedResultSet LoadSnapshot(string path, int keep)
        {
            string[] lines = File.ReadAllLines(path);
            if (lines.Length == 0) throw new InvalidOperationException("the kept-results snapshot '" + path + "' is empty");

            BoundedResultSet set = new BoundedResultSet(keep);
            using (JsonDocument head = JsonDocument.Parse(lines[0]))
            {
                JsonElement h = head.RootElement;
                int snapKeep = h.GetProperty("keep").GetInt32();
                if (snapKeep != keep)
                {
                    throw new InvalidOperationException(
                        "the kept-results snapshot was written with keep = " + snapKeep + " and this run asks for "
                        + keep + "; the two runs do not keep the same set, so resuming would report a mixture");
                }

                set.TotalMatches = h.GetProperty("total").GetInt64();
                set.Dropped = h.GetProperty("dropped").GetInt64();
                set.FirstDropScore = h.TryGetProperty("first_drop_score", out JsonElement fd)
                                     && fd.ValueKind == JsonValueKind.Number ? fd.GetDouble() : double.NaN;
                set.FirstDropAtMatch = h.TryGetProperty("first_drop_at", out JsonElement fa) ? fa.GetInt64() : -1;
            }

            for (int i = 1; i < lines.Length; i++)
            {
                if (lines[i].Length == 0) continue;
                using JsonDocument d = JsonDocument.Parse(lines[i]);
                JsonElement e = d.RootElement;
                set._heap.Add(new Entry(e.GetProperty("seed").GetInt32(),
                                        e.GetProperty("score").GetDouble(),
                                        e.GetProperty("text").GetString() ?? ""));
            }

            // Restore the heap property: the file is in output order, which is the reverse of it.
            for (int i = set._heap.Count / 2 - 1; i >= 0; i--) set.SiftDown(i);
            return set;
        }

        // ---- heap mechanics -------------------------------------------------------------------------

        /// <summary>True when a is WORSE than b, i.e. a belongs closer to the root.</summary>
        private static bool Worse(in Entry a, in Entry b) => !Better(a.Seed, a.Score, b.Seed, b.Score);

        private void SiftUp(int i)
        {
            while (i > 0)
            {
                int parent = (i - 1) / 2;
                if (!Worse(_heap[i], _heap[parent])) break;
                (_heap[i], _heap[parent]) = (_heap[parent], _heap[i]);
                i = parent;
            }
        }

        private void SiftDown(int i)
        {
            int n = _heap.Count;
            while (true)
            {
                int l = 2 * i + 1, r = l + 1, worst = i;
                if (l < n && Worse(_heap[l], _heap[worst])) worst = l;
                if (r < n && Worse(_heap[r], _heap[worst])) worst = r;
                if (worst == i) break;
                (_heap[i], _heap[worst]) = (_heap[worst], _heap[i]);
                i = worst;
            }
        }
    }
}
