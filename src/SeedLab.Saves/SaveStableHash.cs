namespace SeedLab.Saves
{
    /// <summary>
    /// <c>StringExtensionMethods.GetStableHashCode</c> (assembly_utils, decompiled), duplicated here.
    /// <para>
    /// <b>Why a duplicate.</b> The canonical implementation belongs to <c>SeedLab.Seeds</c>
    /// (06-seed-space.md), but that project has no source yet, and
    /// <c>SeedLab.Saves -&gt; SeedLab.Seeds</c> is not one of the dependency edges in
    /// 08-architecture.md section 1.3 (<c>Saves -&gt; WorldGen</c>, BCL). This copy exists only so
    /// <see cref="WorldMeta"/> can cross-check that a world's stored <c>m_seed</c> really is the hash
    /// of its stored <c>m_seedName</c>. <b>When SeedLab.Seeds ships, delete this type and have the
    /// cross-check call it</b>, or keep it and assert the two agree - but do not let two different
    /// hashes exist unnoticed.
    /// </para>
    /// <code>
    /// public static int GetStableHashCode(this string str) {
    ///     int num = 5381;
    ///     int num2 = num;
    ///     for (int i = 0; i &lt; str.Length &amp;&amp; str[i] != 0; i += 2) {
    ///         num = ((num &lt;&lt; 5) + num) ^ str[i];
    ///         if (i == str.Length - 1 || str[i + 1] == '\0') break;
    ///         num2 = ((num2 &lt;&lt; 5) + num2) ^ str[i + 1];
    ///     }
    ///     return num + num2 * 1566083941;
    /// }
    /// </code>
    /// </summary>
    public static class SaveStableHash
    {
        /// <summary>
        /// The game's string hash. All arithmetic wraps: the game runs in an <c>unchecked</c> context.
        /// Verified: <c>"MWd8eV6svz" -&gt; -1772362158</c>, <c>"hnBd9gJf2G" -&gt; 319486907</c>.
        /// </summary>
        public static int GetStableHashCode(string str)
        {
            unchecked
            {
                int num = 5381;
                int num2 = num;
                for (int i = 0; i < str.Length && str[i] != 0; i += 2)
                {
                    num = ((num << 5) + num) ^ str[i];
                    if (i == str.Length - 1 || str[i + 1] == '\0') break;
                    num2 = ((num2 << 5) + num2) ^ str[i + 1];
                }
                return num + num2 * 1566083941;
            }
        }

        /// <summary>
        /// The seed a world gets from its seed text.
        /// <para>
        /// <b>This is not the same function as <see cref="GetStableHashCode"/> on the empty string.</b>
        /// <c>World..ctor</c> line 73 reads
        /// <c>m_seed = ((!(m_seedName == "")) ? m_seedName.GetStableHashCode() : 0);</c>, so an empty
        /// seed text gives seed <b>0</b>, whereas <c>GetStableHashCode("")</c> runs no loop iteration
        /// and returns <c>unchecked(5381 + 5381 * 1566083941)</c> = <b>371 857 150</b>. Conflating the
        /// two gives every empty-seed world the wrong map (05-validation.md T0).
        /// </para>
        /// </summary>
        public static int SeedFromSeedText(string seedText) =>
            seedText == "" ? 0 : GetStableHashCode(seedText);
    }
}
