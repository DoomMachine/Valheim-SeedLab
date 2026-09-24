using System;
using System.Globalization;
using System.IO;

namespace SeedLab.Search.Execution
{
    /// <summary>
    /// The seeds that survived a funnel's first stage: four bytes each, behind a header that says
    /// which run produced them.
    ///
    /// <para><b>It is an output file, so the project's output rules apply to it.</b> Its size is
    /// <c>32 + 4 x survivors</c>, which is knowable before stage one starts only as a range - so the
    /// caller estimates it from the measured survivor ratio, prints it, and refuses above a ceiling
    /// rather than discovering the problem with a full disk. At a survivor ratio of 1e-6 over the
    /// whole space it is 17 KB; at 1e-2 it is 172 MB; at 0.5 it is 8.6 GB, which is the case that has
    /// to be refused rather than written.</para>
    ///
    /// <para><b>The header is the safety.</b> Stage two must not run against survivors from a
    /// different query, a different range or a different game build, and each of those is a way to get
    /// a plausible wrong answer rather than an error. All three are recorded and all three are checked
    /// on read.</para>
    /// </summary>
    public static class SurvivorList
    {
        /// <summary>'S','V','R','1'.</summary>
        private const uint Magic = 0x31525653;

        /// <summary>
        /// Bytes reserved for each of the two identity strings. They are written FIXED WIDTH, padded
        /// with zeros, so that <see cref="BytesFor"/> is exact arithmetic rather than an estimate that
        /// happens to be close: the ceiling check and the caller's disk estimate both rest on it, and
        /// a size rule that is only approximately right is not a size rule. (Found by its own test,
        /// which measured 72 bytes against a predicted 56.) 64 bytes holds a 16-hex query hash and a
        /// build tag like "1.0.15 / 59f53fb5" with room to spare.
        /// </summary>
        private const int FieldWidth = 64;

        /// <summary>magic + version + from + to + scanned + hash + stamp + count.</summary>
        public const int HeaderBytes = 4 + 4 + 8 + 8 + 8 + FieldWidth + FieldWidth + 4;

        /// <summary>Bytes a list of this many survivors occupies. Exact, not an estimate.</summary>
        public static long BytesFor(long survivors) => HeaderBytes + 4L * Math.Max(0, survivors);

        /// <summary>
        /// The default ceiling on the survivor file, 2 GiB. It is deliberately generous - the file is
        /// the cheapest artefact in the pipeline - and deliberately finite, because a query whose
        /// first stage admits half the space does not want a funnel, it wants a different query.
        /// </summary>
        public const long DefaultMaxBytes = 2L * 1024 * 1024 * 1024;

        public sealed class Header
        {
            public string QueryHash = "";
            public string Stamp = "";
            public long From;
            public long To;
            public long Scanned;
            public long Survivors;

            /// <summary>Survivors as a fraction of the seeds stage one actually evaluated.</summary>
            public double Ratio => Scanned > 0 ? Survivors / (double)Scanned : 0.0;
        }

        /// <summary>
        /// Writes the list. The caller owns the ceiling decision; this refuses to write past
        /// <paramref name="maxBytes"/> rather than truncating, because a truncated survivor list is a
        /// silently smaller search.
        /// </summary>
        public static void Write(string path, Header h, int[] seeds, long maxBytes)
        {
            if (seeds == null) throw new ArgumentNullException(nameof(seeds));
            long need = BytesFor(seeds.Length);
            if (maxBytes > 0 && need > maxBytes)
            {
                throw new InvalidOperationException(
                    "the survivor list would be " + Mib(need) + ", past the " + Mib(maxBytes)
                    + " ceiling. Stage one kept " + N(seeds.Length) + " of " + N(h.Scanned)
                    + " seeds (" + Pct(h.Ratio) + "), which is too many for a funnel to pay for - "
                    + "tighten a cheap must-have goal, or run --strategy sample instead.");
            }

            string dir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";
            Directory.CreateDirectory(dir);
            string tmp = path + ".tmp";

            using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (BinaryWriter w = new BinaryWriter(fs))
            {
                w.Write(Magic);
                w.Write(1);                       // version
                w.Write(h.From);
                w.Write(h.To);
                w.Write(h.Scanned);
                // The identity of the producing run, so a mismatched stage two is an error and not an
                // answer.
                WriteFixed(w, h.QueryHash);
                WriteFixed(w, h.Stamp);
                w.Write(seeds.Length);
                foreach (int s in seeds) w.Write(s);
                fs.Flush(true);
            }

            // Temp-then-rename, the same discipline the checkpoint uses: a half-written survivor list
            // must never be readable as a complete one.
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }

        /// <summary>
        /// Reads the list, or throws with a message naming what disagreed.
        ///
        /// <para><paramref name="expectFrom"/>, <paramref name="expectTo"/> and
        /// <paramref name="expectScanned"/> are the run's OWN range and budget, and they are checked as
        /// hard as the query hash is. They have to be: the range and the budget are deliberately not
        /// part of <c>QueryReader.Hash</c>, so one survivor path is shared by every range and every
        /// budget of the same query. Measured 2026-09-23, before this check existed: a 200-seed stage
        /// one silently served a <c>--seeds 400</c> request, and a run restricted to <c>--from 0 --to
        /// 1000000</c> reused a whole-range list and wrote 26 of 26 records with seeds OUTSIDE the
        /// range it had just printed. The ordinary path's <c>Checkpoint.MustMatch</c> rejects both of
        /// those; this is the same rule for the same reason.</para>
        ///
        /// <para>Pass 0 for <paramref name="expectScanned"/> to skip the budget check only when the
        /// caller genuinely has no budget to compare against.</para>
        /// </summary>
        public static int[] Read(string path, string expectQueryHash, string expectStamp,
                                 long expectFrom, long expectTo, long expectScanned, out Header h)
        {
            using FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            using BinaryReader r = new BinaryReader(fs);
            if (r.ReadUInt32() != Magic)
            {
                throw new InvalidDataException(path + " is not a survivor list (bad magic).");
            }

            int version = r.ReadInt32();
            if (version != 1)
            {
                throw new InvalidDataException(path + " is survivor-list version " + version
                                               + ", which this build does not read.");
            }

            h = new Header { From = r.ReadInt64(), To = r.ReadInt64(), Scanned = r.ReadInt64() };
            h.QueryHash = ReadFixed(r);
            h.Stamp = ReadFixed(r);
            int n = r.ReadInt32();
            if (n < 0) throw new InvalidDataException(path + " declares a negative survivor count.");

            // Checked against what is ACTUALLY left after the header, not against a fixed-width
            // guess: the header carries two length-prefixed strings, so its size is not a constant.
            // A truncated list would otherwise read as a smaller search that looks complete.
            long remaining = fs.Length - fs.Position;
            long needed = 4L * n;
            if (remaining < needed)
            {
                throw new InvalidDataException(
                    path + " declares " + N(n) + " survivors, which needs " + N(needed)
                    + " more bytes, but only " + N(remaining) + " remain. It was truncated; re-run "
                    + "stage one.");
            }

            if (!string.IsNullOrEmpty(expectQueryHash)
                && !string.Equals(h.QueryHash, expectQueryHash, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    path + " was produced by a different query (" + Short(h.QueryHash) + " against this "
                    + "run's " + Short(expectQueryHash) + "). Stage two against another query's "
                    + "survivors would answer a question nobody asked; re-run stage one.");
            }

            if (!string.IsNullOrEmpty(expectStamp) && !string.IsNullOrEmpty(h.Stamp)
                && !string.Equals(h.Stamp, expectStamp, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    path + " was produced against game data " + h.Stamp + ", and this run is "
                    + expectStamp + ". Location placement differs between builds, so these survivors "
                    + "are not this build's; re-run stage one.");
            }

            if (h.From != expectFrom || h.To != expectTo)
            {
                throw new InvalidDataException(
                    path + " was produced over the range " + N(h.From) + " .. " + N(h.To)
                    + ", and this run asks for " + N(expectFrom) + " .. " + N(expectTo)
                    + ". Reusing it would answer about seeds this run never asked to see; re-run "
                    + "stage one, or use a checkpoint path of its own for this range.");
            }

            if (expectScanned > 0 && h.Scanned != expectScanned)
            {
                throw new InvalidDataException(
                    path + " was produced by a stage one that scanned " + N(h.Scanned)
                    + " seeds, and this run asks for " + N(expectScanned)
                    + ". A shorter list would silently shrink the search and a longer one would go "
                    + "past what was asked for; re-run stage one.");
            }

            int[] seeds = new int[n];
            for (int i = 0; i < n; i++) seeds[i] = r.ReadInt32();
            h.Survivors = n;
            return seeds;
        }

        /// <summary>
        /// A UTF-8 string in exactly <see cref="FieldWidth"/> bytes, zero-padded. A value too long to
        /// fit is a programming error rather than a user's problem - the callers pass a 16-hex hash
        /// and a build tag - so it throws instead of silently truncating an identity that something
        /// later compares for equality.
        /// </summary>
        private static void WriteFixed(BinaryWriter w, string? value)
        {
            byte[] raw = System.Text.Encoding.UTF8.GetBytes(value ?? "");
            if (raw.Length > FieldWidth)
            {
                throw new ArgumentException(
                    "a survivor-list identity field is " + raw.Length + " bytes, past the "
                    + FieldWidth + "-byte field. Truncating it would make two different runs compare "
                    + "as equal; pass the build tag rather than the whole DATA-STAMP.");
            }

            byte[] pad = new byte[FieldWidth];
            Array.Copy(raw, pad, raw.Length);
            w.Write(pad);
        }

        private static string ReadFixed(BinaryReader r)
        {
            byte[] raw = r.ReadBytes(FieldWidth);
            int n = 0;
            while (n < raw.Length && raw[n] != 0) n++;
            return System.Text.Encoding.UTF8.GetString(raw, 0, n);
        }

        private static string Short(string hash)
            => string.IsNullOrEmpty(hash) ? "(none)" : (hash.Length <= 12 ? hash : hash.Substring(0, 12));

        private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

        private static string Pct(double f)
            => (f * 100).ToString(f < 0.0001 ? "0.######" : "0.###", CultureInfo.InvariantCulture) + " %";

        private static string Mib(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.##", CultureInfo.InvariantCulture) + " GiB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.##", CultureInfo.InvariantCulture) + " MiB";
            if (bytes >= 1L << 10) return (bytes / (double)(1L << 10)).ToString("0.##", CultureInfo.InvariantCulture) + " KiB";
            return bytes.ToString("N0", CultureInfo.InvariantCulture) + " B";
        }
    }
}
