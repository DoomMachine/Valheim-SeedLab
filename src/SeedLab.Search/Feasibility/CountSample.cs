using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace SeedLab.Search.Feasibility
{
    /// <summary>
    /// The calibration COUNT sample: how many instances of each location type a seed actually got,
    /// over 5,000 uniformly drawn seeds.
    ///
    /// <para><b>Why it exists.</b> Rules D4 and D5 were specified and then deferred, with the reason
    /// written down: they are *defined* by a measured count distribution, and the sample that shipped
    /// until now held distance percentiles only. Wired against that sample they would never fire, and
    /// their tests would have passed for the wrong reason. This file is that missing input.</para>
    ///
    /// <para><b>Why the raw matrix and not per-type histograms.</b> A target is often a group, and the
    /// metric is the SUM over its types. The distribution of a sum is not recoverable from the
    /// marginals - only a bound is - so per-type histograms would force every group query onto a
    /// conservative bound. The whole matrix is 5,000 x 183 <c>uint16</c> = 1.8 MB, which is smaller
    /// than one of the dump's river-point files, and it makes every question exact.</para>
    ///
    /// <para><b>What it may be used for.</b> Warnings only, never a refusal. It is a sample of
    /// 5,000 of 4,294,967,296 seeds: "no sampled seed did X" bounds the rate at
    /// <c>3/n = 0.06 %</c> (95 %, rule of three) and is never a proof. The generator ships a code
    /// path for a shortfall and three location types were measured taking it.</para>
    ///
    /// <para><b>Provenance.</b> Drawn with the tool's own shuffled scan order - a 4-round balanced
    /// Feistel permutation of the whole int32 range, key <c>0xA17A25EED10C5117</c>, permutation
    /// indices 0..4,999 - so the sample is reproducible from seven numbers rather than stored as a
    /// seed list. <c>count-sample.json</c> records them. Over all 915,000 type-seed cells the game's
    /// <c>placed</c> counter and the number of instances actually registered were equal, so there is
    /// one number per cell and not two.</para>
    /// </summary>
    public sealed class CountSample
    {
        public const string JsonFile = "count-sample.json";
        public const string BinaryFile = "count-sample.bin";

        /// <summary>Magic of <see cref="BinaryFile"/>: 'S','L','C','S'.</summary>
        private static readonly byte[] Magic = { 0x53, 0x4C, 0x43, 0x53 };

        private readonly Dictionary<string, int> _column = new Dictionary<string, int>(StringComparer.Ordinal);
        private ushort[] _counts = Array.Empty<ushort>();
        private int[] _seeds = Array.Empty<int>();

        public bool Available { get; private set; }

        /// <summary>Why it is not available, in one sentence. Empty when it is.</summary>
        public string UnavailableReason { get; private set; } = "";

        /// <summary>The DATA-STAMP the sample was measured on.</summary>
        public string Stamp { get; private set; } = "";

        public string Path { get; private set; } = "";

        /// <summary>Number of sampled seeds.</summary>
        public int N { get; private set; }

        /// <summary>Number of location types per seed - the ordered placement list's length.</summary>
        public int TypeCount { get; private set; }

        public bool Knows(string prefab) => _column.ContainsKey(prefab);

        /// <summary>
        /// How many sampled seeds had a summed count strictly below <paramref name="n"/> over
        /// <paramref name="prefabs"/>, or -1 when any of them is missing from the sample - which must
        /// be read as "unknown", never as zero.
        /// </summary>
        public int SeedsBelow(IReadOnlyList<string> prefabs, double n)
        {
            int[]? cols = Columns(prefabs);
            if (cols == null) return -1;

            int below = 0;
            for (int s = 0; s < N; s++)
            {
                long sum = 0;
                int baseIndex = s * TypeCount;
                for (int c = 0; c < cols.Length; c++) sum += _counts[baseIndex + cols[c]];
                if (sum < n) below++;
            }

            return below;
        }

        /// <summary>
        /// How many sampled seeds were missing AT LEAST ONE of <paramref name="prefabs"/> entirely -
        /// the quantity rule D5 needs, and not something the per-type absence rows can be added up
        /// into. -1 when any prefab is unknown.
        /// </summary>
        public int SeedsMissingAny(IReadOnlyList<string> prefabs)
        {
            int[]? cols = Columns(prefabs);
            if (cols == null) return -1;

            int missing = 0;
            for (int s = 0; s < N; s++)
            {
                int baseIndex = s * TypeCount;
                for (int c = 0; c < cols.Length; c++)
                {
                    if (_counts[baseIndex + cols[c]] == 0) { missing++; break; }
                }
            }

            return missing;
        }

        /// <summary>The smallest summed count any sampled seed had, or -1 when a prefab is unknown.</summary>
        public long MinSum(IReadOnlyList<string> prefabs)
        {
            int[]? cols = Columns(prefabs);
            if (cols == null || N == 0) return -1;

            long min = long.MaxValue;
            for (int s = 0; s < N; s++)
            {
                long sum = 0;
                int baseIndex = s * TypeCount;
                for (int c = 0; c < cols.Length; c++) sum += _counts[baseIndex + cols[c]];
                if (sum < min) min = sum;
            }

            return min;
        }

        private int[]? Columns(IReadOnlyList<string> prefabs)
        {
            if (!Available || prefabs == null || prefabs.Count == 0) return null;
            int[] cols = new int[prefabs.Count];
            for (int i = 0; i < prefabs.Count; i++)
            {
                if (!_column.TryGetValue(prefabs[i], out int c)) return null;
                cols[i] = c;
            }

            return cols;
        }

        private static CountSample? _cached;
        private static readonly object Gate = new object();

        /// <summary>
        /// Loads the sample from the folder the atlas was found in, once per process. Never throws:
        /// a missing sample is a state, and the rules that need it fall silent rather than guess.
        /// </summary>
        public static CountSample Load(ConstraintAtlas atlas)
        {
            lock (Gate)
            {
                if (_cached != null) return _cached;
                CountSample s = LoadFrom(Folder(atlas));

                // The sample and the atlas must come from the SAME dumped build. They are read out of
                // the same folder, so this can only disagree if someone dropped a file in by hand -
                // which is exactly the case worth catching, because a count measured on another build
                // would be quoted with this build's type list and look perfectly reasonable.
                if (s.Available && atlas != null && atlas.Available)
                {
                    // An UNREADABLE tag is not a match. The old 'theirs.Length > 0' short-circuit
                    // turned "I cannot tell which build this is" into "carry on", which is the one
                    // case this check exists for. The sample's own tag is required by LoadFrom, so
                    // only the atlas's can be empty here - and an empty one fails closed.
                    string mine = ConstraintAtlas.BuildTagOf(s.Stamp);
                    string theirs = atlas.BuildTag;
                    if (theirs.Length == 0 || !string.Equals(mine, theirs, StringComparison.Ordinal))
                    {
                        s.Available = false;
                        s.UnavailableReason =
                            "the calibration count sample was measured on " + mine
                            + (theirs.Length == 0
                                ? " and the constraint atlas does not carry a readable DATA-STAMP, so "
                                  + "the two cannot be shown to describe the same build"
                                : " but the constraint atlas is " + theirs
                                  + ", so the two do not describe the same build")
                            + "; the count-distribution rules D4 and D5 stay silent rather than quote "
                            + "a number from another game version";
                    }
                }

                _cached = s;
                return _cached;
            }
        }

        /// <summary>For tests: load from a named folder without touching the process-wide cache.</summary>
        public static CountSample LoadFrom(string? folder)
        {
            CountSample s = new CountSample();
            if (string.IsNullOrEmpty(folder))
            {
                s.UnavailableReason =
                    "the calibration count sample (" + JsonFile + ") was not found beside the dumped "
                    + "game data, so the count-distribution rules D4 and D5 have nothing to measure "
                    + "against and stay silent";
                return s;
            }

            string json = System.IO.Path.Combine(folder!, JsonFile);
            string bin = System.IO.Path.Combine(folder!, BinaryFile);
            if (!File.Exists(json) || !File.Exists(bin))
            {
                s.UnavailableReason =
                    "the calibration count sample is incomplete beside the dumped game data ("
                    + (File.Exists(json) ? BinaryFile : JsonFile)
                    + " is missing), so the count-distribution rules D4 and D5 stay silent";
                return s;
            }

            try
            {
                List<string> order = new List<string>();
                using (JsonDocument doc = JsonDocument.Parse(File.ReadAllText(json)))
                {
                    JsonElement root = doc.RootElement;
                    s.Stamp = root.TryGetProperty("stamp", out JsonElement st) ? (st.GetString() ?? "") : "";
                    if (root.TryGetProperty("types", out JsonElement types)
                        && types.ValueKind == JsonValueKind.Array)
                    {
                        // The file states an orderedIndex per row; the binary's columns are that
                        // index, so the rows are placed by it rather than by their position here.
                        SortedDictionary<int, string> byIndex = new SortedDictionary<int, string>();
                        foreach (JsonElement t in types.EnumerateArray())
                        {
                            string prefab = t.TryGetProperty("prefab", out JsonElement p)
                                ? (p.GetString() ?? "") : "";
                            int idx = t.TryGetProperty("orderedIndex", out JsonElement oi)
                                && oi.TryGetInt32(out int v) ? v : -1;
                            if (prefab.Length > 0 && idx >= 0) byIndex[idx] = prefab;
                        }

                        foreach (KeyValuePair<int, string> kv in byIndex) order.Add(kv.Value);
                    }
                }

                byte[] blob = File.ReadAllBytes(bin);
                if (blob.Length < 16
                    || blob[0] != Magic[0] || blob[1] != Magic[1]
                    || blob[2] != Magic[2] || blob[3] != Magic[3])
                {
                    s.UnavailableReason = BinaryFile + " does not start with the expected 'SLCS' magic; "
                                          + "the count-distribution rules D4 and D5 stay silent";
                    return s;
                }

                int version = BitConverter.ToInt32(blob, 4);
                int typeCount = BitConverter.ToInt32(blob, 8);
                int n = BitConverter.ToInt32(blob, 12);
                if (version != 1)
                {
                    s.UnavailableReason = BinaryFile + " is version " + version + ", which this build "
                                          + "does not read; the rules D4 and D5 stay silent";
                    return s;
                }

                long need = 16L + (long)n * (4L + 2L * typeCount);
                if (typeCount <= 0 || n <= 0 || blob.LongLength != need || order.Count != typeCount)
                {
                    s.UnavailableReason =
                        "the calibration count sample is inconsistent (" + JsonFile + " names "
                        + order.Count + " types, " + BinaryFile + " holds " + typeCount + " x " + n
                        + " and is " + blob.LongLength + " bytes where " + need + " were expected); "
                        + "the rules D4 and D5 stay silent";
                    return s;
                }

                s._counts = new ushort[(long)n * typeCount <= int.MaxValue ? n * typeCount : 0];
                if (s._counts.Length == 0)
                {
                    s.UnavailableReason = "the calibration count sample is too large to load";
                    return s;
                }

                s._seeds = new int[n];
                int off = 16;
                for (int i = 0; i < n; i++)
                {
                    s._seeds[i] = BitConverter.ToInt32(blob, off);
                    off += 4;
                    int row = i * typeCount;
                    for (int t = 0; t < typeCount; t++)
                    {
                        s._counts[row + t] = BitConverter.ToUInt16(blob, off);
                        off += 2;
                    }
                }

                for (int t = 0; t < typeCount; t++) s._column[order[t]] = t;

                // The sample must say WHICH BUILD it was measured on. The build check in Load()
                // compares PARSED tags, and BuildTagOf returns "" for a stamp it cannot read - an
                // empty tag gates nothing, so a sample with a garbage or absent stamp was used as if it
                // matched and D4 quoted its numbers. Required here, where the sample is declared
                // usable, so it holds however the sample is loaded.
                if (ConstraintAtlas.BuildTagOf(s.Stamp).Length == 0)
                {
                    s.UnavailableReason =
                        "the calibration count sample does not say which game build it was measured on ("
                        + (s.Stamp.Length == 0
                            ? JsonFile + " has no 'stamp'"
                            : JsonFile + " has a 'stamp' that is not a DATA-STAMP")
                        + "), so it cannot be shown to describe this build; the count-distribution "
                        + "rules D4 and D5 stay silent rather than quote a number that may come from "
                        + "another game version";
                    return s;
                }

                s.N = n;
                s.TypeCount = typeCount;
                s.Path = json;
                s.Available = true;
                return s;
            }
            catch (Exception e)
            {
                s.Available = false;
                s.UnavailableReason = "the calibration count sample could not be read ("
                                      + e.GetType().Name + ": " + e.Message
                                      + "); the rules D4 and D5 stay silent";
                return s;
            }
        }

        /// <summary>
        /// The folder the atlas came from. Tying the sample to the atlas's own path is what keeps the
        /// two from being read out of different dumps - the alternative, a second independent search,
        /// is exactly how a stale sample would pair with a fresh atlas.
        /// </summary>
        private static string? Folder(ConstraintAtlas atlas)
        {
            if (atlas == null || atlas.Path.Length == 0) return null;
            try { return System.IO.Path.GetDirectoryName(atlas.Path); }
            catch (Exception) { return null; }
        }
    }
}
