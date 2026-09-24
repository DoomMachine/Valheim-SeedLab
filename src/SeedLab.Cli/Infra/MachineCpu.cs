using System;
using System.Runtime.InteropServices;
using SeedLab.Runtime.Execution;

namespace SeedLab.Cli.Infra
{
    /// <summary>
    /// The whole machine's busy processor time, for the quiet-machine probe.
    ///
    /// <para><b>Why it is here and not in the runtime layer.</b> On Windows about a third of the
    /// processes will not tell a normal account their CPU time - the antivirus, the search indexer,
    /// Windows Update, memory compression - and those are exactly the ones that wake up and burn a core
    /// while a timing runs. Only the machine's own total (<c>GetSystemTimes</c>) sees them, and that is
    /// a system call. The runtime layer, the generator and the placement code must never P/Invoke (the
    /// numerics tripwire enforces it), so the one call lives in the CLI and is handed to the probe.
    /// Elsewhere the runtime layer reads Linux's <c>/proc/stat</c> itself.</para>
    /// </summary>
    internal static class MachineCpu
    {
        /// <summary>The reader to hand to <see cref="QuietMachineProbe"/>.</summary>
        public static MachineCpuReader? Reader => OperatingSystem.IsWindows() ? ReadWindows : MachineCpuTime.Default;

        /// <summary>
        /// (kernel - idle) + user, summed over every logical processor, since the machine started.
        /// Kernel time includes idle time in this call, which is why idle is taken out of it.
        /// </summary>
        private static TimeSpan? ReadWindows()
        {
            try
            {
                if (!GetSystemTimes(out long idle, out long kernel, out long user)) return null;
                long busy = kernel - idle + user;
                return busy >= 0 ? TimeSpan.FromTicks(busy) : null;   // FILETIME units are 100 ns, as TimeSpan ticks
            }
            catch (Exception)
            {
                return null;
            }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);
    }
}
