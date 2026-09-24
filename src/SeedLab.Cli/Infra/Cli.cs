using System;
using System.Collections.Generic;
using System.Globalization;

namespace SeedLab.Cli.Infra
{
    /// <summary>
    /// The process exit codes, fixed so scripts can branch on them.
    /// </summary>
    public static class ExitCodes
    {
        /// <summary>Everything asked for was produced.</summary>
        public const int Ok = 0;

        /// <summary>A check the tool ran came out negative (selftest mismatch, verification failure).</summary>
        public const int CheckFailed = 1;

        /// <summary>The command line was wrong. A usage message was printed, never a stack trace.</summary>
        public const int Usage = 2;

        /// <summary>Something the command needed was not on disk (a world, a save file, the game install).</summary>
        public const int NotFound = 3;

        /// <summary>An unexpected fault. Re-run with --debug for the stack trace.</summary>
        public const int Internal = 4;
    }

    /// <summary>A user-facing error: printed as one line, never as a stack trace.</summary>
    public sealed class CliException : Exception
    {
        public CliException(string message, int exitCode = ExitCodes.Usage, string? hint = null)
            : base(message)
        {
            ExitCode = exitCode;
            Hint = hint;
        }

        public int ExitCode { get; }

        /// <summary>One line of "try this instead", printed under the error.</summary>
        public string? Hint { get; }
    }

    /// <summary>
    /// A small, strict option parser: <c>--name value</c>, <c>--name=value</c>, <c>--flag</c>,
    /// <c>--no-flag</c>, single-dash aliases, and positionals. Unknown options are an error rather
    /// than being ignored, because a silently dropped option is how a wrong number gets printed.
    /// </summary>
    public sealed class Args
    {
        private readonly Dictionary<string, string?> _opts = new Dictionary<string, string?>(StringComparer.Ordinal);
        private readonly List<string> _positional = new List<string>();
        private readonly HashSet<string> _used = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>
        /// The options <c>vseed --help</c> calls global, which are therefore accepted on either side
        /// of the command name. Everything else belongs to one command and stays where it was typed.
        /// </summary>
        private static readonly HashSet<string> GlobalFlags =
            new HashSet<string>(StringComparer.Ordinal)
            {
                "json", "no-json",
                // The runtime layer's switches. They belong to every command that does real work, so
                // they are global rather than repeated per command - and a command that ignores one
                // still has to ACCEPT it, or RejectUnknown would call it a typo.
                "ignore-running-game", "skip-self-test", "accept-unverified-platform",
            };

        private static readonly HashSet<string> GlobalValued =
            new HashSet<string>(StringComparer.Ordinal) { "threads", "mode", "cache-dir", "simd" };

        /// <summary>
        /// The largest <c>--threads</c> this machine will accept. Four times the core count (never
        /// under 64) is already far past any useful oversubscription; above it the number would be a
        /// claim about a thread count nothing is going to run.
        /// </summary>
        public static int MaxThreads => Math.Max(64, 4 * Math.Max(1, Environment.ProcessorCount));

        public static bool IsGlobalOption(string token)
        {
            if (token.Length < 2 || token[0] != '-' || token == "--") return false;
            string name = token.TrimStart('-');
            int eq = name.IndexOf('=');
            if (eq >= 0) name = name.Substring(0, eq);
            return GlobalFlags.Contains(name) || GlobalValued.Contains(name);
        }

        /// <summary>True when the token is a global option written as <c>--name</c>, so its value is the next token.</summary>
        public static bool GlobalOptionTakesValue(string token)
        {
            string name = token.TrimStart('-');
            return name.IndexOf('=') < 0 && GlobalValued.Contains(name);
        }

        public Args(IEnumerable<string> argv)
        {
            string? pending = null;
            bool endOfOptions = false;
            foreach (string a in argv)
            {
                if (endOfOptions) { _positional.Add(a); continue; }

                bool looksLikeOption = a.Length > 1 && a[0] == '-' && !IsNegativeNumber(a) && a != "--";

                if (pending != null)
                {
                    // A pending option only swallows the next token if that token is not itself an
                    // option: "--grid --json" must leave --grid a flag, not give it the value "--json".
                    if (!looksLikeOption && a != "--")
                    {
                        _opts[pending] = a;
                        pending = null;
                        continue;
                    }

                    pending = null;
                }

                if (a == "--") { endOfOptions = true; continue; }

                if (looksLikeOption)
                {
                    string name = a.TrimStart('-');
                    int eq = name.IndexOf('=');
                    if (eq >= 0)
                    {
                        _opts[name.Substring(0, eq)] = name.Substring(eq + 1);
                    }
                    else
                    {
                        // Value-less for now; a later Get() may consume the next token.
                        _opts[name] = null;
                        pending = name;
                    }

                    continue;
                }

                _positional.Add(a);
            }
        }

        private static bool IsNegativeNumber(string s)
            => s.Length > 1 && s[0] == '-' && (char.IsDigit(s[1]) || (s[1] == '.' && s.Length > 2));

        public IReadOnlyList<string> Positional => _positional;

        public bool Has(params string[] names)
        {
            foreach (string n in names)
            {
                if (_opts.ContainsKey(n)) { _used.Add(n); return true; }
            }

            return false;
        }

        /// <summary>A boolean flag: <c>--x</c> is true, <c>--no-x</c> is false, absent gives the default.</summary>
        public bool Flag(string name, bool fallback = false)
        {
            if (_opts.TryGetValue(name, out string? v))
            {
                _used.Add(name);
                if (v == null || v.Length == 0) return true;
                if (bool.TryParse(v, out bool b)) return b;
                if (v == "1" || v == "yes" || v == "on") return true;
                if (v == "0" || v == "no" || v == "off") return false;
                throw new CliException($"--{name} takes yes/no, not '{v}'.");
            }

            if (_opts.ContainsKey("no-" + name)) { _used.Add("no-" + name); return false; }
            return fallback;
        }

        public string? Get(string name, string? alias = null)
        {
            foreach (string n in alias == null ? new[] { name } : new[] { name, alias })
            {
                if (_opts.TryGetValue(n, out string? v))
                {
                    _used.Add(n);
                    if (v == null) throw new CliException($"--{n} needs a value.");
                    return v;
                }
            }

            return null;
        }

        public string Require(string name, string? alias = null)
            => Get(name, alias) ?? throw new CliException($"--{name} is required.");

        public int Int(string name, int fallback, string? alias = null)
        {
            string? v = Get(name, alias);
            if (v == null) return fallback;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                throw new CliException($"--{name} takes a whole number, not '{v}'.");
            }

            return n;
        }

        public double Double(string name, double fallback, string? alias = null)
        {
            string? v = Get(name, alias);
            if (v == null) return fallback;
            // double.TryParse accepts "NaN" and "Infinity" on .NET Core whatever NumberStyles says,
            // and a non-finite value propagates silently into every comparison downstream
            // (NaN > 0 is false, so range checks pass it), printing nonsense with exit code 0.
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                || !double.IsFinite(d))
            {
                throw new CliException($"--{name} takes a finite number, not '{v}'.");
            }

            return d;
        }

        /// <summary>
        /// <c>--threads</c> as the user gave it, or null when they did not give it.
        ///
        /// <para>Null is the important case. "The user did not choose" now means "let <c>--mode</c> and the memory guard choose"
        /// (<c>SeedLab.Runtime.RuntimeContext.PlanWorkers</c>), not "take every logical core". A
        /// command that silently took every core on a machine that is also running the game was the
        /// behaviour decision 7 replaced.</para>
        ///
        /// <para>0, a negative and a number past <see cref="MaxThreads"/> are all refused here rather
        /// than clamped: each was accepted and echoed back before, and none of them ever ran.</para>
        /// </summary>
        public int? ThreadsRequest()
        {
            int cores = Math.Max(1, Environment.ProcessorCount);
            string? v = Get("threads");
            if (v == null) return null;

            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                throw new CliException($"--threads takes a whole number, not '{v}'.");
            }

            if (n < 1)
            {
                throw new CliException($"--threads must be at least 1, not {n}.", ExitCodes.Usage,
                    $"omit --threads to let --mode choose (balanced = about half of the {cores} logical cores here)");
            }

            if (n > MaxThreads)
            {
                throw new CliException(
                    $"--threads is capped at {MaxThreads} here; {n} threads would not run.", ExitCodes.Usage,
                    $"this machine has {cores} logical cores; omit --threads to let --mode choose");
            }

            return n;
        }

        /// <summary>
        /// Reads and validates every global option without using any of them, for the commands that do
        /// no parallel work and open no cache. Without this a documented global - <c>--mode</c>,
        /// <c>--cache-dir</c>, <c>--skip-self-test</c> - typed on <c>vseed hash</c> would reach
        /// <see cref="RejectUnknown"/> and be reported as a typo, and a misspelt VALUE
        /// (<c>--mode ful</c>) would be accepted in silence.
        /// </summary>
        public void ConsumeGlobals()
        {
            Flag("json");
            ThreadsRequest();
            Flag("ignore-running-game");
            Flag("skip-self-test");
            Flag("accept-unverified-platform");
            Get("cache-dir");
            Get("simd");          // applied by Main before any generator type loaded; validated there
            string? mode = Get("mode");
            if (mode != null && !SeedLab.Runtime.Execution.ResourceModes.TryParse(mode, out _, out string err))
            {
                throw new CliException("--mode: " + err, ExitCodes.Usage);
            }
        }

        /// <summary>Every option the command never looked at. Anything left is a typo or a wrong command.</summary>
        public IEnumerable<string> Unused()
        {
            foreach (KeyValuePair<string, string?> kv in _opts)
            {
                if (!_used.Contains(kv.Key)) yield return kv.Key;
            }
        }

        public void RejectUnknown()
        {
            List<string> bad = new List<string>(Unused());
            if (bad.Count == 0) return;
            throw new CliException(
                "unknown option" + (bad.Count > 1 ? "s" : "") + ": --" + string.Join(", --", bad),
                ExitCodes.Usage,
                "run 'vseed <command> --help' for the options this command takes");
        }
    }
}
