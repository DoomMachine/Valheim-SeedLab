using System;
using System.Collections.Generic;

namespace SeedLab.Search.Locations
{
    /// <summary>
    /// The order a list of places is shown in: alphabetical by the string the user can actually see,
    /// with runs of digits compared as numbers so <c>WoodHouse9</c> comes before <c>WoodHouse10</c>.
    ///
    /// <para><b>The sort key is the EXACT visible string.</b> No article stripping. "The Elder" and
    /// "The Queen" sort under T, which makes the eight-row boss group read
    /// <c>Bonemass, Eikthyr, Fader, Kall Fimbulbringer, Moder, The Elder, The Queen, Yagluth</c>.
    /// Dropping the article would make the visible first letters read B, E, T, F, K, M, T, Y -
    /// indistinguishable from unsorted to anyone scanning the column, and it would be reported as a
    /// bug. A sort key that differs from what is on screen is a bug magnet; over eight rows it buys
    /// nothing.</para>
    ///
    /// <para><b>Why <c>ToLowerInvariant</c> then <c>Ordinal</c>, and not
    /// <c>OrdinalIgnoreCase</c>.</b> <c>OrdinalIgnoreCase</c> folds to UPPER, which puts <c>_</c>
    /// (0x5F) AFTER the letters (0x41-0x5A) instead of before them (0x61-0x7A) - the opposite of
    /// JavaScript's <c>toLowerCase</c>, and this order is computed in C# and consumed by a page that
    /// must not disagree with it. No pair among the 183 placed types in 1.0.15 happens to trip the
    /// difference, which is exactly why the golden listing in the test suite has to exist: nothing
    /// else would catch a regression here.</para>
    ///
    /// <para>The order is computed once, server-side, and emitted as an integer
    /// (<see cref="LocationPresentation.SortIndex"/>). The page sorts by that integer and never
    /// compares names itself, which removes the whole class of C#-versus-JavaScript collation
    /// drift.</para>
    /// </summary>
    public static class LocationDisplayOrder
    {
        /// <summary>
        /// Compares two rows by their visible text, tie-breaking on the prefab so the order is total.
        /// <paramref name="aText"/> is <c>DisplayName ?? Prefab</c> - the string on screen, whatever
        /// produced it.
        /// </summary>
        public static int Compare(string aText, string aPrefab, string bText, string bPrefab)
        {
            int c = CompareNatural(aText ?? "", bText ?? "");
            return c != 0 ? c : string.CompareOrdinal(aPrefab ?? "", bPrefab ?? "");
        }

        /// <summary>A comparer over (visible text, prefab) pairs, for sorting a list directly.</summary>
        public static readonly IComparer<KeyValuePair<string, string>> ByTextThenPrefab =
            new PairComparer();

        /// <summary>
        /// Alternating runs of non-digits and digits. A non-digit run is compared case-insensitively
        /// through <c>ToLowerInvariant</c> + ordinal; a digit run is compared as a number. A string
        /// that is a prefix of the other sorts first, which is what puts <c>Mistlands_Statue1</c>
        /// before <c>Mistlands_StatueGroup1</c> and <c>SwampHut1</c> before <c>SwampHut1_1</c>.
        /// </summary>
        public static int CompareNatural(string a, string b)
        {
            int i = 0, j = 0;
            while (i < a.Length && j < b.Length)
            {
                bool da = char.IsDigit(a[i]), db = char.IsDigit(b[j]);
                if (da && db)
                {
                    int ia = i, jb = j;
                    while (i < a.Length && char.IsDigit(a[i])) i++;
                    while (j < b.Length && char.IsDigit(b[j])) j++;

                    int c = CompareDigitRun(a, ia, i, b, jb, j);
                    if (c != 0) return c;
                    continue;
                }

                if (da != db)
                {
                    // One side has a digit where the other has a letter: fall back to the character
                    // comparison, so "Crypt2" and "CryptA" still have a defined order.
                    int c = CompareChar(a[i], b[j]);
                    return c != 0 ? c : (da ? -1 : 1);
                }

                int cc = CompareChar(a[i], b[j]);
                if (cc != 0) return cc;
                i++;
                j++;
            }

            return (a.Length - i).CompareTo(b.Length - j);
        }

        private static int CompareChar(char a, char b)
        {
            char la = char.ToLowerInvariant(a), lb = char.ToLowerInvariant(b);
            return la == lb ? 0 : (la < lb ? -1 : 1);
        }

        /// <summary>
        /// Two runs of digits as numbers, without parsing: leading zeros are skipped, then the shorter
        /// run is the smaller number, then the digits are compared left to right. That is exact for a
        /// run of any length, where <c>long.Parse</c> would overflow on a long one and
        /// <c>double</c> would lose the last digits.
        /// </summary>
        private static int CompareDigitRun(string a, int a0, int a1, string b, int b0, int b1)
        {
            while (a0 < a1 - 1 && a[a0] == '0') a0++;
            while (b0 < b1 - 1 && b[b0] == '0') b0++;

            int la = a1 - a0, lb = b1 - b0;
            if (la != lb) return la < lb ? -1 : 1;

            for (int k = 0; k < la; k++)
            {
                if (a[a0 + k] != b[b0 + k]) return a[a0 + k] < b[b0 + k] ? -1 : 1;
            }

            return 0;
        }

        private sealed class PairComparer : IComparer<KeyValuePair<string, string>>
        {
            public int Compare(KeyValuePair<string, string> x, KeyValuePair<string, string> y)
                => LocationDisplayOrder.Compare(x.Key, x.Value, y.Key, y.Value);
        }
    }
}
