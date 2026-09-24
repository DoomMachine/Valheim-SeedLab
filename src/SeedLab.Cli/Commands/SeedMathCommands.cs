using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using SeedLab.Cli.Infra;
using SeedLab.Seeds;

namespace SeedLab.Cli.Commands
{
    /// <summary><c>vseed hash</c> - the seed text to int32 direction, with the two lanes shown.</summary>
    public static class HashCommand
    {
        public const string Help = @"vseed hash <text> [--json]

  The int32 world seed a seed text produces: string.GetStableHashCode, the function the
  game's World constructor (World..ctor) runs on the text before anything else. The empty
  text is seed 0, not random.

  The two lane values are shown because the hash is two interleaved djb2-xor lanes
  combined as even + odd * 1566083941, and that structure is what makes the inverse possible.

Examples:
  vseed hash MWd8eV6svz
  vseed hash '' --json";

        public static int Run(Args a, Out o)
        {
            if (a.Positional.Count < 1) throw new CliException("give a seed text (use '' for the empty text).", ExitCodes.Usage, Help);
            string text = a.Positional[0];
            a.RejectUnknown();

            int seed = StableHash.SeedFromText(text);
            (uint even, uint odd) = StableHash.ComputeLanes(text);

            // Re-verify before printing: the published identity is seed == even + K * odd, and for the
            // empty text World..ctor short-circuits to 0 instead of hashing.
            int recombined = unchecked((int)(even + (uint)StableHash.LaneCombiner * odd));
            bool identityHolds = text.Length == 0 || recombined == seed;
            if (!identityHolds)
            {
                throw new CliException(
                    $"internal check failed: lanes {even}/{odd} recombine to {recombined}, not {seed}.",
                    ExitCodes.CheckFailed);
            }

            if (o.Json)
            {
                var j = o.J;
                j.WriteStartObject();
                j.WriteString("command", "hash");
                j.WriteString("text", text);
                j.WriteNumber("seed", seed);
                j.WriteNumber("lane_even", even);
                j.WriteNumber("lane_odd", odd);
                j.WriteNumber("lane_combiner", StableHash.LaneCombiner);
                j.WriteBoolean("empty_text_special_case", text.Length == 0);
                j.WriteBoolean("identity_verified", identityHolds);
                j.WriteEndObject();
            }
            else
            {
                o.Header("Hash");
                o.Field("text", text.Length == 0 ? "(empty)" : "\"" + text + "\"");
                o.Field("length", text.Length.ToString(CultureInfo.InvariantCulture) + " chars");
                o.Field("int32 seed", seed.ToString(CultureInfo.InvariantCulture));
                o.Field("even lane", even.ToString(CultureInfo.InvariantCulture));
                o.Field("odd lane", odd.ToString(CultureInfo.InvariantCulture));
                o.Field("combiner", StableHash.LaneCombiner.ToString(CultureInfo.InvariantCulture)
                        + "   seed = even + combiner * odd (checked)");
                if (text.Length == 0)
                {
                    o.Note("");
                    o.Note("The game's World constructor (World..ctor) maps the empty seed box to 0 directly, so an");
                    o.Note("empty box is not 'random': it is the specific world with seed 0, the same one the main");
                    o.Note("menu background uses.");
                }
            }

            return ExitCodes.Ok;
        }
    }

    /// <summary><c>vseed invert</c> - int32 back to a typeable seed text.</summary>
    public static class InvertCommand
    {
        public const string Help = @"vseed invert <int32> [options]

  Finds a seed text that hashes to the given int. Every text is re-hashed before it is
  printed, so an unverified preimage is never returned.

Options:
  --alphabet game|alnum   game = the 59 characters the game's own seed generator uses
                          alnum = all 62 alphanumerics (default)
  --length <n>            produce a text of exactly n characters instead of the shortest
  --count <n>             with --length, produce n distinct texts (default 1)
  --json

Notes:
  The shortest text is 6 characters for 73.75 % of all ints and 7 for the remaining 22.92 %;
  every int32 is reachable at 7 characters, and 984,542,424 of them are not reachable at 6.

Examples:
  vseed invert -1772362158
  vseed invert 319486907 --alphabet game --length 10
  vseed invert 0 --json";

        public static int Run(Args a, Out o)
        {
            if (a.Positional.Count < 1) throw new CliException("give an int32 seed.", ExitCodes.Usage, Help);
            if (!int.TryParse(a.Positional[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int seed))
            {
                throw new CliException($"'{a.Positional[0]}' is not an int32.");
            }

            SeedAlphabet alphabet = ParseAlphabet(a.Get("alphabet"));
            int length = a.Int("length", 0);
            int count = a.Int("count", 1);
            a.RejectUnknown();
            if (count < 1 || count > 1000) throw new CliException("--count must be between 1 and 1000.");

            Stopwatch sw = Stopwatch.StartNew();
            List<string> texts = new List<string>();
            if (length > 0)
            {
                if (length > SeedText.MaxEmittedLength)
                {
                    throw new CliException($"--length is capped at {SeedText.MaxEmittedLength}, the longest text known to be typeable in the game's field.");
                }

                Random rng = new Random(12345);
                HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < count * 40 && texts.Count < count; i++)
                {
                    string? t = SeedText.InvertFixedLength(seed, alphabet, length, rng);
                    if (t != null && seen.Add(t)) texts.Add(t);
                }

                if (texts.Count == 0)
                {
                    throw new CliException(
                        $"no {length}-character {alphabet.Name} text hashes to {seed}.",
                        ExitCodes.CheckFailed,
                        length < SeedSpace.ShortestUniversalLength
                            ? $"every int32 is reachable at {SeedSpace.ShortestUniversalLength} characters - try --length {SeedSpace.ShortestUniversalLength}"
                            : null);
                }
            }
            else
            {
                string? t = SeedText.Invert(seed, alphabet);
                if (t == null) throw new CliException($"no {alphabet.Name} text of 10 characters or fewer hashes to {seed}.", ExitCodes.CheckFailed);
                texts.Add(t);
            }

            sw.Stop();

            // Re-verify every text before printing. SeedText already does this internally; doing it
            // again here is cheap and means the printed line is checked by the code that prints it.
            foreach (string t in texts)
            {
                int back = StableHash.SeedFromText(t);
                if (back != seed)
                {
                    throw new CliException($"internal check failed: '{t}' hashes to {back}, not {seed}.", ExitCodes.CheckFailed);
                }
            }

            if (o.Json)
            {
                var j = o.J;
                j.WriteStartObject();
                j.WriteString("command", "invert");
                j.WriteNumber("seed", seed);
                j.WriteString("alphabet", alphabet.Name);
                j.WriteNumber("alphabet_size", alphabet.Count);
                j.WriteStartArray("texts");
                foreach (string t in texts) j.WriteStringValue(t);
                j.WriteEndArray();
                j.WriteBoolean("rehash_verified", true);
                j.WriteNumber("seconds", sw.Elapsed.TotalSeconds);
                j.WriteEndObject();
            }
            else
            {
                o.Header("Invert");
                o.Field("seed", seed.ToString(CultureInfo.InvariantCulture));
                o.Field("alphabet", alphabet.Name + " (" + alphabet.Count + " characters)");
                foreach (string t in texts) o.Field(texts.Count > 1 ? "text" : "seed text", t + "   (" + t.Length + " chars, re-hashed and checked)");
                o.Field("time", Out.F(sw.Elapsed.TotalSeconds * 1000, 1) + " ms");
            }

            return ExitCodes.Ok;
        }

        internal static SeedAlphabet ParseAlphabet(string? s) => s switch
        {
            null or "alnum" => SeedAlphabet.Alnum62,
            "game" => SeedAlphabet.GameAlphabet59,
            _ => throw new CliException($"--alphabet takes 'game' or 'alnum', not '{s}'."),
        };
    }

    /// <summary><c>vseed space</c> - the size and shape of the seed space, re-verified before printing.</summary>
    public static class SpaceCommand
    {
        public const string Help = @"vseed space [options]

  The arithmetic of the seed space: how many seed texts exist, how they collapse onto the
  2^32 worlds, and how long the shortest text for a given world is.

  Every published figure is re-checked against a live computation before it is printed:
  the reachable-lane counts are recomputed from the built tables, a sample of seeds is
  inverted and re-hashed, and the seeds known to need 7 characters are re-tested at 6.

Options:
  --alphabet game|alnum   default alnum (62)
  --samples <n>           inversion round trips to run as the check (default 200)
  --json";

        public static int Run(Args a, Out o)
        {
            SeedAlphabet alphabet = InvertCommand.ParseAlphabet(a.Get("alphabet"));
            int samples = a.Int("samples", 200);
            a.RejectUnknown();
            if (samples < 0 || samples > 100000) throw new CliException("--samples must be between 0 and 100000.");

            UInt128 texts = SeedSpace.TextCount(alphabet);
            double perWorld = SeedSpace.TextsPerWorld(alphabet);

            // --- re-verification -------------------------------------------------------------------
            Stopwatch sw = Stopwatch.StartNew();
            List<string> checks = new List<string>();
            bool ok = true;

            for (int n = 1; n <= 4; n++)
            {
                int live = SeedSpace.LaneReachableCount(alphabet, n);
                long published = SeedSpace.PublishedLaneReachableCount(alphabet, n);
                bool pass = live == published;
                ok &= pass;
                checks.Add($"|E_{n}| computed from the lane tables = {live:N0} vs published {published:N0}  {(pass ? "ok" : "MISMATCH")}");
            }

            Random rng = new Random(20260922);
            int roundTrips = 0, roundTripFails = 0;
            for (int i = 0; i < samples; i++)
            {
                int seed = rng.Next(int.MinValue, int.MaxValue);
                string? t = SeedText.Invert(seed, alphabet);
                if (t == null || StableHash.SeedFromText(t) != seed) roundTripFails++;
                roundTrips++;
            }

            ok &= roundTripFails == 0;
            if (samples > 0)
            {
                checks.Add($"inverse round trips: {roundTrips - roundTripFails:N0}/{roundTrips:N0} texts re-hashed to their seed  {(roundTripFails == 0 ? "ok" : "MISMATCH")}");
            }

            int unreachableTested = 0, unreachableOk = 0;
            if (ReferenceEquals(alphabet, SeedAlphabet.Alnum62))
            {
                foreach (int seed in SeedSpace.KnownUnreachableAtSixAlnum62)
                {
                    unreachableTested++;
                    bool noneAtSix = SeedText.Invert(seed, alphabet, 6) == null;
                    bool someAtSeven = SeedText.Invert(seed, alphabet, SeedSpace.ShortestUniversalLength) != null;
                    if (noneAtSix && someAtSeven) unreachableOk++;
                }

                ok &= unreachableOk == unreachableTested;
                checks.Add($"seeds published as unreachable at 6 characters: {unreachableOk}/{unreachableTested} have no 6-char text and do have a 7-char one  {(unreachableOk == unreachableTested ? "ok" : "MISMATCH")}");
            }

            sw.Stop();

            long reach5 = SeedSpace.ReachableSeedsUpToLength(alphabet, 5);
            long reach6 = SeedSpace.ReachableSeedsUpToLength(alphabet, 6);
            long reach7 = SeedSpace.ReachableSeedsUpToLength(alphabet, 7);

            if (o.Json)
            {
                var j = o.J;
                j.WriteStartObject();
                j.WriteString("command", "space");
                j.WriteString("alphabet", alphabet.Name);
                j.WriteNumber("alphabet_size", alphabet.Count);
                j.WriteNumber("max_length", SeedText.MaxEmittedLength);
                j.WriteString("seed_texts", texts.ToString());
                j.WriteNumber("distinct_worlds", SeedSpace.DistinctWorlds);
                j.WriteNumber("texts_per_world", perWorld);
                j.WriteStartObject("reachable");
                j.WriteNumber("up_to_5", reach5);
                j.WriteNumber("up_to_6", reach6);
                j.WriteNumber("up_to_7", reach7);
                j.WriteNumber("unreachable_at_6", SeedSpace.UnreachableSeedsUpToLength(alphabet, 6));
                j.WriteEndObject();
                j.WriteStartObject("shortest_length_probability");
                j.WriteNumber("at_most_5", SeedSpace.ShortestLengthAtMostProbability(alphabet, 5));
                j.WriteNumber("exactly_6", SeedSpace.ShortestLengthExactProbability(alphabet, 6));
                j.WriteNumber("exactly_7", SeedSpace.ShortestLengthExactProbability(alphabet, 7));
                j.WriteEndObject();
                j.WriteStartArray("verification");
                foreach (string c in checks) j.WriteStringValue(c);
                j.WriteEndArray();
                j.WriteBoolean("verified", ok);
                j.WriteNumber("verification_seconds", sw.Elapsed.TotalSeconds);
                j.WriteEndObject();
            }
            else
            {
                o.Header("Seed space  (" + alphabet.Name + ", texts of 1.." + SeedText.MaxEmittedLength + " characters)");
                o.Field("seed texts", texts.ToString("N0", CultureInfo.InvariantCulture));
                o.Field("distinct worlds", SeedSpace.DistinctWorlds.ToString("N0", CultureInfo.InvariantCulture)
                        + "   = 2^32, because the game's World constructor (World..ctor) keeps only the int");
                o.Field("texts per world", perWorld.ToString("N0", CultureInfo.InvariantCulture)
                        + "   (a mean, not a guarantee)");
                o.Line();
                o.Field("reachable at <= 5", Out.N(reach5) + "   " + Out.Pct((double)reach5 / SeedSpace.DistinctWorlds));
                o.Field("reachable at <= 6", Out.N(reach6) + "   " + Out.Pct((double)reach6 / SeedSpace.DistinctWorlds));
                o.Field("reachable at <= 7", Out.N(reach7) + "   " + Out.Pct((double)reach7 / SeedSpace.DistinctWorlds) + "  (complete)");
                o.Field("unreachable at 6", Out.N(SeedSpace.UnreachableSeedsUpToLength(alphabet, 6))
                        + "   includes seed 0 itself");
                o.Line();
                o.Field("shortest is <= 5", Out.Pct(SeedSpace.ShortestLengthAtMostProbability(alphabet, 5)));
                o.Field("shortest is 6", Out.Pct(SeedSpace.ShortestLengthExactProbability(alphabet, 6)));
                o.Field("shortest is 7", Out.Pct(SeedSpace.ShortestLengthExactProbability(alphabet, 7)));

                o.Header("Re-verified now  (" + Out.F(sw.Elapsed.TotalSeconds, 2) + " s)");
                foreach (string c in checks) o.Note(c);
                o.Line();
                o.Note(ok ? "all checks passed" : "AT LEAST ONE CHECK FAILED - the numbers above are not trustworthy");
            }

            return ok ? ExitCodes.Ok : ExitCodes.CheckFailed;
        }
    }
}
