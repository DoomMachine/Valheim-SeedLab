using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace SeedLab.Cli.Infra
{
    /// <summary>A vseed process the server registry does not know about.</summary>
    public sealed class UnregisteredVseed
    {
        public int Pid { get; set; }

        public DateTime? StartedLocal { get; set; }

        /// <summary>Its command line, or null when it could not be read.</summary>
        public string? CommandLine { get; set; }
    }

    /// <summary>
    /// Another process's command line, on Windows (2026-09-24): for <c>vseed serve --status</c> and
    /// <c>--stop</c>, which also list a <c>vseed serve</c> the registry does not know - one started with
    /// another <c>--cache-dir</c>, or by a build from before the registry existed.
    ///
    /// <para><b>Why not WMI.</b> <c>Win32_Process.CommandLine</c> is the usual route, and it needs
    /// <c>System.Management</c>, a NuGet package - SeedLab takes none. <c>NtQueryInformationProcess</c>
    /// with <c>ProcessCommandLineInformation</c> (60, Windows 8.1 and later) returns the same string with
    /// only <c>PROCESS_QUERY_LIMITED_INFORMATION</c> access, which a user has on their own processes. It
    /// lives here, in the CLI, because the runtime layer takes no P/Invoke. Anything that fails is "could
    /// not be read", never an exception.</para>
    /// </summary>
    public static class ProcessCommandLine
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const int ProcessCommandLineInformation = 60;

        /// <summary>The command line of <paramref name="pid"/>, or null (not Windows, gone, not allowed).</summary>
        public static string? Of(int pid)
        {
            if (!OperatingSystem.IsWindows() || pid <= 0) return null;
            IntPtr h = IntPtr.Zero;
            try
            {
                h = OpenProcess(ProcessQueryLimitedInformation, false, (uint)pid);
                if (h == IntPtr.Zero) return null;
                NtQueryInformationProcess(h, ProcessCommandLineInformation, IntPtr.Zero, 0, out int size);
                if (size <= 0 || size > (1 << 20)) return null;
                IntPtr buf = Marshal.AllocHGlobal(size);
                try
                {
                    int status = NtQueryInformationProcess(h, ProcessCommandLineInformation, buf, size, out _);
                    if (status != 0) return null;
                    UnicodeString us = Marshal.PtrToStructure<UnicodeString>(buf);
                    if (us.Buffer == IntPtr.Zero || us.Length == 0) return "";
                    return Marshal.PtrToStringUni(us.Buffer, us.Length / 2);
                }
                finally
                {
                    Marshal.FreeHGlobal(buf);
                }
            }
            catch (Exception)
            {
                return null;
            }
            finally
            {
                if (h != IntPtr.Zero) CloseHandle(h);
            }
        }

        /// <summary>
        /// The <c>vseed</c> processes other than this one and <paramref name="known"/> whose command line
        /// says <c>serve</c> (and not <c>--status</c>, <c>--stop</c>, <c>--selftest</c> or <c>--help</c>) -
        /// or whose command line could not be read, which are listed as such rather than guessed about.
        /// Windows only; empty elsewhere.
        /// </summary>
        public static List<UnregisteredVseed> UnregisteredServers(ICollection<int> known)
        {
            List<UnregisteredVseed> found = new List<UnregisteredVseed>();
            if (!OperatingSystem.IsWindows()) return found;
            Process[] all;
            try
            {
                all = Process.GetProcessesByName("vseed");
            }
            catch (Exception)
            {
                return found;
            }

            foreach (Process p in all)
            {
                using (p)
                {
                    int pid;
                    try
                    {
                        pid = p.Id;
                    }
                    catch (Exception)
                    {
                        continue;
                    }

                    if (pid == Environment.ProcessId || known.Contains(pid)) continue;
                    string? cmd = Of(pid);
                    if (cmd != null && !IsServe(cmd)) continue;
                    DateTime? started = null;
                    try
                    {
                        started = p.StartTime;
                    }
                    catch (Exception)
                    {
                    }

                    found.Add(new UnregisteredVseed { Pid = pid, StartedLocal = started, CommandLine = cmd });
                }
            }

            return found;
        }

        /// <summary>True for "vseed serve ..." that starts a server, as opposed to asking about one.</summary>
        public static bool IsServe(string commandLine)
        {
            List<string> words = new List<string>(commandLine.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries));
            if (!words.Contains("serve")) return false;
            foreach (string w in words)
            {
                string x = w.Trim('"');
                if (x == "--status" || x == "--stop" || x == "--selftest" || x == "--help" || x == "-h") return false;
            }

            return true;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct UnicodeString
        {
            public ushort Length;
            public ushort MaximumLength;
            public IntPtr Buffer;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("ntdll.dll")]
        private static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);
    }
}
