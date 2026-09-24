using System;
using System.Text;

namespace SeedLab.Search.Locations
{
    /// <summary>
    /// The one normalisation used whenever a TYPED location name has to be matched against a known
    /// one - by <c>vseed locations --name</c>, by the query rewrite and by the web page. Pure strings:
    /// it holds no dumped data, which is why it can live in <c>SeedLab.Search</c> and be shared by
    /// every surface instead of being re-invented three times with three different ideas of what
    /// "the same name" means.
    ///
    /// <para><b>Folding is for INPUT, never for display or identity.</b> A prefab name is a developer
    /// identifier the user copies out of this tool's own output, so an exact ordinal match is tried
    /// first and always wins; folding exists so that a display name - prose typed from memory - does
    /// not have to be spelled with the game's own capitals and spaces. Nobody can be expected to know
    /// that the game renders it "The Elder" and not "the elder".</para>
    ///
    /// <para><b>Diacritics are deliberately NOT folded.</b> <c>Blåbär</c> and <c>Blabar</c> stay
    /// different strings. Equating them would be a claim about the language the dump was taken in
    /// (<c>localization.json</c> records that language for exactly this reason), and SeedLab does not
    /// make claims the dump does not support. <c>char.IsLetterOrDigit</c> rather than an ASCII range
    /// is what keeps non-ASCII letters alive through the fold.</para>
    /// </summary>
    public static class LocationNameKey
    {
        /// <summary>
        /// Case, spacing and punctuation removed: <c>"The Elder"</c>, <c>"the-elder"</c> and
        /// <c>"THE ELDER"</c> all become <c>theelder</c>. The typographic apostrophe is mapped to the
        /// ASCII one first and then dropped with every other mark, so <c>Hildir's Camp</c> and
        /// <c>Hildirs camp</c> agree - which matters the moment a future dump or another language
        /// produces a possessive.
        /// </summary>
        public static string Fold(string? s)
        {
            if (string.IsNullOrEmpty(s)) return "";

            StringBuilder b = new StringBuilder(s!.Length);
            foreach (char raw in s)
            {
                char c = raw == '’' ? '\'' : raw;
                if (!char.IsLetterOrDigit(c)) continue;   // ' - _ . , space and the rest vanish
                b.Append(char.ToLowerInvariant(c));
            }

            return b.ToString();
        }

        /// <summary>
        /// <see cref="Fold"/> with a leading <c>the</c> dropped, so <c>elder</c> finds "The Elder" and
        /// <c>queen</c> finds "The Queen".
        ///
        /// <para><b>Input tolerance, not a name.</b> "Elder" is not a string the game contains, so it
        /// is never displayed, never an alias in its own right, and never applied to a PREFAB key -
        /// prefab names are full of leading capitals that are not articles (<c>AbandonedLogCabin02</c>,
        /// <c>AncientUpgradeStation</c>, <c>AshlandRuins</c>) and a rule about "articles" in general
        /// would start eating them. The rule is a leading <c>the</c> and nothing else; the help says
        /// so in those words.</para>
        /// </summary>
        public static string FoldDroppingArticle(string? s)
        {
            string k = Fold(s);
            return k.Length > 3 && k.StartsWith("the", StringComparison.Ordinal) ? k.Substring(3) : k;
        }
    }
}
