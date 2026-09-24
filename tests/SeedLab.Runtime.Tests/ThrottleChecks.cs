using System;
using System.Collections.Generic;
using SeedLab.Runtime.Execution;
using SeedLab.Runtime.Throttle;

namespace SeedLab.RuntimeTests
{
    /// <summary>A process table we control, so the throttle can be tested without launching a game.</summary>
    public sealed class FakeProcesses : IProcessLister
    {
        public List<string> Names { get; } = new List<string>();
        public int Calls { get; private set; }

        public IReadOnlyList<string> RunningProcessNames()
        {
            Calls++;
            return Names;
        }
    }

    public static class ThrottleChecks
    {
        public static void Run(Action<bool, string, string> check)
        {
            // --- the game is running --------------------------------------------------------------
            FakeProcesses procs = new FakeProcesses();
            procs.Names.AddRange(new[] { "explorer", "steam", "valheim", "dotnet" });
            List<string> said = new List<string>();

            AutoThrottle t = new AutoThrottle(ResourceMode.Full,
                new GameWatchOptions { PollInterval = TimeSpan.Zero }, said.Add, procs);

            ResourceMode m = t.Start();
            check(m == ResourceMode.Background && t.IsThrottled,
                "a running game drops the run to background", "effective " + ResourceModes.Name(m));
            check(said.Count == 1, "exactly ONE line is printed", said.Count + " line(s)");
            check(said[0].Contains("valheim") && said[0].Contains("--ignore-running-game"),
                "the line names the process and the override", said[0]);

            t.Poll(); t.Poll();
            check(said.Count == 1, "re-checks do not repeat the announcement", said.Count + " line(s) after 3 polls");

            // --- the game exits ---------------------------------------------------------------------
            procs.Names.Remove("valheim");
            ResourceMode after = t.Poll();
            check(after == ResourceMode.Full && !t.IsThrottled,
                "the requested mode comes back when the game exits", ResourceModes.Name(after));
            check(said.Count == 2 && said[1].Contains("exited"),
                "the change back is announced too - no speed change is silent",
                said.Count > 1 ? said[1] : "(nothing said)");
            check(t.EverThrottled, "the run remembers that it was throttled, for the final report", "");
            t.Dispose();

            // --- name matching ----------------------------------------------------------------------
            FakeProcesses linux = new FakeProcesses();
            linux.Names.Add("valheim_server.x86_64");
            check(GameDetector.Detect(new GameWatchOptions(), linux).IsRunning,
                "the default list covers the Linux client and both servers", "valheim_server.x86_64");

            FakeProcesses cased = new FakeProcesses();
            cased.Names.Add("Valheim");
            check(GameDetector.Detect(new GameWatchOptions(), cased).IsRunning,
                "matching ignores case", "Valheim");

            FakeProcesses exe = new FakeProcesses();
            exe.Names.Add("myGame");
            GameWatchOptions custom = new GameWatchOptions { ProcessNames = new[] { "myGame.exe" } };
            check(GameDetector.Detect(custom, exe).IsRunning,
                "a configured name may carry .exe", "myGame.exe matches myGame");

            FakeProcesses other = new FakeProcesses();
            other.Names.AddRange(new[] { "notepad", "valheimlauncher" });
            check(!GameDetector.Detect(new GameWatchOptions(), other).IsRunning,
                "a similar name is not a match", "valheimlauncher");

            // --- disabled ---------------------------------------------------------------------------
            FakeProcesses running = new FakeProcesses();
            running.Names.Add("valheim");
            List<string> quiet = new List<string>();
            AutoThrottle off = new AutoThrottle(ResourceMode.Full,
                new GameWatchOptions { Enabled = false }, quiet.Add, running);
            check(off.Start() == ResourceMode.Full && quiet.Count == 0,
                "--ignore-running-game turns the whole feature off", "stayed at full, said nothing");
            check(running.Calls == 0, "and it does not even look at the process table", "0 calls");
            off.Dispose();

            // --- a run that already asked for background --------------------------------------------
            FakeProcesses withGame = new FakeProcesses();
            withGame.Names.Add("valheim");
            List<string> lines = new List<string>();
            AutoThrottle already = new AutoThrottle(ResourceMode.Background,
                new GameWatchOptions { PollInterval = TimeSpan.Zero }, lines.Add, withGame);
            check(already.Start() == ResourceMode.Background && lines.Count == 0,
                "a run already in background is not told it is being throttled", "no line");
            already.Dispose();

            // --- the real lister works ---------------------------------------------------------------
            SystemProcessLister real = new SystemProcessLister();
            IReadOnlyList<string> names = real.RunningProcessNames();
            check(names.Count > 0, "the real process lister sees this machine", names.Count + " processes");
        }
    }
}
