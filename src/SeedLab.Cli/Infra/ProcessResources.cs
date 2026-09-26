using System;
using System.Runtime.InteropServices;
using SeedLab.Runtime.Execution;

namespace SeedLab.Cli.Infra
{
    /// <summary>
    /// This process's own resource counters for <c>vseed profile</c>: the calling thread's processor
    /// time, the process's input and output, and its memory as the operating system sees it.
    ///
    /// <para><b>Why here.</b> Each needs a system call on Windows, and the runtime layer, the generator
    /// and the placement code must never P/Invoke (the numerics tripwire enforces it), so the calls live
    /// in the CLI beside <see cref="MachineCpu"/>. Linux's per-process files are read by the runtime
    /// layer's <see cref="ProcFiles"/>; the Linux thread clock is a libc call made here. Every reader
    /// returns null rather than throwing when a figure is not available - a figure that was not read is
    /// reported as absent, never as 0.</para>
    ///
    /// <para><b>Resolution.</b> Windows charges thread and process time in scheduler-tick steps of about
    /// 15.6 ms. That is fine over a worker's whole share of a section (seconds), not for one seed.</para>
    /// </summary>
    internal static class ProcessResources
    {
        private const string LinuxClock = "clock_gettime(CLOCK_THREAD_CPUTIME_ID) on the worker thread itself";
        private const string LinuxSchedstat = "/proc/thread-self/schedstat on the worker thread itself (clock_gettime was not available)";

        private static volatile string? s_linuxSource;
        private static volatile bool s_clockBroken;

        /// <summary>
        /// Where the thread CPU figure came from on this machine - the source actually used (on Linux,
        /// the libc clock or, when that call is missing, schedstat) - or null when there is none.
        /// </summary>
        public static string? ThreadCpuSource =>
            OperatingSystem.IsWindows() ? "GetThreadTimes on the worker thread itself"
            : OperatingSystem.IsLinux() ? s_linuxSource ?? LinuxClock
            : null;

        /// <summary>The calling thread's user plus kernel processor time since it started, or null.</summary>
        public static TimeSpan? ThreadCpu()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (!GetThreadTimes(GetCurrentThread(), out long _, out long _, out long kernel, out long user)) return null;
                    return TimeSpan.FromTicks(kernel + user);   // FILETIME units are 100 ns, as TimeSpan ticks
                }

                if (OperatingSystem.IsLinux())
                {
                    // The libc call alone may throw (no "libc" to load, no entry point): then, and when it
                    // fails, the kernel's schedstat file is the answer - never "not measured" while it exists.
                    if (IntPtr.Size == 8 && !s_clockBroken)
                    {
                        try
                        {
                            if (clock_gettime(ClockThreadCpuTimeId, out Timespec ts) == 0)
                            {
                                s_linuxSource = LinuxClock;
                                return TimeSpan.FromTicks(ts.Seconds * TimeSpan.TicksPerSecond + ts.Nanoseconds / 100);
                            }
                        }
                        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
                        {
                            s_clockBroken = true;
                        }
                    }

                    TimeSpan? sched = ProcFiles.ReadThreadSchedstat();
                    if (sched.HasValue) s_linuxSource = LinuxSchedstat;
                    return sched;
                }
            }
            catch (Exception)
            {
                // Not available.
            }

            return null;
        }

        /// <summary>The process's input and output since it started, or null.</summary>
        public static ProcessIoReading? Io()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    if (!GetProcessIoCounters(GetCurrentProcess(), out IoCounters c)) return null;
                    return new ProcessIoReading
                    {
                        ReadBytes = (long)c.ReadTransferCount,
                        WriteBytes = (long)c.WriteTransferCount,
                        OtherBytes = (long)c.OtherTransferCount,
                        ReadOps = (long)c.ReadOperationCount,
                        WriteOps = (long)c.WriteOperationCount,
                        OtherOps = (long)c.OtherOperationCount,
                        Source = "GetProcessIoCounters (every read and write call: files, pipes and the console)",
                    };
                }

                if (OperatingSystem.IsLinux()) return ProcFiles.ReadSelfIo();
            }
            catch (Exception)
            {
                // Not available.
            }

            return null;
        }

        /// <summary>The process's memory now, with its lifetime peaks, or null.</summary>
        public static ProcessMemoryReading? Memory()
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    MemoryCountersEx m = default;
                    m.cb = (uint)Marshal.SizeOf<MemoryCountersEx>();
                    if (!GetProcessMemoryInfo(GetCurrentProcess(), ref m, m.cb)) return null;
                    return new ProcessMemoryReading
                    {
                        WorkingSet = (long)m.WorkingSetSize,
                        PeakWorkingSet = (long)m.PeakWorkingSetSize,
                        PrivateBytes = (long)m.PrivateUsage,
                        PeakPrivateBytes = (long)m.PeakPagefileUsage,
                        PageFaults = m.PageFaultCount,
                        Source = "GetProcessMemoryInfo",
                    };
                }

                if (OperatingSystem.IsLinux()) return ProcFiles.ReadSelfStatus();
            }
            catch (Exception)
            {
                // Not available.
            }

            return null;
        }

        /// <summary>Private bytes alone, for the sampler; null when not available.</summary>
        public static long? PrivateBytes() => Memory()?.PrivateBytes;

        /// <summary>The sampler's readers: the BCL's, plus private bytes and the thread clock from here.</summary>
        public static ResourceReaders SamplerReaders() => ResourceReaders.Bcl(PrivateBytes, ThreadCpu);

        // ---- Windows --------------------------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MemoryCountersEx
        {
            public uint cb;
            public uint PageFaultCount;
            public UIntPtr PeakWorkingSetSize;
            public UIntPtr WorkingSetSize;
            public UIntPtr QuotaPeakPagedPoolUsage;
            public UIntPtr QuotaPagedPoolUsage;
            public UIntPtr QuotaPeakNonPagedPoolUsage;
            public UIntPtr QuotaNonPagedPoolUsage;
            public UIntPtr PagefileUsage;
            public UIntPtr PeakPagefileUsage;
            public UIntPtr PrivateUsage;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentThread();

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetCurrentProcess();

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetThreadTimes(IntPtr thread, out long creation, out long exit, out long kernel, out long user);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessIoCounters(IntPtr process, out IoCounters counters);

        [DllImport("kernel32.dll", SetLastError = true, EntryPoint = "K32GetProcessMemoryInfo")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetProcessMemoryInfo(IntPtr process, ref MemoryCountersEx counters, uint size);

        // ---- Linux ----------------------------------------------------------------------------------

        private const int ClockThreadCpuTimeId = 3;

        [StructLayout(LayoutKind.Sequential)]
        private struct Timespec
        {
            public long Seconds;
            public long Nanoseconds;
        }

        [DllImport("libc", EntryPoint = "clock_gettime")]
        private static extern int clock_gettime(int clock, out Timespec time);
    }
}
