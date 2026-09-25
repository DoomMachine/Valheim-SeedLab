using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace SeedLab.RuntimeTests
{
    /// <summary>
    /// The saturating profile end to end, against the BUILT vseed: a short <c>--saturate</c> run, then
    /// a <c>--plan</c> replay of it, then three refusals. It checks mechanics, never speed - timings on
    /// a shared machine prove nothing - so it asserts that the readers the CLI owns (thread CPU,
    /// input/output, memory) gave sane, non-negative figures or said "not available", that every
    /// measurement was sized as the rule says, and that the replay measured exactly the same seeds.
    ///
    /// <code>dotnet run -c Release --project tests\SeedLab.Runtime.Tests -- --profile-check [--vseed &lt;vseed.exe&gt;]</code>
    ///
    /// Every vseed it starts uses a new cache root under the temporary folder, removed at the end.
    /// </summary>
    public static class ProfileSmoke
    {
        private static int _pass, _fail;

        public static int Run(string[] args)
        {
            string? exe = null;
            int at = Array.IndexOf(args, "--vseed");
            if (at >= 0 && at + 1 < args.Length) exe = args[at + 1];
            exe ??= FindVseed();
            if (exe == null || !File.Exists(exe))
            {
                Console.WriteLine("FAIL  vseed.exe not found (build src\\SeedLab.Cli -c Release, or pass --vseed <path>)");
                return 2;
            }

            Console.WriteLine("vseed profile --saturate / --plan, end to end (mechanics only; timings here are not measurements)");
            Console.WriteLine("  vseed    " + exe);
            string root = Path.Combine(Path.GetTempPath(), "seedlab-profilecheck-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);
            string cache = Path.Combine(root, "cache");
            string a = Path.Combine(root, "a.json"), b = Path.Combine(root, "b.json");
            try
            {
                (int codeA, string outA) = Vseed(exe, cache, "profile", "--tier", "t4", "--threads", "1,2", "--saturate", "2", "--warmup", "1",
                                                 "--quiet-baseline", "0", "--out", a, "--per-seed");
                Check(codeA == 0 && File.Exists(a), "a --saturate run finishes and writes its profile", "exit " + codeA + (codeA != 0 ? Tail(outA) : ""));
                if (codeA != 0) return Finish(root);

                (int codeB, string outB) = Vseed(exe, cache, "profile", "--plan", a, "--quiet-baseline", "0", "--out", b);
                Check(codeB == 0 && File.Exists(b), "its --plan replay finishes and writes its profile", "exit " + codeB + (codeB != 0 ? Tail(outB) : ""));
                if (codeB != 0) return Finish(root);

                using JsonDocument da = JsonDocument.Parse(File.ReadAllText(a));
                using JsonDocument db = JsonDocument.Parse(File.ReadAllText(b));
                JsonElement ra = da.RootElement, rb = db.RootElement;
                Check(ra.GetProperty("schema").GetString() == "seedlab-profile/2" && rb.GetProperty("schema").GetString() == "seedlab-profile/2",
                      "both documents are seedlab-profile/2", "");
                Check(ra.GetProperty("run").TryGetProperty("saturate", out JsonElement sat) && sat.GetProperty("pilots").GetArrayLength() == 2,
                      "the saturated run records one pilot per measurement", "");
                Check(rb.GetProperty("run").TryGetProperty("plan", out JsonElement plan) && plan.GetProperty("file").GetString() == Path.GetFullPath(a),
                      "the replay records the plan it replayed", "");

                JsonElement sa = ra.GetProperty("sections"), sb = rb.GetProperty("sections");
                Check(sa.GetArrayLength() == 2 && sb.GetArrayLength() == 2, "two measurements each (t4 at 1 and at 2 workers)", sa.GetArrayLength() + " and " + sb.GetArrayLength());
                for (int i = 0; i < Math.Min(sa.GetArrayLength(), sb.GetArrayLength()); i++)
                {
                    JsonElement x = sa[i], y = sb[i];
                    int w = x.GetProperty("workers").GetInt32(), n = x.GetProperty("seeds").GetInt32();
                    string label = "t4 at " + w + " worker(s): ";
                    Check(n % w == 0 && n >= 32 * w && x.GetProperty("sizing").GetProperty("source").GetString() == "saturate",
                          label + "sized by the rule (a multiple of the workers, at least 32 each)", n + " seeds");
                    Check(y.GetProperty("workers").GetInt32() == w && y.GetProperty("seeds").GetInt32() == n
                          && y.GetProperty("seed_list_sha256").GetString() == x.GetProperty("seed_list_sha256").GetString()
                          && y.GetProperty("sizing").GetProperty("source").GetString() == "plan",
                          label + "the replay measured the same workers and exactly the same seeds", x.GetProperty("seed_list_sha256").GetString()!.Substring(0, 16));
                    foreach (JsonElement s in new[] { x, y }) Resources(s, label);
                }

                string csv = Path.Combine(root, "a-seeds.csv");
                string[] lines = File.Exists(csv) ? File.ReadAllLines(csv) : Array.Empty<string>();
                Check(lines.Length > 2 && lines[0] == "# seedlab-profile-seeds/2" && lines[1].StartsWith("section,workers,seed,worker,start_us,wall_us,alloc_kb", StringComparison.Ordinal),
                      "the per-seed CSV is /2 with each seed's start", lines.Length > 1 ? lines[1] : "missing");

                // ---- refusals: nothing measured, a plain reason, exit 2 --------------------------------
                (int r1, string o1) = Vseed(exe, cache, "profile", "--plan", a, "--seeds", "5", "--quiet-baseline", "0");
                Check(r1 == 2 && o1.Contains("--plan replays", StringComparison.Ordinal), "--plan with --seeds is refused", "exit " + r1);
                (int r2, string o2) = Vseed(exe, cache, "profile", "--plan", a, "--warmup", "7", "--quiet-baseline", "0");
                Check(r2 == 2 && o2.Contains("differs from the plan", StringComparison.Ordinal), "--plan with another --warmup is refused", "exit " + r2);
                string v1 = Path.Combine(root, "v1.json");
                File.WriteAllText(v1, File.ReadAllText(a).Replace("seedlab-profile/2", "seedlab-profile/1"));
                (int r3, string o3) = Vseed(exe, cache, "profile", "--plan", v1, "--quiet-baseline", "0");
                Check(r3 == 2 && o3.Contains("only seedlab-profile/2", StringComparison.Ordinal), "a seedlab-profile/1 document is not a plan", "exit " + r3);
                (int r4, string o4) = Vseed(exe, cache, "profile", "--tier", "t4", "--seeds", "8", "--saturate", "3", "--quiet-baseline", "0");
                Check(r4 == 2 && o4.Contains("--saturate chooses", StringComparison.Ordinal), "--saturate with --seeds is refused", "exit " + r4);
            }
            catch (Exception ex)
            {
                Check(false, "the check ran to the end", ex.GetType().Name + ": " + ex.Message);
            }

            return Finish(root);
        }

        /// <summary>The resource figures of one section: present, sane and non-negative, or absent where the system gave none.</summary>
        private static void Resources(JsonElement s, string label)
        {
            JsonElement cpu = s.GetProperty("cpu");
            double process = cpu.GetProperty("process_s").GetDouble();
            double util = cpu.GetProperty("utilisation").GetDouble();
            bool workersOk = true;
            string detail = "process " + process.ToString("F2") + " s, utilisation " + util.ToString("F3");
            if (cpu.TryGetProperty("workers_s", out JsonElement ws))
            {
                double w = ws.GetDouble(), other = cpu.GetProperty("other_s").GetDouble();
                // Thread and process times advance in ~15.6 ms steps: allow a few steps either way.
                workersOk = w >= 0 && w <= process + 0.1 && other >= -0.1;
                detail += ", workers " + w.ToString("F2") + " s, other " + other.ToString("F2") + " s";
            }
            else if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
            {
                workersOk = false;
                detail += ", no worker thread CPU although this system has a reader";
            }

            Check(process > 0 && util > 0 && util <= 1.05 && workersOk, label + "CPU: process, utilisation and the worker split are sane", detail);

            bool perWorker = true;
            foreach (JsonElement w in s.GetProperty("worker_stats").EnumerateArray())
            {
                if (w.TryGetProperty("cpu_s", out JsonElement c) && (c.GetDouble() < 0 || w.GetProperty("not_running_s").GetDouble() < -0.1)) perWorker = false;
                if (!w.TryGetProperty("share_s", out JsonElement sh) || sh.GetDouble() <= 0) perWorker = false;
            }

            Check(perWorker, label + "each worker's own CPU and share are non-negative", "");

            if (s.TryGetProperty("io", out JsonElement io))
            {
                Check(io.GetProperty("read_bytes").GetInt64() >= 0 && io.GetProperty("write_bytes").GetInt64() >= 0 && io.GetProperty("source").GetString()!.Length > 0,
                      label + "input/output bytes are non-negative and name their source", io.GetProperty("write_bytes").GetInt64() + " B written");
            }
            else
            {
                Check(!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux(), label + "input/output is absent only where there is no reader", "");
            }

            JsonElement mem = s.GetProperty("memory");
            JsonElement sampler = mem.GetProperty("sampler");
            bool ramOk = sampler.GetProperty("samples").GetInt32() >= 2
                         && sampler.GetProperty("working_set_bytes").GetProperty("peak").GetInt64() > 0
                         && sampler.GetProperty("gc_heap_bytes").GetProperty("peak").GetInt64() > 0
                         && mem.GetProperty("allocated_bytes").GetInt64() > 0;
            if (OperatingSystem.IsWindows()) ramOk &= sampler.GetProperty("private_bytes").GetProperty("peak").GetInt64() > 0;
            Check(ramOk, label + "memory: at least two samples, positive working set, heap and allocation", sampler.GetProperty("samples").GetInt32() + " samples");

            JsonElement st = s.GetProperty("steady_state");
            bool steadyOk = st.GetProperty("defined").GetBoolean() && st.GetProperty("seeds_per_second").GetDouble() > 0
                            && st.GetProperty("window_s").GetDouble() > 0 && st.GetProperty("tail_loss").GetDouble() < 1;
            Check(steadyOk, label + "a steady state exists (every worker measured 32 seeds or more)", "");
            Check(s.GetProperty("gc").TryGetProperty("last_gc", out JsonElement lg) && lg.GetProperty("generations").GetArrayLength() >= 4,
                  label + "the last collection is described, large-object heap included", "");
        }

        private static (int Code, string Output) Vseed(string exe, string cache, params string[] args)
        {
            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = exe,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
            };
            foreach (string s in args) psi.ArgumentList.Add(s);
            psi.ArgumentList.Add("--cache-dir");
            psi.ArgumentList.Add(cache);
            psi.Environment["SEEDLAB_CACHE_DIR"] = cache;
            using Process p = Process.Start(psi)!;
            p.StandardInput.Close();
            System.Threading.Tasks.Task<string> err = p.StandardError.ReadToEndAsync();
            string stdout = p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return (p.ExitCode, stdout + err.Result);
        }

        private static string Tail(string s)
        {
            string t = s.Trim();
            return t.Length > 400 ? " ... " + t.Substring(t.Length - 400) : (t.Length > 0 ? " " + t : "");
        }

        private static void Check(bool ok, string name, string detail)
        {
            if (ok) _pass++;
            else _fail++;
            Console.WriteLine("  [" + (ok ? "ok  " : "FAIL") + "] " + name + (detail.Length > 0 ? " - " + detail : ""));
        }

        private static int Finish(string root)
        {
            try { Directory.Delete(root, recursive: true); }
            catch (Exception) { Console.WriteLine("  (could not remove " + root + ")"); }
            Console.WriteLine((_fail == 0 ? "PASS" : "FAIL") + "  " + _pass + " checks passed, " + _fail + " failed");
            return _fail == 0 ? 0 : 1;
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
