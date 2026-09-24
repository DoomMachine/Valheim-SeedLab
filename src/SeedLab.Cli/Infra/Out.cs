using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

namespace SeedLab.Cli.Infra
{
    /// <summary>
    /// Console output. Two modes: a human layout, and <c>--json</c>, which writes one JSON document to
    /// stdout and nothing else - so every command that produces data can be piped into jq.
    /// Progress and warnings always go to stderr, so they never corrupt the JSON on stdout.
    /// </summary>
    public sealed class Out : IDisposable
    {
        private readonly TextWriter _w;
        private readonly Utf8JsonWriter? _json;
        private readonly MemoryStream? _buf;

        public Out(bool json)
        {
            Json = json;
            _w = Console.Out;
            if (json)
            {
                _buf = new MemoryStream();
                _json = new Utf8JsonWriter(_buf, new JsonWriterOptions { Indented = true, SkipValidation = false });
            }
        }

        public bool Json { get; }

        public Utf8JsonWriter J => _json ?? throw new InvalidOperationException("Not in --json mode.");

        // ---- human output ----------------------------------------------------------------------
        public void Line(string s = "")
        {
            if (!Json) _w.WriteLine(s);
        }

        public void Header(string s)
        {
            if (Json) return;
            _w.WriteLine();
            _w.WriteLine(s);
            _w.WriteLine(new string('-', Math.Min(78, s.Length + 12)));
        }

        /// <summary>A "  label   value" row with the labels padded to a common width.</summary>
        public void Field(string label, string value, int pad = 22)
        {
            if (Json) return;

            // PadRight returns a label that is already too long unchanged, which then ran straight
            // into its own value ("pass rate on this sample6 / 16"). A long label costs a ragged
            // column; it must never cost the space.
            string padded = label.PadRight(pad);
            if (padded.Length > pad) padded += "  ";
            _w.WriteLine("  " + padded + value);
        }

        public void Note(string s)
        {
            if (Json) return;
            _w.WriteLine("  " + s);
        }

        /// <summary>
        /// Warnings and progress; always stderr so --json stdout stays clean. Both also go to the
        /// session log (2026-09-24), so the log holds what the user was told - a warning that scrolled
        /// away is still there to be read, or handed to someone who can help.
        /// </summary>
        public static void Warn(string s)
        {
            Console.Error.WriteLine("warning: " + s);
            SeedLab.Runtime.Storage.SessionLog.Current?.Warn("printed  warning: " + s);
        }

        public static void Info(string s)
        {
            Console.Error.WriteLine(s);
            if (!string.IsNullOrWhiteSpace(s)) SeedLab.Runtime.Storage.SessionLog.Current?.Info("printed  " + s.Trim());
        }

        /// <summary>
        /// A line that already carries its own "warning: " or "error: " (the data stamp's, a refusal's)
        /// on stderr, and into the session log at <paramref name="level"/>.
        /// </summary>
        public static void Said(string s, SeedLab.Runtime.Storage.SessionLogLevel level)
        {
            Console.Error.WriteLine(s);
            if (!string.IsNullOrWhiteSpace(s)) SeedLab.Runtime.Storage.SessionLog.Current?.Write(level, "printed  " + s.Trim());
        }

        /// <summary>A simple column table with right-aligned numeric columns.</summary>
        public void Table(IReadOnlyList<string> headers, IReadOnlyList<string[]> rows, bool[]? rightAlign = null)
        {
            if (Json) return;
            int n = headers.Count;
            int[] w = new int[n];
            for (int i = 0; i < n; i++) w[i] = headers[i].Length;
            foreach (string[] r in rows)
            {
                for (int i = 0; i < n && i < r.Length; i++) w[i] = Math.Max(w[i], r[i].Length);
            }

            StringBuilder sb = new StringBuilder();
            sb.Append("  ");
            for (int i = 0; i < n; i++)
            {
                bool ra = rightAlign != null && i < rightAlign.Length && rightAlign[i];
                sb.Append(ra ? headers[i].PadLeft(w[i]) : headers[i].PadRight(w[i]));
                if (i < n - 1) sb.Append("  ");
            }

            _w.WriteLine(sb.ToString().TrimEnd());
            sb.Clear();
            sb.Append("  ");
            for (int i = 0; i < n; i++)
            {
                sb.Append(new string('-', w[i]));
                if (i < n - 1) sb.Append("  ");
            }

            _w.WriteLine(sb.ToString());

            foreach (string[] r in rows)
            {
                sb.Clear();
                sb.Append("  ");
                for (int i = 0; i < n; i++)
                {
                    string cell = i < r.Length ? r[i] : "";
                    bool ra = rightAlign != null && i < rightAlign.Length && rightAlign[i];
                    sb.Append(ra ? cell.PadLeft(w[i]) : cell.PadRight(w[i]));
                    if (i < n - 1) sb.Append("  ");
                }

                _w.WriteLine(sb.ToString().TrimEnd());
            }
        }

        /// <summary>
        /// Emits whatever has been buffered since the last flush. <b>Idempotent.</b> Program.Main flushes
        /// after every command and some commands also flush before returning; replaying the whole buffer
        /// each time printed the JSON document twice, which is not JSON any parser will read - and the
        /// contract above is "one JSON document to stdout and nothing else".
        /// </summary>
        public void Flush()
        {
            if (_json != null && _buf != null)
            {
                _json.Flush();
                if (_buf.Length <= _emitted) return;
                using Stream stdout = Console.OpenStandardOutput();
                _buf.Position = _emitted;
                _buf.CopyTo(stdout);
                _emitted = _buf.Length;
                stdout.Write(new byte[] { (byte)'\n' }, 0, 1);
                stdout.Flush();
            }
            else
            {
                _w.Flush();
            }
        }

        /// <summary>Bytes of the JSON buffer already written to stdout.</summary>
        private long _emitted;

        public void Dispose()
        {
            _json?.Dispose();
            _buf?.Dispose();
        }

        // ---- formatting helpers ----------------------------------------------------------------
        public static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);

        public static string F(double v, int digits = 2) => v.ToString("F" + digits, CultureInfo.InvariantCulture);

        public static string Metres(double m) => F(m, 1) + " m";

        public static string Km2(double m2) => F(m2 / 1e6, 2) + " km2";

        public static string Pct(double fraction) => F(100.0 * fraction, 2) + " %";

        /// <summary>Compass bearing from the origin, north = +z, clockwise, plus the 16-point name.</summary>
        public static string Bearing(double degrees)
        {
            string[] pts = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE",
                             "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };
            int i = (int)Math.Round(((degrees % 360) + 360) % 360 / 22.5) % 16;
            return F(((degrees % 360) + 360) % 360, 1) + " deg " + pts[i];
        }
    }
}
