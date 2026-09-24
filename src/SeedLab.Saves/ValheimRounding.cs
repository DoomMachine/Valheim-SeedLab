namespace SeedLab.Saves
{
    /// <summary>
    /// The game's two rounding helpers, ported literally.
    /// <code>
    /// // assembly_utils, Utils (decompiled)
    /// [MethodImpl(MethodImplOptions.AggressiveInlining)]
    /// public static int RoundToInt(float f) { return (int)(f + 64000.5f) - 64000; }   // line 1216
    /// [MethodImpl(MethodImplOptions.AggressiveInlining)]
    /// public static int FloorToInt(float f) { return (int)(f + 64000f)   - 64000; }   // line 1222
    /// </code>
    /// <para>
    /// <b>These are not <c>Math.Round</c> and <c>MathF.Floor</c>.</b> The 64000 bias is added in
    /// <b>float</b>, so the fractional part is quantised before the truncation: near 64000 the float
    /// spacing is 2^-8 = 0.00390625, and near 66048 (the <c>WorldToPixel</c> range) it is
    /// 2^-7 = 0.0078125. Measured consequences a port must reproduce (05-validation.md section 1.4):
    /// <c>RoundToInt(100.4999f) == 101</c> (both half-even and half-up would give 100);
    /// <c>RoundToInt(2.5f) == 3</c> (half-even would give 2);
    /// <c>FloorToInt(163.999999f) == 164</c> (<c>MathF.Floor</c> gives 163);
    /// <c>FloorToInt(-0.0001f) == 0</c> (<c>MathF.Floor</c> gives -1).
    /// </para>
    /// <para>
    /// They drive <c>Minimap.WorldToPixel</c>, <c>Minimap.Explore</c> and <c>ZoneSystem.GetZone</c>,
    /// so a wrong implementation corrupts zone assignment and the exploration overlay. It is listed
    /// as risk 7 in 05-validation.md section 6 precisely because nothing in the T0-T5 fixtures
    /// catches it: no instance in <c>_main.3.db2</c> sits in a disagreement band.
    /// </para>
    /// <para>Never reorder these expressions or hoist the bias: the rounding is the point.</para>
    /// </summary>
    public static class ValheimRounding
    {
        /// <summary><c>Utils.RoundToInt(float)</c>, line 1216. Do not substitute an exact rounding rule.</summary>
        public static int RoundToInt(float f) => (int)(f + 64000.5f) - 64000;

        /// <summary><c>Utils.FloorToInt(float)</c>, line 1222. Do not substitute <c>MathF.Floor</c>.</summary>
        public static int FloorToInt(float f) => (int)(f + 64000f) - 64000;
    }
}
