using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using SeedLab.Contracts.Dump;
using UnityEngine;
using URandom = UnityEngine.Random;

namespace SeedLab.Dumper
{
    /// <summary>
    /// The native-function evidence: items D1-D12 of spec 03 section 6.2. These are the measurements
    /// that close the last open variants - how <c>Range(float,float)</c> interpolates, whether
    /// <c>Range(a,a)</c> consumes a draw, what <c>insideUnitCircle</c> is made of, and how
    /// <c>Mathf.FloatToHalf</c> rounds.
    ///
    /// Everything that draws is inside a <see cref="RandomGuard"/>, and no guard spans a
    /// <c>yield</c> - a guard held across frames would undo the game's own draws when it restored.
    /// Each numbered item is therefore one synchronous block.
    ///
    /// Floats are recorded as bit patterns, never as decimal text (the writer adds the parallel
    /// "bits" object); the Perlin samples are raw float32 in a binary sidecar for the same reason.
    /// </summary>
    internal static class ModeNatives
    {
        /// <summary>Spec 03 section 6.2 D3: the world coordinates world generation actually evaluates
        /// its biome masks at, spanning the whole map including the negative side.</summary>
        private static readonly float[] ProbeWorld = { -10494f, -6000f, -12f, 0f, 6f, 4242f, 10494f };

        /// <summary>The offsets recovered for the development seed -1772362158 (spec 03 section 6.1).
        /// Kept as literals so the corpus covers the real operating range even at the main menu, where
        /// no world's offsets are available.</summary>
        private static readonly float[] ProbeOffsets = { -6080f, 718f, 4986f, -7704f };

        public static IEnumerator Run(DumpWriter w, Terminal term)
        {
            string stamp = GameInfo.Stamp("natives");
            var notes = new List<string>();

            string reason;
            if (Safety.MultiplayerBreach(out reason))
            {
                Plugin.Refuse(term, "the native-function dump", reason);
                yield break;
            }

            // See ModeAssets.Run: an all-zero UnityEngine.Random is a dead generator, and this dump's
            // whole purpose is to record what that generator does. Refuse rather than record zeros as
            // ground truth.
            if (RandomStateSafe.CurrentIsZero())
            {
                Plugin.Refuse(term, "the native-function dump",
                    "UnityEngine.Random reads as all zeros - a fixed point of its xorshift128 generator, " +
                    "so every draw returns 0. Recording that as the goldens would poison SeedLab's " +
                    "ground truth. Restart the game and dump again.");
                yield break;
            }
            RandomStateSafe.Trace("natives: START");

            // This dump is the short one - seconds, and its longest single stretch is the 262k-sample
            // Perlin block, about 16 frames - but "short" is a measurement of one machine on one day,
            // and a peer can connect inside it just the same. The rule is re-asked at every section
            // boundary and inside the Perlin loop, throttled to once a second by the Watch. See
            // Aborted().
            var watch = new Watch();

            // ---- UnityEngine.Random --------------------------------------------------------------
            RandomStateSafe.Trace("natives: UnityEngine.Random");
            NativesRandomFile random = RandomEvidence(stamp);
            w.WriteJson(DumpFormat.GoldensDir + "/" + DumpFormat.NativesRandomFile, random);
            if (random.stateRoundTrip != null && !(random.stateRoundTrip.statesEqual && random.stateRoundTrip.drawsEqual))
            {
                notes.Add("Random.state does NOT round-trip through its native setter. Every guard in " +
                          "this plugin - and the game's own WorldGenerator constructor - depends on it. " +
                          "Treat this dump, and any conclusion drawn from it, as unusable.");
                Plugin.Log.LogError("Random.state round-trip FAILED.");
            }
            Plugin.Say(term, "random: " + random.initStates.Length + " seeds, " +
                             random.traces.Length + " traces.");
            yield return null;
            if (Aborted(term, ref watch)) yield break;

            // ---- Mathf.PerlinNoise ---------------------------------------------------------------
            RandomStateSafe.Trace("natives: Mathf.PerlinNoise");
            var blocks = new List<PerlinBlockDef>();
            IEnumerator perlin = WritePerlin(w, blocks);
            while (perlin.MoveNext())
            {
                yield return perlin.Current;
                if (Aborted(term, ref watch)) yield break;
            }

            long perlinBytes = 0;
            string perlinSha = null;
            foreach (FileEntryDef f in w.Files)
            {
                if (f.path != null && f.path.EndsWith(DumpFormat.NativesPerlinBinFile, StringComparison.Ordinal))
                {
                    perlinBytes = f.bytes;
                    perlinSha = f.sha256;
                }
            }
            w.WriteJson(DumpFormat.GoldensDir + "/" + DumpFormat.NativesPerlinIndexFile, new NativesPerlinIndexFile
            {
                stamp = stamp,
                schema = DumpFormat.Schema,
                binFile = DumpFormat.NativesPerlinBinFile,
                binBytes = perlinBytes,
                binSha256 = perlinSha,
                blocks = blocks.ToArray(),
            });
            int perlinSamples = 0;
            foreach (PerlinBlockDef b in blocks) perlinSamples += b.sampleCount;
            Plugin.Say(term, "perlin: " + perlinSamples + " samples in " + blocks.Count + " blocks.");
            yield return null;
            if (Aborted(term, ref watch)) yield break;

            // ---- libm --------------------------------------------------------------------------
            RandomStateSafe.Trace("natives: libm");
            w.WriteJson(DumpFormat.GoldensDir + "/" + DumpFormat.NativesLibmFile, Libm(stamp));
            yield return null;
            if (Aborted(term, ref watch)) yield break;

            // ---- Mathf.FloatToHalf --------------------------------------------------------------
            RandomStateSafe.Trace("natives: Mathf.FloatToHalf");
            w.WriteJson(DumpFormat.GoldensDir + "/" + DumpFormat.NativesHalfFile, Half(stamp));
            yield return null;
            if (Aborted(term, ref watch)) yield break;

            // ---- version constants + manifest ---------------------------------------------------
            RandomStateSafe.Trace("natives: version constants + manifest");
            w.WriteJson(DumpFormat.VersionConstantsFile, GameInfo.VersionConstants(stamp));

            // The seed field's character limit is prefab data; it is only reachable at the main menu.
            // A footnote, but it is the one thing that says whether an inverted seed text is typeable.
            ModeSeedInput.Capture(w, stamp, notes);

            // A zero UnityEngine.Random state was seen or refused while this dump ran, so the
            // generator was dead for at least part of it. Anything here that came from a random draw
            // is suspect; say so in the dump itself, not only in the log.
            if (RandomStateSafe.Poisoned)
            {
                notes.Add("UnityEngine.Random was found in an all-zero state during this dump (see the " +
                          "BepInEx log for the call site). Zero is a fixed point of Unity's xorshift128, " +
                          "so every draw returned 0 until it was re-seeded. Treat this dump as UNVERIFIED " +
                          "and take it again from a freshly started game.");
            }

            var manifest = new DumpManifest
            {
                stamp = stamp,
                schema = DumpFormat.Schema,
                game = GameInfo.Read(),
                dumper = new DumperInfoDef
                {
                    version = Plugin.VERSION,
                    mode = "natives",
                    utc = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    guid = Plugin.GUID,
                },
                world = null,
                counts = null,
                notes = notes.ToArray(),
            };
            Manifest.Write(w, manifest, "natives");
            RandomStateSafe.Trace("natives: END");
        }

        /// <summary>
        /// The mid-run safety re-check, throttled by <paramref name="watch"/>. The natives dump reads
        /// nothing from the world, so the main menu stays legal here and only a MULTIPLAYER breach
        /// stops it - but it does drive <c>UnityEngine.Random</c> inside guards, and driving the
        /// shared stream while a peer is present is the thing that must never happen.
        /// </summary>
        private static bool Aborted(Terminal term, ref Watch watch)
        {
            if (!watch.Due()) return false;

            string reason;
            if (!Safety.MultiplayerBreach(out reason)) return false;

            Plugin.Refuse(term, "the native-function dump part way through",
                          reason + " The dump stopped where it was; no manifest was written and the " +
                          "folder is incomplete - treat it as unusable and run it again.");
            return true;
        }

        // ---- UnityEngine.Random ------------------------------------------------------------------

        private static NativesRandomFile RandomEvidence(string stamp)
        {
            var file = new NativesRandomFile
            {
                stamp = stamp,
                schema = DumpFormat.Schema,
                unityVersion = Application.unityVersion,
                stateRoundTrip = StateRoundTrip(),
            };

            // D4: InitState(s) -> the four state words. Proves the seeding recurrence directly rather
            // than by inference from the draws.
            List<int> seeds = SeedCorpus();
            var inits = new List<RandomInitStateDef>(seeds.Count);
            using (RandomGuard.Capture("natives: InitState corpus"))
            {
                foreach (int s in seeds)
                {
                    URandom.InitState(s);
                    inits.Add(new RandomInitStateDef { seed = s, state = RandomStateUtil.Current() });
                }
            }
            file.initStates = inits.ToArray();

            var traces = new List<RandomTraceDef>();

            // The seven WorldGenerator constructor draws, for every seed in the corpus. This is the
            // sequence the whole port hangs on, and offset4 being drawn LAST - after the two river
            // seeds - is the detail that a careless port gets wrong without any visible symptom
            // except misplaced Mistlands.
            foreach (int s in seeds) traces.Add(WorldGenReader.ReplayConstructorDraws(s));

            traces.Add(D5_Value());
            traces.Add(D6_RangeFloat());
            traces.Add(D6b_ReversedRange());
            traces.Add(D7_SameMinMax());
            traces.Add(D8_InsideUnitCircle());
            traces.Add(D9_LongRun());
            traces.Add(D10_RangeInt());
            traces.Add(D10b_FullRange());

            file.traces = traces.ToArray();
            return file;
        }

        /// <summary>
        /// The first ground-truth item of the whole dumper (spec 04 section 3.4): <c>Random.state</c>
        /// is a public readable/writable struct, but its setter is NATIVE. Every guard in this plugin,
        /// and <c>WorldGenerator..ctor</c> itself, assumes the round trip is exact. Proven, not assumed.
        /// </summary>
        private static RandomStateRoundTripDef StateRoundTrip()
        {
            var d = new RandomStateRoundTripDef();
            using (RandomGuard.Capture("natives: StateRoundTrip"))
            {
                URandom.InitState(4242);
                URandom.State s = URandom.state;
                d.saved = RandomStateUtil.Current();

                d.controlDraws = FourValueBits();

                RandomStateSafe.Restore(s, "natives: StateRoundTrip restore");   // restore
                d.afterRestore = RandomStateUtil.Current();

                URandom.InitState(999);                  // perturb hard
                d.afterInitState = RandomStateUtil.Current();

                RandomStateSafe.Restore(s, "natives: StateRoundTrip restore again");
                d.restoredDraws = FourValueBits();
            }
            d.statesEqual = RandomStateUtil.Equal(d.saved, d.afterRestore);
            d.drawsEqual = SameStrings(d.controlDraws, d.restoredDraws);
            return d;
        }

        private static string[] FourValueBits()
        {
            var r = new string[4];
            for (int i = 0; i < 4; i++) r[i] = Json.Hex(URandom.value);
            return r;
        }

        private static bool SameStrings(string[] a, string[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        /// <summary>
        /// The seeds the corpus covers: the boundary values, the two ground-truth worlds, the river and
        /// stream seeds recovered for the development seed, the <c>+ 920</c> literal that opens
        /// <c>GenerateAltBiomes</c>, a deterministic spread of 256 pseudo-random int32s, and - when a
        /// world is loaded - every real <c>worldSeed + prefabName.GetStableHashCode()</c> location
        /// stream seed, which is what placement actually uses.
        ///
        /// The spread comes from a fixed SplitMix64 in this plugin, NOT from UnityEngine.Random, so the
        /// corpus is identical on every machine and across every run.
        /// </summary>
        private static List<int> SeedCorpus()
        {
            var seeds = new List<int>();
            var seen = new HashSet<int>();
            Action<int> add = s => { if (seen.Add(s)) seeds.Add(s); };

            add(0);
            add(1);
            add(-1);
            add(int.MinValue);
            add(int.MaxValue);
            add(-1772362158);   // 'MWd8eV6svz', the development world
            add(319486907);     // 'hnBd9gJf2G', the hold-out world
            add(744350289);     // the development world's m_riverSeed
            add(952983356);     // and its m_streamSeed
            add(920);           // the GenerateAltBiomes literal
            add(7);
            add(12345);

            ulong x = 0x9E3779B97F4A7C15UL;
            for (int i = 0; i < 256; i++)
            {
                x += 0x9E3779B97F4A7C15UL;
                ulong z = x;
                z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
                z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
                z ^= z >> 31;
                add(unchecked((int)(uint)z));
            }

            try
            {
                ZoneSystem zs = ZoneSystem.instance;
                WorldGenerator wg = WorldGenerator.instance;
                if (zs != null && wg != null)
                {
                    int worldSeed = wg.GetSeed();
                    foreach (ZoneSystem.ZoneLocation l in zs.m_locations)
                    {
                        if (!l.m_enable || l.m_quantity == 0) continue;
                        string name = l.m_prefab.Name;
                        if (string.IsNullOrEmpty(name)) continue;
                        // ZoneSystem.cs:1880 - the per-location stream seed.
                        add(unchecked(worldSeed + name.GetStableHashCode()));
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Location stream seeds not added to the corpus: " + e.Message);
            }

            return seeds;
        }

        /// <summary>D5: 64 x <c>Random.value</c>, proving the step and the <c>1f/8388607f</c> scale.</summary>
        private static RandomTraceDef D5_Value()
        {
            var draws = new List<RandomDrawDef>(64);
            int[] afterInit;
            int[] end;
            using (RandomGuard.Seeded(-1772362158, "natives: D5_Value"))
            {
                afterInit = RandomStateUtil.Current();
                for (int i = 0; i < 64; i++)
                {
                    int[] before = RandomStateUtil.Current();
                    float v = URandom.value;
                    draws.Add(FloatDraw("value", v, before));
                }
                end = RandomStateUtil.Current();
            }
            return Trace("D5-value", "64 x Random.value from InitState(-1772362158)", -1772362158,
                         afterInit, draws, end);
        }

        /// <summary>
        /// D6: settles how <c>Range(float,float)</c> interpolates. The very first draw is the sharpest
        /// discriminator: <c>(1-f)*max + f*min</c> gives 0x42BF3B2E for <c>Range(60f,100f)</c> while
        /// <c>min + f*(max-min)</c> gives 0x4280C4D2 - they differ by 31 ulps, not by one. The ORDER
        /// matters: each call consumes from the same stream, so the expected values only line up if the
        /// calls happen exactly like this.
        /// </summary>
        private static RandomTraceDef D6_RangeFloat()
        {
            var draws = new List<RandomDrawDef>(6);
            int[] afterInit;
            int[] end;
            using (RandomGuard.Seeded(744350289, "natives: D6_RangeFloat"))
            {
                afterInit = RandomStateUtil.Current();

                int[] b0 = RandomStateUtil.Current();
                float r0 = URandom.Range(60f, 100f);
                draws.Add(FloatDraw("Range(60f,100f)", r0, b0));

                int[] b1 = RandomStateUtil.Current();
                float r1 = URandom.Range(60f, r0);
                draws.Add(FloatDraw("Range(60f,<previous result>)", r1, b1));

                int[] b2 = RandomStateUtil.Current();
                float r2 = URandom.Range(20f, 20f);
                draws.Add(FloatDraw("Range(20f,20f)", r2, b2));

                int[] b3 = RandomStateUtil.Current();
                float r3 = URandom.Range(-10000f, 10000f);
                draws.Add(FloatDraw("Range(-10000f,10000f)", r3, b3));

                int[] b4 = RandomStateUtil.Current();
                float r4 = URandom.Range(-10000f, 10000f);
                draws.Add(FloatDraw("Range(-10000f,10000f)", r4, b4));

                int[] b5 = RandomStateUtil.Current();
                float r5 = URandom.Range(0f, Mathf.PI * 2f);
                draws.Add(FloatDraw("Range(0f,PI*2)", r5, b5));

                end = RandomStateUtil.Current();
            }
            return Trace("D6-range-float",
                "Range(float,float) interpolation. Draw 1 discriminates (1-f)*max+f*min from min+f*(max-min).",
                744350289, afterInit, draws, end);
        }

        /// <summary>D6, second half: <c>min &gt; max</c> from a fresh <c>InitState</c>, which the
        /// forward form and the reversed form disagree about by one ulp.</summary>
        private static RandomTraceDef D6b_ReversedRange()
        {
            var draws = new List<RandomDrawDef>(1);
            int[] afterInit;
            int[] end;
            using (RandomGuard.Seeded(744350289, "natives: D6b_ReversedRange"))
            {
                afterInit = RandomStateUtil.Current();
                int[] b = RandomStateUtil.Current();
                draws.Add(FloatDraw("Range(100f,60f)", URandom.Range(100f, 60f), b));
                end = RandomStateUtil.Current();
            }
            return Trace("D6b-range-float-reversed", "min > max, from a fresh InitState(744350289)",
                         744350289, afterInit, draws, end);
        }

        /// <summary>
        /// D7: does <c>Range(a,a)</c> consume a draw? Each entry records whether the four state words
        /// changed. For the int overload the disassembly shows an early return that never touches the
        /// state; no world-generation call site passes min == max to it, so nothing in the game proves
        /// it and only this measurement does.
        /// </summary>
        private static RandomTraceDef D7_SameMinMax()
        {
            var draws = new List<RandomDrawDef>(4);
            int[] afterInit;
            int[] end;
            using (RandomGuard.Seeded(7, "natives: D7_SameMinMax"))
            {
                afterInit = RandomStateUtil.Current();

                int[] b0 = RandomStateUtil.Current();
                draws.Add(IntDraw("Range(5,5)", URandom.Range(5, 5), b0));

                int[] b1 = RandomStateUtil.Current();
                draws.Add(IntDraw("Range(0,0)", URandom.Range(0, 0), b1));

                int[] b2 = RandomStateUtil.Current();
                draws.Add(FloatDraw("Range(20f,20f)", URandom.Range(20f, 20f), b2));

                int[] b3 = RandomStateUtil.Current();
                draws.Add(IntDraw("Range(0,1)", URandom.Range(0, 1), b3));

                end = RandomStateUtil.Current();
            }
            return Trace("D7-same-min-max",
                "Range(a,a) for both overloads: stateUnchanged is the answer, plus Range(0,1) for contrast.",
                7, afterInit, draws, end);
        }

        /// <summary>
        /// D8: <c>insideUnitCircle</c>. The state delta says how many draws it consumes, and the two
        /// components say which of cos/sin is x. Both questions are open in spec 03 section 5.2 and
        /// both are settled by this one trace.
        /// </summary>
        private static RandomTraceDef D8_InsideUnitCircle()
        {
            var draws = new List<RandomDrawDef>(8);
            int[] afterInit;
            int[] end;
            using (RandomGuard.Seeded(12345, "natives: D8_InsideUnitCircle"))
            {
                afterInit = RandomStateUtil.Current();
                for (int i = 0; i < 8; i++)
                {
                    int[] before = RandomStateUtil.Current();
                    Vector2 v = URandom.insideUnitCircle;
                    draws.Add(new RandomDrawDef
                    {
                        call = "insideUnitCircle",
                        kind = "vector2",
                        resultFloat = v.x,
                        resultFloat2 = v.y,
                        stateAfter = RandomStateUtil.Current(),
                        stateUnchanged = RandomStateUtil.Equal(before, RandomStateUtil.Current()),
                    });
                }
                end = RandomStateUtil.Current();
            }
            return Trace("D8-inside-unit-circle",
                "8 x insideUnitCircle from InitState(12345). GetTerrainDelta draws 10 of these per " +
                "candidate location point, so an error here moves every location.",
                12345, afterInit, draws, end);
        }

        /// <summary>D9: 100 000 discarded draws, then the state and one more value. Catches a carry or
        /// ordering error that only shows after thousands of steps.</summary>
        private static RandomTraceDef D9_LongRun()
        {
            var draws = new List<RandomDrawDef>(1);
            int[] afterInit;
            int[] end;
            using (RandomGuard.Seeded(0, "natives: D9_LongRun"))
            {
                afterInit = RandomStateUtil.Current();
                // Summed rather than discarded so no compiler or JIT can elide the loop.
                float sink = 0f;
                for (int i = 0; i < 100000; i++) sink += URandom.value;
                if (float.IsNaN(sink)) Plugin.Log.LogWarning("D9 long run produced NaN.");
                int[] before = RandomStateUtil.Current();
                draws.Add(FloatDraw("value (after 100000 discarded)", URandom.value, before));
                end = RandomStateUtil.Current();
            }
            RandomTraceDef t = Trace("D9-long-run",
                "InitState(0), 100000 discarded Random.value draws, then the state and one more value.",
                0, afterInit, draws, end);
            // stateAfterInit is the state at step 0; the draw's own 'stateAfter' covers step 100001.
            return t;
        }

        /// <summary>D10: the int path's regression corpus.</summary>
        private static RandomTraceDef D10_RangeInt()
        {
            var draws = new List<RandomDrawDef>(16);
            int[] afterInit;
            int[] end;
            using (RandomGuard.Seeded(-1772362158, "natives: D10_RangeInt"))
            {
                afterInit = RandomStateUtil.Current();
                for (int i = 0; i < 16; i++)
                {
                    int[] before = RandomStateUtil.Current();
                    draws.Add(IntDraw("Range(-10000,10000)", URandom.Range(-10000, 10000), before));
                }
                end = RandomStateUtil.Current();
            }
            return Trace("D10-range-int", "16 x Range(-10000,10000) from InitState(-1772362158)",
                         -1772362158, afterInit, draws, end);
        }

        /// <summary>D10, second half: the full int range, which is how the river and stream seeds are
        /// drawn.</summary>
        private static RandomTraceDef D10b_FullRange()
        {
            var draws = new List<RandomDrawDef>(4);
            int[] afterInit;
            int[] end;
            using (RandomGuard.Seeded(7, "natives: D10b_FullRange"))
            {
                afterInit = RandomStateUtil.Current();
                for (int i = 0; i < 4; i++)
                {
                    int[] before = RandomStateUtil.Current();
                    draws.Add(IntDraw("Range(int.MinValue,int.MaxValue)",
                                      URandom.Range(int.MinValue, int.MaxValue), before));
                }
                end = RandomStateUtil.Current();
            }
            return Trace("D10b-range-int-full", "4 x Range(int.MinValue,int.MaxValue) from InitState(7)",
                         7, afterInit, draws, end);
        }

        private static RandomTraceDef Trace(string id, string note, int initState, int[] afterInit,
                                            List<RandomDrawDef> draws, int[] end)
        {
            return new RandomTraceDef
            {
                id = id,
                note = note,
                initState = initState,
                stateAfterInit = afterInit,
                draws = draws.ToArray(),
                stateAtEnd = end,
            };
        }

        private static RandomDrawDef IntDraw(string call, int value, int[] before)
        {
            int[] after = RandomStateUtil.Current();
            return new RandomDrawDef
            {
                call = call,
                kind = "int",
                resultInt = value,
                stateAfter = after,
                stateUnchanged = RandomStateUtil.Equal(before, after),
            };
        }

        private static RandomDrawDef FloatDraw(string call, float value, int[] before)
        {
            int[] after = RandomStateUtil.Current();
            return new RandomDrawDef
            {
                call = call,
                kind = "float",
                resultFloat = value,
                stateAfter = after,
                stateUnchanged = RandomStateUtil.Equal(before, after),
            };
        }

        // ---- Mathf.PerlinNoise --------------------------------------------------------------------

        private static IEnumerator WritePerlin(DumpWriter w, List<PerlinBlockDef> blocks)
        {
            string rel = DumpFormat.GoldensDir + "/" + DumpFormat.NativesPerlinBinFile;
            IEnumerator job = w.WriteBinaryStreaming(rel, bw => PerlinBody(bw, blocks));
            while (job.MoveNext()) yield return job.Current;
        }

        private static IEnumerator PerlinBody(BinaryWriter bw, List<PerlinBlockDef> blocks)
        {
            // Two passes are impossible in one stream, so the block list is built as it is written and
            // the byte offsets are tracked by hand. Header: magic, schema, blockCount.
            var d1 = D1Probes();
            var d3 = D3Probes();
            var d3b = D3BaseHeightProbes();

            foreach (char c in DumpFormat.PerlinMagic) bw.Write((byte)c);
            bw.Write(DumpFormat.Schema);
            bw.Write(4);   // blockCount: D1-2d, D1-1d, D2 grid, D3 (masks + base-height args)

            long offset = 4 + 4 + 4;

            offset = WriteBlock(bw, blocks, "D1-2d",
                "Spec 03 6.2 D1: abs fold, exact lattice points, 2^24 and beyond, real GetBiome arguments.",
                0, d1, offset);

            // The 1-D overload shares the same native entry point (PerlinNoise::NoiseNormalized), so
            // the pair proves the 1-D identity rather than assuming it.
            var d1x = new List<Vector2>(d1.Count);
            foreach (Vector2 p in d1) d1x.Add(new Vector2(p.x, 0f));
            offset = WriteBlock(bw, blocks, "D1-1d", "Mathf.PerlinNoise1D(x) at the same x values.",
                                1, d1x, offset);
            yield return null;

            // D2: a dense 512x512 grid including negatives - the coverage that catches a fold or a
            // fade-curve error rather than a lattice error.
            var block = new PerlinBlockDef
            {
                id = "D2-grid",
                note = "x = i*0.0517f - 60f, y = j*0.0517f - 60f, i,j in [0,512).",
                kind = 0,
                sampleCount = 512 * 512,
                byteOffset = offset,
            };
            blocks.Add(block);
            for (int i = 0; i < 512; i++)
            {
                float x = i * 0.0517f - 60f;
                for (int j = 0; j < 512; j++)
                {
                    float y = j * 0.0517f - 60f;
                    bw.Write(x);
                    bw.Write(y);
                    bw.Write(Mathf.PerlinNoise(x, y));
                }
                if ((i & 31) == 31) yield return null;
            }
            offset += (long)block.sampleCount * 12;

            var d3all = new List<Vector2>(d3.Count + d3b.Count);
            d3all.AddRange(d3);
            d3all.AddRange(d3b);
            WriteBlock(bw, blocks, "D3-real-arguments",
                "The exact arguments world generation feeds Perlin: the four biome-mask offsets over a " +
                "7x7 world grid, and GetBaseHeight's six octave scales plus its two +0.123/+0.321 pairs.",
                0, d3all, offset);
        }

        private static long WriteBlock(BinaryWriter bw, List<PerlinBlockDef> blocks, string id, string note,
                                       int kind, List<Vector2> pts, long offset)
        {
            blocks.Add(new PerlinBlockDef
            {
                id = id,
                note = note,
                kind = kind,
                sampleCount = pts.Count,
                byteOffset = offset,
            });
            for (int i = 0; i < pts.Count; i++)
            {
                float x = pts[i].x;
                float y = pts[i].y;
                bw.Write(x);
                bw.Write(y);
                bw.Write(kind == 1 ? Mathf.PerlinNoise1D(x) : Mathf.PerlinNoise(x, y));
            }
            return offset + (long)pts.Count * 12;
        }

        private static List<Vector2> D1Probes()
        {
            return new List<Vector2>
            {
                new Vector2(0f, 0f),
                new Vector2(0.5f, 0.5f),
                new Vector2(-0.5f, 0.5f),          // abs fold, one axis
                new Vector2(0.5f, -0.5f),          // abs fold, other axis
                new Vector2(1f, 1f),
                new Vector2(2f, 3f),
                new Vector2(5f, 7f),
                new Vector2(255f, 255f),
                new Vector2(256f, 256f),           // the permutation table's period
                new Vector2(0.25f, 0.75f),
                new Vector2(123.456f, 789.012f),
                new Vector2(-123.456f, -789.012f), // abs fold, both axes
                new Vector2(257f, 1.5f),           // wrap past 256
                new Vector2(255.99998f, 0f),       // just below the lattice
                new Vector2(1e-7f, 1e-7f),
                new Vector2(44000f, -44000f),      // the largest magnitude Valheim reaches
                new Vector2(100000f, 100000f),
                new Vector2(119999f, 0.5f),        // float ulp at 1.2e5 is 0.0078
                new Vector2(16777216f, 0.5f),      // 2^24: the fraction is gone
                new Vector2(16777218f, 3.25f),
                new Vector2(-16.08f, -6.08f),      // a real GetBiome mask argument
                new Vector2(-20.5f, 20.499f),      // an extreme mask argument
                new Vector2(110.00001f, 90.00001f),// a real base-height argument
                new Vector2(4.2f, -4.2f),
            };
        }

        /// <summary>
        /// The biome-mask arguments, computed exactly as <c>WorldGenerator.GetBiome</c> computes them:
        /// <c>(float)((double)m_offsetN + (double)w) * 0.0010000000474974513</c>. The literal is
        /// <c>0.001f</c> widened to double, and the inner cast to float before the multiply is real -
        /// writing it as a single double expression changes the last bits.
        /// </summary>
        private static List<Vector2> D3Probes()
        {
            var pts = new List<Vector2>();
            var offsets = new List<float>(ProbeOffsets);
            try
            {
                WorldGenerator wg = WorldGenerator.instance;
                if (wg != null)
                {
                    World world = ZNet.World;
                    if (world != null && !world.m_menu)
                    {
                        WorldGenDumpFile live = WorldGenReader.Read(wg, world, "");
                        offsets.Add(live.offset0);
                        offsets.Add(live.offset1);
                        offsets.Add(live.offset2);
                        offsets.Add(live.offset3);
                        offsets.Add(live.offset4);
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Live offsets not added to the Perlin probes: " + e.Message);
            }

            foreach (float off in offsets)
            {
                foreach (float wx in ProbeWorld)
                {
                    float ax = (float)((double)(float)((double)off + (double)wx) * 0.0010000000474974513);
                    foreach (float wy in ProbeWorld)
                    {
                        float ay = (float)((double)(float)((double)off + (double)wy) * 0.0010000000474974513);
                        pts.Add(new Vector2(ax, ay));
                    }
                }
            }
            return pts;
        }

        /// <summary>
        /// <c>GetBaseHeight</c>'s arguments: <c>num7 = wx + 100000.0 + m_offset0</c> and
        /// <c>num8 = wy + 100000.0 + m_offset1</c>, times each of the six octave scales, plus the two
        /// quarter-scale pairs offset by <c>+0.123.../+0.15123...</c> and <c>+0.321.../+0.231...</c>
        /// that make the sea-channel term. These are the ~1e5-magnitude arguments, where the float
        /// ulp is 0.0078 and a naive port loses the fraction.
        /// </summary>
        private static List<Vector2> D3BaseHeightProbes()
        {
            var pts = new List<Vector2>();
            double[] scales =
            {
                0.0020000000949949026 * 0.5,
                0.003000000026077032 * 0.5,
                0.0020000000949949026 * 1.0,
                0.003000000026077032 * 1.0,
                0.004999999888241291 * 1.0,
                0.009999999776482582 * 1.0,
            };

            float off0 = ProbeOffsets[0];
            float off1 = ProbeOffsets[2];
            foreach (float wx in ProbeWorld)
            {
                double nx = (double)wx + 100000.0 + (double)off0;
                foreach (float wy in ProbeWorld)
                {
                    double ny = (double)wy + 100000.0 + (double)off1;
                    foreach (double s in scales) pts.Add(new Vector2((float)(nx * s), (float)(ny * s)));

                    double q = 0.0020000000949949026 * 0.25;
                    pts.Add(new Vector2((float)(nx * q + 0.12300000339746475),
                                        (float)(ny * q + 0.15123000741004944)));
                    pts.Add(new Vector2((float)(nx * q + 0.32100000977516174),
                                        (float)(ny * q + 0.23100000619888306)));
                }
            }
            return pts;
        }

        // ---- libm (D12) -----------------------------------------------------------------------

        private static NativesLibmFile Libm(string stamp)
        {
            var samples = new List<LibmSampleDef>();
            double[] angles = { 0.0, 1e-8, 0.5, 1.0, 2.0, 3.14159265, 6.2831853, 20.0, 1000.0, -7.5 };
            foreach (double a in angles)
            {
                samples.Add(new LibmSampleDef { fn = "Sin", a = a, b = 0.0, result = Math.Sin(a) });
                samples.Add(new LibmSampleDef { fn = "Cos", a = a, b = 0.0, result = Math.Cos(a) });
            }

            foreach (float wx in ProbeWorld)
            {
                foreach (float wy in ProbeWorld)
                {
                    // WorldGenerator.WorldAngle is Sin((float)((float)Atan2(wx,wy) * 20.0)) - note the
                    // two separate narrowings to float. Both arguments are recorded so a port can diff
                    // Atan2 on its own before blaming Sin.
                    samples.Add(new LibmSampleDef
                    {
                        fn = "Atan2",
                        a = wx,
                        b = wy,
                        result = Math.Atan2(wx, wy),
                    });
                }
            }

            double[] powBases = { 0.05, 0.28, 0.5, 0.71, 0.9, 1.0 };
            double[] powExps = { 1.4, 1.5, 2.0, 4.0 };
            foreach (double b in powBases)
            {
                foreach (double e in powExps)
                {
                    samples.Add(new LibmSampleDef { fn = "Pow", a = b, b = e, result = Math.Pow(b, e) });
                }
            }

            var wa = new List<WorldAngleSampleDef>();
            foreach (float wx in ProbeWorld)
            {
                foreach (float wy in ProbeWorld)
                {
                    wa.Add(new WorldAngleSampleDef
                    {
                        wx = wx,
                        wy = wy,
                        result = WorldGenerator.WorldAngle(wx, wy),
                    });
                }
            }

            return new NativesLibmFile
            {
                stamp = stamp,
                schema = DumpFormat.Schema,
                samples = samples.ToArray(),
                worldAngle = wa.ToArray(),
            };
        }

        // ---- Mathf.FloatToHalf ------------------------------------------------------------------

        /// <summary>
        /// The adversarial float set for <c>Mathf.FloatToHalf</c>: exact ties, subnormals, the
        /// overflow boundary at 65520/65536, zeroes, NaN and the infinities - plus a sweep of real
        /// <c>GetBiomeHeight</c> magnitudes, because that is what the minimap height cache stores and
        /// what every height acceptance test is defined against.
        /// </summary>
        private static NativesHalfFile Half(string stamp)
        {
            var vals = new List<KeyValuePair<string, float>>();
            Action<string, float> add = (n, v) => vals.Add(new KeyValuePair<string, float>(n, v));

            add("zero", 0f);
            add("negative zero", -0f);
            add("one", 1f);
            add("minus one", -1f);
            add("tie 2049 (half spacing 2 at 2048)", 2049f);
            add("tie -2049", -2049f);
            add("tie 2051", 2051f);
            add("tie 1025 (spacing 1 at 1024)", 1025f);
            add("tie 1027", 1027f);
            add("smallest normal half", 0.00006103515625f);
            add("1.5x smallest normal (subnormal tie)", 0.00006103515625f * 1.5f);
            add("subnormal half", 0.000000059604645f);
            add("half subnormal tie", 0.000000089406967f);
            add("largest finite half 65504", 65504f);
            add("65519 (rounds to 65504)", 65519f);
            add("65520 (rounds to infinity)", 65520f);
            add("-65520", -65520f);
            add("65536", 65536f);
            add("NaN", float.NaN);
            add("positive infinity", float.PositiveInfinity);
            add("negative infinity", float.NegativeInfinity);
            add("float max", float.MaxValue);
            add("float epsilon", float.Epsilon);
            add("underflow to zero", 1e-9f);
            add("0.1", 0.1f);
            add("0.3", 0.3f);
            add("pi", Mathf.PI);

            // The real range: GetBiomeHeight is metres, water level 30, the cache's floor is -400 and
            // mountains reach a few hundred. One sample per 0.5 m through the interesting band, plus
            // the exact values a tie is most likely to land on.
            for (float h = -400f; h <= 400f; h += 0.5f) add("height sweep", h);
            float[] near = { 29.999998f, 30f, 30.000002f, 30.5f, 31.000002f, -0.0000001f, 0.0000001f };
            foreach (float h in near) add("near sea level", h);

            var samples = new List<HalfSampleDef>(vals.Count);
            foreach (KeyValuePair<string, float> kv in vals)
            {
                ushort h = Mathf.FloatToHalf(kv.Value);
                samples.Add(new HalfSampleDef
                {
                    note = kv.Key,
                    value = kv.Value,
                    half = h,
                    halfHex = h.ToString("X4"),
                    back = Mathf.HalfToFloat(h),
                });
            }

            return new NativesHalfFile
            {
                stamp = stamp,
                schema = DumpFormat.Schema,
                samples = samples.ToArray(),
            };
        }
    }
}
