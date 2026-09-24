using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using SeedLab.Cli.Infra;
using SeedLab.Runtime.Execution;
using SeedLab.Render;
using SeedLab.Render.Png;
using SeedLab.Saves;
using SeedLab.Seeds;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Simd;
using SeedLab.WorldGen.Unity;

namespace SeedLab.Cli.Commands
{
    /// <summary>
    /// <c>vseed selftest</c> - re-runs a fast subset of the acceptance gate against the bundled ground
    /// truth, so the user can confirm at any moment that the tool still matches their installed game.
    ///
    /// <para>It also compares the installed <c>assembly_valheim.dll</c> against the build every number
    /// here was measured on. A different hash means the game changed under the tool: the ground truth
    /// in this folder still describes the OLD build, so the sweeps would keep passing while the live
    /// game no longer matches. That is exactly the failure mode worth shouting about.</para>
    /// </summary>
    public static class SelfTestCommand
    {
        public const string Help = @"vseed selftest [options]

  Regenerates the bundled ground-truth worlds and compares them, cell by cell, against what
  the game itself wrote, then checks the seed maths and the PNG encoder.

Options:
  --quick          sample every 4th row and column instead of every cell (16x faster)
  --world <name>   check one world only
  --no-map         skip the PNG round-trip check
  --strict         treat a changed game build as a failure, not a warning
  --threads <n>
  --json

Machine checks (no ground truth needed - they work on a clone of the public repository):
  --report         the machine report to send when SeedLab runs on a CPU it has not been
                   tested on: the processor, its ISA flags, the vector path chosen and why,
                   the C runtime's version, the machine self-test run now, every Perlin path
                   the hardware has, the libm-dense digests, and world fingerprints of 8
                   seeds compared with the reference machine's (terrain always; locations when
                   data\ is present). No machine name, user name or path. --seeds <n> (1-64)
                   fingerprints more seeds; --json prints every digest in full
  --isa-json       this process's ISA flags and vector dispatch as JSON (the knob matrix reads it)
  --simd-all       prove every Perlin path this hardware supports, not only the active one

Exit codes: 0 all checks passed, 1 a check failed, 3 the ground truth was not found.";

        private sealed class Check
        {
            public string Id = "";
            public string What = "";
            public string Result = "";
            public bool Pass;
            public bool Gating = true;
        }

        public static int Run(Args a, Out o, CliRuntime rt)
        {
            // The machine checks need no ground truth, so they branch before it is looked for: a
            // tester on another CPU has a clone of the public repository, which does not carry it.
            if (a.Flag("isa-json"))
            {
                a.RejectUnknown();
                return SeedLab.Cli.Analysis.MachineReport.IsaJson(rt);
            }

            if (a.Flag("report")) return SeedLab.Cli.Analysis.MachineReport.Report(a, o, rt);

            if (a.Flag("simd-all"))
            {
                a.RejectUnknown();
                string proof = PerlinSelfTest.ProveEveryPath();
                if (o.Json)
                {
                    o.J.WriteStartObject();
                    o.J.WriteString("command", "selftest --simd-all");
                    o.J.WriteString("dispatch", SimdDispatch.Summary);
                    o.J.WriteString("reason", SimdDispatch.Reason);
                    o.J.WriteString("perlin", proof);
                    o.J.WriteBoolean("passed", true);
                    o.J.WriteEndObject();
                    return ExitCodes.Ok;
                }

                o.Header("Every vector path this hardware supports");
                o.Field("simd path", SimdDispatch.Summary);
                o.Field("reason", SimdDispatch.Reason);
                o.Field("perlin", proof);
                o.Line();
                return ExitCodes.Ok;
            }

            bool quick = a.Flag("quick");
            bool doMap = a.Flag("map", true);
            bool strict = a.Flag("strict");
            string? only = a.Get("world");
            // The sweep holds a full 2048^2 field per worker, so it is planned like the height tier.
            WorkerPlan plan = rt.Plan(WorkTier.HeightsRivers, 12.0);
            int threads = plan.Workers;
            a.RejectUnknown();
            if (!o.Json) foreach (string line in plan.Lines()) Out.Info("  " + line);

            string? gt = Verified.FindGroundTruth();
            if (gt == null)
            {
                throw new CliException("ground truth not found (looked for groundtruth\\decoded above the working directory and the binary).",
                    ExitCodes.NotFound,
                    "run vseed from inside the SeedLab folder, or copy groundtruth\\ next to the executable");
            }

            List<Check> checks = new List<Check>();
            Stopwatch total = Stopwatch.StartNew();

            // ---- the installed game build ----------------------------------------------------------
            string? gameDir = Verified.FindGameDirectory();
            string installedHash = "(not found)";
            bool buildMatches = false;
            if (gameDir != null)
            {
                try
                {
                    installedHash = Verified.Sha256File(Verified.AssemblyPath(gameDir));
                    buildMatches = string.Equals(installedHash, Verified.AssemblyValheimSha256, StringComparison.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    installedHash = "(unreadable: " + ex.Message + ")";
                }
            }

            checks.Add(new Check
            {
                Id = "G1",
                What = "installed assembly_valheim.dll matches the build SeedLab was verified on",
                Result = gameDir == null
                    ? "no Valheim install found (set SEEDLAB_VALHEIM_DIR) - cannot tell"
                    : (buildMatches ? "same build (" + Verified.GameVersion + ", sha256 " + installedHash.Substring(0, 12) + "...)"
                                    : "DIFFERENT: installed " + installedHash + ", verified against " + Verified.AssemblyValheimSha256),
                Pass = buildMatches,
                Gating = strict,
            });

            // ---- the vector path this run used -------------------------------------------------------
            // Not a gate of its own - the Perlin self-test already refused to load if the active path
            // differed from the reference - but every sweep below ran on it, so it is named.
            checks.Add(new Check
            {
                Id = "D1",
                What = "vector path the sweeps below ran on (proved against the reference at startup)",
                Result = SimdDispatch.Summary + "; " + (PerlinFast.Use8Wide ? "8-wide Perlin in use" : "scalar Perlin in use"),
                Pass = true,
                Gating = false,
            });

            // ---- seed maths ------------------------------------------------------------------------
            int roundTrips = 2000, roundTripFail = 0;
            Random rng = new Random(424242);
            for (int i = 0; i < roundTrips; i++)
            {
                int seed = rng.Next(int.MinValue, int.MaxValue);
                string? t = SeedText.Invert(seed, SeedAlphabet.Alnum62);
                if (t == null || StableHash.SeedFromText(t) != seed) roundTripFail++;
            }

            checks.Add(new Check
            {
                Id = "V1",
                What = "GetStableHashCode inverse round trips (" + roundTrips + " random int32 seeds)",
                Result = (roundTrips - roundTripFail) + "/" + roundTrips + " texts re-hash to their seed",
                Pass = roundTripFail == 0,
            });

            checks.Add(new Check
            {
                Id = "V1b",
                What = "the game's World constructor (World..ctor) maps the empty text to seed 0",
                Result = "\"\" -> " + StableHash.SeedFromText("") + " (expected 0)",
                Pass = StableHash.SeedFromText("") == 0,
            });

            // ---- the worlds ------------------------------------------------------------------------
            List<Verified.Fixture> fixtures = new List<Verified.Fixture>();
            foreach (Verified.Fixture f in Verified.Fixtures)
            {
                if (only == null || string.Equals(only, f.Name, StringComparison.OrdinalIgnoreCase)) fixtures.Add(f);
            }

            if (fixtures.Count == 0) throw new CliException($"no bundled ground-truth world called '{only}'.", ExitCodes.NotFound);

            int stride = quick ? 4 : 1;
            foreach (Verified.Fixture f in fixtures)
            {
                string tag = f.Name + (f.HoldOut ? " [HOLD-OUT]" : " [development]");

                // The game's own .fwl2 is an oracle for the seed maths: the game wrote both the text
                // and the int, so the hash must reproduce the int it stored.
                string worldDir = Path.Combine(gt, "worlds", f.Name);
                try
                {
                    WorldSaveSet? set = SaveDiscovery.ResolveNewestSaveSet(worldDir);
                    if (set?.FwlPath != null)
                    {
                        WorldMeta meta = WorldMetaReader.Read(set.FwlPath);
                        bool pass = meta.Seed == StableHash.SeedFromText(meta.SeedName) && meta.Seed == f.Seed;
                        checks.Add(new Check
                        {
                            Id = "V1c/" + f.Name,
                            What = "the game's own .fwl2: GetStableHashCode(\"" + meta.SeedName + "\") == stored seed",
                            Result = StableHash.SeedFromText(meta.SeedName) + " vs stored " + meta.Seed,
                            Pass = pass,
                        });
                    }
                }
                catch (Exception ex)
                {
                    checks.Add(new Check { Id = "V1c/" + f.Name, What = "read the world's .fwl2", Result = ex.Message, Pass = false });
                }

                string biomePath = Path.Combine(gt, "decoded", f.Name + ".biome.u8");
                string heightPath = Path.Combine(gt, "decoded", f.Name + ".height.f32");
                if (!File.Exists(biomePath) || !File.Exists(heightPath))
                {
                    checks.Add(new Check { Id = "T2/" + f.Name, What = "decoded oracle present", Result = "missing", Pass = false });
                    continue;
                }

                byte[] oracleBiome = File.ReadAllBytes(biomePath);
                byte[] oracleHeightBytes = File.ReadAllBytes(heightPath);

                Sweep sw = SweepWorld(f.Seed, oracleBiome, oracleHeightBytes, stride, threads);

                checks.Add(new Check
                {
                    Id = "T2/" + f.Name,
                    What = "biome vs the game's minimap cache, " + tag,
                    Result = Out.N(sw.BiomeCompared - sw.BiomeMismatch) + "/" + Out.N(sw.BiomeCompared)
                             + " decodable cells match; " + Out.N(sw.WhiteCells) + " white cells, "
                             + Out.N(sw.WhiteOutsideSet) + " outside {Ocean, Mountain, Deep North}",
                    Pass = sw.BiomeMismatch == 0 && sw.WhiteOutsideSet == 0,
                });

                checks.Add(new Check
                {
                    Id = "T3/" + f.Name,
                    What = "height vs the game's minimap cache (binary16 code equality), " + tag,
                    Result = Out.N(sw.HeightExact) + "/" + Out.N(sw.HeightCompared) + " codes identical"
                             + (sw.HeightExact == sw.HeightCompared ? "" : ", worst " + Out.F(sw.WorstMetres, 4) + " m"),
                    Pass = sw.HeightExact == sw.HeightCompared,
                });

                checks.Add(new Check
                {
                    Id = "T3a/" + f.Name,
                    What = "world-edge constant: -400 in the cache iff DUtils.Length(x,z) > 10500",
                    Result = Out.N(sw.EdgeOracle) + " cells in the cache, " + Out.N(sw.EdgePredicate)
                             + " by the predicate, " + Out.N(sw.EdgeDisagree) + " disagree",
                    Pass = sw.EdgeDisagree == 0,
                });

                if (doMap && stride == 1)
                {
                    Check c = MapCheck(f, oracleBiome, threads);
                    checks.Add(c);
                }
                else if (doMap)
                {
                    // Saying nothing here read as "the renderer was checked too". It was not.
                    checks.Add(new Check
                    {
                        Id = "M1/" + f.Name,
                        What = "PNG written and read back equals the game's biome texture",
                        Result = "NOT RUN - it compares every pixel, so --quick skips it; drop --quick to run it",
                        Pass = true,
                        Gating = false,
                    });
                }
            }

            total.Stop();

            bool allPass = true;
            foreach (Check c in checks)
            {
                if (c.Gating && !c.Pass) allPass = false;
            }

            if (o.Json)
            {
                var j = o.J;
                j.WriteStartObject();
                j.WriteString("command", "selftest");
                j.WriteString("engine", Verified.EngineVersion);
                j.WriteString("ground_truth", gt);
                j.WriteBoolean("quick", quick);
                j.WriteStartObject("game_build");
                j.WriteString("verified_sha256", Verified.AssemblyValheimSha256);
                j.WriteString("installed_sha256", installedHash);
                j.WriteString("verified_version", Verified.GameVersion);
                j.WriteBoolean("matches", buildMatches);
                if (gameDir != null) j.WriteString("game_directory", gameDir);
                j.WriteEndObject();
                j.WriteStartArray("checks");
                foreach (Check c in checks)
                {
                    j.WriteStartObject();
                    j.WriteString("id", c.Id);
                    j.WriteString("what", c.What);
                    j.WriteString("result", c.Result);
                    j.WriteBoolean("pass", c.Pass);
                    j.WriteBoolean("gating", c.Gating);
                    j.WriteEndObject();
                }

                j.WriteEndArray();
                j.WriteBoolean("passed", allPass);
                j.WriteNumber("seconds", total.Elapsed.TotalSeconds);
                j.WriteEndObject();
                return allPass ? ExitCodes.Ok : ExitCodes.CheckFailed;
            }

            o.Header("Self test");
            o.Field("ground truth", gt);
            o.Field("mode", quick ? "quick (every 4th row and column)" : "full (every cell)");
            o.Field("verified against", "Valheim " + Verified.GameVersion + " (network " + Verified.NetworkVersion
                    + "), Steam build " + Verified.SteamBuildId);
            o.Field("installed build", gameDir == null ? "(no install found)" : installedHash);
            o.Line();

            if (!buildMatches && gameDir != null)
            {
                o.Line("  ############################################################################");
                o.Line("  #  THE INSTALLED GAME IS NOT THE BUILD SEEDLAB WAS VERIFIED ON.            #");
                o.Line("  #  assembly_valheim.dll has a different SHA-256, so world generation may   #");
                o.Line("  #  have changed. The ground truth in this folder still describes the OLD   #");
                o.Line("  #  build, so the checks below can pass while the live game disagrees.      #");
                o.Line("  #  Re-capture the ground truth before trusting any number from this tool.  #");
                o.Line("  ############################################################################");
                o.Line();
            }

            List<string[]> rows = new List<string[]>();
            foreach (Check c in checks)
            {
                rows.Add(new[]
                {
                    c.Pass ? "ok" : (c.Gating ? "FAIL" : "warn"),
                    c.Id,
                    c.What,
                    c.Result,
                });
            }

            o.Table(new[] { "", "id", "check", "result" }, rows);
            o.Line();
            o.Field("time", Out.F(total.Elapsed.TotalSeconds, 2) + " s");
            o.Field("verdict", allPass
                ? "PASS - this build still reproduces the game's own output on the bundled worlds"
                  + (quick ? ", at the 1 cell in 16 that --quick sampled" : ", cell by cell")
                : "FAIL - do not trust numbers from this build until it is fixed");
            o.Line();
            return allPass ? ExitCodes.Ok : ExitCodes.CheckFailed;
        }

        private sealed class Sweep
        {
            public long BiomeCompared, BiomeMismatch, WhiteCells, WhiteOutsideSet, UnknownColour;
            public long HeightCompared, HeightExact;
            public double WorstMetres;
            public long EdgeOracle, EdgePredicate, EdgeDisagree;

            public void Merge(Sweep s)
            {
                BiomeCompared += s.BiomeCompared; BiomeMismatch += s.BiomeMismatch;
                WhiteCells += s.WhiteCells; WhiteOutsideSet += s.WhiteOutsideSet;
                UnknownColour += s.UnknownColour;
                HeightCompared += s.HeightCompared; HeightExact += s.HeightExact;
                if (s.WorstMetres > WorstMetres) WorstMetres = s.WorstMetres;
                EdgeOracle += s.EdgeOracle; EdgePredicate += s.EdgePredicate; EdgeDisagree += s.EdgeDisagree;
            }
        }

        /// <summary>
        /// The game's own sampling, reproduced: <c>Minimap.GenerateWorldMap</c> walks
        /// <c>wy = (i - 1024) * 12 + 6</c> (row 0 south), <c>wx = (j - 1024) * 12 + 6</c> (col 0 west),
        /// calls <c>GetBiome(wx, wy)</c> and <c>GetBiomeHeight(biome, wx, wy, out _)</c>, and stores the
        /// height as <c>Mathf.FloatToHalf</c>. The comparison is on the stored codes, never on a
        /// tolerance.
        /// </summary>
        private static Sweep SweepWorld(int seed, byte[] oracleBiome, byte[] oracleHeightBytes, int stride, int threads)
        {
            const int n = 2048;
            const float px = 12f;
            WorldGeneratorPort gen = new WorldGeneratorPort(seed, Verified.WorldGenVersion, menu: false);
            // The caller always passes a planned count now (WorkerPlan.Workers is never below 1),
            // so this is a guard and not a sizing decision - the last ProcessorCount fallback in the
            // CLI used to live here and would silently have taken the whole machine.
            int workers = Math.Max(1, threads);
            int rows = (n + stride - 1) / stride;
            const int rowsPerChunk = 8;
            int chunks = (rows + rowsPerChunk - 1) / rowsPerChunk;
            Sweep[] parts = new Sweep[chunks];
            ushort edgeBits = BitConverter.HalfToUInt16Bits((Half)(-400f));

            Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = workers }, c =>
            {
                WorldGeneratorPort w = gen.Fork();
                Sweep s = new Sweep();
                int r0 = c * rowsPerChunk, r1 = Math.Min(rows, r0 + rowsPerChunk);
                for (int ri = r0; ri < r1; ri++)
                {
                    int i = ri * stride;
                    float wy = (float)(i - n / 2) * px + px / 2f;
                    for (int j = 0; j < n; j += stride)
                    {
                        float wx = (float)(j - n / 2) * px + px / 2f;
                        int k = i * n + j;

                        Biome b = w.GetBiome(wx, wy);
                        float h = w.GetBiomeHeight(b, wx, wy, out _);

                        // --- biome
                        byte ob = oracleBiome[k];
                        int dense = b.ToDenseIndex();
                        if (ob == 255)
                        {
                            s.WhiteCells++;
                            if (b != Biome.Ocean && b != Biome.Mountain && b != Biome.DeepNorth) s.WhiteOutsideSet++;
                        }
                        else if (ob <= 8)
                        {
                            s.BiomeCompared++;
                            if (dense != ob) s.BiomeMismatch++;
                        }
                        else
                        {
                            s.UnknownColour++;
                        }

                        // --- height: compare the stored binary16 code, via the float32 the decoder wrote
                        ushort ours = UnityHalf.Bits(h);
                        float oursF = (float)BitConverter.UInt16BitsToHalf(ours);
                        int oracleF = BitConverter.ToInt32(oracleHeightBytes, k * 4);
                        s.HeightCompared++;
                        if (BitConverter.SingleToInt32Bits(oursF) == oracleF) s.HeightExact++;
                        else
                        {
                            double d = Math.Abs((double)oursF - BitConverter.Int32BitsToSingle(oracleF));
                            if (d > s.WorstMetres) s.WorstMetres = d;
                        }

                        // --- the world edge is pure geometry
                        bool oracleEdge = BitConverter.HalfToUInt16Bits((Half)BitConverter.Int32BitsToSingle(oracleF)) == edgeBits;
                        bool predicate = DUtils.Length(wx, wy) > 10500f;
                        if (oracleEdge) s.EdgeOracle++;
                        if (predicate) s.EdgePredicate++;
                        if (oracleEdge != predicate) s.EdgeDisagree++;
                    }
                }

                parts[c] = s;
            });

            Sweep totalSweep = new Sweep();
            foreach (Sweep s in parts)
            {
                if (s != null) totalSweep.Merge(s);
            }

            return totalSweep;
        }

        /// <summary>
        /// Renders the world with <c>--plain --palette game</c>, writes the PNG, reads it back with
        /// SeedLab's own decoder and compares every pixel with the oracle's colour. This checks the
        /// renderer AND the encoder: the bytes compared came off the disk, not out of the buffer the
        /// encoder was handed.
        /// </summary>
        private static Check MapCheck(Verified.Fixture f, byte[] oracleBiome, int threads)
        {
            string path = Path.Combine(Path.GetTempPath(), "vseed-selftest-" + f.Name + ".png");
            try
            {
                WorldField field = WorldField.Sample(f.Seed, FieldGrid.G12, sampleLava: false, threads);
                MapOptions opt = new MapOptions
                {
                    Plain = true,
                    Palette = PaletteMode.Game,
                    Legend = false,
                    Rings = false,
                };
                RenderResult r = MapRenderer.Render(field, opt);
                PngEncoder.WriteFile(path, r.Canvas.Pixels, r.Canvas.Width, r.Canvas.Height);
                DecodedPng png = PngDecoder.ReadFile(path);

                long compared = 0, mismatch = 0, ambiguous = 0;
                for (int row = 0; row < 2048; row++)
                {
                    int imgY = 2047 - row;                    // field row 0 is south, PNG row 0 is north
                    for (int col = 0; col < 2048; col++)
                    {
                        int k = row * 2048 + col;
                        int p = (imgY * png.Width + col) * 3;
                        Rgb got = new Rgb(png.Rgb[p], png.Rgb[p + 1], png.Rgb[p + 2]);
                        byte ob = oracleBiome[k];
                        Rgb want = ob == 255
                            ? MapPalette.White
                            : MapPalette.GameColor(BiomeExtensions.FromDenseIndex(ob));
                        if (ob == 255) ambiguous++;
                        compared++;
                        if (!got.Equals(want)) mismatch++;
                    }
                }

                return new Check
                {
                    Id = "M1/" + f.Name,
                    What = "PNG written and read back equals the game's biome texture (--plain --palette game)",
                    Result = Out.N(compared - mismatch) + "/" + Out.N(compared) + " pixels identical ("
                             + Out.N(ambiguous) + " of them the game's ambiguous white)",
                    Pass = mismatch == 0,
                };
            }
            catch (Exception ex)
            {
                return new Check { Id = "M1/" + f.Name, What = "PNG round trip", Result = ex.Message, Pass = false };
            }
            finally
            {
                try { if (File.Exists(path)) File.Delete(path); } catch { /* a temp file is not worth failing over */ }
            }
        }
    }
}
