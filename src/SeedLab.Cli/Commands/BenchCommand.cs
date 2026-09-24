using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using SeedLab.Cli.Analysis;
using SeedLab.Cli.Infra;
using SeedLab.Runtime.Execution;
using SeedLab.Render;
using SeedLab.Render.Png;
using SeedLab.Seeds;
using SeedLab.WorldGen;

namespace SeedLab.Cli.Commands
{
    /// <summary>
    /// <c>vseed bench</c> - what each stage actually costs on this machine, measured now.
    /// The point is that any later throughput estimate is anchored to a number the user watched
    /// being produced, not to a figure copied out of a document.
    /// </summary>
    public static class BenchCommand
    {
        public const string Help = @"vseed bench [options]

  Measures each stage on this machine: generator construction, biome and height sampling
  (one thread and all threads), island analysis, PNG encoding, and the seed maths.

Options:
  --seeds <n>      worlds to construct for the construction figure (default 8)
  --samples <n>    point samples for the single-thread rates (default 2000000)
  --threads <n>    thread count for the parallel figures (default: every logical core)
  --no-map         skip the full-field and PNG figures
  --json";

        private sealed class Row
        {
            public string Stage = "";
            public string Measured = "";
            public string Rate = "";
        }

        public static int Run(Args a, Out o, CliRuntime rt)
        {
            int seeds = a.Int("seeds", 8);
            int samples = a.Int("samples", 2_000_000);
            bool doMap = a.Flag("map", true);
            a.RejectUnknown();
            if (seeds < 1 || seeds > 1000) throw new CliException("--seeds must be between 1 and 1000.");
            if (samples < 1000) throw new CliException("--samples must be at least 1000.");

            // A benchmark that quietly takes every logical core is measuring a machine nobody will
            // run on: the default now is the SAME plan every other command uses, and the plan is
            // printed beside the numbers so a figure can never be read as though it came from a
            // different machine than it did. --mode full --threads N reproduce the old behaviour.
            WorkerPlan plan = rt.Plan(WorkTier.HeightsRivers, 12.0);
            int workers = plan.Workers;
            int threads = workers;
            foreach (string line in plan.Lines()) Out.Info("  " + line);
            List<Row> rows = new List<Row>();
            Dictionary<string, double> json = new Dictionary<string, double>();

            // ---- 1. construction + river/lake pregeneration -----------------------------------------
            Random rng = new Random(7);
            WorldGeneratorPort? keep = null;
            Stopwatch sw = Stopwatch.StartNew();
            for (int i = 0; i < seeds; i++)
            {
                keep = new WorldGeneratorPort(rng.Next(int.MinValue, int.MaxValue), Verified.WorldGenVersion, menu: false);
            }

            sw.Stop();
            double perWorld = sw.Elapsed.TotalSeconds / seeds;
            rows.Add(new Row
            {
                Stage = "generator construct + pregenerate",
                Measured = Out.F(perWorld * 1000, 1) + " ms per world (" + seeds + " worlds)",
                Rate = Out.F(1.0 / perWorld, 1) + " worlds/s, 1 thread",
            });
            json["construct_s_per_world"] = perWorld;

            WorldGeneratorPort gen = keep!;

            // ---- 2. single-thread point rates --------------------------------------------------------
            // Sampled over the whole world disc, so the mix of biome branches and river lookups is the
            // one a real map hits - not a hot spot in one biome.
            float[] xs = new float[4096], zs = new float[4096];
            for (int i = 0; i < xs.Length; i++)
            {
                double ang = rng.NextDouble() * Math.PI * 2, r = 10400 * Math.Sqrt(rng.NextDouble());
                xs[i] = (float)(Math.Cos(ang) * r);
                zs[i] = (float)(Math.Sin(ang) * r);
            }

            double acc = 0;
            sw.Restart();
            for (int i = 0; i < samples; i++)
            {
                int k = i & 4095;
                acc += (int)gen.GetBiome(xs[k], zs[k]);
            }

            sw.Stop();
            double biomeRate = samples / sw.Elapsed.TotalSeconds;
            rows.Add(new Row
            {
                Stage = "GetBiome",
                Measured = Out.N(samples) + " samples in " + Out.F(sw.Elapsed.TotalSeconds, 3) + " s",
                Rate = Out.F(biomeRate / 1e6, 2) + " M/s, 1 thread",
            });
            json["getbiome_per_s_1t"] = biomeRate;

            Biome[] bs = new Biome[4096];
            for (int i = 0; i < bs.Length; i++) bs[i] = gen.GetBiome(xs[i], zs[i]);

            sw.Restart();
            for (int i = 0; i < samples; i++)
            {
                int k = i & 4095;
                acc += gen.GetBiomeHeight(bs[k], xs[k], zs[k], out _);
            }

            sw.Stop();
            double heightRate = samples / sw.Elapsed.TotalSeconds;
            rows.Add(new Row
            {
                Stage = "GetBiomeHeight",
                Measured = Out.N(samples) + " samples in " + Out.F(sw.Elapsed.TotalSeconds, 3) + " s",
                Rate = Out.F(heightRate / 1e6, 2) + " M/s, 1 thread",
            });
            json["getbiomeheight_per_s_1t"] = heightRate;
            if (double.IsNaN(acc)) Out.Warn("unreachable");     // keeps the loops from being optimised away

            // ---- 3. the full G12 field ----------------------------------------------------------------
            if (doMap)
            {
                int testSeed = -1772362158;
                WorldField f1 = WorldField.Sample(testSeed, FieldGrid.G12, sampleLava: false, threads: 1);
                rows.Add(new Row
                {
                    Stage = "full G12 field (2048^2 biome + height)",
                    Measured = Out.F(f1.SampleSeconds, 3) + " s",
                    Rate = Out.F(FieldGrid.G12.Count / f1.SampleSeconds / 1e6, 2) + " M cells/s, 1 thread",
                });
                json["field_g12_s_1t"] = f1.SampleSeconds;

                WorldField fn = WorldField.Sample(testSeed, FieldGrid.G12, sampleLava: false, threads);
                rows.Add(new Row
                {
                    Stage = "full G12 field",
                    Measured = Out.F(fn.SampleSeconds, 3) + " s",
                    Rate = Out.F(FieldGrid.G12.Count / fn.SampleSeconds / 1e6, 2) + " M cells/s, " + workers + " threads",
                });
                json["field_g12_s_nt"] = fn.SampleSeconds;
                json["field_g12_threads"] = workers;

                sw.Restart();
                IslandAnalysis isl = IslandAnalysis.Compute(fn);
                sw.Stop();
                rows.Add(new Row
                {
                    Stage = "island analysis (4-connected, G12)",
                    Measured = Out.F(sw.Elapsed.TotalSeconds, 3) + " s",
                    Rate = Out.N(isl.ComponentsAll) + " components, " + Out.Km2(isl.LandAreaM2) + " land",
                });
                json["islands_s"] = sw.Elapsed.TotalSeconds;

                MapOptions opt = new MapOptions { SeedText = "bench", EngineVersion = Verified.EngineVersion };
                sw.Restart();
                RenderResult rr = MapRenderer.Render(fn, opt);
                sw.Stop();
                rows.Add(new Row
                {
                    Stage = "render 2048^2 (fill + hillshade + overlays)",
                    Measured = Out.F(sw.Elapsed.TotalSeconds, 3) + " s",
                    Rate = "paint " + Out.F(rr.PaintSeconds, 3) + " s, shade " + Out.F(rr.ShadeSeconds, 3)
                           + " s, overlays " + Out.F(rr.OverlaySeconds, 3) + " s",
                });
                json["render_2048_s"] = sw.Elapsed.TotalSeconds;

                string tmp = Path.Combine(Path.GetTempPath(), "vseed-bench.png");
                sw.Restart();
                PngEncoder.WriteFile(tmp, rr.Canvas.Pixels, rr.Canvas.Width, rr.Canvas.Height);
                sw.Stop();
                long bytes = new FileInfo(tmp).Length;
                try { File.Delete(tmp); } catch { /* a temp file is not worth failing over */ }
                rows.Add(new Row
                {
                    Stage = "PNG encode " + rr.Canvas.Width + "x" + rr.Canvas.Height,
                    Measured = Out.F(sw.Elapsed.TotalSeconds, 3) + " s",
                    Rate = Out.F(bytes / 1048576.0, 2) + " MiB written",
                });
                json["png_encode_s"] = sw.Elapsed.TotalSeconds;
            }

            // ---- 4. the seed maths ---------------------------------------------------------------------
            sw.Restart();
            int hashes = 1_000_000;
            string probe = "MWd8eV6svz";
            int sink = 0;
            for (int i = 0; i < hashes; i++) sink += StableHash.Compute(probe);
            sw.Stop();
            rows.Add(new Row
            {
                Stage = "GetStableHashCode (10 chars)",
                Measured = Out.N(hashes) + " in " + Out.F(sw.Elapsed.TotalSeconds, 3) + " s",
                Rate = Out.F(hashes / sw.Elapsed.TotalSeconds / 1e6, 2) + " M/s, 1 thread",
            });
            json["hash_per_s_1t"] = hashes / sw.Elapsed.TotalSeconds;
            if (sink == int.MinValue) Out.Warn("unreachable");

            int inversions = 2000;
            sw.Restart();
            for (int i = 0; i < inversions; i++) SeedText.Invert(rng.Next(int.MinValue, int.MaxValue), SeedAlphabet.Alnum62);
            sw.Stop();
            rows.Add(new Row
            {
                Stage = "shortest-text inverse",
                Measured = Out.N(inversions) + " in " + Out.F(sw.Elapsed.TotalSeconds, 3) + " s",
                Rate = Out.F(inversions / sw.Elapsed.TotalSeconds, 0) + " seeds/s, 1 thread",
            });
            json["invert_per_s_1t"] = inversions / sw.Elapsed.TotalSeconds;

            if (o.Json)
            {
                var j = o.J;
                j.WriteStartObject();
                j.WriteString("command", "bench");
                j.WriteString("engine", Verified.EngineVersion);
                j.WriteNumber("logical_cores", Environment.ProcessorCount);
                j.WriteNumber("threads_used", workers);
                j.WriteString("runtime", Environment.Version.ToString());
                j.WriteStartObject("measured");
                foreach (KeyValuePair<string, double> kv in json) j.WriteNumber(kv.Key, kv.Value);
                j.WriteEndObject();
                j.WriteStartArray("rows");
                foreach (Row r in rows)
                {
                    j.WriteStartObject();
                    j.WriteString("stage", r.Stage);
                    j.WriteString("measured", r.Measured);
                    j.WriteString("rate", r.Rate);
                    j.WriteEndObject();
                }

                j.WriteEndArray();
                if (json.TryGetValue("field_g12_s_nt", out double fieldNt))
                {
                    j.WriteStartObject("implied");
                    WriteImplied(j, fieldNt);
                    j.WriteEndObject();
                }

                j.WriteEndObject();
                return ExitCodes.Ok;
            }

            o.Header("Bench  (" + Environment.ProcessorCount + " logical cores, .NET " + Environment.Version + ")");
            List<string[]> tr = new List<string[]>();
            foreach (Row r in rows) tr.Add(new[] { r.Stage, r.Measured, r.Rate });
            o.Table(new[] { "stage", "measured", "rate" }, tr);

            if (json.TryGetValue("field_g12_s_nt", out double ft))
            {
                o.Header("What that implies for a full-space scan");
                double secs = ft * 4294967296.0;
                o.Field("full G12 field", Out.F(ft, 3) + " s per world on " + workers + " threads");
                o.Field("2^32 worlds", Out.F(secs / 86400.0 / 365.25, 1) + " years at that cost");
                o.Note("");
                o.Note("That is the honest number for the most expensive per-world stage at full resolution.");
                o.Note("A search is only feasible with cheaper prefilters; this figure is what they must beat.");
                o.Note("Construction alone (" + Out.F(perWorld * 1000, 1) + " ms) over 2^32 worlds is already "
                       + Out.F(perWorld * 4294967296.0 / 86400.0 / 365.25, 1) + " years on one thread, "
                       + Out.F(perWorld * 4294967296.0 / workers / 86400.0 / 365.25, 1) + " on " + workers + ".");
            }

            o.Line();
            return ExitCodes.Ok;
        }

        private static void WriteImplied(System.Text.Json.Utf8JsonWriter j, double fieldSecondsPerWorld)
        {
            j.WriteNumber("full_g12_seconds_per_world", fieldSecondsPerWorld);
            j.WriteNumber("seconds_for_2_pow_32", fieldSecondsPerWorld * 4294967296.0);
            j.WriteNumber("years_for_2_pow_32", fieldSecondsPerWorld * 4294967296.0 / 86400.0 / 365.25);
        }
    }
}
