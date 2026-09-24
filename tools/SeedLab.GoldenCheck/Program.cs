using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using SeedLab.WorldGen;
using SeedLab.WorldGen.Unity;

namespace SeedLab.GoldenCheck
{
    /// <summary>
    /// Validates <see cref="WorldGeneratorPort"/> against the generator's OWN INTERNAL STATE, as the
    /// running game wrote it into <c>goldens\worldgen-&lt;seedHex&gt;-&lt;source&gt;.json</c> and its
    /// <c>-riverpoints.bin</c> sidecar (spec 04 section 3.6.1, dumper
    /// <c>WorldGenReader.WriteWorldGen</c>/<c>WriteRiverPoints</c>).
    ///
    /// Every float is compared as its IEEE-754 bit pattern, taken from the golden's sibling
    /// <c>"bits"</c> object - never from the printed decimal, which is lossy.
    ///
    ///   dotnet run -c Release --project tools\SeedLab.GoldenCheck [-- &lt;dumpRoot&gt;]
    ///
    /// Exit 0 when every comparison is exact.
    /// </summary>
    public static class Program
    {
        private static int s_failed;

        public static int Main(string[] args)
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            string root = args.Length > 0 ? args[0] : FindDumpRoot();
            string goldens = Path.Combine(root, "goldens");
            if (!Directory.Exists(goldens))
            {
                Console.Error.WriteLine("no goldens directory under " + root);
                return 2;
            }

            Console.WriteLine("SeedLab world-generator INTERNALS check");
            Console.WriteLine("  dump root   " + root);

            string[] files = Directory.GetFiles(goldens, "worldgen-*.json");
            Array.Sort(files, StringComparer.Ordinal);
            List<string> gen = new List<string>();
            foreach (string f in files) if (!f.EndsWith("-riverpoints.json", StringComparison.Ordinal)) gen.Add(f);
            Console.WriteLine("  captures    " + gen.Count);
            Console.WriteLine();

            foreach (string f in gen) CheckOne(f);

            // A float32 GetHeight oracle for the THIRD seed, from the same dump.
            foreach (string f in Directory.GetFiles(goldens, "locationinstances-*.json")) CheckInstanceHeights(f);

            Console.WriteLine();
            Console.WriteLine(new string('=', 78));
            Console.WriteLine("VERDICT: " + (s_failed == 0 ? "PASS - every internal field is bit-identical"
                                                          : "FAIL - " + s_failed + " check(s) differ"));
            Console.WriteLine(new string('=', 78));
            return s_failed == 0 ? 0 : 1;
        }

        // -------------------------------------------------------------------------------------------

        private static void CheckOne(string path)
        {
            using FileStream fs = File.OpenRead(path);
            using JsonDocument doc = JsonDocument.Parse(fs);
            JsonElement r = doc.RootElement;

            int seed = r.GetProperty("seed").GetInt32();
            int version = r.GetProperty("version").GetInt32();
            bool menu = r.GetProperty("menu").GetBoolean();
            string world = Str(r, "worldName");
            string seedText = Str(r, "seedText");
            string stamp = Str(r, "stamp");
            string mode = Mode(stamp);

            Console.WriteLine(new string('-', 78));
            Console.WriteLine(Path.GetFileName(path));
            Console.WriteLine("  seed " + seed + " (0x" + ((uint)seed).ToString("X8") + ")  seedText \"" + seedText
                              + "\"  world \"" + world + "\"  worldGenVersion " + version
                              + "  menu " + menu + "  dumper mode " + mode);

            System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
            WorldGeneratorPort g = new WorldGeneratorPort(seed, version, menu);
            sw.Stop();
            Console.WriteLine("  port constructed + pregenerated in " + sw.ElapsedMilliseconds + " ms");

            // ---- 1. the seven constructor draws ----------------------------------------------------
            JsonElement bits = r.GetProperty("bits");
            int off = 0;
            off += Flt("offset0", bits, "offset0", g.Offset0);
            off += Flt("offset1", bits, "offset1", g.Offset1);
            off += Flt("offset2", bits, "offset2", g.Offset2);
            off += Flt("offset3", bits, "offset3", g.Offset3);
            off += Flt("offset4", bits, "offset4", g.Offset4);
            off += Int("riverSeed", r.GetProperty("riverSeed").GetInt32(), g.RiverSeed);
            off += Int("streamSeed", r.GetProperty("streamSeed").GetInt32(), g.StreamSeed);
            off += Flt("minMountainDistance", bits, "minMountainDistance", g.MinMountainDistance);
            off += Flt("minDarklandNoise", bits, "minDarklandNoise", g.MinDarklandNoise);
            off += Flt("maxMarshDistance", bits, "maxMarshDistance", g.MaxMarshDistance);
            Console.WriteLine("  [1] constructor state   10 fields (5 offsets, 2 seeds, 3 version constants): "
                              + (off == 0 ? "ALL EXACT" : off + " DIFFER"));
            s_failed += off;

            // The independent replay the dumper made on a scratch stream must agree with the reflected
            // fields; if it does not, the golden itself is suspect.
            if (r.TryGetProperty("constructorTrace", out JsonElement ct) && ct.ValueKind == JsonValueKind.Object)
                CheckCtorTrace(ct, seed, g);

            // ---- 2. lakes, rivers, streams ---------------------------------------------------------
            IReadOnlyList<Vec2>? lakes = g.GetLakes();
            int lakeDiff = CompareLakes(r, lakes);
            int riverDiff = CompareRivers(r, "rivers", g.GetRivers());
            int streamDiff = CompareRivers(r, "streams", g.GetStreams());
            s_failed += lakeDiff + riverDiff + streamDiff;

            // ---- 3. the rendered river point field --------------------------------------------------
            string? rp = r.TryGetProperty("riverPointsFile", out JsonElement rpe) && rpe.ValueKind == JsonValueKind.String
                       ? rpe.GetString() : null;
            if (rp != null)
            {
                string binPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(path))!,
                                              rp.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(binPath))
                    s_failed += CompareRiverPoints(binPath, seed, g,
                                                   r.GetProperty("riverPointCellCount").GetInt32(),
                                                   r.GetProperty("riverPointTotal").GetInt32());
                else
                    Console.WriteLine("  [3] river points        MISSING sidecar " + binPath);
            }
            else Console.WriteLine("  [3] river points        no sidecar recorded");

            // ---- 4. GetBiome / GetHeight grids ------------------------------------------------------
            int grids = 0;
            if (r.TryGetProperty("grids", out JsonElement ge) && ge.ValueKind == JsonValueKind.Array) grids = ge.GetArrayLength();
            string[] gridFiles = Directory.GetFiles(Path.GetDirectoryName(path)!, "worldgrid-*.bin");
            Console.WriteLine("  [4] GetBiome/GetHeight grids: " + grids + " listed in this capture, "
                              + gridFiles.Length + " worldgrid-*.bin present in goldens\\"
                              + (grids == 0 && gridFiles.Length == 0
                                 ? "  - the dumper was run without grid=, so this oracle does not exist" : ""));
        }

        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// <c>goldens\locationinstances-&lt;seedHex&gt;.json</c> stores every candidate's
        /// <c>Vector3.y</c>, and <c>ZoneSystem.GenerateLocationsTimeSliced</c> sets that y to
        /// <c>WorldGenerator.instance.GetHeight(x, z, out mask)</c> and never rewrites it
        /// (<c>RegisterLocation</c> stores the vector unchanged; <c>PlaceLocations</c> only copies it).
        /// So these are float32 GetHeight samples - the sharpest height oracle there is, because the
        /// minimap cache is quantised to binary16 - and this world is FRESH, so nothing was pruned.
        ///
        /// It is the T5 check of 05-validation.md run on a seed the port has never seen.
        /// </summary>
        private static void CheckInstanceHeights(string path)
        {
            using FileStream fs = File.OpenRead(path);
            using JsonDocument doc = JsonDocument.Parse(fs);
            JsonElement r = doc.RootElement;
            int seed = r.GetProperty("seed").GetInt32();
            string world = Str(r, "worldName");
            JsonElement arr = r.GetProperty("instances");

            Console.WriteLine(new string('-', 78));
            Console.WriteLine(Path.GetFileName(path) + "   float32 GetHeight oracle");
            Console.WriteLine("  seed " + seed + " (0x" + ((uint)seed).ToString("X8") + ")  world \"" + world
                              + "\"  instances " + arr.GetArrayLength().ToString("N0"));

            WorldGeneratorPort g = new WorldGeneratorPort(seed, 2, menu: false);
            int n = 0, exact = 0, placed = 0, placedExact = 0, river = 0, riverExact = 0;
            double worst = 0; long worstUlps = 0; string worstWhere = "";
            foreach (JsonElement e in arr.EnumerateArray())
            {
                JsonElement b = e.GetProperty("bits");
                float x = BitConverter.Int32BitsToSingle(ParseBits(b.GetProperty("x").GetString()!));
                float z = BitConverter.Int32BitsToSingle(ParseBits(b.GetProperty("z").GetString()!));
                int wantBits = ParseBits(b.GetProperty("y").GetString()!);
                bool isPlaced = e.GetProperty("placed").GetBoolean();

                float got = g.GetHeight(x, z);
                int gotBits = BitConverter.SingleToInt32Bits(got);
                g.GetRiverWeightPublic(x, z, out float rw, out _);
                bool inRiver = rw > 0f;

                n++;
                if (isPlaced) placed++;
                if (inRiver) river++;
                if (wantBits == gotBits)
                {
                    exact++;
                    if (isPlaced) placedExact++;
                    if (inRiver) riverExact++;
                }
                else
                {
                    float want = BitConverter.Int32BitsToSingle(wantBits);
                    double d = Math.Abs((double)got - (double)want);
                    long u = Math.Abs(Ordinal(got) - Ordinal(want));
                    if (d > worst)
                    {
                        worst = d; worstUlps = u;
                        worstWhere = Str(e, "prefabName") + " at (" + x.ToString("R") + ", " + z.ToString("R")
                                   + ") game " + want.ToString("R") + " ours " + got.ToString("R");
                    }
                }
            }
            Console.WriteLine("  [5] GetHeight as float32  " + exact.ToString("N0") + " / " + n.ToString("N0")
                              + " bit-exact = " + ((double)exact / n * 100.0).ToString("F3") + " %"
                              + "   differing " + (n - exact).ToString("N0"));
            Console.WriteLine("        of the " + placed.ToString("N0") + " PLACED instances: " + placedExact.ToString("N0")
                              + " exact;  of the " + river.ToString("N0") + " with a non-zero river/stream weight: "
                              + riverExact.ToString("N0") + " exact");
            if (worst > 0) Console.WriteLine("        worst " + worst.ToString("G6") + " m = " + worstUlps + " float ULPs: " + worstWhere);
            s_failed += (n - exact) == 0 ? 0 : 1;
        }

        /// <summary>Monotone ordinal over float32 bit patterns, so a difference is a count of ULPs.</summary>
        private static long Ordinal(float f)
        {
            int b = BitConverter.SingleToInt32Bits(f);
            return b >= 0 ? (long)b + 0x80000000L : 0x80000000L - (b & 0x7FFFFFFF);
        }

        private static void CheckCtorTrace(JsonElement ct, int seed, WorldGeneratorPort g)
        {
            int init = ct.GetProperty("initState").GetInt32();
            JsonElement draws = ct.GetProperty("draws");
            int n = draws.GetArrayLength();
            int bad = 0;
            if (init != seed) { bad++; Console.WriteLine("    ctor trace initState " + init + " != seed " + seed); }

            // Replay the same seven calls on our own UnityRandom, in the dumper's documented order.
            UnityRandom rnd = new UnityRandom();
            rnd.InitState(seed);
            int[] got = new int[7];
            got[0] = rnd.Range(-10000, 10000);
            got[1] = rnd.Range(-10000, 10000);
            got[2] = rnd.Range(-10000, 10000);
            got[3] = rnd.Range(-10000, 10000);
            got[4] = rnd.Range(int.MinValue, int.MaxValue);
            got[5] = rnd.Range(int.MinValue, int.MaxValue);
            got[6] = rnd.Range(-10000, 10000);

            int i = 0;
            foreach (JsonElement d in draws.EnumerateArray())
            {
                if (i >= 7) break;
                int want = d.GetProperty("resultInt").GetInt32();
                if (want != got[i])
                {
                    bad++;
                    Console.WriteLine("    ctor draw " + i + " (" + Str(d, "call") + ") golden " + want + " ours " + got[i]);
                }
                i++;
            }
            Console.WriteLine("  [1b] ctor RNG replay    " + n + " recorded draws, " + (n < 7 ? n : 7)
                              + " compared: " + (bad == 0 ? "ALL EXACT" : bad + " DIFFER"));
            s_failed += bad;
        }

        private static int CompareLakes(JsonElement r, IReadOnlyList<Vec2>? ours)
        {
            if (!r.TryGetProperty("lakes", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            {
                Console.WriteLine("  [2a] lakes              not present in the golden");
                return 0;
            }
            int n = arr.GetArrayLength();
            int mine = ours == null ? -1 : ours.Count;
            if (mine != n)
            {
                Console.WriteLine("  [2a] lakes              COUNT golden " + n + " ours " + mine);
                return 1;
            }
            int bad = 0, cmp = 0;
            int i = 0;
            foreach (JsonElement e in arr.EnumerateArray())
            {
                JsonElement b = e.GetProperty("bits");
                cmp += 2;
                bad += FltQ("lake[" + i + "].x", b, "x", ours![i].x);
                bad += FltQ("lake[" + i + "].y", b, "y", ours[i].y);
                i++;
            }
            Console.WriteLine("  [2a] lakes              " + n + " in order, " + cmp + " coordinates compared: "
                              + (bad == 0 ? "ALL BIT-EXACT" : bad + " DIFFER"));
            return bad;
        }

        private static int CompareRivers(JsonElement r, string key, IReadOnlyList<WorldGeneratorPort.River> ours)
        {
            if (!r.TryGetProperty(key, out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
            {
                Console.WriteLine("  [2] " + key + " not present in the golden");
                return 0;
            }
            int n = arr.GetArrayLength();
            if (ours.Count != n)
            {
                Console.WriteLine("  [2] " + key.PadRight(19) + " COUNT golden " + n + " ours " + ours.Count);
                return 1;
            }
            int bad = 0, cmp = 0, i = 0;
            foreach (JsonElement e in arr.EnumerateArray())
            {
                WorldGeneratorPort.River o = ours[i];
                cmp += 10;
                bad += Vec(key + "[" + i + "].p0", e, "p0", o.p0);
                bad += Vec(key + "[" + i + "].p1", e, "p1", o.p1);
                bad += Vec(key + "[" + i + "].center", e, "center", o.center);
                JsonElement b = e.GetProperty("bits");
                bad += FltQ(key + "[" + i + "].widthMin", b, "widthMin", o.widthMin);
                bad += FltQ(key + "[" + i + "].widthMax", b, "widthMax", o.widthMax);
                bad += FltQ(key + "[" + i + "].curveWidth", b, "curveWidth", o.curveWidth);
                bad += FltQ(key + "[" + i + "].curveWavelength", b, "curveWavelength", o.curveWavelength);
                i++;
            }
            Console.WriteLine("  [2" + (key == "rivers" ? "b" : "c") + "] " + key.PadRight(17) + " " + n
                              + " in order, " + cmp + " floats compared: "
                              + (bad == 0 ? "ALL BIT-EXACT" : bad + " DIFFER"));
            return bad;
        }

        private static int Vec(string label, JsonElement owner, string name, Vec2 ours)
        {
            JsonElement v = owner.GetProperty(name);
            JsonElement b = v.GetProperty("bits");
            return FltQ(label + ".x", b, "x", ours.x)
                 + FltQ(label + ".y", b, "y", ours.y);
        }

        // -------------------------------------------------------------------------------------------

        private static int CompareRiverPoints(string binPath, int seed, WorldGeneratorPort g,
                                              int cellCountJson, int totalJson)
        {
            using FileStream fs = File.OpenRead(binPath);
            using BinaryReader br = new BinaryReader(fs);
            char[] magic = { (char)br.ReadByte(), (char)br.ReadByte(), (char)br.ReadByte(), (char)br.ReadByte() };
            string m = new string(magic);
            int schema = br.ReadInt32();
            int binSeed = br.ReadInt32();
            float gridSize = br.ReadSingle();
            int cellCount = br.ReadInt32();
            if (m != "VRP1" || binSeed != seed)
            {
                Console.WriteLine("  [3] river points        BAD HEADER magic \"" + m + "\" seed " + binSeed);
                return 1;
            }

            IReadOnlyDictionary<Vec2i, WorldGeneratorPort.RiverPoint[]> mineAll = g.GetRiverPoints();

            int bad = 0;
            int cellsCompared = 0, pointsCompared = 0, pointsExact = 0;
            int cellsMissing = 0, cellsCountDiff = 0;
            int floatsCompared = 0, floatsDiff = 0;
            int orderDiffs = 0;          // cell matches as a multiset but not in order
            long total = 0;
            double worstAbs = 0; string worstWhere = "";
            HashSet<Vec2i> seen = new HashSet<Vec2i>();

            for (int c = 0; c < cellCount; c++)
            {
                int gx = br.ReadInt32(), gy = br.ReadInt32(), n = br.ReadInt32();
                Vec2i key = new Vec2i(gx, gy);
                seen.Add(key);
                total += n;

                int[] px = new int[n], py = new int[n], pw = new int[n], pw2 = new int[n];
                for (int i = 0; i < n; i++)
                {
                    px[i] = br.ReadInt32(); py[i] = br.ReadInt32();
                    pw[i] = br.ReadInt32(); pw2[i] = br.ReadInt32();
                }

                if (!mineAll.TryGetValue(key, out WorldGeneratorPort.RiverPoint[]? ours))
                {
                    cellsMissing++;
                    if (cellsMissing <= 5)
                        Console.WriteLine("        cell (" + gx + "," + gy + ") holds " + n + " golden points, we have none");
                    continue;
                }
                cellsCompared++;
                if (ours.Length != n)
                {
                    cellsCountDiff++;
                    if (cellsCountDiff <= 5)
                        Console.WriteLine("        cell (" + gx + "," + gy + ") golden " + n + " points, ours " + ours.Length);
                    continue;
                }

                bool cellOrderOk = true;
                for (int i = 0; i < n; i++)
                {
                    pointsCompared++;
                    floatsCompared += 4;
                    int ox = BitConverter.SingleToInt32Bits(ours[i].p.x);
                    int oy = BitConverter.SingleToInt32Bits(ours[i].p.y);
                    int ow = BitConverter.SingleToInt32Bits(ours[i].w);
                    int ow2 = BitConverter.SingleToInt32Bits(ours[i].w2);
                    int d = (ox != px[i] ? 1 : 0) + (oy != py[i] ? 1 : 0) + (ow != pw[i] ? 1 : 0) + (ow2 != pw2[i] ? 1 : 0);
                    if (d == 0) { pointsExact++; continue; }
                    floatsDiff += d;
                    cellOrderOk = false;
                    Track(ref worstAbs, ref worstWhere, "cell(" + gx + "," + gy + ")[" + i + "].p.x", px[i], ox);
                    Track(ref worstAbs, ref worstWhere, "cell(" + gx + "," + gy + ")[" + i + "].p.y", py[i], oy);
                    Track(ref worstAbs, ref worstWhere, "cell(" + gx + "," + gy + ")[" + i + "].w", pw[i], ow);
                    Track(ref worstAbs, ref worstWhere, "cell(" + gx + "," + gy + ")[" + i + "].w2", pw2[i], ow2);
                    if (floatsDiff <= 20)
                        Console.WriteLine("        cell (" + gx + "," + gy + ") point " + i
                                          + "  golden (" + F(px[i]) + ", " + F(py[i]) + ") w " + F(pw[i]) + " w2 " + F(pw2[i])
                                          + "  ours (" + ours[i].p.x.ToString("R") + ", " + ours[i].p.y.ToString("R")
                                          + ") w " + ours[i].w.ToString("R") + " w2 " + ours[i].w2.ToString("R"));
                }

                // A cell that differs only in ORDER is a different defect from one that differs in
                // content, and the spec says the order inside a cell is load-bearing for GetWeight.
                if (!cellOrderOk && SameMultiset(px, py, pw, pw2, ours)) orderDiffs++;
            }

            int extra = 0;
            foreach (KeyValuePair<Vec2i, WorldGeneratorPort.RiverPoint[]> kv in mineAll)
                if (!seen.Contains(kv.Key)) extra++;

            Console.WriteLine("  [3] river points        gridSize " + gridSize + ", schema " + schema);
            Console.WriteLine("        cells    golden " + cellCount.ToString("N0") + " (json says " + cellCountJson.ToString("N0")
                              + "), ours " + mineAll.Count.ToString("N0")
                              + "   matched " + cellsCompared.ToString("N0")
                              + "   missing " + cellsMissing + "   extra " + extra + "   count-mismatch " + cellsCountDiff);
            Console.WriteLine("        points   golden " + total.ToString("N0") + " (json says " + totalJson.ToString("N0")
                              + ")   compared " + pointsCompared.ToString("N0")
                              + "   bit-exact IN ORDER " + pointsExact.ToString("N0")
                              + "   differing " + (pointsCompared - pointsExact).ToString("N0"));
            Console.WriteLine("        floats   compared " + floatsCompared.ToString("N0") + " (x, y, w, w2 per point)"
                              + "   differing " + floatsDiff.ToString("N0"));
            if (orderDiffs > 0)
                Console.WriteLine("        " + orderDiffs + " cell(s) hold the SAME point set in a DIFFERENT ORDER");
            if (worstAbs > 0) Console.WriteLine("        worst absolute difference " + worstAbs.ToString("G6") + " at " + worstWhere);

            bad = cellsMissing + extra + cellsCountDiff + floatsDiff + (cellCount != cellCountJson ? 1 : 0)
                + (total != totalJson ? 1 : 0);
            return bad == 0 ? 0 : 1;
        }

        private static bool SameMultiset(int[] px, int[] py, int[] pw, int[] pw2,
                                         WorldGeneratorPort.RiverPoint[] ours)
        {
            Dictionary<(int, int, int, int), int> bag = new Dictionary<(int, int, int, int), int>();
            for (int i = 0; i < px.Length; i++)
            {
                (int, int, int, int) k = (px[i], py[i], pw[i], pw2[i]);
                bag[k] = bag.TryGetValue(k, out int v) ? v + 1 : 1;
            }
            for (int i = 0; i < ours.Length; i++)
            {
                (int, int, int, int) k = (BitConverter.SingleToInt32Bits(ours[i].p.x),
                                          BitConverter.SingleToInt32Bits(ours[i].p.y),
                                          BitConverter.SingleToInt32Bits(ours[i].w),
                                          BitConverter.SingleToInt32Bits(ours[i].w2));
                if (!bag.TryGetValue(k, out int v) || v == 0) return false;
                bag[k] = v - 1;
            }
            return true;
        }

        private static void Track(ref double worst, ref string where, string label, int goldenBits, int ourBits)
        {
            if (goldenBits == ourBits) return;
            double d = Math.Abs((double)BitConverter.Int32BitsToSingle(goldenBits)
                                - (double)BitConverter.Int32BitsToSingle(ourBits));
            if (d > worst) { worst = d; where = label; }
        }

        // -------------------------------------------------------------------------------------------

        private static int Flt(string label, JsonElement bits, string name, float ours)
        {
            int want = ParseBits(bits.GetProperty(name).GetString()!);
            int got = BitConverter.SingleToInt32Bits(ours);
            if (want == got) return 0;
            Console.WriteLine("        " + label + " golden 0x" + want.ToString("X8") + " (" + F(want)
                              + ") ours 0x" + got.ToString("X8") + " (" + ours.ToString("R") + ")  delta "
                              + ((double)ours - BitConverter.Int32BitsToSingle(want)).ToString("G6"));
            return 1;
        }

        private static int FltQ(string label, JsonElement bits, string name, float ours)
        {
            int want = ParseBits(bits.GetProperty(name).GetString()!);
            int got = BitConverter.SingleToInt32Bits(ours);
            if (want == got) return 0;
            Console.WriteLine("        " + label + " golden 0x" + want.ToString("X8") + " (" + F(want)
                              + ") ours 0x" + got.ToString("X8") + " (" + ours.ToString("R") + ")  delta "
                              + ((double)ours - BitConverter.Int32BitsToSingle(want)).ToString("G6"));
            return 1;
        }

        private static int Int(string label, int want, int got)
        {
            if (want == got) return 0;
            Console.WriteLine("        " + label + " golden " + want + " ours " + got);
            return 1;
        }

        private static string F(int bits) => BitConverter.Int32BitsToSingle(bits).ToString("R");

        private static int ParseBits(string hex)
        {
            if (hex.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) hex = hex.Substring(2);
            return unchecked((int)uint.Parse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture));
        }

        private static string Str(JsonElement e, string name)
            => e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

        private static string Mode(string stamp)
        {
            int i = stamp.IndexOf("mode=", StringComparison.Ordinal);
            if (i < 0) return "?";
            int j = stamp.IndexOf(' ', i);
            return j < 0 ? stamp.Substring(i + 5) : stamp.Substring(i + 5, j - i - 5);
        }

        private static string FindDumpRoot()
        {
            string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string b = Path.Combine(profile, "AppData", "valheim-dumper");
            if (Directory.Exists(b))
            {
                string[] d = Directory.GetDirectories(b);
                Array.Sort(d, StringComparer.Ordinal);
                if (d.Length > 0) return d[d.Length - 1];
            }
            throw new DirectoryNotFoundException("no dump under " + b + "; pass the folder as the first argument");
        }
    }
}
