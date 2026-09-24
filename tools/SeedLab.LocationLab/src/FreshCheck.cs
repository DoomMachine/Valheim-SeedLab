using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.Json;
using SeedLab.Contracts.Dump;
using SeedLab.Data;
using SeedLab.Locations;
using SeedLab.WorldGen;

namespace SeedLab.LocationLab
{
    /// <summary>
    /// THE decisive test: a world the game generated and dumped BEFORE anything was explored, so the
    /// instance set is the raw output of <c>GenerateLocationsTimeSliced</c> with nothing pruned.
    ///
    /// <para>Three oracles, in dependency order, because a failure in an early one explains every later
    /// one:
    /// (1) <c>goldens/altbiomes-assignment-&lt;seed&gt;.json</c> - the sector decomposition
    ///     (<see cref="BiomeField"/>) and the alt-biome assignment (<see cref="AltBiomeAssignment"/>);
    /// (2) the ordered placement list;
    /// (3) <c>goldens/locationinstances-&lt;seed&gt;.json</c> - every instance's zone, prefab and
    ///     position as float32 bits.</para>
    /// </summary>
    public static class FreshCheck
    {
        public static int Run(string[] args)
        {
            GameData data = GameData.Load();
            string seedHex = Args.Str(args, "--seed-hex", "0480A34C");
            int workers = Args.Int(args, "--workers", -1);
            bool noAlt = Args.Has(args, "--no-alt");
            bool quiet = Args.Has(args, "--quiet");

            string instPath = Path.Combine(data.Directory, "goldens", "locationinstances-" + seedHex + ".json");
            string altPath = Path.Combine(data.Directory, "goldens", "altbiomes-assignment-" + seedHex + ".json");
            if (!File.Exists(instPath)) { Console.WriteLine("missing " + instPath); return 2; }

            Console.WriteLine("data         " + data.Directory);
            Console.WriteLine("stamp        " + data.Stamp.GameVersion + " / " + data.Stamp.AssemblyValheimSha256.Substring(0, 8));

            using JsonDocument inst = JsonDocument.Parse(File.ReadAllText(instPath));
            JsonElement ir = inst.RootElement;
            int seed = ir.GetProperty("seed").GetInt32();
            Console.WriteLine("world        " + ir.GetProperty("worldName").GetString() + "  seed " + seed
                              + "  locationVersion " + ir.GetProperty("locationVersion").GetInt32()
                              + "  instances " + ir.GetProperty("count").GetInt32());

            // ---- table -------------------------------------------------------------------------
            LocationTable table = LocationTable.FromDump(data.Locations);
            Console.WriteLine("table        " + table.All.Count + " entries, " + table.Ordered.Count + " ordered");
            foreach (string n in table.Notes) Console.WriteLine("  note: " + n);

            int rc = 0;
            rc |= CheckOrder(table, ir);

            List<AltBiomeRuntime>? alts = null;
            if (!noAlt)
            {
                alts = new List<AltBiomeRuntime>();
                foreach (AltBiomeDef d in data.AltBiomes) alts.Add(AltBiomeRuntime.FromDump(d));
                Console.WriteLine("alt-biomes   " + alts.Count + " entries, "
                                  + CountEnabled(alts) + " enabled");
            }

            // ---- world -------------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("building the 2048^2 grid + sectors" + (alts != null ? " + alt-biomes" : "") + " ...");
            WorldLocations wl = WorldLocations.Build(seed, 2, alts, workers);
            Console.WriteLine("  grid " + wl.GridMilliseconds.ToString("F0") + " ms, sectors "
                              + wl.SectorMilliseconds.ToString("F0") + " ms, alt-biomes "
                              + wl.AltBiomeMilliseconds.ToString("F0") + " ms, "
                              + wl.Field.Sectors.Count + " sectors");

            if (File.Exists(altPath))
            {
                using JsonDocument alt = JsonDocument.Parse(File.ReadAllText(altPath));
                rc |= CheckSectors(wl, alt.RootElement);
                if (alts != null) rc |= CheckAssignments(wl, alts, alt.RootElement, seed);
            }
            else Console.WriteLine("no alt-biome golden at " + altPath);

            // ---- placement ---------------------------------------------------------------------
            Console.WriteLine();
            Console.WriteLine("running placement ...");
            PlacementResult res = wl.PlaceAll(table);
            Console.WriteLine("  " + res.Instances.Count.ToString("N0") + " instances in "
                              + res.Milliseconds.ToString("F0") + " ms");
            foreach (string w in res.Warnings) Console.WriteLine("  warning: " + w);

            rc |= CompareInstances(res, ir, table, quiet);

            Console.WriteLine();
            Console.WriteLine(rc == 0 ? "VERDICT: PASS" : "VERDICT: FAIL");
            return rc;
        }

        private static int CountEnabled(List<AltBiomeRuntime> a)
        {
            int n = 0; foreach (AltBiomeRuntime x in a) if (x.Enabled) n++; return n;
        }

        // ------------------------------------------------------------------------------------------
        // 2. the ordered list
        // ------------------------------------------------------------------------------------------

        private static int CheckOrder(LocationTable table, JsonElement ir)
        {
            JsonElement names = ir.GetProperty("orderedPrefabNames");
            int n = names.GetArrayLength();
            int bad = 0;
            if (n != table.Ordered.Count)
            {
                Console.WriteLine("ORDER        game " + n + " entries, engine " + table.Ordered.Count);
                bad++;
            }
            int lim = Math.Min(n, table.Ordered.Count);
            for (int i = 0; i < lim; i++)
            {
                string g = names[i].GetString() ?? "";
                if (!string.Equals(g, table.Ordered[i].PrefabName, StringComparison.Ordinal))
                {
                    if (bad < 10)
                        Console.WriteLine("ORDER        [" + i + "] game '" + g + "' engine '"
                                          + table.Ordered[i].PrefabName + "'");
                    bad++;
                }
            }
            Console.WriteLine("order        " + (bad == 0
                ? lim + "/" + lim + " prefab names identical to the game's own placement order"
                : bad + " positions differ"));
            return bad == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------------------------------
        // 1a. the sector decomposition
        // ------------------------------------------------------------------------------------------

        private static int CheckSectors(WorldLocations wl, JsonElement root)
        {
            BiomeField f = wl.Field;
            int fail = 0;

            // --- biome keys: point lists and sector membership
            JsonElement keys = root.GetProperty("biomeKeys");
            int keyBad = 0, ptBad = 0, seaBad = 0, memberBad = 0, permBad = 0;
            for (int i = 0; i < keys.GetArrayLength(); i++)
            {
                JsonElement k = keys[i];
                int biome = k.GetProperty("biome").GetInt32();
                if (i >= f.BiomesInKeyOrder.Count || (int)f.BiomesInKeyOrder[i].Biome != biome) { keyBad++; continue; }
                BiomeTypeInfo info = f.BiomesInKeyOrder[i];

                if (info.AllPoints.Count != k.GetProperty("allPointsCount").GetInt32())
                {
                    ptBad++;
                    Console.WriteLine("  POINTS  " + info.Biome + ": game "
                                      + k.GetProperty("allPointsCount").GetInt32() + " engine " + info.AllPoints.Count);
                }
                if (info.AllPointsAboveSeaLevel.Count != k.GetProperty("allPointsAboveSeaLevelCount").GetInt32())
                {
                    seaBad++;
                    Console.WriteLine("  ABOVESEA " + info.Biome + ": game "
                                      + k.GetProperty("allPointsAboveSeaLevelCount").GetInt32()
                                      + " engine " + info.AllPointsAboveSeaLevel.Count);
                }

                JsonElement si = k.GetProperty("sectorIndices");
                if (si.GetArrayLength() != info.Sectors.Count) { memberBad++; continue; }
                // Membership (order-free) and, separately, the post-shuffle permutation.
                HashSet<int> g = new HashSet<int>();
                for (int j = 0; j < si.GetArrayLength(); j++) g.Add(si[j].GetInt32());
                foreach (int s in info.Sectors) if (!g.Contains(s)) { memberBad++; break; }
                for (int j = 0; j < si.GetArrayLength(); j++)
                    if (si[j].GetInt32() != info.Sectors[j]) { permBad++; break; }
            }
            Console.WriteLine("biomeKeys    " + keys.GetArrayLength() + " keys, key order "
                              + (keyBad == 0 ? "ok" : keyBad + " WRONG") + ", AllPoints "
                              + (ptBad == 0 ? "ok" : ptBad + " WRONG") + ", AboveSeaLevel "
                              + (seaBad == 0 ? "ok" : seaBad + " WRONG") + ", sector membership "
                              + (memberBad == 0 ? "ok" : memberBad + " WRONG") + ", post-shuffle order "
                              + (permBad == 0 ? "ok (all keys)" : permBad + " keys differ"));
            fail += keyBad + ptBad + seaBad + memberBad + permBad;

            // --- sectors
            JsonElement sec = root.GetProperty("sectors");
            int n = sec.GetArrayLength();
            if (n != f.Sectors.Count)
            {
                Console.WriteLine("SECTORS      game " + n + " engine " + f.Sectors.Count);
                fail++;
            }
            int lim = Math.Min(n, f.Sectors.Count);
            int biomeBad = 0, edgeBad = 0, centerBad = 0, minmaxBad = 0, zoneBad = 0, hBad = 0, dBad = 0, nbBad = 0;
            int shown = 0, cxBad = 0, cyBad = 0, maxUlpX = 0, maxUlpY = 0;
            for (int i = 0; i < lim; i++)
            {
                JsonElement s = sec[i];
                BiomeSectorData o = f.Sectors[i];
                if ((int)o.Biome != s.GetProperty("biome").GetInt32()) biomeBad++;
                if (o.EdgeCount != s.GetProperty("edgeCount").GetInt32()) edgeBad++;

                uint gcx = Bits(s.GetProperty("center"), "x"), gcy = Bits(s.GetProperty("center"), "y");
                if (!BitEq(o.CenterX, gcx) || !BitEq(o.CenterY, gcy))
                {
                    centerBad++;
                    if (!BitEq(o.CenterX, gcx)) cxBad++;
                    if (!BitEq(o.CenterY, gcy)) cyBad++;
                    int ux = Ulp(gcx, B(o.CenterX)), uy = Ulp(gcy, B(o.CenterY));
                    if (Math.Abs(ux) > maxUlpX) maxUlpX = Math.Abs(ux);
                    if (Math.Abs(uy) > maxUlpY) maxUlpY = Math.Abs(uy);
                    if (shown++ < 8)
                        Console.WriteLine("  CENTER  #" + i + " " + o.Biome + " edges=" + o.EdgeCount
                                          + " sum=(" + Hex(B(o.CenterSumX)) + "," + Hex(B(o.CenterSumY)) + ")"
                                          + " game (" + Hex(gcx) + "," + Hex(gcy) + ")"
                                          + " engine (" + Hex(B(o.CenterX)) + "," + Hex(B(o.CenterY)) + ")"
                                          + " ulp(" + ux + "," + uy + ")"
                                          + "  | cand: div=" + Hex(B(Cand(o.CenterSumY, o.EdgeCount, 0)))
                                          + " dblMap=" + Hex(B(Cand(o.CenterSumY, o.EdgeCount, 1)))
                                          + " fma=" + Hex(B(Cand(o.CenterSumY, o.EdgeCount, 2)))
                                          + " dblDiv=" + Hex(B(Cand(o.CenterSumY, o.EdgeCount, 3))));
                }
                if (!BitEq(o.MinX, Bits(s.GetProperty("min"), "x")) || !BitEq(o.MinY, Bits(s.GetProperty("min"), "y"))
                    || !BitEq(o.MaxX, Bits(s.GetProperty("max"), "x")) || !BitEq(o.MaxY, Bits(s.GetProperty("max"), "y")))
                    minmaxBad++;
                if (o.MinZone.x != s.GetProperty("minZone").GetProperty("x").GetInt32()
                    || o.MinZone.y != s.GetProperty("minZone").GetProperty("y").GetInt32()
                    || o.MaxZone.x != s.GetProperty("maxZone").GetProperty("x").GetInt32()
                    || o.MaxZone.y != s.GetProperty("maxZone").GetProperty("y").GetInt32()) zoneBad++;
                if (!BitEq(o.HeightMin, BitsOf(s, "heightMin")) || !BitEq(o.HeightMax, BitsOf(s, "heightMax"))
                    || !BitEq(o.HeightAvg, BitsOf(s, "heightAvg"))) hBad++;
                if (!BitEq(o.DistanceFromCenter, BitsOf(s, "distanceFromCenter"))) dBad++;

                JsonElement nb = s.GetProperty("neighborBiomes");
                if (nb.GetArrayLength() != o.Neighbors.Count) nbBad++;
                else
                {
                    for (int j = 0; j < nb.GetArrayLength(); j++)
                        if (nb[j].GetInt32() != (int)f.Sectors[o.Neighbors[j]].Biome) { nbBad++; break; }
                }
            }
            Console.WriteLine("sectors      " + lim + " compared: biome " + Verdict(biomeBad) + ", edgeCount "
                              + Verdict(edgeBad) + ", center(bits) " + Verdict(centerBad) + ", min/max(bits) "
                              + Verdict(minmaxBad) + ", zones " + Verdict(zoneBad) + ", heights(bits) "
                              + Verdict(hBad) + ", distanceFromCenter(bits) " + Verdict(dBad)
                              + ", neighbours " + Verdict(nbBad));
            if (centerBad > 0)
                Console.WriteLine("  center detail: X wrong " + cxBad + " (max |ulp| " + maxUlpX
                                  + "), Y wrong " + cyBad + " (max |ulp| " + maxUlpY + ")");
            fail += biomeBad + edgeBad + centerBad + minmaxBad + zoneBad + hBad + dBad + nbBad;
            return fail == 0 ? 0 : 1;
        }

        private static string Verdict(int bad) => bad == 0 ? "ok" : bad + " WRONG";

        /// <summary>Signed ULP distance between two float bit patterns (same sign assumed).</summary>
        private static int Ulp(uint a, uint b)
        {
            long ia = Ord(a), ib = Ord(b);
            long d = ia - ib;
            return d > int.MaxValue ? int.MaxValue : d < int.MinValue ? int.MinValue : (int)d;
            static long Ord(uint v) => (v & 0x80000000u) != 0 ? -(long)(v & 0x7FFFFFFFu) : (long)v;
        }

        /// <summary>Candidate spellings of step E's world-space conversion, to bisect a 1-ULP gap.</summary>
        private static float Cand(float sum, int edges, int which)
        {
            switch (which)
            {
                case 0: return BiomeGrid.MapSpaceToWorldSpace(sum / (float)edges);
                case 1: { float m = sum / (float)edges; return (float)(((double)m - 1024.0) * 12.0 + 6.0); }
                case 2: { float m = sum / (float)edges; return MathF.FusedMultiplyAdd(m - 1024f, 12f, 6f); }
                default: { float m = (float)((double)sum / (double)edges); return BiomeGrid.MapSpaceToWorldSpace(m); }
            }
        }

        // ------------------------------------------------------------------------------------------
        // 1b. the alt-biome assignment
        // ------------------------------------------------------------------------------------------

        private static int CheckAssignments(WorldLocations wl, List<AltBiomeRuntime> alts, JsonElement root, int seed)
        {
            int opening = root.GetProperty("openingInitState").GetInt32();
            int ours = unchecked(seed + AltBiomeAssignment.GlobalSeedOffset);
            Console.WriteLine("altbiome seed  game InitState(" + opening + "), engine InitState(" + ours + ") "
                              + (opening == ours ? "ok" : "MISMATCH"));
            if (root.GetProperty("contaminated").GetBoolean())
                Console.WriteLine("  WARNING: the golden says contaminated=true");

            JsonElement asg = root.GetProperty("assignments");
            int bad = 0, countBad = 0, orderBad = 0, statBad = 0;
            int totalSectorsGame = 0, totalSectorsOurs = 0, exactLists = 0;
            for (int i = 0; i < asg.GetArrayLength(); i++)
            {
                JsonElement a = asg[i];
                string name = a.GetProperty("altBiomeName").GetString() ?? "";
                AltBiomeRuntime? m = null;
                foreach (AltBiomeRuntime x in alts) if (x.Name == name) { m = x; break; }
                if (m == null) { Console.WriteLine("  ALT  no runtime for '" + name + "'"); bad++; continue; }

                JsonElement si = a.GetProperty("sectorIndices");
                int gn = si.GetArrayLength();
                totalSectorsGame += gn;
                totalSectorsOurs += m.Sectors.Count;

                bool ok = gn == m.Sectors.Count;
                if (!ok) countBad++;
                else
                {
                    for (int j = 0; j < gn; j++)
                        if (si[j].GetInt32() != m.Sectors[j]) { ok = false; orderBad++; break; }
                }
                if (ok) exactLists++;

                if (a.GetProperty("validPlacementSectors").GetInt32() != m.ValidPlacementSectors
                    || a.GetProperty("validPlacementSectorCombos").GetInt32() != m.ValidPlacementSectorCombos)
                    statBad++;

                if (!ok || statBad > 0)
                {
                    string gs = "["; for (int j = 0; j < Math.Min(gn, 12); j++) gs += (j > 0 ? "," : "") + si[j].GetInt32(); gs += "]";
                    string os = "["; for (int j = 0; j < Math.Min(m.Sectors.Count, 12); j++) os += (j > 0 ? "," : "") + m.Sectors[j]; os += "]";
                    Console.WriteLine("  ALT  " + name.PadRight(18) + " game n=" + gn + " " + gs
                                      + "  engine n=" + m.Sectors.Count + " " + os
                                      + "  vps game " + a.GetProperty("validPlacementSectors").GetInt32()
                                      + " engine " + m.ValidPlacementSectors
                                      + "  combos game " + a.GetProperty("validPlacementSectorCombos").GetInt32()
                                      + " engine " + m.ValidPlacementSectorCombos);
                }
            }
            Console.WriteLine("assignments  " + asg.GetArrayLength() + " alt-biomes, " + exactLists
                              + " with a bit-identical sector list in AddModifier order; sector slots game "
                              + totalSectorsGame + " engine " + totalSectorsOurs
                              + "; count mismatches " + countBad + ", order mismatches " + orderBad
                              + ", counter mismatches " + statBad);

            // the per-sector AltBiomes lists, in AddModifier order
            JsonElement sec = root.GetProperty("sectors");
            int sectorListBad = 0;
            for (int i = 0; i < Math.Min(sec.GetArrayLength(), wl.Field.Sectors.Count); i++)
            {
                JsonElement names = sec[i].GetProperty("altBiomeNames");
                List<AltBiomeRuntime> o = wl.Field.Sectors[i].AltBiomes;
                if (names.GetArrayLength() != o.Count) { sectorListBad++; continue; }
                for (int j = 0; j < names.GetArrayLength(); j++)
                    if (names[j].GetString() != o[j].Name) { sectorListBad++; break; }
            }
            Console.WriteLine("sector alts  " + Verdict(sectorListBad) + " (per-sector AltBiomes list, in order)");
            return (bad + countBad + orderBad + statBad + sectorListBad) == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------------------------------
        // 3. the instances
        // ------------------------------------------------------------------------------------------

        private sealed class PerPrefab
        {
            public int Game, Ours, ExactZoneAndPos, ZoneMatchPosDiff, ZoneMissing, ZoneWrongPrefab, Extra;
            public int GamePlaced;
        }

        private static int CompareInstances(PlacementResult res, JsonElement ir, LocationTable table, bool quiet)
        {
            JsonElement arr = ir.GetProperty("instances");
            int n = arr.GetArrayLength();

            Dictionary<string, PerPrefab> per = new Dictionary<string, PerPrefab>(StringComparer.Ordinal);
            PerPrefab P(string k) { if (!per.TryGetValue(k, out PerPrefab? p)) { p = new PerPrefab(); per[k] = p; } return p; }

            Dictionary<Vec2s, LocationInstanceResult> mine = new Dictionary<Vec2s, LocationInstanceResult>();
            foreach (LocationInstanceResult i in res.Instances) mine[i.Zone] = i;

            HashSet<Vec2s> seen = new HashSet<Vec2s>();
            int exact = 0, posDiff = 0, missing = 0, wrongPrefab = 0;
            int placedGame = 0;
            List<string> firstDiffs = new List<string>();

            for (int i = 0; i < n; i++)
            {
                JsonElement e = arr[i];
                string name = e.GetProperty("prefabName").GetString() ?? "";
                Vec2s zone = new Vec2s(e.GetProperty("zoneX").GetInt32(), e.GetProperty("zoneY").GetInt32());
                bool placed = e.GetProperty("placed").GetBoolean();
                PerPrefab p = P(name);
                p.Game++;
                if (placed) { p.GamePlaced++; placedGame++; }
                seen.Add(zone);

                if (!mine.TryGetValue(zone, out LocationInstanceResult? o))
                {
                    missing++; p.ZoneMissing++;
                    if (firstDiffs.Count < 20) firstDiffs.Add("MISSING  " + name + " zone " + zone);
                    continue;
                }
                if (!string.Equals(o.PrefabName, name, StringComparison.Ordinal))
                {
                    wrongPrefab++; p.ZoneWrongPrefab++;
                    if (firstDiffs.Count < 20) firstDiffs.Add("PREFAB   zone " + zone + " game " + name + " engine " + o.PrefabName);
                    continue;
                }
                JsonElement bits = e.GetProperty("bits");
                if (B(o.X) == Hex32(bits, "x") && B(o.Y) == Hex32(bits, "y") && B(o.Z) == Hex32(bits, "z"))
                { exact++; p.ExactZoneAndPos++; }
                else
                {
                    posDiff++; p.ZoneMatchPosDiff++;
                    if (firstDiffs.Count < 20)
                        firstDiffs.Add("POS      " + name + " zone " + zone + " game ("
                                       + Hex(Hex32(bits, "x")) + "," + Hex(Hex32(bits, "y")) + "," + Hex(Hex32(bits, "z"))
                                       + ") engine (" + Hex(B(o.X)) + "," + Hex(B(o.Y)) + "," + Hex(B(o.Z)) + ")");
                }
            }

            int extra = 0;
            foreach (LocationInstanceResult o in res.Instances)
                if (!seen.Contains(o.Zone)) { extra++; P(o.PrefabName).Extra++; }
            foreach (LocationInstanceResult o in res.Instances) P(o.PrefabName).Ours++;

            Console.WriteLine();
            Console.WriteLine("=== FRESH WORLD INSTANCE COMPARISON ===");
            Console.WriteLine("  game instances       " + n.ToString("N0") + " (" + placedGame + " placed)");
            Console.WriteLine("  engine instances     " + res.Instances.Count.ToString("N0"));
            Console.WriteLine("  EXACT (zone+prefab+x/y/z bit-identical)  " + exact.ToString("N0")
                              + " / " + n.ToString("N0") + "  = "
                              + (n == 0 ? 0 : 100.0 * exact / n).ToString("F4") + " %");
            Console.WriteLine("  same zone+prefab, position differs       " + posDiff.ToString("N0"));
            Console.WriteLine("  zone holds a different prefab            " + wrongPrefab.ToString("N0"));
            Console.WriteLine("  game zone absent from engine             " + missing.ToString("N0"));
            Console.WriteLine("  engine zone absent from game             " + extra.ToString("N0"));

            if (firstDiffs.Count > 0)
            {
                Console.WriteLine("  first differences:");
                foreach (string s in firstDiffs) Console.WriteLine("    " + s);
            }

            // per prefab, worst first
            List<KeyValuePair<string, PerPrefab>> rows = new List<KeyValuePair<string, PerPrefab>>(per);
            rows.Sort((a, b) =>
            {
                int am = a.Value.Game - a.Value.ExactZoneAndPos, bm = b.Value.Game - b.Value.ExactZoneAndPos;
                int c = bm.CompareTo(am);
                return c != 0 ? c : string.CompareOrdinal(a.Key, b.Key);
            });
            int perfect = 0;
            foreach (KeyValuePair<string, PerPrefab> r in rows)
                if (r.Value.Game == r.Value.ExactZoneAndPos && r.Value.Ours == r.Value.Game) perfect++;
            Console.WriteLine("  prefabs reproduced exactly               " + perfect + " / " + rows.Count);
            Console.WriteLine("  worst prefabs (game / engine / exact / posDiff / missing / wrongPrefab / extra):");
            int shown = 0;
            foreach (KeyValuePair<string, PerPrefab> r in rows)
            {
                PerPrefab p = r.Value;
                if (p.Game == p.ExactZoneAndPos && p.Ours == p.Game) continue;
                if (shown++ >= (quiet ? 15 : 60)) break;
                Console.WriteLine("    " + r.Key.PadRight(34) + p.Game.ToString().PadLeft(5)
                                  + p.Ours.ToString().PadLeft(7) + p.ExactZoneAndPos.ToString().PadLeft(8)
                                  + p.ZoneMatchPosDiff.ToString().PadLeft(9) + p.ZoneMissing.ToString().PadLeft(9)
                                  + p.ZoneWrongPrefab.ToString().PadLeft(13) + p.Extra.ToString().PadLeft(7));
            }

            // per-type placed counters, which the game logs
            Console.WriteLine();
            Console.WriteLine("  per-type counts that differ (engine placed vs game instance count):");
            int cshown = 0, cbad = 0;
            foreach (LocationTypeResult t in res.Types)
            {
                per.TryGetValue(t.Location.PrefabName, out PerPrefab? p);
                int g = p?.Game ?? 0;
                if (g == t.Placed) continue;
                cbad++;
                if (cshown++ < 40)
                    Console.WriteLine("    " + t.Location.PrefabName.PadRight(34) + " game " + g.ToString().PadLeft(5)
                                      + "  engine placed " + t.Placed.ToString().PadLeft(5)
                                      + " / quantity " + t.Quantity);
            }
            if (cbad == 0) Console.WriteLine("    none - every type placed exactly as many as the game did");

            return exact == n && extra == 0 ? 0 : 1;
        }

        // ------------------------------------------------------------------------------------------

        private static uint Hex32(JsonElement bits, string name)
            => uint.Parse((bits.GetProperty(name).GetString() ?? "0x0").Substring(2), NumberStyles.HexNumber,
                          CultureInfo.InvariantCulture);

        private static uint Bits(JsonElement obj, string comp) => Hex32(obj.GetProperty("bits"), comp);

        private static uint BitsOf(JsonElement obj, string name) => Hex32(obj.GetProperty("bits"), name);

        private static uint B(float f) => unchecked((uint)BitConverter.SingleToInt32Bits(f));

        private static bool BitEq(float f, uint bits) => B(f) == bits;

        private static string Hex(uint v) => "0x" + v.ToString("X8", CultureInfo.InvariantCulture);
    }
}
