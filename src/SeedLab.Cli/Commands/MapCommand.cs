using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using SeedLab.Cli.Infra;
using SeedLab.Render;
using SeedLab.Runtime.Execution;
using SeedLab.Render.Png;
using SeedLab.WorldGen;

namespace SeedLab.Cli.Commands
{
    /// <summary>
    /// <c>vseed map</c> - render the world to a PNG with SeedLab's own encoder.
    /// </summary>
    public static class MapCommand
    {
        public const string Help = @"vseed map <text-or-int> [options]

  Renders the world to a PNG. With no options it draws the game's own grid: 2048 x 2048
  samples at 12 m per sample, covering x and z in [-12288, 12288].

Options:
  -o, --out <file>     output path. With no -o the render goes to the cache root
                       (--cache-dir, or %LOCALAPPDATA%\SeedLab\maps) and the path is
                       printed - it is NOT dropped into the directory you are standing in.
  --px <n>             image width in samples (default 2048)
  --res <metres>       metres per sample; default keeps the whole world in view
  --zoom <x,z,r>       centre on (x, z) and cover a radius of r metres
  --scale <n>          image pixels per sample (default 1)
  --palette game|seedlab
                       'game' is Minimap.GetPixelColor exactly - Ocean, Mountain and Deep North
                       are all white, as in game. 'seedlab' (default) separates them and
                       depth-shades water.
  --plain              flat biome fill only: no water, shading, lava, rings, grid or legend
  --shade <0..1>       hillshade strength (default 0.35; 0 turns it off)
  --exaggeration <f>   vertical exaggeration before shading (default 1.5)
  --no-water           paint biome colour below 30 m instead of depth-shaded water
  --no-lava            do not paint Ashlands lava
  --rivers             draw the river and stream centre lines
  --grid               draw a coordinate grid (--grid-step <metres>, default 1000)
  --no-rings           drop the distance rings, origin cross, north arrow and scale bar
  --no-legend          drop the footer band
  --mark <x,z[,label]> mark a point (repeatable via commas: --mark 100,200,home)
  --threads <n>        worker threads
  --json               print the render report as JSON instead of text

Examples:
  vseed map MWd8eV6svz
  vseed map -1772362158 --px 4096 -o world.png
  vseed map MWd8eV6svz --zoom 0,0,2000 --px 1024 --grid
  vseed map MWd8eV6svz --plain --palette game -o exact.png";

        public static int Run(Args a, Out o, CliRuntime rt)
        {
            if (a.Positional.Count < 1) throw new CliException("give a seed text or an int32.", ExitCodes.Usage, Help);
            SeedRef sr = SeedArg.Resolve(a, a.Positional[0]);

            int px = a.Int("px", 2048);
            if (px < 16 || px > 16384) throw new CliException("--px must be between 16 and 16384.");

            float centerX = 0f, centerZ = 0f;
            double? radius = null;
            string? zoom = a.Get("zoom");
            if (zoom != null)
            {
                string[] parts = zoom.Split(',');
                if (parts.Length != 3) throw new CliException("--zoom takes x,z,radius - for example --zoom 0,0,2000.");
                centerX = (float)ParseD(parts[0], "--zoom x");
                centerZ = (float)ParseD(parts[1], "--zoom z");
                radius = ParseD(parts[2], "--zoom radius");
                if (radius <= 0) throw new CliException("--zoom radius must be positive.");
            }

            double res;
            string? resOpt = a.Get("res");
            if (resOpt != null)
            {
                res = ParseD(resOpt, "--res");
                if (res <= 0) throw new CliException("--res must be positive.");
            }
            else if (radius.HasValue)
            {
                res = 2.0 * radius.Value / px;
            }
            else
            {
                // Default: the game's own grid when px is 2048, and the same coverage otherwise.
                res = 2048.0 * 12.0 / px;
            }

            FieldGrid grid = new FieldGrid(px, (float)res, centerX, centerZ);
            bool plain = a.Flag("plain");
            MapOptions opt = new MapOptions
            {
                Plain = plain,
                Palette = ParsePalette(a.Get("palette")),
                Shade = a.Double("shade", 0.35),
                Exaggeration = a.Double("exaggeration", 1.5),
                Water = a.Flag("water", true),
                Lava = a.Flag("lava", true),
                Rivers = a.Flag("rivers"),
                Rings = a.Flag("rings", true),
                Grid = a.Flag("grid"),
                GridStepMetres = a.Double("grid-step", 1000),
                Legend = a.Flag("legend", true),
                Scale = a.Int("scale", 1),
                SeedText = sr.Text ?? "",
                EngineVersion = Verified.EngineVersion,
            };

            if (opt.Shade < 0 || opt.Shade > 1) throw new CliException("--shade must be between 0 and 1.");
            if (opt.Scale < 1 || opt.Scale > 8) throw new CliException("--scale must be between 1 and 8.");

            string? mark = a.Get("mark");
            if (mark != null)
            {
                string[] p = mark.Split(',');
                if (p.Length < 2) throw new CliException("--mark takes x,z or x,z,label.");
                opt.Marks.Add(((float)ParseD(p[0], "--mark x"), (float)ParseD(p[1], "--mark z"),
                               p.Length > 2 ? p[2] : ""));
            }

            // A render holds a full height field, so it is planned as the height tier: the mode's
            // share of the cores, capped by what half of free memory affords at THIS grid.
            WorkerPlan plan = rt.Plan(WorkTier.HeightsRivers, grid.Spacing);
            int threads = plan.Workers;

            // Audit defect 6. 'vseed map <seed>' used to write map-<seed>.png into whatever directory
            // the user happened to be standing in - 3.2 MB at the default size, with a name derived
            // from the seed, so repeated renders accumulated silently in source trees and home
            // directories. The default is now the cache root, which 'vseed clean' can empty and which
            // the user chose with --cache-dir; -o still puts it exactly where it is asked to.
            bool defaulted = a.Get("out", "o") == null;
            string outPath = a.Get("out", "o")
                ?? Path.Combine(rt.Cache.Maps, "map-" + sr.Seed.ToString(CultureInfo.InvariantCulture)
                                + "-" + grid.Size.ToString(CultureInfo.InvariantCulture) + "px.png");
            a.RejectUnknown();

            if (sr.AmbiguityNote != null) Out.Warn(sr.AmbiguityNote);
            if (opt.SeedText.Length == 0)
            {
                // The footer should name a typeable text even when the user gave an int.
                opt.SeedText = SeedArg.Describe(sr.Seed).Shortest;
            }

            Stopwatch total = Stopwatch.StartNew();
            bool needLava = opt.Lava && !plain;
            WorldGeneratorPort? gen = opt.Rivers && !plain
                ? new WorldGeneratorPort(sr.Seed, Verified.WorldGenVersion, menu: false)
                : null;
            WorldField field = WorldField.Sample(sr.Seed, grid, needLava, threads);
            RenderResult r = MapRenderer.Render(field, opt, gen);

            Stopwatch enc = Stopwatch.StartNew();
            string? dir = Path.GetDirectoryName(Path.GetFullPath(outPath));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            PngEncoder.WriteFile(outPath, r.Canvas.Pixels, r.Canvas.Width, r.Canvas.Height);
            enc.Stop();
            total.Stop();

            long bytes = new FileInfo(outPath).Length;
            double half = grid.HalfSpan;
            if (defaulted && !o.Json)
            {
                Out.Info("wrote " + Path.GetFullPath(outPath));
                Out.Info("  (no -o was given, so it went to the cache root rather than this directory; "
                         + "'vseed clean' empties it)");
            }

            if (o.Json)
            {
                var j = o.J;
                j.WriteStartObject();
                j.WriteString("command", "map");
                j.WriteString("engine", Verified.EngineVersion);
                j.WriteNumber("seed", sr.Seed);
                j.WriteString("seed_text", opt.SeedText);
                j.WriteString("path", Path.GetFullPath(outPath));
                j.WriteNumber("width", r.Canvas.Width);
                j.WriteNumber("height", r.Canvas.Height);
                j.WriteNumber("bytes", bytes);
                j.WriteStartObject("grid");
                j.WriteNumber("samples", grid.Size);
                j.WriteNumber("spacing_m", grid.Spacing);
                j.WriteNumber("center_x", grid.CenterX);
                j.WriteNumber("center_z", grid.CenterZ);
                j.WriteNumber("half_span_m", half);
                j.WriteBoolean("is_game_grid", grid.IsGameGrid);
                j.WriteEndObject();
                j.WriteStartObject("options");
                j.WriteString("palette", opt.Palette.ToString().ToLowerInvariant());
                j.WriteBoolean("plain", opt.Plain);
                j.WriteNumber("shade", opt.Shade);
                j.WriteBoolean("water", opt.Water);
                j.WriteBoolean("lava", opt.Lava);
                j.WriteBoolean("rivers", opt.Rivers);
                j.WriteBoolean("rings", opt.Rings);
                j.WriteBoolean("grid_lines", opt.Grid);
                j.WriteBoolean("legend", opt.Legend);
                j.WriteNumber("scale", opt.Scale);
                j.WriteEndObject();
                j.WriteStartObject("timing_s");
                j.WriteNumber("generator_construct", field.ConstructSeconds);
                j.WriteNumber("field", field.SampleSeconds);
                j.WriteNumber("paint", r.PaintSeconds);
                j.WriteNumber("shade", r.ShadeSeconds);
                j.WriteNumber("overlays", r.OverlaySeconds);
                j.WriteNumber("png_encode", enc.Elapsed.TotalSeconds);
                j.WriteNumber("total", total.Elapsed.TotalSeconds);
                j.WriteNumber("threads", field.Workers);
                j.WriteEndObject();
                j.WriteEndObject();
            }
            else
            {
                o.Header("Map");
                o.Field("seed", sr.Seed.ToString(CultureInfo.InvariantCulture) + "  '" + opt.SeedText + "'");
                o.Field("file", Path.GetFullPath(outPath));
                o.Field("image", r.Canvas.Width + " x " + r.Canvas.Height + " px, " + Out.F(bytes / 1024.0, 0) + " KiB");
                o.Field("sampling", grid.Size + " x " + grid.Size + " @ "
                        + grid.Spacing.ToString("0.###", CultureInfo.InvariantCulture) + " m"
                        + (grid.IsGameGrid ? "   (the game's own grid)" : ""));
                o.Field("covers", "x in [" + Out.F(grid.CenterX - half, 0) + ", " + Out.F(grid.CenterX + half, 0)
                        + "], z in [" + Out.F(grid.CenterZ - half, 0) + ", " + Out.F(grid.CenterZ + half, 0) + "]");
                o.Field("palette", opt.Palette == PaletteMode.Game
                        ? "game (Minimap.GetPixelColor exactly; Ocean/Mountain/Deep North all white)"
                        : "seedlab (measured game colours, Deep North recoloured, water depth-shaded)");
                o.Line();
                o.Field("generator", Out.F(field.ConstructSeconds, 3) + " s  (construct + river/lake pregeneration)");
                o.Field("field", Out.F(field.SampleSeconds, 3) + " s  (" + Out.N(grid.Count)
                        + " samples on " + field.Workers + " threads)");
                o.Field("paint / shade", Out.F(r.PaintSeconds, 3) + " s / " + Out.F(r.ShadeSeconds, 3) + " s");
                o.Field("overlays", Out.F(r.OverlaySeconds, 3) + " s");
                o.Field("png encode", Out.F(enc.Elapsed.TotalSeconds, 3) + " s");
                o.Field("total", Out.F(total.Elapsed.TotalSeconds, 3) + " s");
                o.Line();
            }

            return ExitCodes.Ok;
        }

        private static PaletteMode ParsePalette(string? s) => s switch
        {
            null or "seedlab" => PaletteMode.SeedLab,
            "game" => PaletteMode.Game,
            _ => throw new CliException($"--palette takes 'game' or 'seedlab', not '{s}'."),
        };

        private static double ParseD(string s, string what)
        {
            // "nan"/"infinity" parse on .NET Core whatever NumberStyles says. A non-finite window
            // centre silently produced a PNG headed "covers x in [NaN, NaN]" with exit code 0.
            if (!double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                || !double.IsFinite(d))
            {
                throw new CliException($"{what} takes a finite number, not '{s}'.");
            }

            return d;
        }
    }
}
