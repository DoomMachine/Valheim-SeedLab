using System;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using SeedLab.Render;
using SeedLab.Render.Png;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLab.Web.Tiles
{
    /// <summary>
    /// Turns one tile request into 256 x 256 PNG bytes, sampled from the real generator at that
    /// zoom's own resolution.
    ///
    /// <para><b>Why this samples rather than calling <c>WorldField.Sample</c>.</b> The loop below is
    /// the same four generator calls, but it is driven by a <see cref="CancellationToken"/>, so a
    /// tile the user has already panned away from stops costing CPU the moment the browser aborts
    /// its request. <c>WorldField.Sample</c> takes no token. Everything after the sampling - the
    /// palette, the water ramp, the lava threshold, the hillshade - is
    /// <see cref="MapRenderer"/>'s, unchanged, so the tiles and the PNGs <c>vseed map</c> writes
    /// cannot drift apart.</para>
    ///
    /// <para><b>Forks, always.</b> <c>WorldGeneratorPort.Fork</c> documents a real fork/no-fork answer
    /// difference of about one 64 m cell per world. <c>WorldField.Sample</c> uses forks whenever it
    /// runs multi-threaded, and that is the configuration the acceptance suite verified against the
    /// game, so this always forks too - even for a single worker - rather than mixing the two modes
    /// for the same question.</para>
    /// </summary>
    public sealed class TileRenderer
    {
        private readonly WorldCache _worlds;
        private readonly int _perTileWorkers;

        public TileRenderer(WorldCache worlds, int perTileWorkers = 0)
        {
            _worlds = worlds ?? throw new ArgumentNullException(nameof(worlds));
            // A browser opens about six connections to one origin, so several tiles are already in
            // flight at once; a small per-tile degree keeps a single tile quick without the tiles
            // fighting each other for the machine.
            //
            // The caller passes the number now, and WebServer takes it from the runtime WorkerPlan -
            // the mode the user chose, capped by available memory and dropped to background while
            // Valheim is running. ProcessorCount is only the fallback for a host that builds a
            // renderer on its own, and it is a number about the MACHINE rather than about what
            // SeedLab has been allowed to take of it.
            _perTileWorkers = perTileWorkers > 0
                ? perTileWorkers
                : Math.Max(1, Math.Min(4, Environment.ProcessorCount));
        }

        private long _renderTicks;
        private long _tilesRendered;

        /// <summary>Time spent inside <see cref="Render"/> since the server started, in seconds.</summary>
        public double TotalRenderSeconds
            => Interlocked.Read(ref _renderTicks) / (double)System.Diagnostics.Stopwatch.Frequency;

        public long TilesRendered => Interlocked.Read(ref _tilesRendered);

        public byte[] Render(int seed, int worldGenVersion, int z, int x, int y, TileStyle style,
                             CancellationToken ct)
        {
            if (!TileGrid.InRange(z, x, y))
            {
                throw new ArgumentOutOfRangeException(nameof(z), $"tile {z}/{x}/{y} is outside the scheme.");
            }

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            WorldGeneratorPort gen = _worlds.Get(seed, worldGenVersion);
            FieldGrid grid = TileGrid.SamplingGrid(z, x, y);
            int n = grid.Size;                       // 258
            byte[] biome = new byte[grid.Count];
            float[] height = new float[grid.Count];
            bool[] outside = new bool[grid.Count];
            byte[]? lava = style.NeedsLava ? new byte[grid.Count] : null;

            ParallelOptions po = new ParallelOptions
            {
                MaxDegreeOfParallelism = _perTileWorkers,
                CancellationToken = ct,
            };

            Parallel.For(0, n, po, () => gen.Fork(), (row, loopState, w) =>
            {
                float wz = grid.WorldZ(row);
                int b = row * n;
                for (int col = 0; col < n; col++)
                {
                    float wx = grid.WorldX(col);
                    int k = b + col;
                    Biome bi = w.GetBiome(wx, wz);
                    biome[k] = (byte)bi.ToGameIndex();
                    height[k] = w.GetBiomeHeight(bi, wx, wz, out _);
                    outside[k] = DUtils.Length(wx, wz) > WorldField.WaterEdgeRadius;
                    if (lava != null && bi == Biome.AshLands && !outside[k])
                    {
                        w.GetAshlandsHeight(wx, wz, out ColorRGBA mask, cheap: true);
                        lava[k] = (byte)Math.Round(Math.Clamp(mask.a, 0f, 1f) * 255f);
                    }
                }

                return w;
            }, unused => { });

            ct.ThrowIfCancellationRequested();

            WorldField field = WorldField.FromSamples(seed, worldGenVersion, grid, biome, height, outside, lava);
            RenderResult r = MapRenderer.Render(field, style.ToMapOptions());

            byte[] png = EncodeCropped(r.Canvas, TileGrid.TilePixels);
            Interlocked.Increment(ref _tilesRendered);
            Interlocked.Add(ref _renderTicks, System.Diagnostics.Stopwatch.GetTimestamp() - t0);
            return png;
        }

        /// <summary>
        /// Drops the one-pixel apron and encodes. The canvas is already north-up (MapRenderer flips
        /// the field's south-first rows), and the apron is symmetric, so the crop is the same inset on
        /// all four sides.
        /// </summary>
        private static byte[] EncodeCropped(Canvas c, int size)
        {
            int inset = (c.Width - size) / 2;
            byte[] rgb = new byte[size * size * 3];
            for (int row = 0; row < size; row++)
            {
                int src = ((row + inset) * c.Width + inset) * 3;
                Buffer.BlockCopy(c.Pixels, src, rgb, row * size * 3, size * 3);
            }

            using MemoryStream ms = new MemoryStream(size * size / 4);
            // Fastest, not Optimal. Measured by 'vseed serve --selftest' on a shaded 256 px tile:
            // Fastest 4.39 ms / 134,471 B, Optimal 6.38 ms / 96,016 B. The extra 38 KiB cost nothing
            // on a loopback socket; the 2 ms is a seventh of the whole tile. Re-run the selftest
            // rather than trusting these two numbers if the encoder changes.
            PngEncoder.Write(ms, rgb, size, size, CompressionLevel.Fastest);
            return ms.ToArray();
        }
    }
}
