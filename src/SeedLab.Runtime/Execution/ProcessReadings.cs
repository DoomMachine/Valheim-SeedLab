using System;
using System.Globalization;
using System.IO;

namespace SeedLab.Runtime.Execution
{
    /// <summary>
    /// This process's input and output since it started, as the operating system counts it. On
    /// Windows (<c>GetProcessIoCounters</c>) every read and write call counts - files, pipes, the
    /// console - and so do the bytes a cached read never fetched from a disk. On Linux
    /// (<c>/proc/self/io</c>) <c>rchar</c>/<c>wchar</c> are the same kind of figure and
    /// <c>read_bytes</c>/<c>write_bytes</c> are what reached the storage layer.
    /// </summary>
    public sealed class ProcessIoReading
    {
        public long ReadBytes { get; init; }
        public long WriteBytes { get; init; }
        public long? OtherBytes { get; init; }
        public long? ReadOps { get; init; }
        public long? WriteOps { get; init; }
        public long? OtherOps { get; init; }

        /// <summary>Linux only: bytes that reached (or would have reached) the storage layer.</summary>
        public long? StorageReadBytes { get; init; }

        public long? StorageWriteBytes { get; init; }

        /// <summary>Where the figures came from, e.g. "GetProcessIoCounters" or "/proc/self/io".</summary>
        public string Source { get; init; } = "";

        /// <summary>b - a, field by field; a field either side lacks is left out.</summary>
        public static ProcessIoReading Delta(ProcessIoReading a, ProcessIoReading b) => new ProcessIoReading
        {
            ReadBytes = b.ReadBytes - a.ReadBytes,
            WriteBytes = b.WriteBytes - a.WriteBytes,
            OtherBytes = Sub(b.OtherBytes, a.OtherBytes),
            ReadOps = Sub(b.ReadOps, a.ReadOps),
            WriteOps = Sub(b.WriteOps, a.WriteOps),
            OtherOps = Sub(b.OtherOps, a.OtherOps),
            StorageReadBytes = Sub(b.StorageReadBytes, a.StorageReadBytes),
            StorageWriteBytes = Sub(b.StorageWriteBytes, a.StorageWriteBytes),
            Source = b.Source,
        };

        private static long? Sub(long? b, long? a) => a.HasValue && b.HasValue ? b.Value - a.Value : null;
    }

    /// <summary>This process's memory as the operating system sees it, in bytes. A figure it did not give is null.</summary>
    public sealed class ProcessMemoryReading
    {
        public long? WorkingSet { get; init; }

        /// <summary>The largest working set since the process started (Windows PeakWorkingSetSize, Linux VmHWM).</summary>
        public long? PeakWorkingSet { get; init; }

        /// <summary>Memory committed to this process alone (Windows PrivateUsage, Linux RssAnon + VmSwap).</summary>
        public long? PrivateBytes { get; init; }

        /// <summary>The largest commit since the process started (Windows PeakPagefileUsage).</summary>
        public long? PeakPrivateBytes { get; init; }

        public long? PageFaults { get; init; }
        public string Source { get; init; } = "";
    }

    /// <summary>
    /// Linux's per-process files, read and parsed with the BCL (this layer makes no system calls of its
    /// own). The parsers take the file's text, so they can be checked on any machine.
    /// </summary>
    public static class ProcFiles
    {
        /// <summary><c>/proc/self/io</c> now, or null where there is no such file.</summary>
        public static ProcessIoReading? ReadSelfIo() => ReadText("/proc/self/io") is string t ? ParseIo(t) : null;

        /// <summary><c>/proc/self/status</c> now, or null where there is no such file.</summary>
        public static ProcessMemoryReading? ReadSelfStatus() => ReadText("/proc/self/status") is string t ? ParseStatus(t) : null;

        /// <summary>
        /// The calling thread's time on a processor from <c>/proc/thread-self/schedstat</c> (its first
        /// field, in nanoseconds), or null.
        /// </summary>
        public static TimeSpan? ReadThreadSchedstat() => ReadText("/proc/thread-self/schedstat") is string t ? ParseSchedstat(t) : null;

        /// <summary>Parses <c>/proc/self/io</c>: <c>rchar</c>, <c>wchar</c>, <c>syscr</c>, <c>syscw</c>, <c>read_bytes</c>, <c>write_bytes</c>.</summary>
        public static ProcessIoReading? ParseIo(string text)
        {
            long? rchar = null, wchar = null, syscr = null, syscw = null, rb = null, wb = null;
            foreach (string raw in text.Split('\n'))
            {
                int colon = raw.IndexOf(':');
                if (colon <= 0) continue;
                string name = raw.Substring(0, colon).Trim();
                if (!long.TryParse(raw.Substring(colon + 1).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v)) continue;
                switch (name)
                {
                    case "rchar": rchar = v; break;
                    case "wchar": wchar = v; break;
                    case "syscr": syscr = v; break;
                    case "syscw": syscw = v; break;
                    case "read_bytes": rb = v; break;
                    case "write_bytes": wb = v; break;
                }
            }

            if (!rchar.HasValue || !wchar.HasValue) return null;
            return new ProcessIoReading
            {
                ReadBytes = rchar.Value,
                WriteBytes = wchar.Value,
                ReadOps = syscr,
                WriteOps = syscw,
                StorageReadBytes = rb,
                StorageWriteBytes = wb,
                Source = "/proc/self/io",
            };
        }

        /// <summary>Parses <c>/proc/self/status</c>: <c>VmRSS</c>, <c>VmHWM</c>, <c>RssAnon</c> and <c>VmSwap</c>, given in kB.</summary>
        public static ProcessMemoryReading? ParseStatus(string text)
        {
            long? rss = null, hwm = null, anon = null, swap = null;
            foreach (string raw in text.Split('\n'))
            {
                int colon = raw.IndexOf(':');
                if (colon <= 0) continue;
                string name = raw.Substring(0, colon).Trim();
                if (name != "VmRSS" && name != "VmHWM" && name != "RssAnon" && name != "VmSwap") continue;
                string[] f = raw.Substring(colon + 1).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (f.Length < 1 || !long.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long kb)) continue;
                long bytes = kb * 1024;
                switch (name)
                {
                    case "VmRSS": rss = bytes; break;
                    case "VmHWM": hwm = bytes; break;
                    case "RssAnon": anon = bytes; break;
                    case "VmSwap": swap = bytes; break;
                }
            }

            if (!rss.HasValue && !anon.HasValue) return null;
            return new ProcessMemoryReading
            {
                WorkingSet = rss,
                PeakWorkingSet = hwm,
                PrivateBytes = anon.HasValue ? anon.Value + (swap ?? 0) : null,
                Source = "/proc/self/status",
            };
        }

        /// <summary>Parses <c>schedstat</c>'s first field, nanoseconds on a processor.</summary>
        public static TimeSpan? ParseSchedstat(string text)
        {
            string[] f = text.Split(new[] { ' ', '\t', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length < 1 || !long.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ns) || ns < 0) return null;
            return TimeSpan.FromTicks(ns / 100);
        }

        private static string? ReadText(string path)
        {
            try
            {
                if (!OperatingSystem.IsLinux() || !File.Exists(path)) return null;
                return File.ReadAllText(path);
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
