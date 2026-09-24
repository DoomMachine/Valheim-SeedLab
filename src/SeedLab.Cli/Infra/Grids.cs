using System;
using SeedLab.Render;

namespace SeedLab.Cli.Infra
{
    /// <summary>
    /// The sampling grids <c>vseed</c> measures on, to the definition in 07-features.md section 2.1:
    /// <c>N = 2 * ceil(10500 / r)</c> samples per axis at <c>x_i = (i - N/2) * r + r/2</c>, with
    /// <c>r = 12</c> special-cased to <c>N = 2048</c> so that it reproduces the game's own
    /// <c>AltBiomeWorldData</c> / <c>Minimap</c> grid index for index.
    ///
    /// <para>A coarser grid is a cost knob, not an approximation: a figure measured on G96 is a
    /// different, equally well-defined measurement, so the grid is printed with every number.</para>
    /// </summary>
    public static class Grids
    {
        public const double WorldRadiusM = 10500.0;

        public static FieldGrid ForSpacing(double spacing)
        {
            if (!(spacing > 0) || double.IsNaN(spacing))
            {
                throw new CliException($"--grid takes a positive spacing in metres, not '{spacing}'.");
            }

            if (spacing == 12.0) return FieldGrid.G12;

            int n = 2 * (int)Math.Ceiling(WorldRadiusM / spacing);
            if (n < 2) n = 2;
            long cells = (long)n * n;
            if (cells > 400_000_000L)
            {
                throw new CliException(
                    $"--grid {spacing} would need {n}x{n} = {cells:N0} samples.",
                    ExitCodes.Usage,
                    "use a coarser spacing, or 'vseed map --zoom x,z,r' for a detailed window");
            }

            return new FieldGrid(n, (float)spacing);
        }

        /// <summary>A human label such as "G12 (2048 x 2048 @ 12 m, the game's own grid)".</summary>
        public static string Describe(FieldGrid g)
        {
            string tag = "G" + g.Spacing.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
            string note = g.IsGameGrid ? ", the grid the game itself samples" : "";
            return tag + " (" + g.Size + " x " + g.Size + " @ "
                   + g.Spacing.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture) + " m"
                   + note + ")";
        }
    }
}
