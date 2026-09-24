using System;
using System.Diagnostics;

namespace SeedLab.Runtime.Storage
{
    /// <summary>
    /// "Is process N still alive, and is it the same process that wrote this?" - the question the
    /// scratch reaper has to answer before deleting someone else's working directory.
    ///
    /// <para>A pid on its own is not enough: operating systems reuse them, and deleting a live run's
    /// scratch because an unrelated process inherited its number would be a data-loss bug. So the owner
    /// file records the pid AND the process start time, and both must match.</para>
    /// </summary>
    public static class ProcessLiveness
    {
        public static bool IsAlive(int pid)
        {
            if (pid <= 0) return false;
            try
            {
                using Process p = Process.GetProcessById(pid);
                return !p.HasExited;
            }
            catch (ArgumentException) { return false; }   // no such process
            catch (InvalidOperationException) { return false; }
            catch (Exception) { return true; }            // cannot tell: assume alive, never delete on a guess
        }

        /// <summary>UTC start time of a live process, or null when it is gone or will not say.</summary>
        public static DateTime? StartTimeUtc(int pid)
        {
            if (pid <= 0) return null;
            try
            {
                using Process p = Process.GetProcessById(pid);
                if (p.HasExited) return null;
                return p.StartTime.ToUniversalTime();
            }
            catch (Exception) { return null; }
        }

        /// <summary>
        /// True when pid is alive AND started at the recorded time (to the second - the BCL's start time
        /// round-trips through text). An unreadable start time falls back to "alive", which errs towards
        /// keeping a directory rather than deleting a live one's.
        /// </summary>
        public static bool IsSameProcess(int pid, DateTime startedUtc)
        {
            if (!IsAlive(pid)) return false;
            DateTime? actual = StartTimeUtc(pid);
            if (actual == null) return true;
            return Math.Abs((actual.Value - startedUtc).TotalSeconds) < 1.5;
        }

        /// <summary>
        /// The same question, biased the other way: true only when pid is alive AND its start time can be
        /// read AND it is the recorded one. For a file that says "a server is running here" - the web
        /// servers' registry - where a wrong "alive" locks the user out rather than protecting anything.
        ///
        /// <para><b>Why the scratch reaper's bias is wrong there</b> (review of 2026-09-25). A registry file
        /// left by a server that was ended without cleaning up (Task Manager, a power cut) names a pid the
        /// operating system later gives to something else - after a reboot, often a system process whose
        /// start time this account cannot read. <see cref="IsSameProcess"/> called that "alive", so the
        /// next <c>vseed serve</c> said "already running" and started nothing, <c>--stop</c> could not stop
        /// it and the uninstall refused - with nothing naming the file. The user's own vseed always has a
        /// start time the user can read, so "cannot read it" means "not ours".</para>
        /// </summary>
        public static bool IsSameProcessStrict(int pid, DateTime startedUtc)
        {
            DateTime? actual = StartTimeUtc(pid);
            if (actual == null) return false;
            return Math.Abs((actual.Value - startedUtc).TotalSeconds) < 1.5;
        }

        public static int CurrentPid => Environment.ProcessId;

        public static DateTime CurrentStartTimeUtc
        {
            get
            {
                try
                {
                    using Process p = Process.GetCurrentProcess();
                    return p.StartTime.ToUniversalTime();
                }
                catch (Exception)
                {
                    return DateTime.UtcNow;
                }
            }
        }
    }
}
