using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using SeedLab.WorldGen;

namespace SeedLab.Render
{
    /// <summary>Everything the map renderer can be told to do. Defaults produce the standard map.</summary>
    public sealed class MapOptions
    {
        /// <summary>Which colour table. <see cref="PaletteMode.Game"/> + <see cref="Plain"/> = the game's own texture.</summary>
        public PaletteMode Palette { get; set; } = PaletteMode.SeedLab;

        /// <summary>
        /// Flat biome fill only: no water depth, no hillshade, no lava, no rings, no grid, no legend.
        /// Exactly one image pixel per sample.
        /// </summary>
        public bool Plain { get; set; }

        /// <summary>Hillshade strength, 0 disables it. 0.35 is the spec's "~35 %".</summary>
        public double Shade { get; set; } = 0.35;

        /// <summary>Vertical exaggeration applied to the height gradient before shading.</summary>
        public double Exaggeration { get; set; } = 1.5;

        /// <summary>Depth-shade everything below the 30 m water line instead of painting it a biome colour.</summary>
        public bool Water { get; set; } = true;

        /// <summary>Paint Ashlands lava (mask alpha &gt; 0.6).</summary>
        public bool Lava { get; set; } = true;

        /// <summary>Draw the river and stream centre lines from the generator's own river list.</summary>
        public bool Rivers { get; set; }

        /// <summary>Distance rings at 2/4/6/8/10 km, the 10 500 m water edge and the Ashlands / Deep North rings.</summary>
        public bool Rings { get; set; } = true;

        /// <summary>Coordinate grid; <see cref="GridStepMetres"/> apart, labelled.</summary>
        public bool Grid { get; set; }

        public double GridStepMetres { get; set; } = 1000;

        /// <summary>Legend and footer band under the map.</summary>
        public bool Legend { get; set; } = true;

        /// <summary>Image pixels per sample. 1 keeps the PNG the same size as the grid.</summary>
        public int Scale { get; set; } = 1;

        public string SeedText { get; set; } = "";
        public string EngineVersion { get; set; } = "";

        /// <summary>Extra marks: world position and label, drawn as a ringed dot.</summary>
        public List<(float X, float Z, string Label)> Marks { get; } = new List<(float, float, string)>();
    }

    /// <summary>What a render cost, so the CLI can print honest timings.</summary>
    public sealed class RenderResult
    {
        public RenderResult(Canvas canvas, double paintSeconds, double shadeSeconds, double overlaySeconds)
        {
            Canvas = canvas;
            PaintSeconds = paintSeconds;
            ShadeSeconds = shadeSeconds;
            OverlaySeconds = overlaySeconds;
        }

        public Canvas Canvas { get; }
        public double PaintSeconds { get; }
        public double ShadeSeconds { get; }
        public double OverlaySeconds { get; }
        public double TotalSeconds => PaintSeconds + ShadeSeconds + OverlaySeconds;
    }

    /// <summary>
    /// Paints a <see cref="WorldField"/> into a <see cref="Canvas"/>: water depth, biome fill, hillshade,
    /// lava, rings, grid, marks and a self-describing footer. Layer order follows 07-features.md
    /// section 6.1.
    /// </summary>
    public static class MapRenderer
    {
        private static readonly Rgb Ink = Rgb.FromHex("#F2F2EC");
        private static readonly Rgb InkDim = Rgb.FromHex("#9AA0A6");
        private static readonly Rgb FooterBg = Rgb.FromHex("#12161C");
        private static readonly Rgb RingColor = Rgb.FromHex("#FFFFFF");
        private static readonly Rgb GridColor = Rgb.FromHex("#FFFFFF");
        private static readonly Rgb RiverColor = Rgb.FromHex("#5FC8FF");

        public static RenderResult Render(WorldField field, MapOptions o, WorldGeneratorPort? generator = null)
        {
            FieldGrid g = field.Grid;
            int scale = Math.Max(1, o.Plain ? 1 : o.Scale);
            int mapPx = g.Size * scale;
            int fontScale = Math.Max(1, (int)Math.Round(mapPx / 640.0));
            FooterLayout? footerLayout = (o.Legend && !o.Plain) ? BuildFooter(field, o, mapPx, fontScale) : null;
            int footer = footerLayout?.Height ?? 0;

            Canvas c = new Canvas(mapPx, mapPx + footer, MapPalette.Void);

            // ---- 1-2. water depth + biome fill ----------------------------------------------------
            Stopwatch sw = Stopwatch.StartNew();
            Rgb[] cell = new Rgb[g.Count];
            bool gamePalette = o.Palette == PaletteMode.Game;
            Parallel.For(0, g.Size, row =>
            {
                int b = row * g.Size;
                for (int col = 0; col < g.Size; col++)
                {
                    int k = b + col;
                    Biome biome = field.BiomeAt(k);
                    if (gamePalette)
                    {
                        // Minimap.GetPixelColor, unconditional - including outside the water edge,
                        // where the game's own GenerateWorldMap also just colours the biome.
                        cell[k] = MapPalette.GameColor(biome);
                        continue;
                    }

                    if (field.Outside[k]) { cell[k] = MapPalette.Void; continue; }

                    float h = field.Height[k];
                    if (o.Water && h < MapPalette.WaterLevel)
                    {
                        cell[k] = MapPalette.WaterColor(h);
                    }
                    else
                    {
                        Rgb col0 = MapPalette.LandColor(biome);
                        if (o.Lava && biome == Biome.AshLands && field.LavaAlpha != null)
                        {
                            double a = field.LavaAlpha[k] / 255.0;
                            if (a > 0.6) col0 = Rgb.Lerp(col0, MapPalette.Lava, Math.Clamp((a - 0.6) / 0.4, 0, 1));
                        }

                        cell[k] = col0;
                    }
                }
            });
            sw.Stop();
            double paint = sw.Elapsed.TotalSeconds;

            // ---- 3. hillshade ---------------------------------------------------------------------
            sw.Restart();
            if (!o.Plain && o.Shade > 0)
            {
                ApplyHillshade(field, cell, o);
            }

            sw.Stop();
            double shade = sw.Elapsed.TotalSeconds;

            // blit (row 0 of the field is SOUTH, so it becomes the BOTTOM row of the image)
            sw.Restart();
            for (int row = 0; row < g.Size; row++)
            {
                int y0 = (g.Size - 1 - row) * scale;
                int b = row * g.Size;
                for (int col = 0; col < g.Size; col++)
                {
                    Rgb v = cell[b + col];
                    int x0 = col * scale;
                    for (int sy = 0; sy < scale; sy++)
                    {
                        for (int sx = 0; sx < scale; sx++) c.Set(x0 + sx, y0 + sy, v);
                    }
                }
            }

            if (!o.Plain)
            {
                if (o.Rivers && generator != null) DrawRivers(c, g, scale, generator);
                if (o.Grid) DrawGrid(c, g, scale, o, fontScale);
                if (o.Rings) DrawRings(c, g, scale, fontScale);
                DrawMarks(c, g, scale, o, fontScale);
                if (footerLayout != null) DrawFooter(c, footerLayout, mapPx, fontScale);
            }

            sw.Stop();
            return new RenderResult(c, paint, shade, sw.Elapsed.TotalSeconds);
        }

        /// <summary>
        /// Lambert shading from the 4-neighbour height gradient, light at azimuth 315 deg (north-west),
        /// altitude 45 deg, multiplied over the fill. Water is shaded at half strength so the sea floor
        /// still reads without the depth ramp being overwhelmed.
        /// </summary>
        private static void ApplyHillshade(WorldField field, Rgb[] cell, MapOptions o)
        {
            FieldGrid g = field.Grid;
            float[] h = field.Height;
            double az = 315.0 * Math.PI / 180.0, alt = 45.0 * Math.PI / 180.0;
            double lx = Math.Cos(alt) * Math.Sin(az);
            double ly = Math.Cos(alt) * Math.Cos(az);
            double lz = Math.Sin(alt);
            double flat = lz;                                   // dot((0,0,1), L)
            double inv2s = o.Exaggeration / (2.0 * g.Spacing);

            Parallel.For(0, g.Size, row =>
            {
                int b = row * g.Size;
                for (int col = 0; col < g.Size; col++)
                {
                    int k = b + col;
                    if (field.Outside[k]) continue;

                    int kw = col > 0 ? k - 1 : k;
                    int ke = col < g.Size - 1 ? k + 1 : k;
                    int ks = row > 0 ? k - g.Size : k;
                    int kn = row < g.Size - 1 ? k + g.Size : k;

                    // Outside cells hold the -400 constant; clamping to the water line keeps the world
                    // edge from producing a fake 400 m cliff all the way round the map.
                    double hw = field.Outside[kw] ? MapPalette.WaterLevel : h[kw];
                    double he = field.Outside[ke] ? MapPalette.WaterLevel : h[ke];
                    double hs = field.Outside[ks] ? MapPalette.WaterLevel : h[ks];
                    double hn = field.Outside[kn] ? MapPalette.WaterLevel : h[kn];

                    double dzdx = (he - hw) * inv2s;
                    double dzdy = (hn - hs) * inv2s;
                    double len = Math.Sqrt(dzdx * dzdx + dzdy * dzdy + 1.0);
                    double dot = (-dzdx * lx - dzdy * ly + lz) / len;
                    double rel = dot / flat;                    // 1.0 on flat ground

                    double strength = (o.Water && h[k] < MapPalette.WaterLevel) ? o.Shade * 0.5 : o.Shade;
                    double f = 1.0 + strength * (rel - 1.0);
                    f = Math.Clamp(f, 1.0 - strength * 1.4, 1.0 + strength * 0.9);
                    cell[k] = cell[k].Scale(f);
                }
            });
        }

        private static double ImageX(FieldGrid g, int scale, double wx)
            => ((wx - g.CenterX) / g.Spacing + g.Size / 2.0) * scale;

        private static double ImageY(FieldGrid g, int scale, double wz)
            => (g.Size / 2.0 - (wz - g.CenterZ) / g.Spacing) * scale;

        private static void DrawRivers(Canvas c, FieldGrid g, int scale, WorldGeneratorPort gen)
        {
            foreach (WorldGeneratorPort.River r in gen.GetRivers())
            {
                c.Line(ImageX(g, scale, r.p0.x), ImageY(g, scale, r.p0.y),
                       ImageX(g, scale, r.p1.x), ImageY(g, scale, r.p1.y), RiverColor, 0.20, Math.Max(1, scale));
            }

            foreach (WorldGeneratorPort.River r in gen.GetStreams())
            {
                c.Line(ImageX(g, scale, r.p0.x), ImageY(g, scale, r.p0.y),
                       ImageX(g, scale, r.p1.x), ImageY(g, scale, r.p1.y), RiverColor, 0.20, Math.Max(1, scale));
            }
        }

        private static void DrawGrid(Canvas c, FieldGrid g, int scale, MapOptions o, int fontScale)
        {
            double step = o.GridStepMetres;
            double half = g.HalfSpan;
            double lo = Math.Ceiling((g.CenterX - half) / step) * step;
            for (double x = lo; x <= g.CenterX + half; x += step)
            {
                double px = ImageX(g, scale, x);
                c.Line(px, 0, px, g.Size * scale - 1, GridColor, 0.13);
            }

            lo = Math.Ceiling((g.CenterZ - half) / step) * step;
            for (double z = lo; z <= g.CenterZ + half; z += step)
            {
                double py = ImageY(g, scale, z);
                c.Line(0, py, g.Size * scale - 1, py, GridColor, 0.13);
                string lab = FormatMetres(z);
                c.TextOutlined(4 * fontScale, (int)py + 2, lab, InkDim, Rgb.FromHex("#000000"), Math.Max(1, fontScale - 1));
            }

            lo = Math.Ceiling((g.CenterX - half) / step) * step;
            for (double x = lo; x <= g.CenterX + half; x += step)
            {
                double px = ImageX(g, scale, x);
                c.TextOutlined((int)px + 3, 4 * fontScale, FormatMetres(x), InkDim, Rgb.FromHex("#000000"), Math.Max(1, fontScale - 1));
            }
        }

        private static string FormatMetres(double m)
        {
            if (Math.Abs(m) < 0.5) return "0";
            double km = m / 1000.0;
            return km.ToString(Math.Abs(km % 1) < 0.001 ? "F0" : "F1", CultureInfo.InvariantCulture) + "k";
        }

        private static void DrawRings(Canvas c, FieldGrid g, int scale, int fontScale)
        {
            double cx = ImageX(g, scale, 0), cy = ImageY(g, scale, 0);
            double pxPerM = scale / (double)g.Spacing;

            foreach (int r in new[] { 2000, 4000, 6000, 8000, 10000 })
            {
                c.Circle(cx, cy, r * pxPerM, RingColor, 0.22, Math.Max(1, scale));
            }

            // The water edge: beyond it GetBiomeHeight returns -400 and no terrain exists.
            c.Circle(cx, cy, WorldField.WaterEdgeRadius * pxPerM, RingColor, 0.45, Math.Max(1, scale));

            // IsAshlands / IsDeepnorth are pure geometry: |(x, z -+ 4000)| = 12000 + WorldAngle*100.
            // Drawn without the +-100 m angle term, which is a wobble far below one pixel at map scale.
            c.Circle(ImageX(g, scale, 0), ImageY(g, scale, 4000), 12000 * pxPerM,
                     Rgb.FromHex("#FF8A5B"), 0.35, Math.Max(1, scale), 12 * scale, 10 * scale);
            c.Circle(ImageX(g, scale, 0), ImageY(g, scale, -4000), 12000 * pxPerM,
                     Rgb.FromHex("#BBD8FF"), 0.35, Math.Max(1, scale), 12 * scale, 10 * scale);

            // Origin cross.
            int arm = 5 * fontScale;
            c.Line(cx - arm, cy, cx + arm, cy, Ink, 0.85, Math.Max(1, scale));
            c.Line(cx, cy - arm, cx, cy + arm, Ink, 0.85, Math.Max(1, scale));

            // North arrow.
            int m = 8 * fontScale;
            int top = m, bx = g.Size * scale - m;
            c.Line(bx, top + 9 * fontScale, bx, top + 22 * fontScale, Ink, 0.9, Math.Max(1, scale));
            c.Line(bx, top + 9 * fontScale, bx - 3 * fontScale, top + 14 * fontScale, Ink, 0.9, Math.Max(1, scale));
            c.Line(bx, top + 9 * fontScale, bx + 3 * fontScale, top + 14 * fontScale, Ink, 0.9, Math.Max(1, scale));
            c.TextOutlined(bx - 2 * fontScale, top, "N", Ink, Rgb.FromHex("#000000"), fontScale);

            // Scale bar: the largest round number of kilometres that fits in a quarter of the map.
            double maxPx = g.Size * scale * 0.25;
            double bar = 1000;
            foreach (double cand in new double[] { 1000, 2000, 5000, 10000, 20000 })
            {
                if (cand * pxPerM <= maxPx) bar = cand;
            }

            double barPx = bar * pxPerM;
            int by = g.Size * scale - m - 3 * fontScale;
            int bx0 = m;
            c.FillRect(bx0, by, (int)barPx, Math.Max(2, fontScale), Ink);
            c.FillRect(bx0, by - 2 * fontScale, Math.Max(2, fontScale), 2 * fontScale, Ink);
            c.FillRect(bx0 + (int)barPx - Math.Max(2, fontScale), by - 2 * fontScale, Math.Max(2, fontScale), 2 * fontScale, Ink);
            c.TextOutlined(bx0, by - 11 * fontScale,
                           (bar / 1000).ToString("F0", CultureInfo.InvariantCulture) + " km",
                           Ink, Rgb.FromHex("#000000"), fontScale);
        }

        private static void DrawMarks(Canvas c, FieldGrid g, int scale, MapOptions o, int fontScale)
        {
            foreach ((float x, float z, string label) in o.Marks)
            {
                double px = ImageX(g, scale, x), py = ImageY(g, scale, z);
                c.FillCircle(px, py, 3.0 * fontScale, Rgb.FromHex("#000000"), 0.55);
                c.FillCircle(px, py, 2.0 * fontScale, Rgb.FromHex("#FFD24A"), 1.0);
                if (!string.IsNullOrEmpty(label))
                {
                    c.TextOutlined((int)px + 5 * fontScale, (int)py - 3 * fontScale, label,
                                   Ink, Rgb.FromHex("#000000"), fontScale);
                }
            }
        }

        /// <summary>
        /// The footer band, measured before it is drawn.
        ///
        /// <para>Its height used to be a constant and its legend a fixed five columns, so at small
        /// <c>--px</c> - where the font cannot shrink below one pixel per glyph pixel - the information
        /// lines ran off the right edge, the swatch labels overlapped one another and the last row of
        /// swatches fell outside the band. Everything here is therefore derived from the width the band
        /// actually has: the lines are wrapped to it, the column count is whatever fits the widest
        /// label, and the height is the total of what that produced.</para>
        /// </summary>
        private sealed class FooterLayout
        {
            public readonly List<(string Text, int Scale, bool Dim)> Lines = new List<(string, int, bool)>();
            public readonly List<(Rgb Colour, string Label)> Entries = new List<(Rgb, string)>();
            public int Pad, Columns, Rows, ColumnWidth, Swatch, EntryScale, EntryStep, Height;
        }

        /// <summary>Width in pixels of <paramref name="text"/> at this scale, matching <see cref="Canvas.Text"/>.</summary>
        private static int TextWidth(string text, int scale)
            => text.Length == 0 ? 0 : (text.Length * BitmapFont.Advance - 1) * scale;

        /// <summary>Drops characters until the text fits, so a label can never run past its column.</summary>
        private static string Fit(string text, int maxPx, int scale)
        {
            if (TextWidth(text, scale) <= maxPx) return text;
            int chars = (maxPx / scale + 1) / BitmapFont.Advance;
            if (chars < 1) chars = 1;
            return text.Substring(0, Math.Min(text.Length, chars));
        }

        /// <summary>Joins the pieces with <paramref name="sep"/>, breaking to a new line before an overrun.</summary>
        private static void AddWrapped(FooterLayout l, IEnumerable<string> pieces, string sep, int width, int scale)
        {
            string line = "";
            foreach (string piece in pieces)
            {
                string candidate = line.Length == 0 ? piece : line + sep + piece;
                if (line.Length > 0 && TextWidth(candidate, scale) > width)
                {
                    l.Lines.Add((Fit(line, width, scale), scale, true));
                    line = piece;
                }
                else
                {
                    line = candidate;
                }
            }

            if (line.Length > 0) l.Lines.Add((Fit(line, width, scale), scale, true));
        }

        private static FooterLayout BuildFooter(WorldField field, MapOptions o, int width, int fs)
        {
            FieldGrid g = field.Grid;
            FooterLayout l = new FooterLayout();
            l.Pad = 6 * fs;
            int small = Math.Max(1, fs - 1);
            int inner = Math.Max(BitmapFont.Advance * small, width - 2 * l.Pad);

            string seedLine = "SeedLab  seed " + (o.SeedText.Length > 0 ? "'" + o.SeedText + "' = " : "")
                              + field.Seed.ToString(CultureInfo.InvariantCulture);
            l.Lines.Add((Fit(seedLine, inner, fs), fs, false));

            double half = g.HalfSpan;
            AddWrapped(l, new[]
            {
                "worldGenVersion " + field.WorldGenVersion,
                "grid " + g.Size + "x" + g.Size + " @ "
                    + g.Spacing.ToString("0.###", CultureInfo.InvariantCulture) + " m",
                "covers x,z in [" + (g.CenterX - half).ToString("F0", CultureInfo.InvariantCulture)
                    + ", " + (g.CenterX + half).ToString("F0", CultureInfo.InvariantCulture) + "]",
                "engine " + o.EngineVersion,
            }, "   ", inner, small);

            AddWrapped(l, (o.Palette == PaletteMode.Game
                    ? "palette: Minimap.GetPixelColor exactly - Ocean, Mountain and Deep North are all white in the game"
                    : "palette: the game's measured biome bytes; Deep North recoloured #E8F0FF (the game paints it white); water depth-shaded below 30 m")
                .Split(' '), " ", inner, small);

            // Shares of in-world cells, counted here so the legend never disagrees with the image.
            long[] counts = new long[10];
            long inWorld = 0;
            for (int k = 0; k < g.Count; k++)
            {
                if (field.Outside[k]) continue;
                inWorld++;
                counts[field.BiomeIndices[k]]++;
            }

            foreach (Biome b in MapPalette.LegendOrder)
            {
                double pct = inWorld > 0 ? 100.0 * counts[b.ToGameIndex()] / inWorld : 0;
                l.Entries.Add((MapPalette.LandColor(b),
                               MapPalette.Name(b) + " " + pct.ToString("F1", CultureInfo.InvariantCulture) + "%"));
            }

            l.Entries.Add((MapPalette.WaterShallow, "water < 30 m"));

            l.EntryScale = small;
            l.Swatch = BitmapFont.GlyphHeight * fs;
            int widest = 0;
            foreach ((Rgb _, string label) in l.Entries) widest = Math.Max(widest, TextWidth(label, l.EntryScale));
            int entryW = l.Swatch + 3 * fs + widest + 6 * fs;
            l.Columns = Math.Max(1, Math.Min(5, inner / Math.Max(1, entryW)));
            l.ColumnWidth = inner / l.Columns;
            l.Rows = (l.Entries.Count + l.Columns - 1) / l.Columns;
            l.EntryStep = Math.Max(l.Swatch, BitmapFont.GlyphHeight * l.EntryScale) + 3 * fs;

            int linesPx = 0;
            foreach ((string _, int scale, bool _) in l.Lines) linesPx += BitmapFont.GlyphHeight * scale + 3 * fs;
            l.Height = l.Pad + linesPx + 2 * fs + l.Rows * l.EntryStep + l.Pad;
            return l;
        }

        private static void DrawFooter(Canvas c, FooterLayout l, int mapPx, int fs)
        {
            c.FillRect(0, mapPx, c.Width, l.Height, FooterBg);
            c.FillRect(0, mapPx, c.Width, Math.Max(1, fs / 2), Rgb.FromHex("#2A3138"));

            int y = mapPx + l.Pad;
            foreach ((string text, int scale, bool dim) in l.Lines)
            {
                c.Text(l.Pad, y, text, dim ? InkDim : Ink, scale);
                y += BitmapFont.GlyphHeight * scale + 3 * fs;
            }

            y += 2 * fs;
            int labelX = l.Swatch + 3 * fs;
            int labelRoom = Math.Max(BitmapFont.Advance * l.EntryScale, l.ColumnWidth - labelX - 2 * fs);
            for (int i = 0; i < l.Entries.Count; i++)
            {
                int cx = l.Pad + (i % l.Columns) * l.ColumnWidth;
                int cy = y + (i / l.Columns) * l.EntryStep;
                c.FillRect(cx, cy, l.Swatch, l.Swatch, l.Entries[i].Colour);
                c.StrokeRect(cx, cy, l.Swatch, l.Swatch, Rgb.FromHex("#000000"), 0.5);
                c.Text(cx + labelX, cy, Fit(l.Entries[i].Label, labelRoom, l.EntryScale), Ink, l.EntryScale);
            }
        }
    }
}
