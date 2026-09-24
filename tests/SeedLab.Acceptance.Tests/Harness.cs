using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace SeedLabAcceptanceTests
{
    /// <summary>
    /// The two ground-truth worlds. Both were written by Valheim 1.0.15 itself (world file version 41,
    /// worldGenVersion 2, minimap cache version 1), so every number below is the game's own output.
    ///
    /// asdasdasd       is the DEVELOPMENT seed - the port was debugged against it.
    /// testworldclaude is the HOLD-OUT seed - never used while the biome and height code was ported,
    ///                 which is what makes its result evidence of a port rather than of a fit. It
    ///                 matched blind on biome and to 99.9998 % on height; a last one-ulp height
    ///                 residual was then diagnosed on both worlds and closed.
    /// </summary>
    public sealed class WorldFixture
    {
        public WorldFixture(string name, string seedText, int seed, int locationInstances, bool holdOut)
        {
            Name = name;
            SeedText = seedText;
            Seed = seed;
            LocationInstances = locationInstances;
            HoldOut = holdOut;
        }

        public string Name { get; }
        public string SeedText { get; }
        public int Seed { get; }

        /// <summary>Instance count in _main.&lt;N&gt;.db2, measured in 05-validation.md section 5.2.</summary>
        public int LocationInstances { get; }

        public bool HoldOut { get; }

        public static readonly WorldFixture Development =
            new WorldFixture("asdasdasd", "MWd8eV6svz", -1772362158, 12314, holdOut: false);

        public static readonly WorldFixture HoldOutWorld =
            new WorldFixture("testworldclaude", "hnBd9gJf2G", 319486907, 12287, holdOut: true);

        public static readonly WorldFixture[] All = { Development, HoldOutWorld };
    }

    /// <summary>
    /// Locates groundtruth\ by walking up from the working directory and from the binary directory,
    /// so the suite runs the same whether it is started with `dotnet run --project` from the solution
    /// root or by executing the built exe.
    /// </summary>
    public static class GroundTruth
    {
        private static string? s_root;

        public static string Root
        {
            get
            {
                if (s_root != null) return s_root;
                foreach (string start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
                {
                    DirectoryInfo? d = new DirectoryInfo(start);
                    for (int i = 0; i < 12 && d != null; i++, d = d.Parent)
                    {
                        string cand = Path.Combine(d.FullName, "groundtruth");
                        if (Directory.Exists(Path.Combine(cand, "decoded")))
                        {
                            s_root = cand;
                            return s_root;
                        }
                    }
                }
                throw new DirectoryNotFoundException(
                    "groundtruth\\decoded not found from " + Directory.GetCurrentDirectory()
                    + " or " + AppContext.BaseDirectory);
            }
        }

        public static string DecodedBiome(WorldFixture w) => Path.Combine(Root, "decoded", w.Name + ".biome.u8");
        public static string DecodedHeight(WorldFixture w) => Path.Combine(Root, "decoded", w.Name + ".height.f32");
        public static string WorldDirectory(WorldFixture w) => Path.Combine(Root, "worlds", w.Name);

        /// <summary>The newest _main.&lt;N&gt;.&lt;ext&gt; in the fixture folder. The fixtures are frozen
        /// copies, but resolve anyway so the suite never hard-codes a save number
        /// (05-validation.md section 5.0: the number moves under you on a live folder).</summary>
        public static string SaveFile(WorldFixture w, string extension)
        {
            string dir = WorldDirectory(w);
            string? best = null;
            int bestN = -1;
            foreach (string f in Directory.GetFiles(dir, "_main.*." + extension))
            {
                string stem = Path.GetFileNameWithoutExtension(f);           // _main.<N>
                string tail = Path.GetExtension(stem);                        // .<N>
                if (tail.Length > 1 && int.TryParse(tail.Substring(1), out int n) && n > bestN)
                {
                    bestN = n;
                    best = f;
                }
            }
            if (best == null) throw new FileNotFoundException("No _main.*." + extension + " under " + dir);
            return best;
        }
    }

    /// <summary>One pass/fail line in the final summary.</summary>
    public sealed class CheckResult
    {
        public CheckResult(string id, string title, bool passed, string detail, bool gating = true)
        {
            Id = id;
            Title = title;
            Passed = passed;
            Detail = detail;
            Gating = gating;
        }

        public string Id { get; }
        public string Title { get; }
        public bool Passed { get; }
        public string Detail { get; }

        /// <summary>A non-gating line is reported but does not set the exit code (measurements that
        /// have no oracle, or that the specification itself marks Unverified).</summary>
        public bool Gating { get; }
    }

    public sealed class Report
    {
        private readonly List<CheckResult> m_results = new List<CheckResult>();
        private readonly Stopwatch m_clock = Stopwatch.StartNew();

        public IReadOnlyList<CheckResult> Results => m_results;
        public TimeSpan Elapsed => m_clock.Elapsed;

        public void Add(CheckResult r)
        {
            m_results.Add(r);
            Console.WriteLine("  [" + (r.Passed ? "PASS" : (r.Gating ? "FAIL" : "note")) + "] "
                              + r.Id + "  " + r.Title + (r.Detail.Length > 0 ? " - " + r.Detail : ""));
        }

        public void Add(string id, string title, bool passed, string detail, bool gating = true)
            => Add(new CheckResult(id, title, passed, detail, gating));

        public bool Failed
        {
            get
            {
                foreach (CheckResult r in m_results) if (r.Gating && !r.Passed) return true;
                return false;
            }
        }

        public static void Section(string title)
        {
            Console.WriteLine();
            Console.WriteLine("== " + title + " " + new string('=', Math.Max(0, 76 - title.Length)));
        }
    }
}
