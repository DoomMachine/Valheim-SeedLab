using System;
using SeedLab.WorldGen;

namespace SeedLab.Saves
{
    /// <summary>One stored <c>Color32</c>: four bytes in R, G, B, A order.</summary>
    /// <remarks>
    /// <c>Color32</c> is an explicit-layout struct with <c>r</c> at offset 0, <c>g</c> at 1,
    /// <c>b</c> at 2 and <c>a</c> at 3 (UnityEngine.CoreModule.dll, decompiled), and
    /// <c>Utils.ColorsToCompressedBuffer</c> gzips <c>MemoryMarshal.AsBytes(px.AsSpan())</c>
    /// verbatim, so the on-disk order is exactly R,G,B,A (05-validation.md section 1.3).
    /// </remarks>
    public readonly struct Rgba32 : IEquatable<Rgba32>
    {
        public readonly byte R;
        public readonly byte G;
        public readonly byte B;
        public readonly byte A;

        public Rgba32(byte r, byte g, byte b, byte a)
        {
            R = r;
            G = g;
            B = b;
            A = a;
        }

        public bool Equals(Rgba32 o) => R == o.R && G == o.G && B == o.B && A == o.A;
        public override bool Equals(object? obj) => obj is Rgba32 o && Equals(o);
        public override int GetHashCode() => (R << 24) | (G << 16) | (B << 8) | A;
        public override string ToString() => "(" + R + "," + G + "," + B + "," + A + ")";
    }

    /// <summary>
    /// The minimap biome colour table, and the Ocean / Mountain / DeepNorth white collision.
    /// <para>
    /// <c>Minimap.GetPixelColor(Heightmap.Biome)</c> (decompiled) maps each biome to a serialized
    /// <c>m_*Color</c> field. Every one of those except <c>m_mistlandsColor</c> is public and
    /// serialized, so <b>the prefab overrides the code default</b> and the code default is wrong.
    /// The bytes below are the ones <b>measured on disk</b> over all 4 194 304 pixels of both
    /// ground-truth worlds (05-validation.md sections 1.6 and 2.1), not the code defaults.
    /// </para>
    /// <para>
    /// <b>The collision is real and is part of the contract.</b> <c>Ocean</c>, <c>Mountain</c> and
    /// <c>DeepNorth</c> all render as pure white, so a biome cannot be recovered from a biome-cache
    /// pixel for those three. <see cref="TryDecode"/> therefore returns the flag set
    /// <see cref="WhiteCandidates"/> rather than picking one, and
    /// <see cref="MinimapBiomeIndex.AmbiguousWhite"/> is a distinct index. Resolve it with the height
    /// (an Ocean/Mountain confusion moves the height by tens of metres) or with the geometric
    /// <c>WorldGenerator.IsDeepnorth</c> predicate - 05-validation.md section 2.3.
    /// </para>
    /// <para>
    /// These bytes are prefab data (risk 8, section 6): a game update that re-tints the map changes
    /// them. The dumper plugin should emit the live <c>Minimap</c> colour fields so that is detected
    /// instead of showing up as a generation bug.
    /// </para>
    /// </summary>
    public static class MinimapColors
    {
        /// <summary>1 651 812 pixels in <c>asdasdasd</c> (39.382 %) carry this colour.</summary>
        public static readonly Rgba32 White = new Rgba32(255, 255, 255, 255);

        public static readonly Rgba32 Meadows = new Rgba32(146, 167, 92, 255);
        public static readonly Rgba32 Swamp = new Rgba32(163, 114, 88, 255);
        public static readonly Rgba32 BlackForest = new Rgba32(107, 116, 63, 255);
        public static readonly Rgba32 Plains = new Rgba32(231, 171, 120, 255);
        public static readonly Rgba32 AshLands = new Rgba32(123, 32, 32, 255);
        public static readonly Rgba32 Mistlands = new Rgba32(51, 51, 51, 255);

        /// <summary><c>Ocean</c> is <c>Color.white</c> in <c>GetPixelColor</c>; Mountain and DeepNorth are prefab-white.</summary>
        public static readonly Rgba32 Ocean = White;
        public static readonly Rgba32 Mountain = White;
        public static readonly Rgba32 DeepNorth = White;

        /// <summary>What a white pixel can be. Exactly the three biomes that share <c>Color.white</c>.</summary>
        public const Biome WhiteCandidates = Biome.Ocean | Biome.Mountain | Biome.DeepNorth;

        /// <summary>
        /// The colour <c>GetPixelColor</c> stores for a biome, as it appears in
        /// <c>cacheMinimapBiome</c>. The <c>_ =&gt; Color.white</c> default means any biome outside the
        /// eight named ones also renders white.
        /// </summary>
        public static Rgba32 ForBiome(Biome biome)
        {
            switch (biome)
            {
                case Biome.Meadows: return Meadows;
                case Biome.AshLands: return AshLands;
                case Biome.BlackForest: return BlackForest;
                case Biome.DeepNorth: return DeepNorth;
                case Biome.Plains: return Plains;
                case Biome.Swamp: return Swamp;
                case Biome.Mountain: return Mountain;
                case Biome.Mistlands: return Mistlands;
                case Biome.Ocean: return Ocean;
                default: return White;
            }
        }

        /// <summary>
        /// Decodes a stored pixel. Returns the single biome for the six unambiguous colours,
        /// <see cref="WhiteCandidates"/> (three bits set) for white, and <c>false</c> with
        /// <see cref="Biome.None"/> for a colour that is not in the table at all - which means the
        /// game's colour fields changed and the caller must stop, not guess.
        /// </summary>
        public static bool TryDecode(byte r, byte g, byte b, out Biome candidates)
        {
            if (r == 255 && g == 255 && b == 255) { candidates = WhiteCandidates; return true; }
            if (r == 123 && g == 32 && b == 32) { candidates = Biome.AshLands; return true; }
            if (r == 51 && g == 51 && b == 51) { candidates = Biome.Mistlands; return true; }
            if (r == 231 && g == 171 && b == 120) { candidates = Biome.Plains; return true; }
            if (r == 107 && g == 116 && b == 63) { candidates = Biome.BlackForest; return true; }
            if (r == 146 && g == 167 && b == 92) { candidates = Biome.Meadows; return true; }
            if (r == 163 && g == 114 && b == 88) { candidates = Biome.Swamp; return true; }
            candidates = Biome.None;
            return false;
        }

        /// <summary>True when more than one biome shares this pixel's colour.</summary>
        public static bool IsAmbiguous(Biome candidates) => candidates == WhiteCandidates;

        /// <summary>
        /// <c>Color</c> to <c>Color32</c> for one channel: <b>round to nearest, half to even</b>.
        /// <code>
        /// public static implicit operator Color32(Color c) =>
        ///     new Color32((byte)Mathf.Round(Mathf.Clamp01(c.r) * 255f), ...);
        /// // UnityEngine.Mathf.Round(float f) => (float)Math.Round(f);   // System.Math.Round, MidpointRounding.ToEven
        /// </code>
        /// <para>
        /// <b>Do not implement <c>+ 0.5f</c> then truncate</b>: that is half-<i>up</i> and differs at
        /// every exact tie. The smoke test is the BlackForest code default: <c>0.7f * 255f</c> is
        /// exactly <c>178.5f</c>, and the game yields <b>178</b>, not 179 (05-validation.md sections
        /// 1.3 and 2.1). Note the multiply is a <b>float</b> multiply and only then widened inside
        /// <c>Math.Round</c>; <c>MathF.Round(float)</c> is the same rounding on the same value.
        /// </para>
        /// <para>Provided here because it is how a computed colour becomes the bytes this file holds
        /// (acceptance tests T2 and T4 compare in colour space).</para>
        /// </summary>
        public static byte EncodeChannel(float value) => (byte)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f);
    }

    /// <summary>
    /// The dense biome index this assembly reports for a cache pixel.
    /// <para>
    /// 0..8 are <c>Heightmap.BiomeIndex</c> (Meadows 0, Swamp 1, Mountain 2, BlackForest 3, Plains 4,
    /// AshLands 5, DeepNorth 6, Ocean 7, Mistlands 8). 255 and 254 are this tool's sentinels and are
    /// the same convention as the ground-truth <c>groundtruth/decoded/&lt;world&gt;.biome.u8</c> files.
    /// </para>
    /// </summary>
    public static class MinimapBiomeIndex
    {
        /// <summary>The pixel is white: Ocean, Mountain or DeepNorth, and the cache cannot say which.</summary>
        public const byte AmbiguousWhite = 255;

        /// <summary>The pixel's colour is not in the measured table - the game's colours changed.</summary>
        public const byte Unknown = 254;
    }
}
