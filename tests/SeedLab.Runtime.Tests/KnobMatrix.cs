using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;

namespace SeedLab.RuntimeTests
{
    /// <summary>
    /// ST2: the runtime knobs really switch the vector level, and <c>--simd</c> really caps it.
    ///
    ///   dotnet run -c Release --project tests\SeedLab.Runtime.Tests -- --knob-matrix [--vseed &lt;vseed.exe&gt;]
    ///
    /// <para>Spawns the built <c>vseed.exe</c> directly (never through <c>dotnet run</c>, so no build tool
    /// runs under a knob) as <c>vseed selftest --isa-json</c> under each configuration of design 12.5 that
    /// this machine can reach, and asserts the EFFECT in the child - its <c>IsSupported</c> flags, the
    /// runtime's acceleration opinion and the path the dispatch chose - never the knob's name. Knob names
    /// drift between runtimes (<c>EnableAVX512F</c> does not exist in 10.0.12; <c>EnableAVX512</c> does),
    /// and a knob the runtime ignores would otherwise pass quietly. A configuration whose knob cannot show
    /// an effect on this CPU (a knob for a feature it lacks) is reported as not applicable, never as a
    /// pass.</para>
    ///
    /// <para>Every child also runs the machine self-test (numerics, libm-dense, denormals) under its knob,
    /// in a fresh cache folder so no stamp is reused, and must pass it. The <c>SEEDLAB_SIMD_EXPECT</c>
    /// hook is checked both ways: a true assertion lets the child start, a false one and an unknown name
    /// stop it.</para>
    /// </summary>
    public static class KnobMatrix
    {
        private sealed class Child
        {
            public int Exit;
            public string Stdout = "";
            public string Stderr = "";
            public Dictionary<string, string> Facts = new Dictionary<string, string>(StringComparer.Ordinal);
            public string StampKey = "";
            public string SelfTest = "";
            public bool Perlin8;
        }

        private static int _pass, _fail, _na;

        public static int Run(string[] args)
        {
            Console.WriteLine("SeedLab knob matrix (ST2): each runtime knob and --simd request, asserted by its effect in the child");
            Console.WriteLine(new string('=', 78));
            string? exe = null;
            for (int i = 0; i < args.Length - 1; i++) if (args[i] == "--vseed") exe = Path.GetFullPath(args[i + 1]);
            exe ??= FindVseed();
            if (exe == null || !File.Exists(exe))
            {
                Console.WriteLine("FAIL  vseed.exe not found (build src\\SeedLab.Cli -c Release, or pass --vseed <path>)");
                return 1;
            }

            Console.WriteLine("  vseed    " + exe);
            string cache = Path.Combine(Path.GetTempPath(), "seedlab-knobs-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(cache);
            try
            {
                Child k0 = Spawn(exe, cache, new Dictionary<string, string>());
                if (!Parsed(k0, "K0 default")) return Finish();
                Console.WriteLine("  K0       " + Line(k0));
                bool has2 = k0.Facts["avx2"] == "1", has512 = k0.Facts["avx512f"] == "1";
                bool vbmi = k0.Facts["avx512vbmi"] == "1", acc512 = k0.Facts["v512acc"] == "1";
                Check(k0.SelfTest is "Passed" or "PassedCached", "K0 default: the machine self-test passes", k0.SelfTest);
                Check(k0.Facts["active"] == (has2 ? "avx2" : "scalar") && k0.Perlin8 == has2,
                      "K0 default: the active path is the widest built kernel the hardware allows", Line(k0));
                Check(k0.Facts["hardware"] == (has512 && has2 && k0.Facts["avx512bw"] == "1" ? "avx512" : has2 ? "avx2" : "scalar"),
                      "K0 default: the hardware level is what IsSupported says", k0.Facts["hardware"]);

                List<(string Id, Child C)> all = new List<(string, Child)> { ("K0", k0) };

                // ---- the runtime's own knobs -----------------------------------------------------------
                Level(exe, cache, all, "K3", "DOTNET_EnableAVX512", "0", has512, k0,
                      c => Is(c, "avx512f", "0") && Is(c, "avx512bw", "0") && Is(c, "avx512vbmi", "0") && Same(c, k0, "avx2")
                           && Is(c, "hardware", has2 ? "avx2" : "scalar") && Is(c, "active", has2 ? "avx2" : "scalar"),
                      "AVX-512 off, AVX2 untouched, path " + (has2 ? "avx2" : "scalar"));
                Level(exe, cache, all, "K4", "DOTNET_EnableAVX512v2", "0", vbmi, k0,
                      c => Is(c, "avx512vbmi", "0") && Same(c, k0, "avx512f") && Same(c, k0, "avx512bw") && Same(c, k0, "active"),
                      "VBMI off, AVX-512 F/BW kept - the Skylake-X shape");
                Level(exe, cache, all, "K5", "DOTNET_PreferredVectorBitWidth", "256", has512 && acc512, k0,
                      c => Is(c, "v512acc", "0") && Same(c, k0, "avx512f") && Same(c, k0, "active"),
                      "512-bit vectors unaccelerated, AVX-512 still supported - the throttling-CPU state");
                Level(exe, cache, all, "K6", "DOTNET_EnableAVX2", "0", has2, k0,
                      c => Is(c, "avx2", "0") && Is(c, "active", "scalar") && Is(c, "hardware", "scalar") && !c.Perlin8,
                      "AVX2 off, scalar path");
                Level(exe, cache, all, "K7", "DOTNET_EnableHWIntrinsic", "0", k0.Facts["x86base"] == "1", k0,
                      c => AllFlagsOff(c) && Is(c, "active", "scalar") && !c.Perlin8,
                      "every intrinsic off (x86base, SSE..AVX-512, all Vector acceleration), scalar path");

                // ---- SeedLab's request ------------------------------------------------------------------
                Level(exe, cache, all, "K8", "SEEDLAB_SIMD", "scalar", has2, k0,
                      c => SameFlags(c, k0) && Is(c, "requested", "scalar") && Is(c, "active", "scalar") && !c.Perlin8,
                      "flags as K0, request scalar, scalar path");
                Level(exe, cache, all, "K2", "SEEDLAB_SIMD", "avx2", has2, k0,
                      c => SameFlags(c, k0) && Is(c, "requested", "avx2") && Is(c, "active", "avx2") && c.Perlin8,
                      "flags as K0, request avx2, avx2 path");
                Level(exe, cache, all, "K9", "SEEDLAB_SIMD", "avx512", has2, k0,
                      c => SameFlags(c, k0) && Is(c, "requested", "avx512") && Is(c, "active", "avx2"),
                      "request avx512 is a ceiling, capped at the widest kernel built (avx2)");

                Child flag = Spawn(exe, cache, new Dictionary<string, string>(), "--simd", "scalar");
                if (Parsed(flag, "--simd scalar"))
                {
                    Check(Is(flag, "requested", "scalar") && Is(flag, "active", "scalar") && !flag.Perlin8,
                          "--simd scalar on the command line reaches the dispatch before it is read", Line(flag));
                }

                Child badFlag = Spawn(exe, cache, new Dictionary<string, string>(), "--simd", "avx9");
                Check(badFlag.Exit == 2 && badFlag.Stderr.Contains("--simd", StringComparison.Ordinal),
                      "--simd with an unknown path is a usage error (exit 2)", "exit " + badFlag.Exit + ": " + FirstLine(badFlag.Stderr));

                Child badEnv = Spawn(exe, cache, new Dictionary<string, string> { ["SEEDLAB_SIMD"] = "avx9" });
                if (Parsed(badEnv, "SEEDLAB_SIMD=avx9"))
                {
                    Check(Is(badEnv, "requested", "auto") && Same(badEnv, k0, "active"),
                          "an unknown SEEDLAB_SIMD set by hand is treated as auto, and never crashes a type initialiser", Line(badEnv));
                }

                // ---- the in-process assertion hook, both ways --------------------------------------------
                Child okExpect = Spawn(exe, cache, new Dictionary<string, string>
                {
                    ["SEEDLAB_SIMD_EXPECT"] = "avx2=" + k0.Facts["avx2"] + ",active=" + k0.Facts["active"],
                });
                Check(okExpect.Exit == 0, "SEEDLAB_SIMD_EXPECT that holds lets the process start", "exit " + okExpect.Exit);
                Child badExpect = Spawn(exe, cache, new Dictionary<string, string>
                {
                    ["SEEDLAB_SIMD_EXPECT"] = "avx2=" + (has2 ? "0" : "1"),
                });
                Check(badExpect.Exit == 1 && (badExpect.Stderr + badExpect.Stdout).Contains("SEEDLAB_SIMD_EXPECT", StringComparison.Ordinal),
                      "SEEDLAB_SIMD_EXPECT that does not hold stops the process (exit 1, a failed check), naming the hook", "exit " + badExpect.Exit + ": " + FirstLine(badExpect.Stderr));
                Child typoExpect = Spawn(exe, cache, new Dictionary<string, string> { ["SEEDLAB_SIMD_EXPECT"] = "avx3=1" });
                Check(typoExpect.Exit == 1 && (typoExpect.Stderr + typoExpect.Stdout).Contains("not a fact", StringComparison.Ordinal),
                      "an assertion about a fact that does not exist stops it too (a typo cannot pass)", "exit " + typoExpect.Exit + ": " + FirstLine(typoExpect.Stderr));

                // ---- every distinct state has its own stamp -----------------------------------------------
                HashSet<string> keys = new HashSet<string>(StringComparer.Ordinal);
                int states = 0;
                foreach ((string id, Child c) in all)
                {
                    if (id == "K2" || id == "K9") continue;     // same flags and path as K0 by design; the request differs
                    states++;
                    keys.Add(c.StampKey);
                }

                Check(keys.Count == states, "each level reached has its own self-test stamp key (" + states + " states)",
                      keys.Count + " distinct keys");
            }
            finally
            {
                try { Directory.Delete(cache, true); } catch (Exception) { }
            }

            return Finish();
        }

        private static int Finish()
        {
            Console.WriteLine(new string('=', 78));
            Console.WriteLine((_fail == 0 ? "PASS" : "FAIL") + "  " + _pass + " checks passed, " + _fail + " failed, "
                              + _na + " not applicable on this CPU");
            return _fail == 0 ? 0 : 1;
        }

        private static void Level(string exe, string cache, List<(string, Child)> all, string id, string knob, string value,
                                  bool applicable, Child k0, Func<Child, bool> expect, string what)
        {
            string label = id + " " + knob + "=" + value;
            if (!applicable)
            {
                _na++;
                Console.WriteLine("  [n/a ] " + label + " - this CPU lacks what the knob turns off, so no effect can be shown");
                return;
            }

            Child c = Spawn(exe, cache, new Dictionary<string, string> { [knob] = value });
            if (!Parsed(c, label)) return;
            Console.WriteLine("  " + id.PadRight(8) + " " + Line(c));
            Check(expect(c), label + ": " + what, expect(c) ? "" : "knob had no effect, or a different one: " + Line(c));
            Check(c.SelfTest is "Passed" or "PassedCached", label + ": the machine self-test passes at this level", c.SelfTest);
            all.Add((id, c));
        }

        private static bool Is(Child c, string fact, string value) =>
            c.Facts.TryGetValue(fact, out string? v) && v == value;

        private static bool Same(Child c, Child k0, string fact) =>
            c.Facts.TryGetValue(fact, out string? v) && k0.Facts.TryGetValue(fact, out string? w) && v == w;

        private static readonly string[] FlagNames =
        {
            "x86base", "sse", "sse2", "sse41", "sse42", "avx", "avx2", "fma", "avx512f", "avx512bw", "avx512cd",
            "avx512dq", "avx512vbmi", "avx10v1", "avx10v1_v512", "v128acc", "v256acc", "v512acc",
        };

        private static bool SameFlags(Child c, Child k0)
        {
            foreach (string f in FlagNames) if (!Same(c, k0, f)) return false;
            return true;
        }

        private static bool AllFlagsOff(Child c)
        {
            foreach (string f in FlagNames) if (!Is(c, f, "0")) return false;
            return true;
        }

        private static string Line(Child c)
        {
            List<string> on = new List<string>();
            foreach (string f in FlagNames) if (Is(c, f, "1")) on.Add(f);
            return "flags [" + string.Join(" ", on) + "] hardware " + Get(c, "hardware") + ", request " + Get(c, "requested")
                   + ", active " + Get(c, "active") + (c.Perlin8 ? ", 8-wide Perlin" : ", scalar Perlin");
        }

        private static string Get(Child c, string k) => c.Facts.TryGetValue(k, out string? v) ? v : "?";

        private static bool Parsed(Child c, string label)
        {
            if (c.Exit == 0 && c.Facts.Count > 0) return true;
            Check(false, label + ": vseed selftest --isa-json ran and printed its state",
                  "exit " + c.Exit + ": " + FirstLine(c.Stderr.Length > 0 ? c.Stderr : c.Stdout));
            return false;
        }

        private static void Check(bool ok, string name, string detail)
        {
            if (ok) _pass++;
            else _fail++;
            Console.WriteLine("  [" + (ok ? "ok  " : "FAIL") + "] " + name + (detail.Length > 0 ? " - " + detail : ""));
        }

        private static string FirstLine(string s)
        {
            s = s.Trim();
            int nl = s.IndexOf('\n');
            return nl < 0 ? s : s.Substring(0, nl).Trim();
        }

        private static Child Spawn(string exe, string cache, Dictionary<string, string> env, params string[] extra)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string a in extra) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("selftest");
            psi.ArgumentList.Add("--isa-json");
            psi.ArgumentList.Add("--cache-dir");
            psi.ArgumentList.Add(cache);

            // The parent's own settings must not leak into a configuration that does not name them.
            foreach (string k in new[] { "DOTNET_EnableAVX512", "DOTNET_EnableAVX512v2", "DOTNET_EnableAVX2",
                                         "DOTNET_EnableHWIntrinsic", "DOTNET_PreferredVectorBitWidth", "SEEDLAB_SIMD",
                                         "SEEDLAB_SIMD_EXPECT" })
            {
                psi.Environment.Remove(k);
            }

            foreach (KeyValuePair<string, string> kv in env) psi.Environment[kv.Key] = kv.Value;

            Child c = new Child();
            using Process p = Process.Start(psi) ?? throw new InvalidOperationException("vseed did not start");
            Task<string> err = p.StandardError.ReadToEndAsync();
            c.Stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            c.Stderr = err.Result;
            c.Exit = p.ExitCode;
            if (c.Exit != 0) return c;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(c.Stdout);
                JsonElement root = doc.RootElement;
                foreach (JsonProperty f in root.GetProperty("facts").EnumerateObject()) c.Facts[f.Name] = f.Value.GetString() ?? "";
                c.StampKey = root.GetProperty("stamp_key").GetString() ?? "";
                c.SelfTest = root.GetProperty("self_test").GetString() ?? "";
                c.Perlin8 = root.GetProperty("perlin_8wide").GetBoolean();
            }
            catch (Exception ex)
            {
                c.Exit = -1;
                c.Stderr = "could not parse the child's JSON: " + ex.Message;
            }

            return c;
        }

        private static string? FindVseed()
        {
            DirectoryInfo? d = new DirectoryInfo(AppContext.BaseDirectory);
            while (d != null)
            {
                string p = Path.Combine(d.FullName, "src", "SeedLab.Cli", "bin", "Release", "net10.0", "vseed.exe");
                if (File.Exists(p)) return p;
                d = d.Parent;
            }

            return null;
        }
    }
}
