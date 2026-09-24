using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using SeedLab.Cli.Analysis;
using SeedLab.Cli.Infra;
using SeedLab.Runtime.Execution;
using SeedLab.Render;
using SeedLab.Seeds;
using SeedLab.WorldGen;

namespace SeedLab.Cli.Commands
{
    /// <summary>
    /// <c>vseed seed</c> - resolve a seed both ways and summarise the world it makes.
    /// Every figure is counted on one stated grid and printed with it.
    /// </summary>
    public static class SeedCommand
    {
        public const string Help = @"vseed seed <text-or-int> [options]

  Resolves the seed text and the int32 into each other, then measures the world.

  A bare token that parses as an int32 is read as the INT (that is what a script emits);
  the seed-TEXT reading is always reported too. Force either one with --text / --int.

Options:
  --grid <metres>      sampling grid; 12 is the game's own grid (default 12)
  --min-island <m2>    smallest component counted as an island (default 10000 = 1 ha)
  --islands <n>        also list the n largest islands (default 0)
  --no-landmarks       skip the boss / trader section (it runs location placement)
  --dungeons           extend the landmark section to every dungeon type (runs all 183)
  --threads <n>        worker threads (default: every logical core)
  --json               machine-readable output
  --text | --int       force how the seed token is read

Examples:
  vseed seed MWd8eV6svz
  vseed seed -1772362158 --islands 5
  vseed seed hnBd9gJf2G --grid 24 --json";

        public static int Run(Args a, Out o, CliRuntime rt)
        {
            if (a.Positional.Count < 1) throw new CliException("give a seed text or an int32.", ExitCodes.Usage, Help);
            SeedRef sr = SeedArg.Resolve(a, a.Positional[0]);
            FieldGrid grid = Grids.ForSpacing(a.Double("grid", 12.0));
            double minIsland = a.Double("min-island", IslandAnalysis.DefaultMinAreaM2);
            int listIslands = a.Int("islands", 0);
            // The mode's share of the cores, capped by memory at THIS grid - not every logical core
            // regardless of what else the machine is doing.
            int threads = rt.Workers(WorkTier.HeightsRivers, grid.Spacing);
            bool wantLandmarks = a.Flag("landmarks", true);
            bool wantDungeons = a.Flag("dungeons");
            a.RejectUnknown();

            if (sr.AmbiguityNote != null) Out.Warn(sr.AmbiguityNote);

            (string shortest, string gameStyle) = SeedArg.Describe(sr.Seed);

            Stopwatch sw = Stopwatch.StartNew();
            WorldField field = WorldField.Sample(sr.Seed, grid, sampleLava: false, threads);
            double sampleS = sw.Elapsed.TotalSeconds;
            sw.Restart();
            WorldSummary s = WorldSummary.Compute(field, minIsland);
            double analyseS = sw.Elapsed.TotalSeconds;

            // Location placement is a separate, heavier pass (it needs the game's 2048^2 biome-point
            // grid), so it is reported rather than folded into the figures above, and it is skippable.
            Landmarks? lm = null;
            if (wantLandmarks || wantDungeons)
            {
                try
                {
                    lm = Landmarks.Compute(sr.Seed, field.WorldGenVersion, threads, wantDungeons);
                    foreach (string n in lm.Catalog.Notes) Out.Warn(n);
                }
                catch (SeedLab.Data.GameDataException ex)
                {
                    Out.Warn("no landmark section: " + ex.Message);
                }
            }

            if (o.Json) WriteJson(o, sr, shortest, gameStyle, s, sampleS, analyseS, listIslands, lm);
            else WriteHuman(o, sr, shortest, gameStyle, s, sampleS, analyseS, listIslands, lm);
            return ExitCodes.Ok;
        }

        private static void WriteHuman(Out o, SeedRef sr, string shortest, string gameStyle,
                                       WorldSummary s, double sampleS, double analyseS, int listIslands,
                                       Landmarks? lm)
        {
            FieldGrid g = s.Grid;
            o.Header("Seed");
            if (sr.Text != null) o.Field("as typed", "\"" + sr.Text + "\"  (" + sr.How + ")");
            o.Field("int32", sr.Seed.ToString(CultureInfo.InvariantCulture));
            o.Field("shortest text", shortest + "  (" + shortest.Length + " chars, alphanumeric)");
            o.Field("game-style text", gameStyle + "  (10 chars, the 59 the game's own generator uses - ONE of the "
                    + "many texts for this seed)");
            o.Field("worldGenVersion", s.Field.WorldGenVersion.ToString(CultureInfo.InvariantCulture));
            o.Note("");
            o.Note("The text is hashed to the int by World..ctor and never looked at again, so the int IS the world.");

            o.Header("Measurement");
            o.Field("grid", Grids.Describe(g));
            o.Field("cells in world", Out.N(s.InWorldCells) + " of " + Out.N(g.Count)
                                      + "   (DUtils.Length(x,z) <= 10500 m)");
            o.Field("area sampled", Out.Km2(s.InWorldAreaM2) + "   (cell " + Out.F(g.CellArea, 0) + " m2)");
            o.Field("time", Out.F(sampleS, 2) + " s field, " + Out.F(analyseS, 2) + " s analysis, "
                            + s.Field.Workers + " threads");

            o.Header("Land and water  (land = height >= 30.0 m, the game's water level)");
            o.Field("land", Out.Km2(s.LandAreaM2) + "   " + Out.Pct((double)s.LandCells / Math.Max(1, s.InWorldCells)));
            o.Field("water", Out.Km2(s.WaterAreaM2) + "   " + Out.Pct((double)s.WaterCells / Math.Max(1, s.InWorldCells)));

            o.Header("Biomes  (share of the sampled in-world area)");
            List<string[]> rows = new List<string[]>();
            foreach (Biome b in MapPalette.LegendOrder)
            {
                int bi = b.ToGameIndex();
                if (s.BiomeCells[bi] == 0) continue;
                rows.Add(new[]
                {
                    MapPalette.Name(b),
                    Out.F(s.BiomeCells[bi] * s.Grid.CellArea / 1e6, 2),
                    Out.F(100.0 * s.BiomeCells[bi] / Math.Max(1, s.InWorldCells), 2),
                    Out.F(s.BiomeLandCells[bi] * s.Grid.CellArea / 1e6, 2),
                    double.IsInfinity(s.NearestBiomeM[bi]) ? "-" : Out.F(s.NearestBiomeM[bi], 0),
                    double.IsInfinity(s.NearestBiomeLandM[bi]) ? "-" : Out.F(s.NearestBiomeLandM[bi], 0),
                });
            }

            o.Table(new[] { "biome", "area km2", "%", "land km2", "nearest m", "nearest land m" }, rows,
                    new[] { false, true, true, true, true, true });
            o.Note("");
            o.Note("'nearest' is the centre of the closest cell of that biome to (0,0), so it is within "
                   + Out.F(s.GridDistanceUncertaintyM, 1) + " m (half a cell diagonal) of the true nearest point.");

            IslandAnalysis isl = s.Islands;
            o.Header("Islands  (4-connected components of land cells)");
            o.Field("min island area", Out.F(isl.MinAreaM2 / 10000.0, 2) + " ha (" + Out.F(isl.MinAreaM2, 0) + " m2)");
            o.Field("islands >= that", Out.N(isl.ComponentsAtLeastMin));
            // The 45x was measured on ONE world - the development fixture - and was printed as
            // though it were a property of this seed, on every seed. It is a real measurement and it
            // stays, with the world it was measured on named, because the CLAIM it supports ("do not
            // compare this number across grids") is general even though the factor is not.
            o.Field("components (all)", Out.N(isl.ComponentsAll) + "   <- strongly resolution-dependent: "
                                        + "do not compare it across grids. On seed "
                                        + Verified.Fixtures[0].Seed.ToString(CultureInfo.InvariantCulture)
                                        + " (the development world) it moved ~45x over a 16x change of "
                                        + "grid; this seed's own factor was not measured");
            o.Field("largest island", isl.Largest == null ? "-" : Out.Km2(isl.Largest.AreaM2)
                    + "   nearest point " + Out.F(isl.Largest.NearestToOriginM, 0) + " m from the centre");
            o.Field("nearest land", double.IsInfinity(isl.NearestLandM) ? "none"
                    : Out.F(isl.NearestLandM, 0) + " m at (" + Out.F(isl.NearestLandX, 0) + ", "
                      + Out.F(isl.NearestLandZ, 0) + "), bearing " + Out.Bearing(WorldSummary.BearingFromOrigin(isl.NearestLandX, isl.NearestLandZ)));
            o.Field("centre island", isl.CentreIsland == null
                    ? "none - the spawn area is at sea (nearest land is beyond 500 m)"
                    : Out.Km2(isl.CentreIsland.AreaM2) + "   " + Out.N(isl.CentreIsland.Cells) + " cells, "
                      + "extent x [" + Out.F(isl.CentreIsland.MinX, 0) + ", " + Out.F(isl.CentreIsland.MaxX, 0) + "] "
                      + "z [" + Out.F(isl.CentreIsland.MinZ, 0) + ", " + Out.F(isl.CentreIsland.MaxZ, 0) + "]");
            HalfPrecisionProbe hp = s.HalfPrecision;
            o.Note("");
            o.Note("The raw count is also sensitive to the 30 m cut itself, and that part was re-measured for");
            o.Note("THIS seed: stored at the binary16 precision the game's own map cache keeps (which steps by");
            o.Note(Out.F(HalfPrecisionProbe.UlpAt30M, 6) + " m at 30 m), " + Out.N(hp.FlippedCells) + " of "
                   + Out.N(hp.ComparedCells) + " in-world cells change side of the line,");
            o.Note("giving " + Out.N(hp.ComponentsAll) + " components as stored rather than the "
                   + Out.N(isl.ComponentsAll) + " above. The >= " + Out.F(isl.MinAreaM2 / 10000.0, 2)
                   + " ha count and the land area move");
            o.Note("less: " + Out.N(hp.ComponentsAtLeastMin) + " and " + Out.F(hp.LandAreaM2 / 1e6, 2)
                   + " km2 as stored, against " + Out.N(isl.ComponentsAtLeastMin) + " and "
                   + Out.F(isl.LandAreaM2 / 1e6, 2) + " km2 here.");
            o.Note("");
            o.Note("Centre island = the land component whose nearest cell is closest to (0,0), rule 2 of the");
            o.Note("spec's spawn-island definition. Rule 1 anchors it on the StartTemple instance; placement");
            o.Note(lm != null
                ? "does run in this build (see Landmarks below), but this figure is still rule 2, so it is"
                : "does run in this build ('vseed locations --name StartTemple'), but this figure is rule 2, so it is");
            o.Note("comparable with every earlier run of this command.");

            if (listIslands > 0)
            {
                List<string[]> ir = new List<string[]>();
                int i = 1;
                foreach (Island il in isl.Top)
                {
                    if (i > listIslands) break;
                    ir.Add(new[]
                    {
                        i.ToString(CultureInfo.InvariantCulture),
                        Out.F(il.AreaM2 / 1e6, 3),
                        Out.N(il.Cells),
                        Out.F(il.NearestToOriginM, 0),
                        "(" + Out.F(il.NearestX, 0) + ", " + Out.F(il.NearestZ, 0) + ")",
                    });
                    i++;
                }

                o.Line();
                o.Table(new[] { "#", "km2", "cells", "nearest m", "nearest point" }, ir,
                        new[] { true, true, true, true, false });
            }

            o.Header("Spawn area  (the world origin)");
            o.Field("biome at (0,0)", MapPalette.Name(s.OriginBiome));
            o.Field("height at (0,0)", Out.F(s.OriginHeight, 2) + " m   ("
                    + (s.OriginHeight >= MapPalette.WaterLevel
                        ? Out.F(s.OriginHeight - MapPalette.WaterLevel, 2) + " m above the 30 m water line"
                        : Out.F(MapPalette.WaterLevel - s.OriginHeight, 2) + " m BELOW the 30 m water line - the origin is under water") + ")");
            o.Field("forest factor", Out.F(s.OriginForestFactor, 3)
                    + (WorldGeneratorPort.InForest(0f, 0f, 0f) ? "   (in forest, < 1.15)" : "   (not in forest)"));
            o.Note("Evaluated at exactly (0, 0) by the generator - the grid never samples the origin itself.");

            o.Header("Extremes on this grid");
            o.Field("highest point", Out.F(s.PeakHeight, 2) + " m at (" + Out.F(s.PeakX, 0) + ", " + Out.F(s.PeakZ, 0)
                    + "), " + MapPalette.Name(s.PeakBiome) + ", "
                    + Out.F(Math.Sqrt((double)s.PeakX * s.PeakX + (double)s.PeakZ * s.PeakZ), 0) + " m from the centre");
            o.Field("lowest in-world", Out.F(s.DeepestHeight, 2) + " m at (" + Out.F(s.DeepestX, 0) + ", "
                    + Out.F(s.DeepestZ, 0) + ")");

            lm?.WriteHuman(o);

            o.Header("World edge geometry  (pure geometry - no seed enters these)");
            o.Field("WorldSize", "10000 m   WorldGenerator.WorldSize");
            o.Field("water edge", "10500 m   beyond it GetBiomeHeight returns -2 * 200 = -400 m and no terrain is evaluated");
            o.Field("cells beyond edge", Out.N(s.OutsideCells) + " of " + Out.N(s.Grid.Count) + " sampled");
            o.Field("Ashlands", "DUtils.Length(x, z - 4000) > 12000 + WorldAngle(x,z)*100     (double-accumulated length)");
            o.Field("Deep North", "Vector2(x, z + 4000).magnitude > 12000 + WorldAngle(x,z)*100  (float-accumulated magnitude)");
            o.Note("The two rings use different length functions in the game, and that difference is reproduced.");
            o.Line();
        }

        private static void WriteJson(Out o, SeedRef sr, string shortest, string gameStyle,
                                      WorldSummary s, double sampleS, double analyseS, int listIslands,
                                      Landmarks? lm)
        {
            FieldGrid g = s.Grid;
            var j = o.J;
            j.WriteStartObject();
            j.WriteString("command", "seed");
            j.WriteString("engine", Verified.EngineVersion);

            j.WriteStartObject("seed");
            j.WriteNumber("int32", sr.Seed);
            if (sr.Text != null) j.WriteString("as_typed", sr.Text);
            j.WriteString("read_as", sr.How);
            j.WriteString("shortest_text", shortest);
            j.WriteString("game_style_text", gameStyle);
            j.WriteBoolean("game_style_text_is_one_of_many", true);
            j.WriteString("game_style_text_note",
                "an arbitrary 10-character A59 text that hashes to this seed, chosen deterministically "
                + "from the seed; 'vseed invert --alphabet game --length 10 --count n' lists more");
            j.WriteNumber("worldgen_version", s.Field.WorldGenVersion);
            j.WriteEndObject();

            j.WriteStartObject("grid");
            j.WriteNumber("spacing_m", g.Spacing);
            j.WriteNumber("size", g.Size);
            j.WriteBoolean("is_game_grid", g.IsGameGrid);
            j.WriteNumber("cell_area_m2", g.CellArea);
            j.WriteNumber("cells_total", g.Count);
            j.WriteNumber("cells_in_world", s.InWorldCells);
            j.WriteNumber("cells_outside_water_edge", s.OutsideCells);
            j.WriteNumber("area_sampled_m2", s.InWorldAreaM2);
            j.WriteNumber("distance_uncertainty_m", s.GridDistanceUncertaintyM);
            j.WriteEndObject();

            j.WriteStartObject("land");
            j.WriteNumber("water_level_m", MapPalette.WaterLevel);
            j.WriteNumber("land_cells", s.LandCells);
            j.WriteNumber("land_m2", s.LandAreaM2);
            j.WriteNumber("water_cells", s.WaterCells);
            j.WriteNumber("water_m2", s.WaterAreaM2);
            j.WriteEndObject();

            j.WriteStartArray("biomes");
            foreach (Biome b in MapPalette.LegendOrder)
            {
                int bi = b.ToGameIndex();
                j.WriteStartObject();
                j.WriteString("biome", MapPalette.Name(b));
                j.WriteNumber("biome_index", bi);
                j.WriteNumber("cells", s.BiomeCells[bi]);
                j.WriteNumber("area_m2", s.BiomeCells[bi] * g.CellArea);
                j.WriteNumber("share", (double)s.BiomeCells[bi] / Math.Max(1, s.InWorldCells));
                j.WriteNumber("land_cells", s.BiomeLandCells[bi]);
                j.WriteNumber("land_m2", s.BiomeLandCells[bi] * g.CellArea);
                WriteNullableNumber(j, "nearest_m", s.NearestBiomeM[bi]);
                WriteNullableNumber(j, "nearest_land_m", s.NearestBiomeLandM[bi]);
                j.WriteEndObject();
            }

            j.WriteEndArray();

            IslandAnalysis isl = s.Islands;
            j.WriteStartObject("islands");
            j.WriteNumber("min_area_m2", isl.MinAreaM2);
            j.WriteNumber("count_at_least_min", isl.ComponentsAtLeastMin);
            j.WriteNumber("components_all", isl.ComponentsAll);
            j.WriteString("components_all_note",
                "raw component count is strongly resolution-dependent; not comparable across grids");
            j.WriteStartObject("as_stored_binary16");
            j.WriteString("what", "the same analysis re-run on the heights rounded to binary16, the precision "
                                  + "Minimap.GenerateWorldMap stores; measured for this seed and this grid");
            j.WriteNumber("ulp_at_30m", HalfPrecisionProbe.UlpAt30M);
            j.WriteNumber("cells_compared", s.HalfPrecision.ComparedCells);
            j.WriteNumber("cells_flipped", s.HalfPrecision.FlippedCells);
            j.WriteNumber("components_all", s.HalfPrecision.ComponentsAll);
            j.WriteNumber("count_at_least_min", s.HalfPrecision.ComponentsAtLeastMin);
            j.WriteNumber("land_m2", s.HalfPrecision.LandAreaM2);
            j.WriteEndObject();
            if (isl.Largest != null)
            {
                j.WriteStartObject("largest");
                j.WriteNumber("area_m2", isl.Largest.AreaM2);
                j.WriteNumber("cells", isl.Largest.Cells);
                j.WriteNumber("nearest_to_origin_m", isl.Largest.NearestToOriginM);
                j.WriteEndObject();
            }

            WriteNullableNumber(j, "nearest_land_m", isl.NearestLandM);
            j.WriteNumber("nearest_land_x", isl.NearestLandX);
            j.WriteNumber("nearest_land_z", isl.NearestLandZ);
            if (isl.CentreIsland != null)
            {
                j.WriteStartObject("centre_island");
                j.WriteNumber("area_m2", isl.CentreIsland.AreaM2);
                j.WriteNumber("cells", isl.CentreIsland.Cells);
                j.WriteNumber("min_x", isl.CentreIsland.MinX);
                j.WriteNumber("max_x", isl.CentreIsland.MaxX);
                j.WriteNumber("min_z", isl.CentreIsland.MinZ);
                j.WriteNumber("max_z", isl.CentreIsland.MaxZ);
                j.WriteEndObject();
            }
            else
            {
                j.WriteNull("centre_island");
                j.WriteString("centre_island_note", "spawn area is at sea: nearest land is beyond 500 m");
            }

            j.WriteString("rule", "rule 2 (nearest land component to the origin); the StartTemple anchor needs location placement and was not used");
            if (listIslands > 0)
            {
                j.WriteStartArray("top");
                int i = 0;
                foreach (Island il in isl.Top)
                {
                    if (i++ >= listIslands) break;
                    j.WriteStartObject();
                    j.WriteNumber("area_m2", il.AreaM2);
                    j.WriteNumber("cells", il.Cells);
                    j.WriteNumber("nearest_to_origin_m", il.NearestToOriginM);
                    j.WriteNumber("nearest_x", il.NearestX);
                    j.WriteNumber("nearest_z", il.NearestZ);
                    j.WriteEndObject();
                }

                j.WriteEndArray();
            }

            j.WriteEndObject();

            j.WriteStartObject("origin");
            j.WriteString("biome", MapPalette.Name(s.OriginBiome));
            j.WriteNumber("height_m", s.OriginHeight);
            j.WriteNumber("above_water_m", s.OriginHeight - MapPalette.WaterLevel);
            j.WriteNumber("forest_factor", s.OriginForestFactor);
            j.WriteBoolean("in_forest", WorldGeneratorPort.InForest(0f, 0f, 0f));
            j.WriteEndObject();

            j.WriteStartObject("extremes");
            j.WriteStartObject("highest");
            j.WriteNumber("height_m", s.PeakHeight);
            j.WriteNumber("x", s.PeakX);
            j.WriteNumber("z", s.PeakZ);
            j.WriteString("biome", MapPalette.Name(s.PeakBiome));
            j.WriteEndObject();
            j.WriteStartObject("lowest");
            j.WriteNumber("height_m", s.DeepestHeight);
            j.WriteNumber("x", s.DeepestX);
            j.WriteNumber("z", s.DeepestZ);
            j.WriteEndObject();
            j.WriteEndObject();

            if (lm != null) lm.WriteJson(j); else j.WriteNull("landmarks");

            j.WriteStartObject("world_edge");
            j.WriteNumber("world_size_m", 10000);
            j.WriteNumber("water_edge_m", 10500);
            j.WriteNumber("outside_height_m", WorldField.OutsideHeight);
            j.WriteString("ashlands", "DUtils.Length(x, z - 4000) > 12000 + WorldAngle(x,z)*100");
            j.WriteString("deep_north", "Vector2(x, z + 4000).magnitude > 12000 + WorldAngle(x,z)*100");
            j.WriteEndObject();

            j.WriteStartObject("timing_s");
            j.WriteNumber("field", sampleS);
            j.WriteNumber("analysis", analyseS);
            j.WriteNumber("threads", s.Field.Workers);
            j.WriteEndObject();

            j.WriteEndObject();
        }

        internal static void WriteNullableNumber(System.Text.Json.Utf8JsonWriter j, string name, double v)
        {
            if (double.IsInfinity(v) || double.IsNaN(v)) j.WriteNull(name);
            else j.WriteNumber(name, v);
        }
    }
}
