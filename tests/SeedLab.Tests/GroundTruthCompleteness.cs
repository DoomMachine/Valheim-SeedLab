using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Tasks;

namespace SeedLabTests
{
    /// <summary>
    /// <c>vseed selftest</c> fails on an incomplete ground truth, and says so; it still behaves as before
    /// when there is no ground truth at all (review F1b, 2026-09-26).
    ///
    ///   dotnet run -c Release --project tests\SeedLab.Tests -- groundtruth-completeness --work &lt;scratch folder&gt; [--vseed &lt;folder of vseed.exe&gt;]
    ///
    /// <para><b>Never on the real ground truth.</b> It copies the parts of <c>groundtruth\</c> the self-test
    /// reads (the two fixture worlds' folders, their decoded maps, <c>natives\</c>) into
    /// <c>&lt;work&gt;\gtc-&lt;time&gt;\with\groundtruth\</c>, checks every copied file's SHA-256 against its
    /// source, and moves files out of THAT copy and back. The real folder is only read.</para>
    ///
    /// <para><b>The binaries sit beside the copy</b> (<c>with\bin\vseed\</c>), and each run's working
    /// directory is <c>with\</c>, so both of the CLI's walk-ups (from the working directory and from the
    /// binary) find the copy and nothing else. A binary run from its build folder inside a SeedLab tree
    /// would walk up to that tree's own <c>groundtruth\</c> (fix.md section 6, item 4). A second copy
    /// under <c>without\</c> has no ground truth anywhere above it - the public repository's layout. The
    /// run refuses to start if any folder above <c>&lt;work&gt;</c> holds a <c>groundtruth\</c>, since
    /// that would be found instead.</para>
    ///
    /// <para>It deletes nothing: the scratch folder is left where it is and its path is printed. The
    /// binaries must carry the same commit as this test (their informational version), so a stale build
    /// cannot pass it.</para>
    /// </summary>
    public static class GroundTruthCompleteness
    {
        private static readonly string[] Fixtures = { "asdasdasd", "testworldclaude" };

        private static readonly string[] RequiredNatives =
        {
            "natives-perlin.bin", "natives-perlin.json", "natives-libm.json", "natives-hash.json"
        };

        private static int _pass, _fail;

        public static int Run(string[] args)
        {
            string? work = Arg(args, "--work");
            if (string.IsNullOrEmpty(work))
            {
                Console.Error.WriteLine("usage: groundtruth-completeness --work <scratch folder> [--vseed <folder of vseed.exe>]");
                return 2;
            }

            string? root = FindSeedLabRoot();
            if (root == null)
            {
                Console.Error.WriteLine("Could not find the SeedLab root (a folder with groundtruth\\decoded and src\\SeedLab.Cli) above "
                                        + Directory.GetCurrentDirectory() + " or " + AppContext.BaseDirectory + ".");
                return 2;
            }

            string realGt = Path.Combine(root, "groundtruth");
            string vseedDir = Arg(args, "--vseed") ?? Path.Combine(root, "src", "SeedLab.Cli", "bin", "Release", "net10.0");
            string vseedDll = Path.Combine(vseedDir, "vseed.dll");
            if (!File.Exists(Path.Combine(vseedDir, "vseed.exe")) || !File.Exists(vseedDll))
            {
                Console.Error.WriteLine("No vseed.exe in " + vseedDir + " - build src\\SeedLab.Cli -c Release first.");
                return 2;
            }

            work = Path.GetFullPath(work);
            string? above = GroundTruthAbove(work);
            if (above != null)
            {
                Console.Error.WriteLine("Refusing: " + above + " holds a groundtruth\\ folder, and the CLI would find it from under " + work + ".");
                return 2;
            }

            string runDir = Path.Combine(work, "gtc-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture));
            if (Directory.Exists(runDir))
            {
                Console.Error.WriteLine("Refusing: " + runDir + " exists.");
                return 2;
            }

            string with = Path.Combine(runDir, "with");
            string without = Path.Combine(runDir, "without");
            string copy = Path.Combine(with, "groundtruth");
            string aside = Path.Combine(with, "aside");

            Console.WriteLine("SeedLab: vseed selftest on an incomplete ground truth (review F1b)");
            Console.WriteLine("  real ground truth  " + realGt + " (read only)");
            Console.WriteLine("  vseed              " + vseedDir);
            Console.WriteLine("  scratch            " + runDir + " (left in place; nothing is deleted)");

            // ---- provenance: the binaries are this commit's ------------------------------------------
            string mine = typeof(GroundTruthCompleteness).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";
            string theirs = FileVersionInfo.GetVersionInfo(vseedDll).ProductVersion ?? "?";
            Check(string.Equals(mine, theirs, StringComparison.Ordinal),
                  "vseed.dll carries the same commit as this test (" + theirs + ")",
                  "vseed.dll is " + theirs + ", this test is " + mine + " - rebuild src\\SeedLab.Cli");

            // ---- the scratch copy ---------------------------------------------------------------------
            Directory.CreateDirectory(aside);
            List<(string Source, string Copy)> copied = new List<(string, string)>();
            foreach (string w in Fixtures)
            {
                foreach (string f in Directory.GetFiles(Path.Combine(realGt, "worlds", w)))
                {
                    copied.Add((f, Path.Combine(copy, "worlds", w, Path.GetFileName(f))));
                }

                foreach (string ext in new[] { ".biome.u8", ".height.f32" })
                {
                    copied.Add((Path.Combine(realGt, "decoded", w + ext), Path.Combine(copy, "decoded", w + ext)));
                }
            }

            foreach (string f in Directory.GetFiles(Path.Combine(realGt, "natives")))
            {
                copied.Add((f, Path.Combine(copy, "natives", Path.GetFileName(f))));
            }

            int same = 0;
            foreach ((string src, string dst) in copied)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, overwrite: false);
                if (Sha(src) == Sha(dst)) same++;
            }

            Check(same == copied.Count, "scratch copy of the ground truth: " + same + "/" + copied.Count + " files, SHA-256 equal to the source",
                  same + "/" + copied.Count + " copied files equal their source");

            string withBin = Path.Combine(with, "bin", "vseed");
            string withoutBin = Path.Combine(without, "bin", "vseed");
            CopyTree(vseedDir, withBin);
            CopyTree(vseedDir, withoutBin);
            Directory.CreateDirectory(Path.Combine(without, "work"));

            string exeWith = Path.Combine(withBin, "vseed.exe");
            string exeWithout = Path.Combine(withoutBin, "vseed.exe");
            string cache = Path.Combine(runDir, "cache");

            // ---- C0: complete --------------------------------------------------------------------------
            SelfTest c0 = RunSelfTest(exeWith, with, cache, "C0");
            Check(c0.Exit == 0 && c0.Passed, "C0 complete copy: selftest exit 0, PASS", "C0: exit " + c0.Exit + ", passed " + c0.Passed + c0.Tail);
            Check(c0.Row("N1") is { Pass: true } n1 && n1.Result.StartsWith("seedlab/natives ", StringComparison.Ordinal) && n1.Result.Contains("exact", StringComparison.Ordinal),
                  "C0: N1 ok - " + (c0.Row("N1")?.Result ?? "?"), "C0: N1 is " + Describe(c0.Row("N1")));
            foreach (string w in Fixtures)
            {
                Check(c0.Row("V1c/" + w) is { Pass: true }, "C0: V1c/" + w + " ok", "C0: V1c/" + w + " is " + Describe(c0.Row("V1c/" + w)));
            }

            Check(c0.NativesState == "complete", "C0: natives state complete", "C0: natives state " + c0.NativesState);

            // ---- C1: a fixture world without its .fwl2 -------------------------------------------------
            string fwl = Path.Combine(copy, "worlds", "testworldclaude", "_main.1.fwl2");
            WithAside(copy, aside, fwl, () =>
            {
                SelfTest t = RunSelfTest(exeWith, with, cache, "C1");
                Check(t.Exit == 1 && !t.Passed, "C1 no testworldclaude .fwl2: selftest exit 1, FAIL", "C1: exit " + t.Exit + ", passed " + t.Passed + t.Tail);
                SelfTest.RowData? r = t.Row("V1c/testworldclaude");
                Check(r is { Pass: false, Gating: true } && r.Result == "incomplete: groundtruth\\worlds\\testworldclaude\\_main.*.fwl2 missing",
                      "C1: V1c/testworldclaude FAIL - " + (r?.Result ?? "?"), "C1: V1c/testworldclaude is " + Describe(r));
                Check(t.OnlyFailing("V1c/testworldclaude"), "C1: every other gating row passes", "C1: other failing rows: " + t.FailingIds());
            });

            // ---- C2: each of the four natives files missing --------------------------------------------
            foreach (string f in RequiredNatives)
            {
                string p = Path.Combine(copy, "natives", f);
                WithAside(copy, aside, p, () =>
                {
                    string label = "C2-" + f;
                    SelfTest t = RunSelfTest(exeWith, with, cache, label);
                    Check(t.Exit == 1 && !t.Passed, label + ": selftest exit 1, FAIL", label + ": exit " + t.Exit + ", passed " + t.Passed + t.Tail);
                    SelfTest.RowData? r = t.Row("N1");
                    Check(r is { Pass: false, Gating: true } && r.Result.StartsWith("incomplete: " + f + " missing", StringComparison.Ordinal),
                          label + ": N1 FAIL - " + (r?.Result ?? "?"), label + ": N1 is " + Describe(r));
                    Check(t.NativesState == "incomplete" && t.NativesMissing.Count == 1 && t.NativesMissing[0] == f,
                          label + ": natives state incomplete, missing [" + f + "]",
                          label + ": natives state " + t.NativesState + ", missing [" + string.Join(", ", t.NativesMissing) + "]");
                    Check(t.OnlyFailing("N1"), label + ": every other gating row passes", label + ": other failing rows: " + t.FailingIds());
                });
            }

            // ---- C3: the whole natives folder missing --------------------------------------------------
            WithAside(copy, aside, Path.Combine(copy, "natives"), () =>
            {
                SelfTest t = RunSelfTest(exeWith, with, cache, "C3");
                SelfTest.RowData? r = t.Row("N1");
                Check(t.Exit == 1 && r is { Pass: false } && r.Result.StartsWith("incomplete: natives\\ missing", StringComparison.Ordinal),
                      "C3 no natives\\ folder: exit 1, N1 FAIL - " + (r?.Result ?? "?"), "C3: exit " + t.Exit + ", N1 is " + Describe(r) + t.Tail);
            });

            // ---- C4 / C5: the machine report, incomplete and complete ----------------------------------
            WithAside(copy, aside, Path.Combine(copy, "natives", "natives-perlin.bin"), () =>
            {
                Proc p = RunVseed(exeWith, with, "selftest", "--report", "--seeds", "1", "--cache-dir", Path.Combine(cache, "C4"));
                Check(p.Exit == 1 && Line(p.Out, "seedlab/natives").Contains("incomplete: natives-perlin.bin missing", StringComparison.Ordinal)
                      && p.Out.Contains("FAIL - the ground truth beside this build is incomplete (groundtruth\\natives: natives-perlin.bin missing)", StringComparison.Ordinal)
                      && !p.Out.Contains("not beside this build", StringComparison.Ordinal),
                      "C4 report, no natives-perlin.bin: exit 1, \"incomplete: natives-perlin.bin missing\", verdict FAIL",
                      "C4: exit " + p.Exit + "; natives line: " + Line(p.Out, "seedlab/natives") + "; verdict: " + Line(p.Out, "PASS -") + Line(p.Out, "FAIL -"));
            });

            {
                Proc p = RunVseed(exeWith, with, "selftest", "--report", "--seeds", "1", "--cache-dir", Path.Combine(cache, "C5"));
                string line = Line(p.Out, "seedlab/natives");
                Check(p.Exit == 0 && line.Contains("exact", StringComparison.Ordinal) && !line.Contains("not run", StringComparison.Ordinal)
                      && p.Out.Contains("PASS - ", StringComparison.Ordinal),
                      "C5 report, complete: exit 0, " + line.Trim(), "C5: exit " + p.Exit + "; natives line: " + line + Tail(p));
            }

            // ---- C6 / C7: no ground truth at all - today's behaviour, and it says why -------------------
            {
                string cwd = Path.Combine(without, "work");
                SelfTest t = RunSelfTest(exeWithout, cwd, cache, "C6");
                Check(t.Exit == 3 && t.Err.Contains("ground truth not found", StringComparison.Ordinal)
                      && t.Err.Contains("need only the seed", StringComparison.Ordinal),
                      "C6 no groundtruth\\: selftest exit 3 (not found, as before) and names 'selftest --report' as the check that needs none",
                      "C6: exit " + t.Exit + ", stderr: " + t.Err.Trim());

                Proc p = RunVseed(exeWithout, cwd, "selftest", "--report", "--seeds", "1", "--cache-dir", Path.Combine(cache, "C7"));
                string line = Line(p.Out, "seedlab/natives");
                Check(p.Exit == 0 && line.Contains("not run - no groundtruth\\ beside this build", StringComparison.Ordinal)
                      && line.Contains("the terrain fingerprints need only the seed", StringComparison.Ordinal)
                      && p.Out.Contains("PASS - ", StringComparison.Ordinal),
                      "C7 no groundtruth\\: report exit 0, PASS, " + line.Trim(), "C7: exit " + p.Exit + "; natives line: " + line + Tail(p));
            }

            // ---- the copy is whole again -----------------------------------------------------------------
            int back = 0;
            foreach ((string src, string dst) in copied)
            {
                if (File.Exists(dst) && Sha(src) == Sha(dst)) back++;
            }

            Check(back == copied.Count && Directory.GetFileSystemEntries(aside).Length == 0,
                  "every mutated file was moved back: " + back + "/" + copied.Count + " equal to the source, aside\\ empty",
                  back + "/" + copied.Count + " equal to the source; aside\\ holds " + Directory.GetFileSystemEntries(aside).Length);

            Console.WriteLine();
            Console.WriteLine((_fail == 0 ? "PASS" : "FAIL") + "  ground-truth completeness: " + _pass + " checks passed, " + _fail + " failed");
            return _fail == 0 ? 0 : 1;
        }

        // ---- running vseed ------------------------------------------------------------------------------

        private sealed class Proc
        {
            public int Exit;
            public string Out = "";
            public string Err = "";
        }

        private sealed class SelfTest
        {
            public sealed class RowData
            {
                public string Id = "";
                public string Result = "";
                public bool Pass;
                public bool Gating;
            }

            public int Exit;
            public bool Passed;
            public string Err = "";
            public string Tail = "";
            public string NativesState = "";
            public List<string> NativesMissing = new List<string>();
            public List<RowData> Rows = new List<RowData>();

            public RowData? Row(string id) => Rows.Find(r => r.Id == id);

            public bool OnlyFailing(string id)
            {
                foreach (RowData r in Rows)
                {
                    if (r.Gating && !r.Pass && r.Id != id) return false;
                }

                return true;
            }

            public string FailingIds()
            {
                List<string> l = new List<string>();
                foreach (RowData r in Rows) if (!r.Pass) l.Add(r.Id + (r.Gating ? "" : " (not gating)"));
                return string.Join(", ", l);
            }
        }

        private static SelfTest RunSelfTest(string exe, string cwd, string cache, string label)
        {
            Proc p = RunVseed(exe, cwd, "selftest", "--quick", "--no-map", "--json", "--cache-dir", Path.Combine(cache, label));
            SelfTest t = new SelfTest { Exit = p.Exit, Err = p.Err, Tail = Tail(p) };
            if (p.Out.TrimStart().StartsWith("{", StringComparison.Ordinal))
            {
                using JsonDocument doc = JsonDocument.Parse(p.Out);
                JsonElement root = doc.RootElement;
                t.Passed = root.GetProperty("passed").GetBoolean();
                if (root.TryGetProperty("natives", out JsonElement n))
                {
                    t.NativesState = n.GetProperty("state").GetString() ?? "";
                    foreach (JsonElement m in n.GetProperty("missing").EnumerateArray()) t.NativesMissing.Add(m.GetString() ?? "");
                }

                foreach (JsonElement c in root.GetProperty("checks").EnumerateArray())
                {
                    t.Rows.Add(new SelfTest.RowData
                    {
                        Id = c.GetProperty("id").GetString() ?? "",
                        Result = c.GetProperty("result").GetString() ?? "",
                        Pass = c.GetProperty("pass").GetBoolean(),
                        Gating = c.GetProperty("gating").GetBoolean(),
                    });
                }
            }

            return t;
        }

        private static Proc RunVseed(string exe, string cwd, params string[] args)
        {
            ProcessStartInfo psi = new ProcessStartInfo(exe)
            {
                WorkingDirectory = cwd,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string a in args) psi.ArgumentList.Add(a);

            // Nothing of the caller's SeedLab settings may steer the child: the walk-ups decide.
            List<string> drop = new List<string>();
            foreach (string k in psi.Environment.Keys)
            {
                if (k.StartsWith("SEEDLAB_", StringComparison.OrdinalIgnoreCase)) drop.Add(k);
            }

            foreach (string k in drop) psi.Environment.Remove(k);

            using Process p = Process.Start(psi) ?? throw new InvalidOperationException("vseed did not start");
            Task<string> err = p.StandardError.ReadToEndAsync();
            string stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(600_000))
            {
                p.Kill(entireProcessTree: true);
                throw new TimeoutException("vseed " + string.Join(" ", args) + " did not finish in 10 minutes");
            }

            return new Proc { Exit = p.ExitCode, Out = stdout, Err = err.Result };
        }

        // ---- helpers ------------------------------------------------------------------------------------

        /// <summary>Moves one file or folder of the COPY aside, runs <paramref name="body"/>, moves it back.</summary>
        private static void WithAside(string copyRoot, string asideDir, string path, Action body)
        {
            string full = Path.GetFullPath(path);
            if (!full.StartsWith(Path.GetFullPath(copyRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("refusing to move " + full + ": it is not inside the scratch copy " + copyRoot);
            }

            string parked = Path.Combine(asideDir, Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)));
            bool dir = Directory.Exists(full);
            if (dir) Directory.Move(full, parked); else File.Move(full, parked);
            try
            {
                body();
            }
            finally
            {
                if (dir) Directory.Move(parked, full); else File.Move(parked, full);
            }
        }

        private static void Check(bool ok, string passText, string failText)
        {
            if (ok)
            {
                _pass++;
                Console.WriteLine("PASS  " + passText);
            }
            else
            {
                _fail++;
                Console.WriteLine("FAIL  " + failText);
            }
        }

        private static string Describe(SelfTest.RowData? r)
            => r == null ? "absent" : (r.Pass ? "ok" : r.Gating ? "FAIL" : "warn") + " - " + r.Result;

        private static string Line(string text, string contains)
        {
            foreach (string l in text.Split('\n'))
            {
                if (l.Contains(contains, StringComparison.Ordinal)) return l.TrimEnd('\r');
            }

            return "";
        }

        private static string Tail(Proc p)
        {
            string e = p.Err.Trim();
            return e.Length == 0 ? "" : " (stderr: " + (e.Length > 400 ? e.Substring(e.Length - 400) : e) + ")";
        }

        private static string? Arg(string[] args, string name)
        {
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == name) return args[i + 1];
            }

            return null;
        }

        private static string? FindSeedLabRoot()
        {
            foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            {
                DirectoryInfo? d = new DirectoryInfo(start);
                for (int i = 0; i < 12 && d != null; i++, d = d.Parent)
                {
                    if (Directory.Exists(Path.Combine(d.FullName, "groundtruth", "decoded"))
                        && Directory.Exists(Path.Combine(d.FullName, "src", "SeedLab.Cli")))
                    {
                        return d.FullName;
                    }
                }
            }

            return null;
        }

        /// <summary>The first folder at or above <paramref name="dir"/> holding a groundtruth\, or null.</summary>
        private static string? GroundTruthAbove(string dir)
        {
            DirectoryInfo? d = new DirectoryInfo(dir);
            for (int i = 0; i < 64 && d != null; i++, d = d.Parent)
            {
                if (Directory.Exists(Path.Combine(d.FullName, "groundtruth"))) return d.FullName;
            }

            return null;
        }

        private static void CopyTree(string from, string to)
        {
            Directory.CreateDirectory(to);
            foreach (string f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), overwrite: false);
            foreach (string d in Directory.GetDirectories(from)) CopyTree(d, Path.Combine(to, Path.GetFileName(d)));
        }

        private static string Sha(string path)
        {
            using FileStream fs = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(fs));
        }
    }
}
