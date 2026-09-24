using System;
using System.Collections.Generic;
using SeedLab.Saves;
using SeedLab.WorldGen;

namespace SeedLabAcceptanceTests
{
    /// <summary>
    /// A localisation experiment, not a gate.
    ///
    /// T5 shows that every full-precision disagreement that no river touches is in DeepNorth, and is one
    /// or two float ULPs. WorldGenerator.GetDeepNorthHeight (decomp 1355-1382) is a short chain, so this
    /// re-derives it here from public pieces of the port (GetBaseHeightPublic, DUtils.PerlinNoise,
    /// Offset3) and re-evaluates it under several single-change variants. If one variant reproduces the
    /// save's float32 y on the instances that currently differ AND leaves the ones that currently agree
    /// alone, that variant is the bug. If none does, the difference is upstream of this function and the
    /// next step is the dumper's base-height grid (spec 04 section 3.6.2, GridFlagBaseHeight).
    ///
    /// Only instances with zero river weight and both gap functions exactly 1 are used, so nothing here
    /// depends on the river pass or on the multiplier.
    /// </summary>
    public static class DeepNorthVariants
    {
        private const int VariantCount = 6;

        private static readonly string[] s_names =
        {
            "as transcribed (must equal the port)",
            "b = (float)((double)base + 0.1)            [0.1 as a double, not 0.1f]",
            "h -= over * (1 - k) * 0.75                 [GetPlainsHeight's association]",
            "k = Clamp01(base / 0.4)                    [raw base, without the +0.1]",
            "over = h - sea as a plain float subtract",
            "h += d * 0.1f                              [float scale instead of the double 0.1000000014]",
        };

        private static float Chain(WorldGeneratorPort g, float wx, float wy, int variant)
        {
            float off3 = g.Offset3;
            float baseH = g.GetBaseHeightPublic(wx, wy);
            float b = variant == 1 ? (float)((double)baseH + 0.1) : baseH + 0.1f;

            float px = (float)((double)wx + 100000.0 + (double)off3);
            float py = (float)((double)wy + 100000.0 + (double)off3);
            double u = px, v = py;

            float d = (float)((double)DUtils.PerlinNoise(u * 0.009999999776482582, v * 0.009999999776482582)
                              * (double)DUtils.PerlinNoise(u * 0.019999999552965164, v * 0.019999999552965164));
            d = (float)((double)d + (double)DUtils.PerlinNoise(u * 0.05000000074505806, v * 0.05000000074505806)
                                    * (double)DUtils.PerlinNoise(u * 0.10000000149011612, v * 0.10000000149011612)
                                    * (double)d * 0.5);
            float h = b;
            h = variant == 5 ? h + d * 0.1f
                             : (float)((double)h + (double)d * 0.10000000149011612);

            float sea = 0.15f;
            float over = variant == 4 ? h - sea : (float)((double)h - (double)sea);
            float kIn = variant == 3 ? baseH : b;
            float k = (float)DUtils.Clamp01((double)kIn / 0.4000000059604645);
            if (over > 0f)
            {
                h = variant == 2
                    ? (float)((double)h - (double)over * (1.0 - (double)k) * 0.75)
                    : (float)((double)h - (double)over * ((1.0 - (double)k) * 0.75));
            }
            // AddRivers is skipped: callers only pass points whose river weight is zero, where
            // AddRivers (decomp 937-941) returns h unchanged.
            h = (float)((double)h + (double)DUtils.PerlinNoise(u * 0.10000000149011612, v * 0.10000000149011612) * 0.009999999776482582);
            h = (float)((double)h + (double)DUtils.PerlinNoise(u * 0.4000000059604645, v * 0.4000000059604645) * 0.003000000026077032);
            return h;
        }

        public static void Run(WorldFixture world)
        {
            WorldGeneratorPort gen = new WorldGeneratorPort(world.Seed, 2, menu: false);
            IReadOnlyList<LocationInstance> all = SaveChecks.Locations(world);

            int[] fixes = new int[VariantCount];      // currently differing instances this variant repairs
            int[] breaks = new int[VariantCount];     // currently agreeing instances this variant ruins
            int usable = 0, differing = 0, chainMismatch = 0;

            foreach (LocationInstance li in all)
            {
                if (gen.GetBiome(li.X, li.Z) != Biome.DeepNorth) continue;
                gen.GetRiverWeightPublic(li.X, li.Z, out float rw, out _);
                if (rw > 0f) continue;
                if (WorldGeneratorPort.CreateAshlandsGap(li.X, li.Z) != 1.0) continue;
                if (WorldGeneratorPort.CreateDeepNorthGap(li.X, li.Z) != 1.0) continue;
                usable++;

                int saveBits = BitConverter.SingleToInt32Bits(li.Y);
                float portH = gen.GetBiomeHeight(Biome.DeepNorth, li.X, li.Z, out _);
                bool agrees = BitConverter.SingleToInt32Bits(portH) == saveBits;
                if (!agrees) differing++;

                for (int variant = 0; variant < VariantCount; variant++)
                {
                    // The multiplier is exactly 200 here, and GetBiomeHeight returns
                    // (float)((double)normalised * (double)mult + 0.0) (decomp 1045).
                    float norm = Chain(gen, li.X, li.Z, variant);
                    float full = (float)((double)norm * 200.0 + 0.0);
                    if (variant == 0 && BitConverter.SingleToInt32Bits(full) != BitConverter.SingleToInt32Bits(portH))
                        chainMismatch++;
                    bool ok = BitConverter.SingleToInt32Bits(full) == saveBits;
                    if (!agrees && ok) fixes[variant]++;
                    if (agrees && !ok) breaks[variant]++;
                }
            }

            Console.WriteLine("  " + world.Name + ": " + usable + " DeepNorth instances with no river and both gaps = 1; "
                              + differing + " of them currently differ from the save");
            if (chainMismatch > 0)
                Console.WriteLine("    (the re-derived chain disagrees with the port on " + chainMismatch
                                  + " of them - the experiment itself is not a faithful copy, ignore the rows below)");
            for (int variant = 0; variant < VariantCount; variant++)
                Console.WriteLine("    variant " + variant + "  repairs " + fixes[variant] + " / " + differing
                                  + ", breaks " + breaks[variant] + " that agree   -  " + s_names[variant]);
        }
    }
}
