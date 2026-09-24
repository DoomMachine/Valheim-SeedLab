using System;

namespace SeedLab.Render
{
    /// <summary>A straight 8-bit RGB colour. Row 0 of a <see cref="Canvas"/> is the TOP row.</summary>
    public readonly struct Rgb : IEquatable<Rgb>
    {
        public readonly byte R, G, B;

        public Rgb(byte r, byte g, byte b) { R = r; G = g; B = b; }

        public Rgb(int r, int g, int b)
            : this((byte)Math.Clamp(r, 0, 255), (byte)Math.Clamp(g, 0, 255), (byte)Math.Clamp(b, 0, 255)) { }

        public static Rgb FromHex(string hex)
        {
            string h = hex.TrimStart('#');
            if (h.Length != 6) throw new ArgumentException("Expected #RRGGBB, got '" + hex + "'.", nameof(hex));
            return new Rgb(
                Convert.ToByte(h.Substring(0, 2), 16),
                Convert.ToByte(h.Substring(2, 2), 16),
                Convert.ToByte(h.Substring(4, 2), 16));
        }

        public static Rgb Lerp(Rgb a, Rgb b, double t)
        {
            t = Math.Clamp(t, 0.0, 1.0);
            return new Rgb(
                (int)Math.Round(a.R + (b.R - a.R) * t),
                (int)Math.Round(a.G + (b.G - a.G) * t),
                (int)Math.Round(a.B + (b.B - a.B) * t));
        }

        /// <summary>Multiplies every channel by <paramref name="f"/> (a shading factor around 1.0).</summary>
        public Rgb Scale(double f) => new Rgb(
            (int)Math.Round(R * f), (int)Math.Round(G * f), (int)Math.Round(B * f));

        public string Hex => "#" + R.ToString("X2") + G.ToString("X2") + B.ToString("X2");

        public bool Equals(Rgb o) => R == o.R && G == o.G && B == o.B;
        public override bool Equals(object? obj) => obj is Rgb o && Equals(o);
        public override int GetHashCode() => (R << 16) | (G << 8) | B;
        public override string ToString() => Hex;
    }

    /// <summary>
    /// A plain RGB pixel buffer with the few drawing primitives the map needs: filled rectangles,
    /// alpha-blended lines and circles, and 5x7 bitmap text. Deliberately tiny - nothing here is a
    /// general graphics library, and nothing here is on the hot path (the field sampling is).
    /// </summary>
    public sealed class Canvas
    {
        public Canvas(int width, int height, Rgb background)
        {
            if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Canvas must be non-empty.");
            Width = width;
            Height = height;
            Pixels = new byte[width * height * 3];
            Fill(background);
        }

        public int Width { get; }
        public int Height { get; }

        /// <summary>width*height*3, row-major, row 0 at the top - the layout <see cref="Png.PngEncoder"/> wants.</summary>
        public byte[] Pixels { get; }

        public void Fill(Rgb c)
        {
            for (int i = 0; i < Pixels.Length; i += 3)
            {
                Pixels[i] = c.R; Pixels[i + 1] = c.G; Pixels[i + 2] = c.B;
            }
        }

        public void Set(int x, int y, Rgb c)
        {
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
            int i = (y * Width + x) * 3;
            Pixels[i] = c.R; Pixels[i + 1] = c.G; Pixels[i + 2] = c.B;
        }

        public Rgb Get(int x, int y)
        {
            int i = (y * Width + x) * 3;
            return new Rgb(Pixels[i], Pixels[i + 1], Pixels[i + 2]);
        }

        public void Blend(int x, int y, Rgb c, double alpha)
        {
            if (alpha <= 0.0) return;
            if ((uint)x >= (uint)Width || (uint)y >= (uint)Height) return;
            if (alpha >= 1.0) { Set(x, y, c); return; }
            int i = (y * Width + x) * 3;
            Pixels[i] = (byte)Math.Round(Pixels[i] + (c.R - Pixels[i]) * alpha);
            Pixels[i + 1] = (byte)Math.Round(Pixels[i + 1] + (c.G - Pixels[i + 1]) * alpha);
            Pixels[i + 2] = (byte)Math.Round(Pixels[i + 2] + (c.B - Pixels[i + 2]) * alpha);
        }

        public void FillRect(int x0, int y0, int w, int h, Rgb c)
        {
            for (int y = Math.Max(0, y0); y < Math.Min(Height, y0 + h); y++)
            {
                for (int x = Math.Max(0, x0); x < Math.Min(Width, x0 + w); x++) Set(x, y, c);
            }
        }

        public void StrokeRect(int x0, int y0, int w, int h, Rgb c, double alpha = 1.0)
        {
            for (int x = x0; x < x0 + w; x++) { Blend(x, y0, c, alpha); Blend(x, y0 + h - 1, c, alpha); }
            for (int y = y0; y < y0 + h; y++) { Blend(x0, y, c, alpha); Blend(x0 + w - 1, y, c, alpha); }
        }

        /// <summary>Bresenham-free float line with round-to-nearest stepping; good enough at map scale.</summary>
        public void Line(double x0, double y0, double x1, double y1, Rgb c, double alpha = 1.0, int thickness = 1)
        {
            double dx = x1 - x0, dy = y1 - y0;
            int steps = (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)));
            if (steps <= 0) { Dot((int)Math.Round(x0), (int)Math.Round(y0), c, alpha, thickness); return; }
            for (int s = 0; s <= steps; s++)
            {
                double t = (double)s / steps;
                Dot((int)Math.Round(x0 + dx * t), (int)Math.Round(y0 + dy * t), c, alpha, thickness);
            }
        }

        public void DashedLine(double x0, double y0, double x1, double y1, Rgb c, double alpha,
                               int onPx, int offPx, int thickness = 1)
        {
            double dx = x1 - x0, dy = y1 - y0;
            int steps = (int)Math.Ceiling(Math.Max(Math.Abs(dx), Math.Abs(dy)));
            if (steps <= 0) return;
            int period = Math.Max(1, onPx + offPx);
            for (int s = 0; s <= steps; s++)
            {
                if (s % period >= onPx) continue;
                double t = (double)s / steps;
                Dot((int)Math.Round(x0 + dx * t), (int)Math.Round(y0 + dy * t), c, alpha, thickness);
            }
        }

        public void Dot(int x, int y, Rgb c, double alpha = 1.0, int thickness = 1)
        {
            if (thickness <= 1) { Blend(x, y, c, alpha); return; }
            int r = thickness / 2;
            for (int j = -r; j <= r; j++)
            {
                for (int i = -r; i <= r; i++) Blend(x + i, y + j, c, alpha);
            }
        }

        /// <summary>A circle of <paramref name="radius"/> px centred on (cx, cy), drawn point by point.</summary>
        public void Circle(double cx, double cy, double radius, Rgb c, double alpha = 1.0,
                           int thickness = 1, int dashOn = 0, int dashOff = 0)
        {
            if (radius <= 0) return;
            int steps = (int)Math.Ceiling(2.0 * Math.PI * radius);
            steps = Math.Clamp(steps, 32, 20000);
            int period = dashOn > 0 ? dashOn + dashOff : 0;
            for (int s = 0; s < steps; s++)
            {
                if (period > 0 && s % period >= dashOn) continue;
                double a = 2.0 * Math.PI * s / steps;
                Dot((int)Math.Round(cx + radius * Math.Cos(a)),
                    (int)Math.Round(cy + radius * Math.Sin(a)), c, alpha, thickness);
            }
        }

        public void FillCircle(double cx, double cy, double radius, Rgb c, double alpha = 1.0)
        {
            int r = (int)Math.Ceiling(radius);
            for (int j = -r; j <= r; j++)
            {
                for (int i = -r; i <= r; i++)
                {
                    if (i * i + j * j <= radius * radius) Blend((int)cx + i, (int)cy + j, c, alpha);
                }
            }
        }

        /// <summary>Draws <paramref name="text"/> with its top-left corner at (x, y). Returns the width drawn.</summary>
        public int Text(int x, int y, string text, Rgb c, int scale = 1, double alpha = 1.0)
        {
            int pen = x;
            foreach (char ch in text)
            {
                for (int row = 0; row < BitmapFont.GlyphHeight; row++)
                {
                    for (int col = 0; col < BitmapFont.GlyphWidth; col++)
                    {
                        if (!BitmapFont.Pixel(ch, col, row)) continue;
                        for (int sy = 0; sy < scale; sy++)
                        {
                            for (int sx = 0; sx < scale; sx++)
                            {
                                Blend(pen + col * scale + sx, y + row * scale + sy, c, alpha);
                            }
                        }
                    }
                }

                pen += BitmapFont.Advance * scale;
            }

            return pen - x;
        }

        /// <summary>Text with a one-pixel dark outline, so labels stay readable over any terrain.</summary>
        public int TextOutlined(int x, int y, string text, Rgb c, Rgb outline, int scale = 1)
        {
            for (int dy = -scale; dy <= scale; dy += scale)
            {
                for (int dx = -scale; dx <= scale; dx += scale)
                {
                    if (dx == 0 && dy == 0) continue;
                    Text(x + dx, y + dy, text, outline, scale, 0.75);
                }
            }

            return Text(x, y, text, c, scale);
        }
    }
}
