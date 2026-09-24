using System;
using System.Text;
using SeedLab.Seeds;

namespace SeedLabAcceptanceTests
{
    /// <summary>
    /// The seed maths: the hash itself (T0 of 05-validation.md section 4) and the inverse, which is
    /// what lets a search result be handed back to the user as something they can type into the
    /// create-world box.
    /// </summary>
    public static class SeedChecks
    {
        /// <summary>
        /// A second, independent transcription of StringExtensionMethods.GetStableHashCode
        /// (decomp/StringExtensionMethods.cs 62-76). It exists so that an edit to SeedLab.Seeds cannot
        /// quietly redefine the hash and still pass: the two implementations are compared on random
        /// strings below.
        /// </summary>
        private static int ReferenceHash(string s)
        {
            unchecked
            {
                int num = 5381;
                int num2 = num;
                for (int i = 0; i < s.Length && s[i] != 0; i += 2)
                {
                    num = ((num << 5) + num) ^ s[i];
                    if (i == s.Length - 1 || s[i + 1] == '\0') break;
                    num2 = ((num2 << 5) + num2) ^ s[i + 1];
                }
                return num + num2 * 1566083941;
            }
        }

        public static void Run(Report rep, int roundTripSamples)
        {
            // ---- T0 ---------------------------------------------------------------------------------
            // 05-validation.md T0. Note the empty string does NOT hash to 0: GetStableHashCode("") runs
            // no loop iteration and returns unchecked(5381 + 5381 * 1566083941) = 371857150. The 0 is a
            // separate rule one level up, in World..ctor.
            (string text, int expect)[] vectors =
            {
                ("MWd8eV6svz", -1772362158),
                ("hnBd9gJf2G", 319486907),
                ("j", 372029384),
                ("", 371857150),
            };
            bool t0 = true;
            StringBuilder sb = new StringBuilder();
            foreach ((string text, int expect) in vectors)
            {
                int got = StableHash.Compute(text);
                if (got != expect) t0 = false;
                if (sb.Length > 0) sb.Append("; ");
                sb.Append('"').Append(text).Append("\" -> ").Append(got);
            }
            bool emptyWorld = StableHash.SeedFromText("") == 0;
            rep.Add("T0", "GetStableHashCode vectors", t0 && emptyWorld,
                    sb + "; SeedFromText(\"\") = " + StableHash.SeedFromText("") + " (World..ctor's separate rule)");

            // ---- the hash wraps at 32 bits, on long random strings ----------------------------------
            Random rng = new Random(20260922);
            bool refMatch = true;
            const string pool = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 _-.";
            for (int i = 0; i < 100_000 && refMatch; i++)
            {
                int len = rng.Next(0, 40);
                char[] c = new char[len];
                for (int k = 0; k < len; k++) c[k] = pool[rng.Next(pool.Length)];
                string s = new string(c);
                if (StableHash.Compute(s) != ReferenceHash(s)) refMatch = false;
            }
            rep.Add("T0b", "StableHash agrees with a second transcription of the decompiled body",
                    refMatch, "100,000 random strings up to 40 chars (unchecked 32-bit wraparound throughout)");

            // ---- the real world, end to end ---------------------------------------------------------
            // "MWd8eV6svz" is the seed text stored in asdasdasd's .fwl2; -1772362158 is the int in the
            // same file and in cacheMinimapMeta (both re-read in the save section).
            int real = StableHash.SeedFromText(WorldFixture.Development.SeedText);
            string? back = SeedText.Invert(real, SeedAlphabet.GameAlphabet59);
            bool realOk = real == WorldFixture.Development.Seed
                          && back != null && StableHash.Compute(back) == real;
            rep.Add("T0c", "'MWd8eV6svz' -> " + real + " -> a text that hashes back", realOk,
                    "inverse text \"" + back + "\" (length " + (back?.Length ?? -1)
                    + ") re-hashes to " + (back == null ? "n/a" : StableHash.Compute(back).ToString())
                    + "; the two texts are different strings opening the same world, which is expected -"
                    + " 4.29e9 worlds have far more than 4.29e9 names");

            // ---- round trip over a large random sample ----------------------------------------------
            foreach (SeedAlphabet alpha in new[] { SeedAlphabet.GameAlphabet59, SeedAlphabet.Alnum62 })
            {
                Random r2 = new Random(1234567);
                long bad = 0, nulls = 0;
                int[] lengthHist = new int[12];
                for (int i = 0; i < roundTripSamples; i++)
                {
                    int seed = r2.Next(int.MinValue, int.MaxValue);
                    string? t = SeedText.Invert(seed, alpha);
                    if (t == null) { nulls++; continue; }
                    if (t.Length < lengthHist.Length) lengthHist[t.Length]++;
                    if (StableHash.Compute(t) != seed) bad++;
                }
                StringBuilder hist = new StringBuilder();
                for (int L = 0; L < lengthHist.Length; L++)
                    if (lengthHist[L] > 0) hist.Append(hist.Length > 0 ? ", " : "").Append(L).Append(':').Append(lengthHist[L]);
                rep.Add("T0d/" + alpha.Name, "invert then re-hash", bad == 0 && nulls == 0,
                        roundTripSamples.ToString("N0") + " random int32 seeds, " + bad + " wrong, " + nulls
                        + " with no text found; shortest-text lengths " + hist);
            }

            // Also exercise the 10-character game-style generator, which takes a different path.
            {
                Random r3 = new Random(777);
                long bad = 0;
                for (int i = 0; i < 20_000; i++)
                {
                    int seed = r3.Next(int.MinValue, int.MaxValue);
                    string t = SeedText.GenerateGameStyle(seed, r3);
                    if (t.Length != 10 || StableHash.Compute(t) != seed) bad++;
                }
                rep.Add("T0e", "10-character game-style texts re-hash", bad == 0,
                        "20,000 seeds, " + bad + " wrong");
            }
        }
    }
}
