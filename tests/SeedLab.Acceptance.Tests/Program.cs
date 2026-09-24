using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SeedLab.WorldGen;

namespace SeedLabAcceptanceTests
{
    /// <summary>
    /// SeedLab's permanent acceptance gate.
    ///
    ///   dotnet run -c Release --project tests\SeedLab.Acceptance.Tests [-- --serial] [--samples N]
    ///
    /// It regenerates both ground-truth worlds pixel by pixel against what the game itself wrote,
    /// cross-checks the save readers, and round-trips the seed maths. Exit code 0 only if every gating
    /// check passed.
    ///
    /// The hold-out world testworldclaude (seed 319486907) was never used while the biome and height
    /// code was being ported (a last one-ulp height residual was later diagnosed on both worlds and
    /// closed). Its result is the one that distinguishes a port from a fit, so it is reported
    /// separately everywhere and never averaged in with the development world.
    /// </summary>
    public static class Program
    {
        public static int Main(string[] args)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            bool serial = Array.IndexOf(args, "--serial") >= 0;
            int samples = 200_000;
            for (int i = 0; i < args.Length - 1; i++)
                if (args[i] == "--samples" && int.TryParse(args[i + 1], out int s)) samples = s;

            // --probe <seed> <x> <z> prints the intermediates at one point. It is here because the only
            // way to localise a sub-ULP height disagreement is to look at the factors that produce it.
            if (args.Length >= 4 && args[0] == "--probe") return Probe(args);
            if (args.Length >= 1 && args[0] == "--dn-variants")
            {
                foreach (WorldFixture wf in WorldFixture.All) DeepNorthVariants.Run(wf);
                return 0;
            }

            Report rep = new Report();
            Stopwatch total = Stopwatch.StartNew();

            Console.WriteLine("SeedLab acceptance suite");
            Console.WriteLine("  ground truth   " + GroundTruth.Root);
            Console.WriteLine("  machine        " + Environment.ProcessorCount + " logical cores, .NET "
                              + Environment.Version + (serial ? ", SERIAL mode" : ""));
            Console.WriteLine("  worlds         asdasdasd (development, seed -1772362158) and "
                              + "testworldclaude (HOLD-OUT, seed 319486907)");

            // ---------------------------------------------------------------------------------------
            Report.Section("Seed maths");
            SeedChecks.Run(rep, samples);

            // ---------------------------------------------------------------------------------------
            Report.Section("Save readers (.fwl2, .db2, minimap cache)");
            SaveChecks.WorldOracles[] oracles = new SaveChecks.WorldOracles[WorldFixture.All.Length];
            for (int i = 0; i < WorldFixture.All.Length; i++)
                oracles[i] = SaveChecks.Run(WorldFixture.All[i], rep);

            // ---------------------------------------------------------------------------------------
            WorldSweep.SweepResult[] sweeps = new WorldSweep.SweepResult[WorldFixture.All.Length];
            for (int i = 0; i < WorldFixture.All.Length; i++)
            {
                WorldFixture w = WorldFixture.All[i];
                Report.Section("World sweep: " + w.Name + (w.HoldOut ? "  [HOLD-OUT]" : "  [development]"));
                sweeps[i] = WorldSweep.Run(w, oracles[i].BiomeIndices, oracles[i].HeightHalf, serial);
                PrintSweep(w, sweeps[i], rep);
            }

            // ---------------------------------------------------------------------------------------
            Report.Section("Location instances: float32 GetHeight oracle (T5)");
            LocationHeightCheck.Result[] t5 = new LocationHeightCheck.Result[WorldFixture.All.Length];
            for (int i = 0; i < WorldFixture.All.Length; i++) t5[i] = LocationHeightCheck.Run(WorldFixture.All[i], rep);

            // ---------------------------------------------------------------------------------------
            total.Stop();
            PrintSummary(rep, sweeps, t5, total.Elapsed);
            return rep.Failed ? 1 : 0;
        }

        private static void PrintSweep(WorldFixture w, WorldSweep.SweepResult r, Report rep)
        {
            WorldSweep.Stats s = r.S;
            int n = WorldSweep.TextureSize * WorldSweep.TextureSize;

            Console.WriteLine("  construction + full pregeneration " + (r.ConstructSeconds * 1000).ToString("F0") + " ms");
            Console.WriteLine("    offsets " + r.Offsets[0] + ", " + r.Offsets[1] + ", " + r.Offsets[2] + ", "
                              + r.Offsets[3] + ", " + r.Offsets[4]
                              + "   riverSeed " + r.RiverSeed + "   streamSeed " + r.StreamSeed);
            Console.WriteLine("    lakes " + r.Lakes + "   rivers " + r.Rivers + "   streams " + r.Streams
                              + "   river grid cells " + r.RiverGridCells.ToString("N0")
                              + "   river points " + r.RiverPoints.ToString("N0"));
            Console.WriteLine("  sweep " + n.ToString("N0") + " pixels in " + r.Seconds.ToString("F2") + " s on "
                              + r.Workers + " worker(s)   (" + (n / r.Seconds / 1e6).ToString("F2") + " M px/s)");

            // ---- biome ---------------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("  BIOME");
            Console.WriteLine("    decodable pixels " + s.BiomeCompared.ToString("N0")
                              + "   mismatches " + s.BiomeMismatches.ToString("N0")
                              + "   rate " + Rate(s.BiomeMismatches, s.BiomeCompared));
            if (s.UnknownColourPixels > 0)
                Console.WriteLine("    UNKNOWN COLOUR pixels in the oracle: " + s.UnknownColourPixels.ToString("N0"));
            Console.WriteLine("    confusion (oracle -> ours):");
            if (s.BiomeMismatches == 0)
            {
                Console.WriteLine("      none - every decodable pixel agrees");
            }
            else
            {
                for (int a = 0; a < 9; a++)
                    for (int b = 0; b < 10; b++)
                        if (s.Confusion[a, b] > 0)
                            Console.WriteLine("      " + WorldSweep.BiomeName(a).PadRight(12) + " -> "
                                              + WorldSweep.BiomeName(b).PadRight(12) + " " + s.Confusion[a, b].ToString("N0"));
            }
            Console.WriteLine("    white pixels (Ocean|Mountain|DeepNorth share (255,255,255)): "
                              + s.WhiteCount.ToString("N0"));
            StringBuilder wb = new StringBuilder("      we answer ");
            for (int a = 0; a < 10; a++)
                if (s.WhiteGot[a] > 0) wb.Append(WorldSweep.BiomeName(a)).Append(' ').Append(s.WhiteGot[a].ToString("N0")).Append("   ");
            Console.WriteLine(wb.ToString());
            Console.WriteLine("      outside {Ocean, Mountain, DeepNorth}: " + s.WhiteOutsideSet.ToString("N0"));

            rep.Add(w.Name + "/T2", "biome over all decodable minimap pixels", s.BiomeMismatches == 0,
                    s.BiomeMismatches.ToString("N0") + " / " + s.BiomeCompared.ToString("N0") + " mismatched"
                    + (s.UnknownColourPixels > 0 ? ", " + s.UnknownColourPixels + " undecodable colours" : ""));
            rep.Add(w.Name + "/T2w", "white pixels resolve to Ocean, Mountain or DeepNorth",
                    s.WhiteOutsideSet == 0 && s.WhiteCount > 0,
                    s.WhiteOutsideSet.ToString("N0") + " of " + s.WhiteCount.ToString("N0") + " fall outside the set");

            // ---- T3a ------------------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("  T3a  world-edge constant (-400 m, pure geometry, seed independent)");
            Console.WriteLine("    oracle pixels holding -400: " + s.Minus400Oracle.ToString("N0")
                              + "   DUtils.Length(wx,wy) > 10500f: " + s.Minus400Predicate.ToString("N0")
                              + "   disagreements: " + s.Minus400Disagree.ToString("N0"));
            rep.Add(w.Name + "/T3a", "-400 m edge set matches DUtils.Length > 10500",
                    s.Minus400Disagree == 0 && s.Minus400Oracle == 1_788_980,
                    s.Minus400Oracle.ToString("N0") + " pixels (05-validation.md measured 1,788,980 for both worlds), "
                    + s.Minus400Disagree + " disagreements");

            // ---- height ---------------------------------------------------------------------------
            long diff = s.HeightCompared - s.ExactUnity;
            Console.WriteLine();
            Console.WriteLine("  HEIGHT  (ours quantised to IEEE binary16 and compared as 16-bit codes)");
            Console.WriteLine("    compared            " + s.HeightCompared.ToString("N0"));
            Console.WriteLine("    bit-exact           " + s.ExactUnity.ToString("N0") + "   rate "
                              + ((double)s.ExactUnity / s.HeightCompared).ToString("F9"));
            Console.WriteLine("    differing           " + diff.ToString("N0") + "   rate " + Rate(diff, s.HeightCompared));
            Console.WriteLine("    with .NET's (Half) cast instead (ties to EVEN, the rule 05-validation.md");
            Console.WriteLine("    section 1.3 prescribes while Mathf.FloatToHalf stays Unverified): bit-exact "
                              + s.ExactNet.ToString("N0") + ", differing " + (s.HeightCompared - s.ExactNet).ToString("N0"));
            Console.WriteLine("    exact midpoints encountered " + s.Ties.ToString("N0")
                              + " - the game stored the away-from-zero neighbour on " + s.TiesOracleAwayFromZero.ToString("N0")
                              + ", the even one on " + s.TiesOracleToEven.ToString("N0")
                              + ", neither on " + s.TiesOracleNeither.ToString("N0"));
            Console.WriteLine("    sign-of-zero-only differences " + s.SignOfZeroDifferences.ToString("N0"));
            Console.WriteLine("    max |difference|    " + s.MaxMetres.ToString("G6") + " m");

            string[] mnames = { "<= 0.001 m", "<= 0.01 m", "<= 0.0625 m", "<= 0.25 m", "<= 1 m", "> 1 m" };
            Console.WriteLine("    magnitude of the differences:");
            bool anyM = false;
            for (int a = 0; a < s.MetreHist.Length; a++)
                if (s.MetreHist[a] > 0) { Console.WriteLine("      " + mnames[a].PadRight(12) + " " + s.MetreHist[a].ToString("N0")); anyM = true; }
            if (!anyM) Console.WriteLine("      none");

            Console.WriteLine("    signed distance in half-ULPs (ours - game):");
            bool anyU = false;
            for (int a = 0; a < s.UlpHist.Length; a++)
                if (s.UlpHist[a] > 0)
                {
                    Console.WriteLine("      " + ((a - WorldSweep.Stats.UlpSpan) >= 0 ? "+" : "")
                                      + (a - WorldSweep.Stats.UlpSpan) + " ulp   " + s.UlpHist[a].ToString("N0"));
                    anyU = true;
                }
            if (s.UlpOutOfRange > 0) Console.WriteLine("      beyond +-10 ulps  " + s.UlpOutOfRange.ToString("N0"));
            if (!anyU && s.UlpOutOfRange == 0) Console.WriteLine("      none");

            if (diff > 0)
            {
                StringBuilder bb = new StringBuilder("    differing pixels by our biome: ");
                for (int a = 0; a < 10; a++)
                    if (s.DiffByBiome[a] > 0) bb.Append(WorldSweep.BiomeName(a)).Append('=').Append(s.DiffByBiome[a]).Append("  ");
                Console.WriteLine(bb.ToString());
                Console.WriteLine("    worst locations:");
                foreach (WorldSweep.Worst wo in s.Worsts)
                    Console.WriteLine("      (" + WorldSweep.WorldX(wo.J) + ", " + WorldSweep.WorldY(wo.I) + ") px["
                                      + wo.I + "," + wo.J + "]  " + wo.B
                                      + "  game " + HalfCodec.Decode(wo.OracleBits) + " (0x" + wo.OracleBits.ToString("X4") + ")"
                                      + "  ours " + HalfCodec.Decode(wo.OurBits) + " (0x" + wo.OurBits.ToString("X4") + ")"
                                      + "  " + wo.Ulps + " ulp  " + wo.Metres.ToString("G6") + " m"
                                      + "  riverWeight " + wo.RiverWeight.ToString("G6") + " width " + wo.RiverWidth.ToString("G6"));
            }

            // ---- do the heights actually test the rivers? -------------------------------------------
            Console.WriteLine();
            Console.WriteLine("  RIVERS - how much of the map the river/stream pass touches, and whether it is tested");
            Console.WriteLine("    pixels inside the world disc            " + s.InWorld.ToString("N0")
                              + "  (" + Pct(s.InWorld, WorldSweep.TextureSize * (long)WorldSweep.TextureSize) + " of the image)");
            Console.WriteLine("    with a non-zero river/stream weight     " + s.RiverTouched.ToString("N0")
                              + "  (" + Pct(s.RiverTouched, s.InWorld) + " of the world disc)");
            Console.WriteLine("      of those, bit-exact                   " + s.RiverTouchedExact.ToString("N0")
                              + "   differing " + s.RiverTouchedDiff.ToString("N0")
                              + "   rate " + Rate(s.RiverTouchedDiff, s.RiverTouched));
            Console.WriteLine("    weight > 0.5 (inside a channel)         " + s.RiverStrong.ToString("N0")
                              + "   bit-exact " + s.RiverStrongExact.ToString("N0")
                              + "   differing " + (s.RiverStrong - s.RiverStrongExact));
            Console.WriteLine("    weight > 0.9 on land                    " + s.RiverVeryStrongLand.ToString("N0")
                              + "   of which the GAME'S OWN height lies in the 23-30 m river floor band: "
                              + s.RiverVeryStrongLandInFloorBand.ToString("N0")
                              + "  (" + Pct(s.RiverVeryStrongLandInFloorBand, s.RiverVeryStrongLand) + ")");
            Console.WriteLine("    land with NO river weight               " + s.DryLand.ToString("N0")
                              + "   of which in that same band: " + s.DryLandInFloorBand.ToString("N0")
                              + "  (" + Pct(s.DryLandInFloorBand, s.DryLand) + ")");
            Console.WriteLine("    Reading: AddRivers (decomp 937-957) lerps the normalised height toward");
            Console.WriteLine("    Lerp(0.14, 0.12, LerpStep(20, 60, width)), i.e. 24-28 m after the x200 multiplier.");
            Console.WriteLine("    The gap between those two band percentages is the game's own data agreeing that a");
            Console.WriteLine("    river is where this port puts one; the per-pixel match rate above is the strength");
            Console.WriteLine("    of that agreement.");

            bool riversExercised = s.RiverTouched > 100_000
                                   && s.RiverVeryStrongLand > 1_000
                                   && s.RiverVeryStrongLandInFloorBand * 2 > s.RiverVeryStrongLand;
            rep.Add(w.Name + "/T3r", "the height comparison exercises the river/stream pass", riversExercised,
                    s.RiverTouched.ToString("N0") + " river-weighted pixels (" + Pct(s.RiverTouched, s.InWorld)
                    + " of the world disc), " + s.RiverVeryStrongLandInFloorBand.ToString("N0") + " of "
                    + s.RiverVeryStrongLand.ToString("N0") + " deep-channel land pixels carry the game's own river floor");

            // ---- the gate --------------------------------------------------------------------------
            // 05-validation.md T3: tier A is bit-identical. Tier B (>= 99.99 % and EVERY mismatch exactly
            // one half-ULP) is the specification's own conditional pass, and it is conditional on
            // Mathf.FloatToHalf still being Unverified - which it is: the dumper that would settle it
            // (spec 04 / test T7) has never been loaded by the game.
            bool oneUlpOnly = s.UlpOutOfRange == 0
                              && s.UlpHist[WorldSweep.Stats.UlpSpan - 1] + s.UlpHist[WorldSweep.Stats.UlpSpan + 1] == diff;
            double rate = (double)s.ExactUnity / s.HeightCompared;
            bool tierA = diff == 0;
            bool tierB = rate >= 0.9999 && oneUlpOnly;
            rep.Add(w.Name + "/T3", "height over all minimap pixels",
                    tierA || tierB,
                    tierA ? "TIER A - all " + s.HeightCompared.ToString("N0") + " codes identical"
                          : (tierB ? "TIER B - " + diff + " of " + s.HeightCompared.ToString("N0")
                                     + " differ, every one by exactly one half-ULP (max "
                                     + s.MaxMetres.ToString("G4") + " m); tier A needs Mathf.FloatToHalf settled by the dumper"
                                   : "FAIL - " + diff + " differ, not all within one half-ULP"));
        }

        /// <summary>Intermediates of GetBiomeHeight at one point, for localising a disagreement.</summary>
        private static int Probe(string[] args)
        {
            int seed = int.Parse(args[1]);
            float x = float.Parse(args[2]), z = float.Parse(args[3]);
            WorldGeneratorPort g = new WorldGeneratorPort(seed, 2, menu: false);
            Biome b = g.GetBiome(x, z);
            float h = g.GetBiomeHeight(b, x, z, out _);
            double ga = WorldGeneratorPort.CreateAshlandsGap(x, z);
            double gd = WorldGeneratorPort.CreateDeepNorthGap(x, z);
            float mult = (float)(200.0 * ga * gd);
            g.GetRiverWeightPublic(x, z, out float rw, out float rwid);
            Console.WriteLine("seed " + seed + "  (" + x + ", " + z + ")");
            Console.WriteLine("  biome              " + b);
            Console.WriteLine("  height             " + h.ToString("R") + "   bits 0x"
                              + BitConverter.SingleToInt32Bits(h).ToString("X8"));
            Console.WriteLine("  baseHeight         " + g.GetBaseHeightPublic(x, z).ToString("R"));
            Console.WriteLine("  CreateAshlandsGap  " + ga.ToString("R"));
            Console.WriteLine("  CreateDeepNorthGap " + gd.ToString("R"));
            Console.WriteLine("  multiplier         " + mult.ToString("R") + "  (exactly 200: "
                              + (mult == 200f) + ", both gaps exactly 1: " + (ga == 1.0 && gd == 1.0) + ")");
            Console.WriteLine("  WorldAngle         " + WorldGeneratorPort.WorldAngle(x, z).ToString("R"));
            Console.WriteLine("  DUtils.Length      " + DUtils.Length(x, z).ToString("R"));
            Console.WriteLine("  river weight/width " + rw.ToString("R") + " / " + rwid.ToString("R"));
            Console.WriteLine("  normalised height  " + (mult == 0f ? double.NaN : (double)h / mult).ToString("R"));
            return 0;
        }

        private static string Rate(long a, long b) => b == 0 ? "n/a"
            : (a == 0 ? "0" : ((double)a / b).ToString("G3") + " (" + ((double)a / b * 1e6).ToString("F2") + " ppm)");

        private static string Pct(long a, long b) => b == 0 ? "n/a" : ((double)a / b * 100.0).ToString("F2") + " %";

        private static void PrintSummary(Report rep, WorldSweep.SweepResult[] sweeps,
                                         LocationHeightCheck.Result[] t5, TimeSpan elapsed)
        {
            Console.WriteLine();
            Console.WriteLine(new string('=', 80));
            Console.WriteLine("SUMMARY");
            Console.WriteLine(new string('=', 80));
            for (int i = 0; i < WorldFixture.All.Length; i++)
            {
                WorldFixture w = WorldFixture.All[i];
                WorldSweep.Stats s = sweeps[i].S;
                long hdiff = s.HeightCompared - s.ExactUnity;
                Console.WriteLine("  " + (w.HoldOut ? "HOLD-OUT " : "dev      ") + w.Name.PadRight(16)
                                  + " seed " + w.Seed.ToString().PadLeft(12));
                Console.WriteLine("      biome   " + s.BiomeMismatches.ToString("N0") + " mismatches over "
                                  + s.BiomeCompared.ToString("N0") + " decodable pixels"
                                  + "   (+" + s.WhiteCount.ToString("N0") + " white, "
                                  + s.WhiteOutsideSet + " outside Ocean/Mountain/DeepNorth)");
                Console.WriteLine("      height  " + s.ExactUnity.ToString("N0") + " / " + s.HeightCompared.ToString("N0")
                                  + " exact = " + ((double)s.ExactUnity / s.HeightCompared * 100.0).ToString("F6")
                                  + " %   (" + hdiff + " differ, max " + s.MaxMetres.ToString("G4") + " m)");
                Console.WriteLine("      rivers  " + s.RiverTouched.ToString("N0") + " pixels carry river weight ("
                                  + Pct(s.RiverTouched, s.InWorld) + " of the world disc), "
                                  + s.RiverTouchedDiff + " of them differ");
                LocationHeightCheck.Result r5 = t5[i];
                Console.WriteLine("      T5      " + r5.Exact.ToString("N0") + " / " + r5.Total.ToString("N0")
                                  + " location heights bit-exact as float32 = "
                                  + ((double)r5.Exact / r5.Total * 100.0).ToString("F3") + " %   ("
                                  + (r5.Total - r5.Exact) + " differ: " + r5.RiverTouchedBad + " in a river, "
                                  + r5.NoRiverBad + " not; worst " + r5.MaxMetres.ToString("G3") + " m = "
                                  + r5.MaxUlps + " float ULPs)");
            }
            Console.WriteLine();
            int pass = 0, fail = 0, notes = 0;
            foreach (CheckResult r in rep.Results)
            {
                if (!r.Gating) { notes++; continue; }
                if (r.Passed) pass++; else fail++;
            }
            foreach (CheckResult r in rep.Results)
                if (r.Gating && !r.Passed)
                    Console.WriteLine("  FAILED: " + r.Id + "  " + r.Title + " - " + r.Detail);
            Console.WriteLine("  " + pass + " checks passed, " + fail + " failed"
                              + (notes > 0 ? ", " + notes + " non-gating notes" : ""));
            Console.WriteLine("  runtime " + elapsed.TotalSeconds.ToString("F1") + " s");
            Console.WriteLine("  VERDICT: " + (rep.Failed ? "FAIL" : "PASS"));
            Console.WriteLine(new string('=', 80));
        }
    }
}
