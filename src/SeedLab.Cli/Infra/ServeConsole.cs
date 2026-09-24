using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using SeedLab.Web;
using SeedLab.Web.Search;

namespace SeedLab.Cli.Infra
{
    /// <summary>
    /// The console window <c>vseed serve</c> runs in (2026-09-24, the web server's lifecycle as the user
    /// confirmed it): its title and banner, what Ctrl+C, Ctrl+Break, closing it and a shutdown do, and
    /// the idle reminder printed there.
    ///
    /// <para><b>The window IS the server.</b> It is titled "SeedLab web server" and says so first, before
    /// anything else is printed - here, in vseed, so it is right however the server was started: by the
    /// scripts, by hand, from another terminal. Never hidden, never a service, never started at login.</para>
    ///
    /// <para><b>Ctrl+C counts only in this window, and only twice.</b> Ctrl+C in the browser is Copy and
    /// never reaches the server. Here the first press stops nothing - it says what is running and what a
    /// stop would do to it - and a second within 10 seconds is the graceful stop. Ctrl+Break is a stop
    /// that was already confirmed. Closing the window (Windows' CTRL_CLOSE_EVENT, which .NET reports as
    /// SIGHUP), a Unix hang-up and SIGTERM cannot be refused: the graceful stop runs at once, INSIDE the
    /// handler, because Windows ends the process when the handler returns - about five seconds after a
    /// window is closed. A press while a stop is already under way stops nothing twice and says so.</para>
    ///
    /// <para><b>Signing out and shutting down, on Windows</b> (review of 2026-09-25). They never reach a console
    /// handler here: Windows does not send CTRL_LOGOFF_EVENT or CTRL_SHUTDOWN_EVENT to a console program that
    /// has loaded user32.dll, and vseed serve has (Microsoft's SetConsoleCtrlHandler documentation, which
    /// recommends a hidden window instead). So the server keeps one - <see cref="SessionEndWindow"/> - whose
    /// WM_ENDSESSION runs the same graceful stop before Windows ends the process.</para>
    ///
    /// <para><b>Why .NET's own Ctrl+C handling is not used.</b> The web host's console lifetime stopped the
    /// server on the first press and ended the process under a running search (see WebServer); it is
    /// replaced by a lifetime that does nothing, and these handlers call the server's one graceful stop.
    /// A handler also re-enables Ctrl+C for this process first (Windows only): the "ignore Ctrl+C" flag a
    /// launching script may have set on itself (SetConsoleCtrlHandler with a NULL routine) is INHERITED by
    /// the processes it starts - a handler routine is not - and a server started that way would never hear
    /// Ctrl+C at all.</para>
    /// </summary>
    public sealed class ServeConsole : IDisposable
    {
        public const string Title = "SeedLab web server";

        /// <summary>How long a first Ctrl+C waits for the second.</summary>
        public static readonly TimeSpan SecondPressWindow = TimeSpan.FromSeconds(10);

        private static readonly object TitleGate = new object();
        private static string? _originalTitle;
        private static bool _prepared;

        private readonly WebServerControl _control;
        private readonly List<PosixSignalRegistration> _signals = new List<PosixSignalRegistration>();
        private readonly SessionEndWindow? _sessionEnd;
        private readonly object _gate = new object();
        private long _firstPressMs = long.MinValue;
        private bool _stopAsked;
        private bool _secondPressed;

        /// <summary>True once <see cref="PrepareWindow"/> has run: this process is a server in its window.</summary>
        public static bool Prepared => _prepared;

        /// <summary>
        /// The console <see cref="PrepareWindow"/> set up, whose <see cref="Control"/> the server is then
        /// given - null when this process is not a server in its window (the self-test, --status).
        /// </summary>
        public static ServeConsole? Current { get; private set; }

        /// <summary>The server this window drives. A stop asked for before it exists is kept and applied.</summary>
        public WebServerControl Control => _control;

        /// <summary>
        /// Before anything else is printed: re-enables Ctrl+C for this process, titles the window, prints
        /// the banner, and takes over Ctrl+C and closing at once - so the banner's "Ctrl+C twice" is true
        /// from the moment it is shown, while the runtime and the game data are still loading, and not
        /// only once the server is listening. A stop asked for then is applied as soon as the server
        /// exists, and it stops before it has listened to anything.
        /// </summary>
        public static void PrepareWindow()
        {
            _prepared = true;
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    SetConsoleCtrlHandler(IntPtr.Zero, false);
                }
                catch (Exception)
                {
                    // No console, or not a Windows that has the call: nothing to re-enable.
                }
            }

            SetTitle(Title, remember: true);
            foreach (string line in BannerLines()) Console.Out.WriteLine(line);
            Console.Out.WriteLine();
            Current ??= new ServeConsole(new WebServerControl());
        }

        /// <summary>The banner, word for word the confirmed design's, wrapped for an 80-column window.</summary>
        public static IReadOnlyList<string> BannerLines()
        {
            string script = OperatingSystem.IsWindows() ? "'SeedLab 3 - Stop web page'" : "'sh seedlab.sh stop'";
            string text = "This window IS SeedLab's web server. Minimise it - do not close it - while you use the page. "
                          + "To stop it: Stop SeedLab on the page, " + script + " (or vseed serve --stop), or press Ctrl+C "
                          + "in THIS window twice.";
            List<string> lines = new List<string> { new string('=', 78) };
            foreach (string l in Wrap(text, 76)) lines.Add(" " + l);
            lines.Add(new string('=', 78));
            return lines;
        }

        /// <summary>
        /// Takes over Ctrl+C, Ctrl+Break, closing the window and SIGTERM for <paramref name="control"/>'s
        /// server, and prints the idle reminder. Dispose it when the server has stopped.
        /// </summary>
        public ServeConsole(WebServerControl control)
        {
            _control = control ?? throw new ArgumentNullException(nameof(control));
            Register(PosixSignal.SIGINT, OnCtrlC);
            Register(PosixSignal.SIGQUIT, ctx =>
            {
                ctx.Cancel = true;
                AskStop(OperatingSystem.IsWindows() ? "Ctrl+Break was pressed in its window" : "it was sent SIGQUIT", wait: false);
            });
            Register(PosixSignal.SIGHUP, ctx =>
            {
                ctx.Cancel = true;
                AskStop(OperatingSystem.IsWindows() ? "its window was closed" : "its terminal was closed", wait: true);
            });
            Register(PosixSignal.SIGTERM, ctx =>
            {
                // On Windows this is CTRL_SHUTDOWN_EVENT, which a program that has loaded user32.dll is never
                // sent; the session-end window below is what hears a shutdown or a sign-out there.
                ctx.Cancel = true;
                AskStop(OperatingSystem.IsWindows() ? "Windows is shutting down" : "it was sent SIGTERM (a shutdown, a logout or a kill)",
                        wait: true);
            });

            // Windows: signing out, shutting down and restarting, through a hidden window's WM_ENDSESSION. The
            // stop runs inside the message, as for a closed window: Windows ends the process once it returns.
            _sessionEnd = SessionEndWindow.Start(reason => AskStop(reason, wait: true));

            _control.IdleReminder += OnIdle;
            _control.IdleEnded += () => SetTitle(Title, remember: false);
        }

        private void Register(PosixSignal signal, Action<PosixSignalContext> handler)
        {
            try
            {
                _signals.Add(PosixSignalRegistration.Create(signal, handler));
            }
            catch (Exception)
            {
                // A signal this platform does not have (SIGQUIT and SIGHUP are Unix names; .NET maps them on
                // Windows, but a platform without one simply keeps its default).
            }
        }

        private void OnCtrlC(PosixSignalContext ctx)
        {
            ctx.Cancel = true;
            long now = Environment.TickCount64;
            bool second;
            bool stopping;
            lock (_gate)
            {
                // A press while the stop is under way - the impatient third press, the likeliest one of all -
                // used to count as a new first press and say "nothing has been stopped yet" (review of
                // 2026-09-25). The stop only needs to be asked once. The second press is noted HERE, under
                // the lock: each press arrives on a thread of its own, and one right behind it must already
                // see that a stop is on its way.
                stopping = _stopAsked || _secondPressed || _control.Stopping;
                second = !stopping && _firstPressMs != long.MinValue && now - _firstPressMs <= (long)SecondPressWindow.TotalMilliseconds;
                if (second) _secondPressed = true;
                if (!stopping) _firstPressMs = second ? long.MinValue : now;
            }

            if (stopping)
            {
                Say("");
                Say(AlreadyStoppingLine);
                return;
            }

            if (second)
            {
                Say("");
                Say("Ctrl+C again - stopping SeedLab.");
                AskStop("Ctrl+C was pressed twice in its window", wait: false);
                return;
            }

            foreach (string line in FirstPressLines(_control.Url, _control.RunningSearches())) Say(line);
        }

        /// <summary>What Ctrl+C prints while SeedLab is already stopping.</summary>
        public const string AlreadyStoppingLine = "Ctrl+C - SeedLab is already stopping; wait a moment.";

        /// <summary>What the first Ctrl+C prints: nothing is stopped yet, what is running, and what a second press does.</summary>
        public static IReadOnlyList<string> FirstPressLines(string url, IReadOnlyList<SearchRunInfo> searches)
        {
            List<string> l = new List<string> { "", "Ctrl+C - nothing has been stopped yet." };
            if (url.Length == 0)
            {
                l.AddRange(Indent("SeedLab's web server is still starting. Press Ctrl+C again within 10 seconds to stop it."));
                return l;
            }

            string running = "SeedLab's web server is running at " + url;
            if (searches.Count == 0)
            {
                l.AddRange(Indent(running + "; no search is running."));
                l.AddRange(Indent("Press Ctrl+C again within 10 seconds to stop SeedLab."));
                return l;
            }

            l.AddRange(Indent(running + ", and " + (searches.Count == 1 ? "a search is" : searches.Count + " searches are")
                              + " running: " + Describe(searches) + "."));
            l.AddRange(Indent("Press Ctrl+C again within 10 seconds to stop SeedLab. Stopping it now means:"));
            l.AddRange(StopCosts(searches));
            return l;
        }

        /// <summary>
        /// What stopping SeedLab costs each running search, said for THAT search (review of 2026-09-25): one
        /// with a resume point is saved, and the command that continues it is printed whole; one in a funnel's
        /// first stage has none, and loses its work. One sentence for all of them used to promise a saved part
        /// and a command, and then print no command and say the work was lost.
        /// </summary>
        public static IEnumerable<string> StopCosts(IReadOnlyList<SearchRunInfo> searches)
        {
            foreach (SearchRunInfo s in searches)
            {
                if (s.ResumeCommand == null)
                {
                    foreach (string line in Indent("\"" + s.Name + "\" has no resume point yet (it is in its first stage): "
                                                   + "stopping it now loses the work it has done so far.")) yield return line;
                    continue;
                }

                foreach (string line in Indent("\"" + s.Name + "\" stops at once; the part it has finished is saved, and it can "
                                               + "be continued later from a terminal with:")) yield return line;
                yield return "    " + s.ResumeCommand;
                if (s.ResumeCommand.Contains(SeedLab.Web.Search.EngineSearchEngine.QueryFilePlaceholder, StringComparison.Ordinal))
                {
                    foreach (string line in Indent("(its query file could not be saved beside the checkpoint: the page's Export "
                                                   + "button saves it)")) yield return line;
                }
            }
        }

        /// <summary>'"gentle-start", 12.3 % done (49,152 of 400,000 seeds)', joined for several.</summary>
        public static string Describe(IReadOnlyList<SearchRunInfo> searches)
        {
            List<string> parts = new List<string>();
            foreach (SearchRunInfo s in searches) parts.Add(Describe(s));
            return string.Join("; ", parts);
        }

        public static string Describe(SearchRunInfo s) =>
            "\"" + s.Name + "\", " + s.Percent.ToString("0.0", CultureInfo.InvariantCulture) + " % done ("
            + s.Scanned.ToString("N0", CultureInfo.InvariantCulture) + " of " + s.Limit.ToString("N0", CultureInfo.InvariantCulture)
            + " seeds" + (s.Message != null && s.Message.StartsWith("stage", StringComparison.Ordinal) ? "; " + s.Message : "") + ")";

        private void AskStop(string reason, bool wait)
        {
            lock (_gate)
            {
                if (_stopAsked && !wait) return;
                _stopAsked = true;
            }

            if (wait)
            {
                // The process ends when this handler returns: the stop is done here, inside the ~5 s Windows
                // gives a closed window, and the checkpoints are saved before it lets go.
                _control.Stop(reason, TimeSpan.FromSeconds(4.5));
            }
            else
            {
                _control.BeginStop(reason);
            }
        }

        private void OnIdle(IdleReminderInfo info)
        {
            string at = DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            Say("");
            foreach (string line in Wrap("[" + at + "] SeedLab hasn't been used for " + info.IdleText + ". If you are done with "
                                         + "it, stop it: Stop SeedLab on the page, or press Ctrl+C twice in this window. It does "
                                         + "not stop by itself; this reminder comes back every "
                                         + WebServerControl.Span(info.Every) + " while nobody uses it.", 78))
            {
                Say(line);
            }

            SetTitle(Title + " - idle " + WebServerControl.Short(info.IdleFor), remember: false);
        }

        /// <summary>Puts the window's title back as it was before this server (Windows; elsewhere it is left).</summary>
        public static void RestoreTitle()
        {
            lock (TitleGate)
            {
                if (_originalTitle == null) return;
                try
                {
                    Console.Title = _originalTitle;
                }
                catch (Exception)
                {
                }
            }
        }

        private static void SetTitle(string title, bool remember)
        {
            lock (TitleGate)
            {
                try
                {
                    if (remember && _originalTitle == null && OperatingSystem.IsWindows()) _originalTitle = Console.Title;
                }
                catch (Exception)
                {
                    // No console to read a title from.
                }

                try
                {
                    Console.Title = title;
                }
                catch (Exception)
                {
                    // Redirected, or no console at all: a title is a courtesy, not a requirement.
                }
            }
        }

        private static void Say(string line)
        {
            try
            {
                Console.Out.WriteLine(line);
            }
            catch (Exception)
            {
                // A window being closed cannot be written to.
            }
        }

        private static IEnumerable<string> Indent(string text)
        {
            foreach (string l in Wrap(text, 76)) yield return "  " + l;
        }

        /// <summary>Word-wraps <paramref name="text"/> at <paramref name="width"/> columns; a longer word stays whole.</summary>
        public static List<string> Wrap(string text, int width)
        {
            List<string> lines = new List<string>();
            string line = "";
            foreach (string word in text.Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > width)
                {
                    lines.Add(line);
                    line = word;
                }
                else
                {
                    line = line.Length == 0 ? word : line + " " + word;
                }
            }

            if (line.Length > 0) lines.Add(line);
            return lines;
        }

        public void Dispose()
        {
            _sessionEnd?.Dispose();
            foreach (PosixSignalRegistration r in _signals)
            {
                try
                {
                    r.Dispose();
                }
                catch (Exception)
                {
                }
            }

            _signals.Clear();
            _control.IdleReminder -= OnIdle;
        }

        // =========================================================================================
        // A window that closes when vseed ends.

        /// <summary>
        /// True when this process is the only one on its console - the window was made for vseed (a
        /// double-click, a script's "new window"), so it disappears the moment vseed ends. Windows only;
        /// false elsewhere, and false when it cannot be told.
        /// </summary>
        public static bool OwnsWindowAlone()
        {
            if (!OperatingSystem.IsWindows()) return false;
            try
            {
                uint[] pids = new uint[4];
                uint n = GetConsoleProcessList(pids, (uint)pids.Length);
                return n == 1;
            }
            catch (Exception)
            {
                return false;
            }
        }

        /// <summary>
        /// True once the server has stopped and a search it stopped left a checkpoint - so the lines that
        /// say where it is and how to continue it are on the screen, and must stay readable.
        /// </summary>
        public static bool StopLeftResumePoint()
        {
            try
            {
                Task<ServerStopReport>? t = Current?.Control.StopTask;
                if (t == null || !t.IsCompletedSuccessfully) return false;
                foreach (SearchRunInfo s in t.Result.Searches)
                {
                    if (s.CheckpointPath != null) return true;
                }
            }
            catch (Exception)
            {
            }

            return false;
        }

        /// <summary>
        /// Keeps a window vseed owns alone open, so what it says last can be read: "Press Enter to close this
        /// window." After a failure, and after a stop that left a search's checkpoint - the command that
        /// continues it is on the screen, and a window that vanished took it along (review of 2026-09-25).
        /// Only when someone could press it (stdin is a keyboard), and for ten minutes at most - a window
        /// left waiting must not become the permanently running thing this whole design exists to prevent.
        /// </summary>
        public static void PauseBeforeClosing(string? why = null)
        {
            if (!OwnsWindowAlone() || Console.IsInputRedirected) return;
            try
            {
                Console.Out.WriteLine();
                if (why != null)
                {
                    foreach (string line in Wrap(why, 78)) Console.Out.WriteLine(line);
                }

                Console.Out.WriteLine("Press Enter to close this window (it closes by itself in 10 minutes).");
                Task<string?> read = Task.Run(() => Console.ReadLine());
                read.Wait(TimeSpan.FromMinutes(10));
            }
            catch (Exception)
            {
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint GetConsoleProcessList([Out] uint[] processList, uint processCount);
    }
}
