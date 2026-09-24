using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading.Tasks;
using SeedLab.Web.Search;

namespace SeedLab.Web
{
    /// <summary>The idle reminder, as the host's console is told it.</summary>
    public sealed class IdleReminderInfo
    {
        public IdleReminderInfo(TimeSpan idleFor, TimeSpan every, int count)
        {
            IdleFor = idleFor;
            Every = every;
            Count = count;
        }

        /// <summary>How long nobody has used the page.</summary>
        public TimeSpan IdleFor { get; }

        /// <summary>The reminder's period (<c>--idle-reminder</c>).</summary>
        public TimeSpan Every { get; }

        /// <summary>1 for the first reminder since the last activity, 2 for the next, and so on.</summary>
        public int Count { get; }

        /// <summary>"60 minutes", "2 hours", "45 seconds" - how long, as a person says it.</summary>
        public string IdleText => WebServerControl.Span(IdleFor);
    }

    /// <summary>What the server's graceful stop did, for the page, <c>vseed serve --stop</c> and the console.</summary>
    public sealed class ServerStopReport
    {
        /// <summary>Why it stopped: "Stop SeedLab was pressed on the page", "Ctrl+C was pressed twice in its window", ...</summary>
        public string Reason { get; set; } = "";

        /// <summary>Each search that was running, as it stood once it had stopped (or once the wait for it ran out).</summary>
        public List<SearchRunInfo> Searches { get; set; } = new List<SearchRunInfo>();

        /// <summary>The server's registry file could not be deleted (it is ignored by then: its process has ended).</summary>
        public bool RegistryLeft { get; set; }
    }

    /// <summary>
    /// The host's handle on a running <see cref="WebServer"/> (2026-09-24): the graceful stop, what is
    /// running, and the idle reminder - everything <c>vseed serve</c> needs for its console window
    /// (its title, Ctrl+C, closing the window, the reminder printed there) without the web project
    /// knowing what a console is.
    ///
    /// <para><b>One stop, many triggers.</b> The page's Stop SeedLab, <c>vseed serve --stop</c>, Ctrl+C
    /// pressed twice, Ctrl+Break, closing the window, a sign-out or shutdown and a Unix SIGTERM or hang-up
    /// all end in the same place: every running search is stopped at once with its checkpoint saved,
    /// the page is told, the registry file is deleted, and <c>RunAsync</c> returns 0. It is bounded - a
    /// few seconds - because closing a console window gives the process about five. On Windows a sign-out
    /// or shutdown reaches it through the host's hidden session-end window (WM_ENDSESSION), not a console
    /// handler: Windows sends those no console event once user32.dll is loaded (review of 2026-09-25).</para>
    /// </summary>
    public sealed class WebServerControl
    {
        private readonly object _gate = new object();
        private WebServer? _server;
        private string? _earlyStop;

        /// <summary>The server's address once it is listening, else "".</summary>
        public string Url => _server?.Url ?? "";

        /// <summary>True once a stop has begun.</summary>
        public bool Stopping => _server?.IsStopping ?? _earlyStop != null;

        /// <summary>Raised on a timer thread when the idle reminder is due, and again after every further idle period.</summary>
        public event Action<IdleReminderInfo>? IdleReminder;

        /// <summary>Raised when someone uses the page again after a reminder.</summary>
        public event Action? IdleEnded;

        /// <summary>
        /// Begins the graceful stop and returns at once. A stop asked for before the server has started
        /// listening is kept and applied the moment it has.
        /// </summary>
        public void BeginStop(string reason)
        {
            WebServer? s;
            lock (_gate)
            {
                s = _server;
                if (s == null)
                {
                    _earlyStop ??= reason;
                    return;
                }
            }

            _ = s.BeginStop(reason);
        }

        /// <summary>
        /// The same, and waits - for a signal handler whose process ends when it returns (a console window
        /// being closed, a shutdown). True when every search was saved and the registry file removed in
        /// time.
        /// </summary>
        public bool Stop(string reason, TimeSpan timeout)
        {
            WebServer? s;
            lock (_gate)
            {
                s = _server;
                if (s == null)
                {
                    _earlyStop ??= reason;
                    return true;
                }
            }

            try
            {
                return s.BeginStop(reason).Wait(timeout);
            }
            catch (AggregateException)
            {
                return false;
            }
        }

        /// <summary>The searches that are running now.</summary>
        public IReadOnlyList<SearchRunInfo> RunningSearches() =>
            _server?.RunningSearches() ?? (IReadOnlyList<SearchRunInfo>)Array.Empty<SearchRunInfo>();

        /// <summary>What the stop did, once it has; null before.</summary>
        public Task<ServerStopReport>? StopTask => _server?.StopTask;

        internal void Attach(WebServer server)
        {
            string? early;
            lock (_gate)
            {
                _server = server;
                early = _earlyStop;
            }

            if (early != null) _ = server.BeginStop(early);
        }

        internal void RaiseIdle(IdleReminderInfo info)
        {
            try
            {
                IdleReminder?.Invoke(info);
            }
            catch (Exception)
            {
                // A console that can no longer be written to must not stop the reminder.
            }
        }

        internal void RaiseIdleEnded()
        {
            try
            {
                IdleEnded?.Invoke();
            }
            catch (Exception)
            {
            }
        }

        /// <summary>"45 seconds", "60 minutes", "2 hours", "2 hours 30 minutes" - never "01:00:00".</summary>
        public static string Span(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalMinutes < 2)
            {
                long s = (long)Math.Floor(t.TotalSeconds);
                return s.ToString(CultureInfo.InvariantCulture) + " second" + (s == 1 ? "" : "s");
            }

            long minutes = (long)Math.Floor(t.TotalMinutes);
            if (minutes < 120) return minutes.ToString(CultureInfo.InvariantCulture) + " minutes";
            long h = minutes / 60, m = minutes % 60;
            return h.ToString(CultureInfo.InvariantCulture) + " hours"
                   + (m == 0 ? "" : " " + m.ToString(CultureInfo.InvariantCulture) + " minute" + (m == 1 ? "" : "s"));
        }

        /// <summary>"60 min", "2 h", "45 s" - for a window title, where there is no room.</summary>
        public static string Short(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalMinutes < 2) return ((long)Math.Floor(t.TotalSeconds)).ToString(CultureInfo.InvariantCulture) + " s";
            long minutes = (long)Math.Floor(t.TotalMinutes);
            if (minutes < 120) return minutes.ToString(CultureInfo.InvariantCulture) + " min";
            long h = minutes / 60, m = minutes % 60;
            return h.ToString(CultureInfo.InvariantCulture) + " h" + (m == 0 ? "" : " " + m.ToString(CultureInfo.InvariantCulture) + " min");
        }
    }
}
