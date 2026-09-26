namespace SeedLab.Saves
{
    /// <summary>
    /// The game's two rounding helpers, ported exactly.
    /// <code>
    /// // assembly_utils, Utils (decompiled 1.0.16)
    /// [MethodImpl(MethodImplOptions.AggressiveInlining)]
    /// public static int RoundToInt(float f) { return (int)(f + 64000.5f) - 64000; }   // line 1216
    /// [MethodImpl(MethodImplOptions.AggressiveInlining)]
    /// public static int FloorToInt(float f) { return (int)(f + 64000f)   - 64000; }   // line 1222
    /// // IL of both: ldarg.0; ldc.r4 64000.5 | 64000; add; conv.i4; ldc.i4 64000; sub; ret
    /// </code>
    /// <para>
    /// <b>These are not <c>Math.Round</c> and <c>MathF.Floor</c>, and they are not float
    /// arithmetic either.</b> The game's Mono runs with <c>-O=-float32</c> (the option string in
    /// <c>UnityPlayer.dll</c>), which keeps every float evaluation-stack value at R8; nothing narrows
    /// between the <c>add</c> and the <c>conv.i4</c>, so the float32 argument and the bias (both
    /// exact in double) are added in DOUBLE and the unrounded double is truncated towards zero:
    /// <c>(int)((double)f + 64000.0) - 64000</c> and <c>(int)((double)f + 64000.5) - 64000</c>.
    /// </para>
    /// <para>
    /// Measured on the game's own <c>mono-2.0-bdwgc.dll</c> with that option, running the IL read out
    /// of <c>assembly_utils.dll</c> (and a replica with the same IL, inlined or not;
    /// <c>tools\SeedLab.MonoProbe</c>):
    /// <c>RoundToInt(100.4999f) == 100</c>, <c>RoundToInt(0.4999f) == 0</c>,
    /// <c>RoundToInt(-0.50000006f) == -1</c>, <c>RoundToInt(2.5f) == 3</c>, <c>RoundToInt(-0.5f) == 0</c>;
    /// <c>FloorToInt(-0.0001f) == -1</c>, <c>FloorToInt(0.99999994f) == 0</c>,
    /// <c>FloorToInt(-1e-10f) == -1</c>, <c>FloorToInt(-1e-12f) == 0</c> - the last because the double
    /// sum itself rounds to 64000.0, so the helpers are not exact rounding rules either
    /// (<c>tests\SeedLab.Tests -- rounding</c> holds all of these). Exact halves round up, as before.
    /// </para>
    /// <para>
    /// <b>Corrected 2026-09-26.</b> Until then these were the C# above, which .NET evaluates in FLOAT:
    /// the fraction was rounded to 2^-8 (2^-7 near 66048) before the truncation, and the examples this
    /// comment used to give as "measured consequences" - <c>RoundToInt(100.4999f) == 101</c>,
    /// <c>FloorToInt(-0.0001f) == 0</c>, a 0.125 m band at every zone boundary - were .NET's, not the
    /// game's. The game has no such band. (<c>FloorToInt(163.999999f) == 164</c> was true under both,
    /// because <c>163.999999f</c> is <c>164f</c>.)
    /// </para>
    /// <para>
    /// They drive <c>Minimap.WorldToPixel</c> (so <c>Minimap.Explore</c>) and <c>ZoneSystem.GetZone</c>.
    /// No instance in the ground-truth saves sits where the two readings differ, which is why no gate
    /// caught this. Never reorder these expressions or hoist the bias. Domain: a finite <c>f</c> with
    /// <c>|f + 64000.5| &lt; 2^31</c> (outside it .NET saturates where Mono's <c>cvttsd2si</c> gives
    /// <c>int.MinValue</c>; no map or world coordinate comes near).
    /// </para>
    /// </summary>
    public static class ValheimRounding
    {
        /// <summary><c>Utils.RoundToInt(float)</c>, line 1216: the add in double, then truncation.</summary>
        public static int RoundToInt(float f) => (int)((double)f + 64000.5) - 64000;

        /// <summary><c>Utils.FloorToInt(float)</c>, line 1222: the add in double, then truncation.</summary>
        public static int FloorToInt(float f) => (int)((double)f + 64000.0) - 64000;
    }
}
