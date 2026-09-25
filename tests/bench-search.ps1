# What each stage of a real 'vseed search' costs: wall-clock time, processor time, how busy the
# machine's cores were, peak memory, the bytes read and written, and the files left behind.
#
#   powershell -ExecutionPolicy Bypass -File tests\bench-search.ps1 [-Queries Q1,Q3] [-Quick] [-OutDir <folder>]
#                                                                    [-Vseed <vseed.exe>] [-SkipDryRun] [-IntervalMs 250]
#
# Build first: dotnet build src\SeedLab.Cli -c Release. The script runs a fixed set of representative
# searches with the BUILT vseed at --mode full (every core) and a fixed --seeds, so runs on different days
# or builds cover the same worlds:
#
#   Q1  preset custom           one biome goal, the cheapest tier (T2)            409,600 seeds  (Quick 16,384)
#   Q2  preset gentle-start     heights and a coarse screen (T3)                    1,280 seeds  (Quick 128)
#   Q3  bench-rivers (a file)   rivers only: nearly all pre-generation (T4)         6,144 seeds  (Quick 384)
#   Q4  preset compact-progression  five bosses placed (T5)                         1,024 seeds  (Quick 64)
#   Q5  bench-funnel (a file)   a T3 must-have funnelling into a T5 must-have       4,096 seeds  (Quick 512)
#   Q6  bench-funnel with --strategy sample: the same seeds without the funnel      4,096 seeds  (Quick 512)
#   Q7  preset custom --keep all --rotate 32MB --compress gz (not in the default set; the write-heavy path)
#
# Seed counts are multiples of 64 (16 workers x 4 blocks), so no worker is left with a short last block.
# The whole default set takes about 12 to 15 minutes on a 16-thread machine; -Quick about 2 minutes.
#
# For each query it watches the vseed process every -IntervalMs (processor time, working set, private
# memory, page faults, bytes read and written), stamps every line vseed prints to the millisecond, reads
# the session log's own timestamps, and cuts the run into stages from what vseed already prints:
#
#   start-up         process start to the self-test line in the session log
#   preflight        reading the query and the game data, planning, access checks (log: "start")
#   plan             printing the plan, to the "Strategy" header
#   open the run     results file created, workers started, to the first progress line
#   scan             the search itself, to the end of its progress lines
#   (a funnel instead: open stage 1, stage 1, survivor list + gate (the gate is measured on ONE thread),
#    open stage 2, stage 2)
#   finish writing   the results file finished, to the "Result" header
#   report and exit  the report, the session log closed, the process gone
#
# and reports per stage: wall seconds, processor seconds (user and kernel), busy cores and utilisation
# (processor time / (wall x logical cores)), peak working set and private memory, bytes read and written
# and page faults; the steady state inside each scan (the longest stretch at 85 % or more of the scan's
# typical busy cores, with its seed rate); the files left and their sizes; and vseed's own Result lines.
#
# Everything goes to one fresh folder under %TEMP% (or -OutDir): the cache root every vseed here uses (one
# per bench run, warmed by an untimed dry run so no timed run pays the first self-test), the results
# files, the stamped outputs, each query's session log, and bench-search.txt / bench-search.json. Nothing
# is written anywhere else; the repository's data\ and groundtruth\ are only read.
#
# A timing from a busy machine is not a measurement. A query is marked TAINTED when another vseed or
# SeedLab program, Valheim, or a build tool (dotnet, VBCSCompiler, MSBuild) ran at any point, when other
# processes used more than one core on average, or when vseed says it throttled itself for a running game.

param(
    [string[]]$Queries = @('Q1', 'Q2', 'Q3', 'Q4', 'Q5', 'Q6'),
    [switch]$Quick,
    [string]$OutDir = '',
    [string]$Vseed = '',
    [switch]$SkipDryRun,
    [int]$IntervalMs = 250
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$inv = [Globalization.CultureInfo]::InvariantCulture
$utf8 = New-Object Text.UTF8Encoding($false)
if ($Queries.Count -eq 1 -and $Queries[0].Contains(',')) { $Queries = $Queries[0].Split(',') }
if ($IntervalMs -lt 50 -or $IntervalMs -gt 5000) { throw "-IntervalMs takes 50 to 5000." }
if ($Vseed -eq '') { $Vseed = Join-Path $root 'src\SeedLab.Cli\bin\Release\net10.0\vseed.exe' }
if (-not (Test-Path -LiteralPath $Vseed)) { throw "vseed.exe not found at $Vseed - build it first (dotnet build src\SeedLab.Cli -c Release) or pass -Vseed." }
$Vseed = (Resolve-Path -LiteralPath $Vseed).Path
if ($OutDir -eq '') {
    $OutDir = Join-Path ([IO.Path]::GetTempPath()) ('seedlab-bench-' + (Get-Date).ToString('yyyyMMdd-HHmmss', $inv) + '-' + $PID)
}
$OutDir = [IO.Path]::GetFullPath($OutDir)
if (Test-Path -LiteralPath $OutDir) {
    if (@(Get-ChildItem -LiteralPath $OutDir -Force).Count -gt 0) { throw "-OutDir $OutDir is not empty; give a new folder." }
}
foreach ($guard in @('data', 'groundtruth')) {
    $g = [IO.Path]::GetFullPath((Join-Path $root $guard)) + '\'
    if (($OutDir + '\').StartsWith($g, [StringComparison]::OrdinalIgnoreCase)) { throw "-OutDir is inside $g, which is only ever read." }
}
$cache = Join-Path $OutDir 'cache'
$results = Join-Path $OutDir 'results'
$stamped = Join-Path $OutDir 'output'
$logs = Join-Path $OutDir 'logs'
$qdir = Join-Path $OutDir 'queries'
foreach ($d in @($OutDir, $cache, $results, $stamped, $logs, $qdir)) { New-Item -ItemType Directory -Force $d | Out-Null }

# ---------------------------------------------------------------------------------------------------
# The helper: starts vseed, stamps its output, samples it. C# 5 (the compiler Windows PowerShell 5.1
# ships), kernel32 only.
# ---------------------------------------------------------------------------------------------------
$helper = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace SeedLabBench
{
    public sealed class Segment
    {
        public double Start;      // seconds since launch when its first character arrived
        public double End;        // ... when the character that ended it arrived
        public string Text;
        public char Terminator;   // '\r', '\n', or '\0' at the end of the stream
    }

    public sealed class Sample
    {
        public double T;
        public double User, Kernel, WorkingSet, PeakWorkingSet, Private, PeakPrivate;
        public double ReadBytes, WriteBytes, OtherBytes, ReadOps, WriteOps, PageFaults;
        public double MachineBusy = -1;   // whole machine, seconds of processor time; -1 = unknown
        public double DiskBytes = -1;     // bytes in the watched folders; -1 = not taken on this tick
        public int DiskFiles;
    }

    public sealed class Runner
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters { public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; }

        [StructLayout(LayoutKind.Sequential)]
        private struct Pmc
        {
            public uint cb, PageFaults;
            public UIntPtr PeakWS, WS, QPeakPaged, QPaged, QPeakNonPaged, QNonPaged, Pagefile, PeakPagefile, Private;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessIoCounters(IntPtr h, out IoCounters c);
        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "K32GetProcessMemoryInfo")]
        private static extern bool GetProcessMemoryInfo(IntPtr h, ref Pmc m, uint cb);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetProcessTimes(IntPtr h, out long c, out long e, out long k, out long u);
        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetSystemTimes(out long idle, out long k, out long u);

        private static readonly string[] Watched = { "vseed", "valheim", "dotnet", "VBCSCompiler", "MSBuild" };

        public List<Sample> Samples = new List<Sample>();
        public List<Segment> Out = new List<Segment>();
        public List<Segment> Err = new List<Segment>();
        public List<string> Seen = new List<string>();
        public int ExitCode;
        public double ExitT;
        public double CreatedT;          // the process's creation, on the same clock (a little below 0)
        public DateTime ClockZeroUtc;    // DateTime.UtcNow when the clock read 0
        public int Pid;

        private readonly string _exe, _args, _dir, _outFile, _errFile;
        private readonly string[] _watch;
        private readonly int _interval;
        private readonly Stopwatch _sw = new Stopwatch();
        private readonly ManualResetEvent _stop = new ManualResetEvent(false);
        private Process _p;
        private Thread _tOut, _tErr, _tSample;

        public Runner(string exe, string args, string workDir, string outFile, string errFile, string[] watch, int intervalMs)
        {
            _exe = exe; _args = args; _dir = workDir; _outFile = outFile; _errFile = errFile; _watch = watch; _interval = intervalMs;
        }

        private double Now() { return _sw.Elapsed.TotalSeconds; }

        public void Start()
        {
            ProcessStartInfo psi = new ProcessStartInfo(_exe, _args);
            psi.UseShellExecute = false;
            psi.WorkingDirectory = _dir;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.RedirectStandardInput = true;
            psi.StandardOutputEncoding = new UTF8Encoding(false);
            psi.StandardErrorEncoding = new UTF8Encoding(false);
            psi.CreateNoWindow = true;
            _p = Process.Start(psi);
            _sw.Start();
            ClockZeroUtc = DateTime.UtcNow;
            Pid = _p.Id;
            _p.StandardInput.Close();   // a question vseed asks is refused, never waited on
            long c, e, k, u;
            if (GetProcessTimes(_p.Handle, out c, out e, out k, out u)) CreatedT = (DateTime.FromFileTimeUtc(c) - ClockZeroUtc).TotalSeconds;
            _tOut = new Thread(delegate () { Pump(_p.StandardOutput, Out, _outFile); });
            _tErr = new Thread(delegate () { Pump(_p.StandardError, Err, _errFile); });
            _tSample = new Thread(SampleLoop);
            _tOut.IsBackground = true; _tErr.IsBackground = true; _tSample.IsBackground = true;
            _tOut.Start(); _tErr.Start(); _tSample.Start();
        }

        public void Wait()
        {
            _p.WaitForExit();
            ExitT = Now();
            _tOut.Join(); _tErr.Join();
            _stop.Set();
            _tSample.Join();
            Take(ExitT, true);           // the handle still answers after exit: the final totals
            ExitCode = _p.ExitCode;
        }

        private void Pump(StreamReader r, List<Segment> list, string file)
        {
            using (StreamWriter w = new StreamWriter(file, false, new UTF8Encoding(false)))
            {
                char[] buf = new char[8192];
                StringBuilder cur = new StringBuilder();
                double curStart = -1;
                int n;
                while ((n = r.Read(buf, 0, buf.Length)) > 0)
                {
                    double now = Now();
                    w.Write(buf, 0, n);
                    w.Flush();
                    for (int i = 0; i < n; i++)
                    {
                        char ch = buf[i];
                        if (ch == '\r' || ch == '\n')
                        {
                            Segment s = new Segment();
                            s.Start = curStart < 0 ? now : curStart; s.End = now; s.Text = cur.ToString(); s.Terminator = ch;
                            lock (list) list.Add(s);
                            cur.Length = 0; curStart = -1;
                        }
                        else
                        {
                            if (curStart < 0) curStart = now;
                            cur.Append(ch);
                        }
                    }
                }

                if (cur.Length > 0)
                {
                    Segment s = new Segment();
                    s.Start = curStart; s.End = Now(); s.Text = cur.ToString(); s.Terminator = '\0';
                    lock (list) list.Add(s);
                }
            }
        }

        private void SampleLoop()
        {
            int tick = 0;
            int perSecond = Math.Max(1, 1000 / _interval);
            long next = 0;
            while (true)
            {
                Take(Now(), tick % perSecond == 0);
                tick++;
                next += _interval;
                long wait = next - _sw.ElapsedMilliseconds;
                if (wait < 0) wait = 0;
                if (_stop.WaitOne((int)wait)) break;
            }
        }

        private void Take(double t, bool slow)
        {
            Sample s = new Sample();
            s.T = t;
            try
            {
                long c, e, k, u;
                if (GetProcessTimes(_p.Handle, out c, out e, out k, out u)) { s.User = u / 1e7; s.Kernel = k / 1e7; }
                Pmc m = new Pmc();
                m.cb = (uint)Marshal.SizeOf(typeof(Pmc));
                if (GetProcessMemoryInfo(_p.Handle, ref m, m.cb))
                {
                    s.WorkingSet = m.WS.ToUInt64(); s.PeakWorkingSet = m.PeakWS.ToUInt64();
                    s.Private = m.Private.ToUInt64(); s.PeakPrivate = m.PeakPagefile.ToUInt64(); s.PageFaults = m.PageFaults;
                }
                IoCounters io;
                if (GetProcessIoCounters(_p.Handle, out io))
                {
                    s.ReadBytes = io.ReadBytes; s.WriteBytes = io.WriteBytes; s.OtherBytes = io.OtherBytes;
                    s.ReadOps = io.ReadOps; s.WriteOps = io.WriteOps;
                }
                long idle, kk, uu;
                if (GetSystemTimes(out idle, out kk, out uu)) s.MachineBusy = (kk - idle + uu) / 1e7;
            }
            catch (Exception) { }

            if (slow)
            {
                long bytes = 0; int files = 0;
                foreach (string d in _watch)
                {
                    try
                    {
                        foreach (string f in Directory.GetFiles(d, "*", SearchOption.AllDirectories))
                        {
                            try { bytes += new FileInfo(f).Length; files++; } catch (Exception) { }
                        }
                    }
                    catch (Exception) { }
                }
                s.DiskBytes = bytes; s.DiskFiles = files;

                try
                {
                    foreach (Process q in Process.GetProcesses())
                    {
                        try
                        {
                            string name = q.ProcessName;
                            bool hit = name.StartsWith("SeedLab.", StringComparison.OrdinalIgnoreCase);
                            foreach (string wn in Watched) if (string.Equals(name, wn, StringComparison.OrdinalIgnoreCase)) hit = true;
                            if (hit && q.Id != Pid)
                            {
                                string tag = name + " (pid " + q.Id + ")";
                                lock (Seen) if (!Seen.Contains(tag)) Seen.Add(tag);
                            }
                        }
                        catch (Exception) { }
                        finally { q.Dispose(); }
                    }
                }
                catch (Exception) { }
            }

            lock (Samples) Samples.Add(s);
        }
    }

    /// <summary>A stage of a run and what the process used in it.</summary>
    public sealed class Stage
    {
        public string Name;
        public double Start, End, Wall, User, Kernel, Cpu, BusyCores, Utilisation, ForeignCores = -1;
        public double PeakWorkingSet, PeakPrivate, ReadBytes, WriteBytes, PageFaults;
        public int SamplesInside;
    }

    public static class Measure
    {
        /// <summary>A cumulative counter at time t, interpolated between the samples around it (0 at creation).</summary>
        public static double At(List<Sample> s, double createdT, double t, int field)
        {
            double t0 = createdT, v0 = 0;
            for (int i = 0; i < s.Count; i++)
            {
                double v = Field(s[i], field);
                if (s[i].T >= t)
                {
                    if (s[i].T <= t0) return v;
                    return v0 + (v - v0) * (t - t0) / (s[i].T - t0);
                }
                t0 = s[i].T; v0 = v;
            }
            return v0;
        }

        public static double Field(Sample s, int f)
        {
            switch (f)
            {
                case 0: return s.User;
                case 1: return s.Kernel;
                case 2: return s.ReadBytes;
                case 3: return s.WriteBytes;
                case 4: return s.PageFaults;
                default: return s.MachineBusy;
            }
        }

        public static Stage Of(string name, List<Sample> s, double createdT, double start, double end, int cores)
        {
            Stage g = new Stage();
            g.Name = name; g.Start = start; g.End = end; g.Wall = Math.Max(0, end - start);
            g.User = At(s, createdT, end, 0) - At(s, createdT, start, 0);
            g.Kernel = At(s, createdT, end, 1) - At(s, createdT, start, 1);
            g.Cpu = g.User + g.Kernel;
            g.ReadBytes = At(s, createdT, end, 2) - At(s, createdT, start, 2);
            g.WriteBytes = At(s, createdT, end, 3) - At(s, createdT, start, 3);
            g.PageFaults = At(s, createdT, end, 4) - At(s, createdT, start, 4);
            g.BusyCores = g.Wall > 0 ? g.Cpu / g.Wall : 0;
            g.Utilisation = g.Wall > 0 ? g.Cpu / (g.Wall * cores) : 0;
            bool machine = true;
            foreach (Sample x in s) if (x.MachineBusy < 0) machine = false;
            if (machine && s.Count > 0 && g.Wall > 0)
            {
                double busy = At(s, s[0].T, end, 5) - At(s, s[0].T, Math.Max(start, s[0].T), 5);
                double own = At(s, createdT, end, 0) + At(s, createdT, end, 1) - At(s, createdT, Math.Max(start, s[0].T), 0) - At(s, createdT, Math.Max(start, s[0].T), 1);
                double span = end - Math.Max(start, s[0].T);
                if (span > 0) g.ForeignCores = Math.Max(0, (busy - own) / span);
            }
            // Peaks: the samples inside the stage, and the one on either side of it (a short stage may
            // fall between two samples; its peak is then bounded by its neighbours).
            for (int i = 0; i < s.Count; i++)
            {
                bool inside = s[i].T >= start && s[i].T <= end;
                bool edge = (i + 1 < s.Count && s[i].T < start && s[i + 1].T >= start) || (i > 0 && s[i].T > end && s[i - 1].T <= end);
                if (!inside && !edge) continue;
                if (inside) g.SamplesInside++;
                g.PeakWorkingSet = Math.Max(g.PeakWorkingSet, s[i].WorkingSet);
                g.PeakPrivate = Math.Max(g.PeakPrivate, s[i].Private);
            }
            return g;
        }

        /// <summary>
        /// The steady state inside a stage: the median busy cores of its middle third, then the longest run
        /// of sample intervals at 85 % of that or more. Returns {start, end, busy cores} or null.
        /// </summary>
        public static double[] Steady(List<Sample> s, double start, double end)
        {
            List<double> t = new List<double>(), cores = new List<double>();
            for (int i = 1; i < s.Count; i++)
            {
                if (s[i - 1].T < start || s[i].T > end) continue;
                double dt = s[i].T - s[i - 1].T;
                if (dt <= 0) continue;
                t.Add(s[i - 1].T);
                cores.Add((s[i].User + s[i].Kernel - s[i - 1].User - s[i - 1].Kernel) / dt);
            }
            if (cores.Count < 3) return null;
            int a = cores.Count / 3, b = Math.Max(a + 1, 2 * cores.Count / 3);
            List<double> mid = cores.GetRange(a, b - a);
            mid.Sort();
            double median = mid[mid.Count / 2];
            if (median <= 0) return null;
            int bestStart = -1, bestLen = 0, runStart = -1;
            for (int i = 0; i <= cores.Count; i++)
            {
                bool ok = i < cores.Count && cores[i] >= 0.85 * median;
                if (ok && runStart < 0) runStart = i;
                if (!ok && runStart >= 0)
                {
                    if (i - runStart > bestLen) { bestLen = i - runStart; bestStart = runStart; }
                    runStart = -1;
                }
            }
            if (bestLen == 0) return null;
            double ws = t[bestStart];
            double we = bestStart + bestLen < t.Count ? t[bestStart + bestLen] : end;
            double sum = 0;
            for (int i = bestStart; i < bestStart + bestLen; i++) sum += cores[i];
            return new double[] { ws, Math.Min(we, end), sum / bestLen };
        }

        /// <summary>Every file under the folders, path to size.</summary>
        public static Dictionary<string, long> Inventory(string[] roots)
        {
            Dictionary<string, long> d = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            foreach (string r in roots)
            {
                if (!Directory.Exists(r)) continue;
                foreach (string f in Directory.GetFiles(r, "*", SearchOption.AllDirectories))
                {
                    try { d[f] = new FileInfo(f).Length; } catch (Exception) { }
                }
            }
            return d;
        }
    }
}
'@
if (-not ('SeedLabBench.Runner' -as [type])) { Add-Type -TypeDefinition $helper -Language CSharp }

# ---------------------------------------------------------------------------------------------------
# The queries
# ---------------------------------------------------------------------------------------------------
$benchRivers = @'
{ "version": 1, "defs": 1, "name": "bench-rivers",
  "description": "Benchmark: river pre-generation only (one nice river_count goal at G384).",
  "world": { "gen_version": 2 },
  "search": { "order": "shuffled", "grid": 384, "screen": "off", "keep": 100 },
  "goals": [ { "id": "rivers", "target": "world:river_count", "metric": "river_count",
               "test": "at_least", "value": 1, "importance": "nice" } ] }
'@
$benchFunnel = @'
{ "version": 1, "defs": 1, "name": "bench-funnel",
  "description": "Benchmark: a T3 must-have in a 1 km disc funnels into a T5 placement.",
  "world": { "gen_version": 2 },
  "search": { "order": "shuffled", "grid": 12, "screen": "off", "keep": 100 },
  "goals": [
    { "id": "shoreline", "target": "world:shore_area_within", "metric": "shore_area_within",
      "radius": 1000, "test": "at_least", "value": 1150000, "importance": "must" },
    { "id": "eikthyr", "target": "location:Eikthyrnir", "metric": "nearest_distance",
      "test": "near", "value": 300, "importance": "must" },
    { "id": "meadows", "target": "biome:Meadows", "metric": "area_within",
      "radius": 600, "test": "at_least", "value": 300000, "importance": "nice" } ] }
'@
$riversFile = Join-Path $qdir 'bench-rivers.json'
$funnelFile = Join-Path $qdir 'bench-funnel.json'
[IO.File]::WriteAllText($riversFile, $benchRivers.Replace("`r`n", "`n"), $utf8)
[IO.File]::WriteAllText($funnelFile, $benchFunnel.Replace("`r`n", "`n"), $utf8)

$catalog = [ordered]@{
    'Q1' = @{ Query = 'custom'; What = 'one biome goal (T2), every seed matches'; Seeds = 409600; QuickSeeds = 16384; Extra = @() }
    'Q2' = @{ Query = 'gentle-start'; What = 'heights and a coarse screen (T3)'; Seeds = 1280; QuickSeeds = 128; Extra = @() }
    'Q3' = @{ Query = $riversFile; What = 'rivers only: nearly all pre-generation (T4)'; Seeds = 6144; QuickSeeds = 384; Extra = @() }
    'Q4' = @{ Query = 'compact-progression'; What = 'five bosses placed (T5)'; Seeds = 1024; QuickSeeds = 64; Extra = @() }
    'Q5' = @{ Query = $funnelFile; What = 'a T3 must-have funnelling into a T5 must-have'; Seeds = 4096; QuickSeeds = 512; Extra = @() }
    'Q6' = @{ Query = $funnelFile; What = 'the same seeds as Q5, without the funnel'; Seeds = 4096; QuickSeeds = 512; Extra = @('--strategy', 'sample') }
    'Q7' = @{ Query = 'custom'; What = 'every match streamed, rotated and gzipped (T2)'; Seeds = 409600; QuickSeeds = 16384; Extra = @('--keep', 'all', '--rotate', '32MB', '--compress', 'gz') }
}
foreach ($q in $Queries) { if (-not $catalog.Contains($q)) { throw "unknown query '$q'; known: $($catalog.Keys -join ', ')" } }

function Quote([string]$s) { if ($s -match '[\s"]') { return '"' + ($s -replace '"', '\"') + '"' } return $s }
function ArgLine([string[]]$a) { return (($a | ForEach-Object { Quote $_ }) -join ' ') }
function MiB([double]$b) { return ($b / 1048576.0).ToString('F1', $inv) }
function F1([double]$v) { return $v.ToString('F1', $inv) }
function F2([double]$v) { return $v.ToString('F2', $inv) }
function Say([string]$s) { Write-Host $s; [void]$script:text.AppendLine($s) }
$script:text = New-Object Text.StringBuilder
$cores = [Environment]::ProcessorCount
$vseedHash = (Get-FileHash -LiteralPath $Vseed -Algorithm SHA256).Hash.ToLowerInvariant()
$runtimeConfig = Join-Path (Split-Path -Parent $Vseed) 'vseed.runtimeconfig.json'
$knobs = @(Get-ChildItem Env: | Where-Object { $_.Name -like 'DOTNET_*' -or $_.Name -like 'COMPlus_*' -or $_.Name -eq 'SEEDLAB_PROFILE_COUNTERS' -or $_.Name -eq 'SEEDLAB_SIMD' } | ForEach-Object { $_.Name + '=' + $_.Value })

Say ('vseed search benchmark - ' + (Get-Date).ToString('yyyy-MM-dd HH:mm:ss zzz', $inv))
Say ('  vseed       ' + $Vseed)
Say ('  sha256      ' + $vseedHash)
Say ('  machine     ' + $cores + ' logical cores; ' + [Environment]::OSVersion.VersionString)
Say ('  knobs set   ' + $(if ($knobs.Count -gt 0) { $knobs -join ', ' } else { 'none' }))
Say ('  folder      ' + $OutDir)
Say ('  queries     ' + ($Queries -join ', ') + $(if ($Quick) { ' (Quick: a smoke test, not a measurement)' } else { '' }))
Say ''

# ---------------------------------------------------------------------------------------------------
# One run of vseed, sampled
# ---------------------------------------------------------------------------------------------------
function Invoke-Vseed([string]$name, [string[]]$vargs) {
    $outFile = Join-Path $stamped ($name + '.out.txt')
    $errFile = Join-Path $stamped ($name + '.err.txt')
    $r = [SeedLabBench.Runner]::new($Vseed, (ArgLine $vargs), $OutDir, $outFile, $errFile, [string[]]@($results, (Join-Path $cache 'checkpoints')), $IntervalMs)
    $r.Start()
    $r.Wait()
    $log = Join-Path $cache 'logs\vseed.log'
    if (Test-Path -LiteralPath $log) { Copy-Item -LiteralPath $log -Destination (Join-Path $logs ($name + '-vseed.log')) -Force }
    return $r
}

# Log lines: "yyyy-MM-dd HH:mm:ss.fff zzz  LEVEL  <category> <text>", on the runner's clock.
function Read-LogMarks($r, [string]$name) {
    $marks = @{}
    $path = Join-Path $logs ($name + '-vseed.log')
    if (-not (Test-Path -LiteralPath $path)) { return $marks }
    foreach ($line in [IO.File]::ReadAllLines($path)) {
        if ($line.Length -lt 32) { continue }
        [DateTimeOffset]$ts = [DateTimeOffset]::MinValue
        if (-not [DateTimeOffset]::TryParseExact($line.Substring(0, 30), 'yyyy-MM-dd HH:mm:ss.fff zzz', $inv, [Globalization.DateTimeStyles]::None, [ref]$ts)) { continue }
        $t = ($ts.UtcDateTime - $r.ClockZeroUtc).TotalSeconds
        $body = $line.Substring(30).Trim()
        $body = $body -replace '^(INFO|WARN|ERROR)\s+', ''
        if ($body -match '^selftest \S+ with the generator goldens' -and -not $marks.ContainsKey('selftest')) { $marks['selftest'] = $t }
        elseif ($body -match '^mode\s' -and -not $marks.ContainsKey('mode')) { $marks['mode'] = $t; $marks['modeText'] = $body }
        elseif ($body -match '^start\s+file access checked' -and -not $marks.ContainsKey('start')) { $marks['start'] = $t }
        elseif ($body -match '^selftest (\S+):' -and -not $marks.ContainsKey('selftestStatus')) { $marks['selftestStatus'] = $Matches[1] }
    }
    return $marks
}

function First-Segment($segs, [string]$pattern, [double]$after) {
    foreach ($s in $segs) { if ($s.Start -ge $after -and $s.Text -match $pattern) { return $s } }
    return $null
}

function Last-Segment($segs, [string]$pattern, [double]$after, [double]$before) {
    $last = $null
    foreach ($s in $segs) { if ($s.Start -ge $after -and $s.Start -le $before -and $s.Text -match $pattern) { $last = $s } }
    return $last
}

$progressRx = '^\s*([\d,]+) / ([\d,]+) seeds \| ([\d.]+)/s \| ([\d,]+) hits'
$stage1Rx = '^\s*stage 1: ([\d,]+) / ([\d,]+) seeds \| ([\d,]+) kept'
$fieldRx = '^  (seeds evaluated|matches|wall clock|measured rate|thread time split|pre-generation|T5 placements|results file|what was kept|checkpoint|this run|threads|stage 1 scanned|stage 1 kept|survivor list|stage 2 will place|chosen)\s+(.*)$'

function Seeds-At($segs, [string]$rx, [double]$t) {
    # The seed count the progress lines showed at time t (the last line at or before it).
    $n = 0.0
    foreach ($s in $segs) {
        if ($s.Start -gt $t) { break }
        if ($s.Text -match $rx) { $n = [double]($Matches[1] -replace ',', '') }
    }
    return $n
}

# ---------------------------------------------------------------------------------------------------
# Warm-up and dry runs: untimed
# ---------------------------------------------------------------------------------------------------
$ready = New-Object System.Collections.Generic.List[string]
$first = $true
foreach ($q in $Queries) {
    $c = $catalog[$q]
    if ($SkipDryRun -and -not $first) { $ready.Add($q); continue }
    $seeds = $(if ($Quick) { $c.QuickSeeds } else { $c.Seeds })
    $vargs = @('search', $c.Query, '--seeds', [string]$seeds, '--mode', 'full', '--yes', '--dry-run', '--calibrate', '1',
               '--progress', 'none', '--cache-dir', $cache, '--out', (Join-Path $results ($q + '-dry.jsonl'))) + $c.Extra
    Write-Host ("  dry run " + $q + $(if ($first) { ' (also warms the cache root: the first self-test runs here, untimed)' } else { '' }) + '...')
    $r = Invoke-Vseed ($q + '-dry') $vargs
    $first = $false
    if ($r.ExitCode -ne 0) {
        $tail = ($r.Err | Select-Object -Last 6 | ForEach-Object { $_.Text }) -join ' | '
        Say ('  ' + $q + ': the dry run exited ' + $r.ExitCode + ' - skipped. ' + $tail)
        continue
    }
    $ready.Add($q)
}
Say ''

# ---------------------------------------------------------------------------------------------------
# The timed runs
# ---------------------------------------------------------------------------------------------------
$report = New-Object System.Collections.Generic.List[object]
foreach ($q in $ready) {
    $c = $catalog[$q]
    $seeds = $(if ($Quick) { $c.QuickSeeds } else { $c.Seeds })
    $outPath = Join-Path $results ($q + '.jsonl')
    $vargs = @('search', $c.Query, '--seeds', [string]$seeds, '--mode', 'full', '--yes', '--progress', 'tty',
               '--cache-dir', $cache, '--out', $outPath) + $c.Extra
    $before = [SeedLabBench.Measure]::Inventory([string[]]@($cache, $results))
    Write-Host ("  " + $q + ": vseed " + (ArgLine $vargs))
    $r = Invoke-Vseed $q $vargs
    $after = [SeedLabBench.Measure]::Inventory([string[]]@($cache, $results))
    $marks = Read-LogMarks $r $q
    $samples = New-Object 'System.Collections.Generic.List[SeedLabBench.Sample]'
    foreach ($s in $r.Samples) { $samples.Add($s) }
    $out = @($r.Out); $err = @($r.Err)

    # Stage boundaries.
    $t = [ordered]@{}
    $t['created'] = $r.CreatedT
    $t['startup'] = $(if ($marks.ContainsKey('selftest')) { $marks['selftest'] } elseif ($marks.ContainsKey('mode')) { $marks['mode'] } else { $null })
    $t['preflight'] = $(if ($marks.ContainsKey('start')) { $marks['start'] } else { $null })
    $sStrategy = First-Segment $out '^Strategy$' 0
    $sResult = First-Segment $out '^Result$' 0
    $sGate = First-Segment $out '^Funnel gate - measured' 0
    $funnel = $null -ne $sGate

    # The fields vseed printed: its plan (before the Result block) and its Result block, kept apart.
    $planFields = [ordered]@{}; $resultFields = [ordered]@{}
    $resultAt = $(if ($null -ne $sResult) { $sResult.Start } else { 1e9 })
    foreach ($s in $out) {
        if ($s.Text -notmatch $fieldRx) { continue }
        $into = $(if ($s.Start -ge $resultAt) { $resultFields } else { $planFields })
        if (-not $into.Contains($Matches[1])) { $into[$Matches[1]] = $Matches[2].Trim() }
    }
    $fields = [ordered]@{}
    foreach ($k in $planFields.Keys) { $fields[$k] = $planFields[$k] }
    foreach ($k in $resultFields.Keys) { $fields[$k] = $resultFields[$k] }
    $stages = New-Object System.Collections.Generic.List[object]
    $bounds = New-Object System.Collections.Generic.List[object]
    function Add-Bound([string]$name, $start, $end) { if ($null -ne $start -and $null -ne $end -and $end -ge $start) { $bounds.Add(@($name, [double]$start, [double]$end)) } }
    $scanName = 'scan'; $scanStart = $null; $scanEnd = $null; $scanRx = $progressRx
    Add-Bound 'start-up' $t['created'] $t['startup']
    Add-Bound 'preflight' $t['startup'] $t['preflight']
    if ($null -ne $sStrategy) { Add-Bound 'plan' $t['preflight'] $sStrategy.Start }
    $finishFrom = $null
    if ($funnel) {
        $s1First = First-Segment $err $stage1Rx 0
        $s1Last = Last-Segment $err $stage1Rx 0 $sGate.Start
        $s2First = First-Segment $err $progressRx $sGate.Start
        $s2Last = $(if ($null -ne $sResult) { Last-Segment $err $progressRx $sGate.Start $sResult.Start } else { Last-Segment $err $progressRx $sGate.Start 1e9 })
        if ($null -ne $sStrategy -and $null -ne $s1First) { Add-Bound 'open stage 1' $sStrategy.Start $s1First.Start }
        if ($null -ne $s1First -and $null -ne $s1Last) { Add-Bound 'stage 1' $s1First.Start $s1Last.End }
        if ($null -ne $s1Last) { Add-Bound 'survivor list + gate (1 thread)' $s1Last.End $sGate.Start }
        if ($null -ne $s2First) { Add-Bound 'open stage 2' $sGate.Start $s2First.Start }
        if ($null -ne $s2First -and $null -ne $s2Last) { Add-Bound 'stage 2' $s2First.Start $s2Last.End; $scanName = 'stage 2'; $scanStart = $s2First.Start; $scanEnd = $s2Last.End; $finishFrom = $s2Last.End }
        $stage1 = @{ Start = $(if ($null -ne $s1First) { $s1First.Start } else { $null }); End = $(if ($null -ne $s1Last) { $s1Last.End } else { $null }) }
    } else {
        $scanFirst = First-Segment $err $progressRx 0
        $scanLast = $(if ($null -ne $sResult) { Last-Segment $err $progressRx 0 $sResult.Start } else { Last-Segment $err $progressRx 0 1e9 })
        if ($null -ne $sStrategy -and $null -ne $scanFirst) { Add-Bound 'open the run' $sStrategy.Start $scanFirst.Start }
        if ($null -ne $scanFirst -and $null -ne $scanLast) { Add-Bound 'scan' $scanFirst.Start $scanLast.End; $scanStart = $scanFirst.Start; $scanEnd = $scanLast.End; $finishFrom = $scanLast.End }
    }
    if ($null -ne $finishFrom -and $null -ne $sResult) { Add-Bound 'finish writing' $finishFrom $sResult.Start }
    if ($null -ne $sResult) { Add-Bound 'report and exit' $sResult.Start $r.ExitT }
    Add-Bound 'whole run' $t['created'] $r.ExitT

    foreach ($b in $bounds) {
        $g = [SeedLabBench.Measure]::Of($b[0], $samples, $r.CreatedT, $b[1], $b[2], $cores)
        $stages.Add($g)
    }

    # Steady state inside the scan (or stage 2), and stage 1 for a funnel.
    $steady = @()
    $scans = @()
    if ($null -ne $scanStart) { $scans += ,@($scanName, $scanStart, $scanEnd, $progressRx) }
    if ($funnel -and $null -ne $stage1.Start -and $null -ne $stage1.End) { $scans += ,@('stage 1', $stage1.Start, $stage1.End, $stage1Rx) }
    foreach ($sc in $scans) {
        $w = [SeedLabBench.Measure]::Steady($samples, $sc[1], $sc[2])
        if ($null -eq $w) { $steady += [ordered]@{ stage = $sc[0]; defined = $false }; continue }
        $n0 = Seeds-At $err $sc[3] $w[0]; $n1 = Seeds-At $err $sc[3] $w[1]
        $len = $w[1] - $w[0]
        $steady += [ordered]@{
            stage = $sc[0]; defined = $true; start_s = [Math]::Round($w[0] - $sc[1], 2); window_s = [Math]::Round($len, 2)
            outside_s = [Math]::Round(($sc[2] - $sc[1]) - $len, 2); busy_cores = [Math]::Round($w[2], 2)
            utilisation = [Math]::Round($w[2] / $cores, 4)
            # The progress lines count seeds as the results are written, in block order: over a short
            # window they may not move at all, and then there is no rate to give, not a rate of 0.
            seeds_per_second = $(if ($len -gt 0 -and $n1 -gt $n0) { [Math]::Round(($n1 - $n0) / $len, 2) } else { $null })
        }
    }

    # Files: what the run left, what it removed, and the most the watched folders held at once.
    $left = @(); $gone = @()
    foreach ($k in $after.Keys) {
        $state = $null
        if (-not $before.ContainsKey($k)) { $state = 'new' } elseif ($before[$k] -ne $after[$k]) { $state = 'changed' }
        if ($null -eq $state) { continue }
        $item = [ordered]@{}
        $item['path'] = $k.Substring($OutDir.Length + 1)
        $item['bytes'] = $after[$k]
        $item['state'] = $state
        if ($k.StartsWith($results, [StringComparison]::OrdinalIgnoreCase)) { $item['sha256'] = (Get-FileHash -LiteralPath $k -Algorithm SHA256).Hash.ToLowerInvariant() }
        $left += $item
    }
    foreach ($k in $before.Keys) { if (-not $after.ContainsKey($k)) { $gone += $k.Substring($OutDir.Length + 1) } }
    $peakDisk = 0.0
    foreach ($s in $samples) { if ($s.DiskBytes -gt $peakDisk) { $peakDisk = $s.DiskBytes } }
    $last = $samples[$samples.Count - 1]

    # Taint.
    $taint = New-Object System.Collections.Generic.List[string]
    $byName = [ordered]@{}
    foreach ($seen in $r.Seen) {
        $n = ($seen -split ' \(pid ')[0]
        if ($byName.Contains($n)) { $byName[$n]++ } else { $byName[$n] = 1 }
    }
    foreach ($n in $byName.Keys) { $taint.Add($n + $(if ($byName[$n] -gt 1) { ' x' + $byName[$n] } else { '' }) + ' ran during the run') }
    $whole = $stages | Where-Object { $_.Name -eq 'whole run' } | Select-Object -First 1
    if ($null -ne $whole -and $whole.ForeignCores -gt 1.0) { $taint.Add('other processes used ' + (F2 $whole.ForeignCores) + ' cores on average') }
    if ($marks.ContainsKey('modeText') -and $marks['modeText'] -match 'auto-throttled') { $taint.Add('vseed throttled itself: ' + $marks['modeText']) }

    $entry = [ordered]@{
        id = $q; query = $(if ($c.Query.EndsWith('.json')) { Split-Path -Leaf $c.Query } else { $c.Query }); what = $c.What
        arguments = (ArgLine $vargs); seeds = $seeds; exit_code = $r.ExitCode; funnel = $funnel
        tainted = ($taint.Count -gt 0); taint_reasons = @($taint)
        selftest = $(if ($marks.ContainsKey('selftestStatus')) { $marks['selftestStatus'] } else { $null })
        process = [ordered]@{
            wall_s = [Math]::Round($r.ExitT - $r.CreatedT, 3); cpu_s = [Math]::Round($last.User + $last.Kernel, 3)
            kernel_s = [Math]::Round($last.Kernel, 3); peak_working_set_bytes = $last.PeakWorkingSet; peak_private_bytes = $last.PeakPrivate
            read_bytes = $last.ReadBytes; write_bytes = $last.WriteBytes; page_faults = $last.PageFaults; samples = $samples.Count; interval_ms = $IntervalMs
        }
        stages = @($stages | ForEach-Object { [ordered]@{
            name = $_.Name; start_s = [Math]::Round($_.Start, 3); wall_s = [Math]::Round($_.Wall, 3); cpu_s = [Math]::Round($_.Cpu, 3)
            user_s = [Math]::Round($_.User, 3); kernel_s = [Math]::Round($_.Kernel, 3); busy_cores = [Math]::Round($_.BusyCores, 2)
            utilisation = [Math]::Round($_.Utilisation, 4); peak_working_set_bytes = $_.PeakWorkingSet; peak_private_bytes = $_.PeakPrivate
            read_bytes = [Math]::Round($_.ReadBytes); write_bytes = [Math]::Round($_.WriteBytes); page_faults = [Math]::Round($_.PageFaults)
            samples_inside = $_.SamplesInside; foreign_cores = $(if ($_.ForeignCores -ge 0) { [Math]::Round($_.ForeignCores, 2) } else { $null }) } })
        steady_state = $steady
        disk = [ordered]@{ peak_bytes_in_results_and_checkpoints = $peakDisk; files_left = $left; files_removed = $gone }
        vseed_said = [ordered]@{ plan = $planFields; result = $resultFields }
    }
    $report.Add($entry)

    # ---- the text block ----------------------------------------------------------------------------
    Say ($q + '  ' + $entry.query + ' - ' + $c.What)
    $threadsText = $(if ($fields.Contains('threads')) { $fields['threads'].Split(',')[0] + ' threads' } else { '? threads' })
    $funnelText = $(if ($funnel) { ', funnel' } else { '' })
    $taintText = $(if ($taint.Count -gt 0) { '  TAINTED: ' + ($taint -join '; ') } else { '' })
    Say ('    ' + $seeds.ToString('N0', $inv) + ' seeds, ' + $threadsText + ', exit ' + $r.ExitCode + ', ' + (F1 $entry.process.wall_s) + ' s' + $funnelText + $taintText)
    Say ('    {0,-32} {1,8} {2,8} {3,7} {4,7} {5,9} {6,9} {7,9} {8,9} {9,9}' -f 'stage', 'wall s', 'CPU s', 'cores', 'util %', 'peak WS', 'peak priv', 'written', 'read', 'faults')
    Say ('    {0,-32} {1,8} {2,8} {3,7} {4,7} {5,9} {6,9} {7,9} {8,9} {9,9}' -f '', '', '(kernel)', 'busy', '', 'MiB', 'MiB', 'KiB', 'KiB', 'k')
    foreach ($g in $stages) {
        Say ('    {0,-32} {1,8} {2,8} {3,7} {4,7} {5,9} {6,9} {7,9} {8,9} {9,9}' -f $g.Name, (F2 $g.Wall), ((F1 $g.Cpu) + ' (' + (F1 $g.Kernel) + ')').PadLeft(8),
             (F1 $g.BusyCores), (F1 (100 * $g.Utilisation)), (MiB $g.PeakWorkingSet), (MiB $g.PeakPrivate),
             ($g.WriteBytes / 1024).ToString('N0', $inv), ($g.ReadBytes / 1024).ToString('N0', $inv), ($g.PageFaults / 1000).ToString('N0', $inv))
    }
    foreach ($s in $steady) {
        if ($s.defined) {
            $rateText = $(if ($null -ne $s.seeds_per_second) { ', ' + (F1 $s.seeds_per_second) + ' seeds/s' } else { '' })
            Say ('    steady ' + $s.stage + ': ' + (F1 $s.window_s) + ' s at ' + (F1 $s.busy_cores) + ' busy cores (' + (F1 (100 * $s.utilisation)) + ' %)' +
                 $rateText + '; ramp-up and tail ' + (F1 $s.outside_s) + ' s')
        } else {
            Say ('    steady ' + $s.stage + ': too short to find one at a ' + $IntervalMs + ' ms sampling interval')
        }
    }
    Say ('    peak on disk (results + checkpoints) ' + (MiB $peakDisk) + ' MiB; the process wrote ' + (MiB $last.WriteBytes) + ' MiB and read ' +
         (MiB $last.ReadBytes) + ' MiB (every read and write call: files, pipes, the console)')
    foreach ($f in $left) {
        $hashText = $(if ($f.Contains('sha256')) { ', sha256 ' + $f['sha256'].Substring(0, 16) } else { '' })
        Say ('    left   ' + $f.path + '  ' + ([double]$f.bytes).ToString('N0', $inv) + ' bytes (' + $f.state + $hashText + ')')
    }
    foreach ($f in $gone) { Say ('    gone   ' + $f) }
    if (@($left | Where-Object { $_.path -like '*.survivors' }).Count -gt 0) {
        Say '    note   a finished funnel leaves its survivor list in the cache''s checkpoints folder (4 bytes per survivor); nothing removes it'
    }
    foreach ($k in @('measured rate', 'thread time split', 'pre-generation', 'T5 placements', 'stage 1 scanned', 'stage 1 kept', 'stage 2 will place')) {
        if ($fields.Contains($k)) { Say ('    vseed  ' + $k + ': ' + $fields[$k]) }
    }
    Say ''
}

# The funnel and the tier ladder visit the same seeds with the same goals: their results must agree.
$q5 = Join-Path $results 'Q5.jsonl'; $q6 = Join-Path $results 'Q6.jsonl'
if ((Test-Path -LiteralPath $q5) -and (Test-Path -LiteralPath $q6)) {
    $h5 = (Get-FileHash -LiteralPath $q5 -Algorithm SHA256).Hash; $h6 = (Get-FileHash -LiteralPath $q6 -Algorithm SHA256).Hash
    Say ('Q5 (funnel) and Q6 (sample) results files: ' + $(if ($h5 -eq $h6) { 'identical, as they must be' } else { 'DIFFERENT - the funnel and the tier ladder disagree on the same seeds' }))
    Say ''
}

# ---------------------------------------------------------------------------------------------------
# The files
# ---------------------------------------------------------------------------------------------------
[string]$runtimeConfigText = ''
if (Test-Path -LiteralPath $runtimeConfig) { $runtimeConfigText = [IO.File]::ReadAllText($runtimeConfig).Replace("`r`n", "`n") }
# Built one key at a time: Windows PowerShell 5.1 fails a large literal like this one with "Argument
# types do not match".
$doc = [ordered]@{}
$doc['schema'] = 'seedlab-bench-search/1'
$doc['created'] = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz', $inv)
$doc['vseed'] = $Vseed
$doc['vseed_sha256'] = $vseedHash
$doc['runtimeconfig'] = $runtimeConfigText
$doc['logical_cores'] = $cores
$doc['os'] = [Environment]::OSVersion.VersionString
$doc['knobs_set'] = $knobs
$doc['quick'] = $Quick.IsPresent
$doc['interval_ms'] = $IntervalMs
$doc['folder'] = $OutDir
$doc['note'] = 'stage boundaries come from vseed''s own output, stamped as it arrived, and its session log; CPU, memory and bytes are the vseed process''s, sampled every interval_ms and interpolated at the boundaries'
$doc['queries'] = $report.ToArray()
$jsonPath = Join-Path $OutDir 'bench-search.json'
$textPath = Join-Path $OutDir 'bench-search.txt'
[IO.File]::WriteAllText($jsonPath, ($doc | ConvertTo-Json -Depth 10).Replace("`r`n", "`n") + "`n", $utf8)
[IO.File]::WriteAllText($textPath, $script:text.ToString().Replace("`r`n", "`n"), $utf8)
Write-Host ('  written     ' + $textPath)
Write-Host ('              ' + $jsonPath)
$bad = @($report | Where-Object { $_.exit_code -ne 0 })
if ($bad.Count -gt 0 -or $ready.Count -lt $Queries.Count) { exit 1 }
exit 0
