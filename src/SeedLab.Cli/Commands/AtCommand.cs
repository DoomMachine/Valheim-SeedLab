using System;
using System.Globalization;
using System.Text;
using SeedLab.Cli.Analysis;
using SeedLab.Cli.Infra;
using SeedLab.Render;
using SeedLab.Saves;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLab.Cli.Commands
{
    /// <summary>
    /// <c>vseed at</c> - everything the generator knows about one point, evaluated at exactly that
    /// point rather than snapped to a grid.
    /// </summary>
    public static class AtCommand
    {
        public const string Help = @"vseed at <text-or-int> <x> <z> [options]

  Asks the generator about one world position. Nothing is snapped to a sampling grid:
  the coordinates go straight into GetBiome / GetBiomeHeight.

Options:
  --ascii              also draw a small terrain sketch around the point
  --ascii-size <n>     characters across (default 41, odd numbers centre the point)
  --ascii-radius <m>   half-width of the sketch in metres (default 500)
  --json               machine-readable output

Examples:
  vseed at MWd8eV6svz 0 0
  vseed at MWd8eV6svz 70.5 -2.8 --ascii
  vseed at -1772362158 -4590 8334 --json";

        public static int Run(Args a, Out o)
        {
            if (a.Positional.Count < 3)
            {
                throw new CliException("give a seed and the x and z coordinates.", ExitCodes.Usage, Help);
            }

            SeedRef sr = SeedArg.Resolve(a, a.Positional[0]);
            float x = ParseF(a.Positional[1], "x");
            float z = ParseF(a.Positional[2], "z");
            bool ascii = a.Flag("ascii");
            int asciiSize = a.Int("ascii-size", 41);
            double asciiRadius = a.Double("ascii-radius", 500);
            a.RejectUnknown();
            if (sr.AmbiguityNote != null) Out.Warn(sr.AmbiguityNote);

            WorldGeneratorPort gen = new WorldGeneratorPort(sr.Seed, Verified.WorldGenVersion, menu: false);

            double dist = Math.Sqrt((double)x * x + (double)z * z);
            float distF = DUtils.Length(x, z);
            bool outside = distF > WorldField.WaterEdgeRadius;

            Biome biome = gen.GetBiome(x, z);
            float height = gen.GetBiomeHeight(biome, x, z, out ColorRGBA mask);
            float baseHeight = gen.GetBaseHeightPublic(x, z);
            gen.GetRiverWeightPublic(x, z, out float riverWeight, out float riverWidth);
            float forest = WorldGeneratorPort.GetForestFactor(x, 0f, z);
            bool inForest = WorldGeneratorPort.InForest(x, 0f, z);

            // ZoneSystem.GetZone: the divide happens in double and is narrowed to float once, and the
            // floor is Utils.FloorToInt (a float +64000 bias), not MathF.Floor. Both matter at a
            // boundary, so the game's own helpers are used rather than an "equivalent".
            int zx = ValheimRounding.FloorToInt((float)(((double)x + 32.0) / 64.0));
            int zy = ValheimRounding.FloorToInt((float)(((double)z + 32.0) / 64.0));

            double bearing = WorldSummary.BearingFromOrigin(x, z);
            bool ashlands = WorldGeneratorPort.IsAshlands(x, z);
            bool deepnorth = WorldGeneratorPort.IsDeepnorth(x, z);

            if (o.Json)
            {
                var j = o.J;
                j.WriteStartObject();
                j.WriteString("command", "at");
                j.WriteString("engine", Verified.EngineVersion);
                j.WriteNumber("seed", sr.Seed);
                j.WriteNumber("x", x);
                j.WriteNumber("z", z);
                j.WriteString("biome", MapPalette.Name(biome));
                j.WriteNumber("biome_index", biome.ToGameIndex());
                j.WriteNumber("height_m", height);
                j.WriteNumber("above_sea_level_m", height - MapPalette.WaterLevel);
                j.WriteNumber("water_level_m", MapPalette.WaterLevel);
                j.WriteBoolean("underwater", height < MapPalette.WaterLevel);
                j.WriteNumber("base_height", baseHeight);
                j.WriteBoolean("in_river_field", riverWeight > 0f);
                j.WriteNumber("river_weight", riverWeight);
                j.WriteNumber("river_width", riverWidth);
                j.WriteNumber("forest_factor", forest);
                j.WriteBoolean("in_forest", inForest);
                j.WriteStartObject("zone");
                j.WriteNumber("x", zx);
                j.WriteNumber("y", zy);
                j.WriteNumber("centre_x", (long)zx * 64);
                j.WriteNumber("centre_z", (long)zy * 64);
                j.WriteEndObject();
                j.WriteNumber("distance_from_centre_m", dist);
                j.WriteNumber("bearing_deg", bearing);
                j.WriteBoolean("outside_water_edge", outside);
                j.WriteBoolean("is_ashlands_geometry", ashlands);
                j.WriteBoolean("is_deepnorth_geometry", deepnorth);
                j.WriteStartObject("mask");
                j.WriteNumber("r", mask.r);
                j.WriteNumber("g", mask.g);
                j.WriteNumber("b", mask.b);
                j.WriteNumber("a", mask.a);
                j.WriteEndObject();
                j.WriteEndObject();
            }
            else
            {
                o.Header("Point  (" + Out.F(x, 2) + ", " + Out.F(z, 2) + ")  in seed " + sr.Seed);
                o.Field("biome", MapPalette.Name(biome) + "   (Heightmap.BiomeIndex " + biome.ToGameIndex() + ")");
                o.Field("height", Out.F(height, 3) + " m");
                o.Field("vs sea level", height >= MapPalette.WaterLevel
                        ? Out.F(height - MapPalette.WaterLevel, 3) + " m above the 30 m water line"
                        : Out.F(MapPalette.WaterLevel - height, 3) + " m below the 30 m water line (underwater)");
                o.Field("base height", Out.F(baseHeight, 5) + "   (normalised; x200 before biome shaping)");
                o.Field("river / stream", riverWeight > 0f
                        ? "yes - weight " + Out.F(riverWeight, 4) + ", width " + Out.F(riverWidth, 2) + " m"
                        : "no  (weight 0)");
                o.Field("forest factor", Out.F(forest, 4) + (inForest ? "   in forest (< 1.15)" : "   not in forest"));
                o.Field("zone", "(" + zx + ", " + zy + ")   64 m zone, centre (" + (long)zx * 64 + ", " + (long)zy * 64 + ")");
                o.Field("from the centre", Out.F(dist, 1) + " m, bearing " + Out.Bearing(bearing));
                o.Field("geometry", (ashlands ? "inside the Ashlands ring; " : "")
                        + (deepnorth ? "inside the Deep North ring; " : "")
                        + (outside ? "OUTSIDE the 10500 m water edge - the height is the -400 constant"
                                   : "inside the 10500 m water edge"));
                if (biome == Biome.AshLands)
                {
                    o.Field("ashlands mask a", Out.F(mask.a, 4) + (mask.a > 0.6f ? "   lava (> 0.6)" : ""));
                }
            }

            if (ascii && !o.Json) DrawAscii(o, gen, x, z, asciiSize, asciiRadius);
            return ExitCodes.Ok;
        }

        /// <summary>
        /// A terminal sketch: one character per sample, north up. Cheap - an n x n block of GetBiome +
        /// GetBiomeHeight, which at the default 41 x 41 is 1 681 samples.
        /// </summary>
        private static void DrawAscii(Out o, WorldGeneratorPort gen, float cx, float cz, int size, double radius)
        {
            if (size < 5 || size > 201) throw new CliException("--ascii-size must be between 5 and 201.");
            if (radius <= 0) throw new CliException("--ascii-radius must be positive.");

            double step = 2.0 * radius / size;
            o.Header("Sketch  " + size + " x " + size + " samples, " + Out.F(step, 1)
                     + " m apart, +-" + Out.F(radius, 0) + " m, north up");
            StringBuilder sb = new StringBuilder();
            for (int row = size - 1; row >= 0; row--)
            {
                sb.Clear();
                sb.Append("  ");
                float wz = (float)(cz + (row - size / 2) * step);
                for (int col = 0; col < size; col++)
                {
                    float wx = (float)(cx + (col - size / 2) * step);
                    if (row == size / 2 && col == size / 2) { sb.Append('X'); continue; }
                    Biome b = gen.GetBiome(wx, wz);
                    float h = gen.GetBiomeHeight(b, wx, wz, out _);
                    sb.Append(Glyph(b, h));
                }

                o.Line(sb.ToString());
            }

            o.Line();
            o.Note("X you   . ocean/deep   ~ shallow water   , meadows   f black forest   s swamp");
            o.Note("^ mountain   p plains   m mistlands   a ashlands   n deep north   # above 200 m");
        }

        private static char Glyph(Biome b, float h)
        {
            if (h < MapPalette.WaterLevel - 20f) return '.';
            if (h < MapPalette.WaterLevel) return '~';
            if (h > 230f) return '#';
            return b switch
            {
                Biome.Meadows => ',',
                Biome.BlackForest => 'f',
                Biome.Swamp => 's',
                Biome.Mountain => '^',
                Biome.Plains => 'p',
                Biome.Mistlands => 'm',
                Biome.AshLands => 'a',
                Biome.DeepNorth => 'n',
                Biome.Ocean => '~',
                _ => '?',
            };
        }

        private static float ParseF(string s, string what)
        {
            // "nan" and "infinity" parse on .NET Core however NumberStyles is set. A non-finite
            // coordinate makes every comparison in the generator false, so the point would be
            // reported as Meadows inside the world edge with exit code 0 - a silent wrong answer.
            if (!float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float f)
                || !float.IsFinite(f))
            {
                throw new CliException($"{what} takes a finite number, not '{s}'.");
            }

            return f;
        }
    }
}
