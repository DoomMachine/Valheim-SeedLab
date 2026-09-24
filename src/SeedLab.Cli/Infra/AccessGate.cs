using System;
using System.Collections.Generic;
using System.IO;
using SeedLab.Cli.Commands;
using SeedLab.Runtime.Storage;

namespace SeedLab.Cli.Infra
{
    /// <summary>
    /// The files and folders one command is about to use, checked before it spends any time, and what
    /// happens when one of them fails - the same rule the run's confirmations follow.
    ///
    /// <para><b>Why</b> (2026-09-24). A search died an hour in with "Access to the path is denied." -
    /// no file named, no cause. The retries in <see cref="FileRetry"/> are what survive a virus scanner
    /// or a sync tool opening a file for a moment; this is for the conditions that LAST - a results
    /// file a spreadsheet has open, a read-only file, a folder this account may not write - which are
    /// better found before the first seed than at the first save. A check that passes is a fact about
    /// that moment and no more: it cannot stop another program opening the file later.</para>
    ///
    /// <para><b>What a failure does.</b> In a terminal someone is watching, the diagnosis and
    /// "[r]etry / [a]bort": retry checks again, so closing the program that held the file and pressing
    /// r is the whole fix. With <c>--json</c> or stdin redirected nobody can answer, so the command is
    /// refused (exit 3) with the diagnosis - never a prompt that hangs a script. <c>--dry-run</c> says
    /// what it found and goes on, because it exists to answer "what would this do".</para>
    ///
    /// <para>Nothing here leaves anything behind: a file is opened without truncation and closed, a
    /// folder is probed with a temp file that deletes itself on close, and a folder that does not exist
    /// yet is not created - the one above it is probed instead.</para>
    /// </summary>
    public sealed class AccessGate
    {
        private readonly CliRuntime _rt;
        private readonly List<Func<AccessResult?>> _checks = new List<Func<AccessResult?>>();
        private List<AccessResult> _results = new List<AccessResult>();

        public AccessGate(CliRuntime rt)
        {
            _rt = rt ?? throw new ArgumentNullException(nameof(rt));
        }

        /// <summary>The latest outcome of every check, in the order they were added.</summary>
        public IReadOnlyList<AccessResult> Results => _results;

        public bool Ok => _results.TrueForAll(r => r.Ok);

        /// <summary>A file the command will write or replace (it need not exist yet).</summary>
        public AccessGate Write(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) _checks.Add(() => AccessCheck.FileForWrite(path!));
            return this;
        }

        /// <summary>A file the command will read. Checked only when it is there: a missing one is the command's own business.</summary>
        public AccessGate ReadIfPresent(string? path)
        {
            if (!string.IsNullOrWhiteSpace(path)) _checks.Add(() => File.Exists(path) ? AccessCheck.FileForRead(path!) : null);
            return this;
        }

        /// <summary>A folder the command will create files in. Created only when <paramref name="create"/> says so.</summary>
        public AccessGate Folder(string? path, bool create = false)
        {
            if (!string.IsNullOrWhiteSpace(path)) _checks.Add(() => AccessCheck.Directory(path!, create));
            return this;
        }

        /// <summary>The folder a file lives in (not created).</summary>
        public AccessGate FolderOf(string? filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return this;
            string? dir = null;
            try
            {
                dir = Path.GetDirectoryName(Path.GetFullPath(filePath!));
            }
            catch (Exception)
            {
            }

            return Folder(dir);
        }

        /// <summary>Runs every check (again), logs the outcome, and returns this.</summary>
        public AccessGate Run()
        {
            List<AccessResult> results = new List<AccessResult>();
            foreach (Func<AccessResult?> c in _checks)
            {
                AccessResult? r = c();
                if (r != null) results.Add(r);
            }

            _results = results;
            _rt.Log.Write(Ok ? SessionLogLevel.Info : SessionLogLevel.Warn, "access   " + AccessCheck.Summary(_results));
            return this;
        }

        /// <summary>
        /// "file access checked: 9 paths OK; integrity confirmed (...)" - the start-of-session line the
        /// startup block and the log carry. Counts the cache root's own checks (made when the session
        /// started) together with this command's.
        /// </summary>
        public string Summary()
        {
            List<AccessResult> all = new List<AccessResult>(_rt.Context.AccessChecks);
            all.AddRange(_results);
            return AccessCheck.Summary(all) + "; " + _rt.IntegritySummary();
        }

        /// <summary>
        /// Acts on the checks: null to go on, or the exit code to stop with. See the class comment for
        /// the three cases. <paramref name="nothingDone"/> is the sentence that ends a refusal ("Nothing
        /// was scanned and nothing was written.").
        /// </summary>
        public int? Enforce(string command, bool json, bool dryRun, string nothingDone)
        {
            if (Ok) return null;

            // The same test decides what a real run does and what --dry-run says it would do: it used to
            // say "would stop here and ask" to a script whose real run is refused (review of 2026-09-24).
            bool nobodyToAsk = json || Console.IsInputRedirected;
            if (dryRun)
            {
                Console.Error.WriteLine();
                foreach (AccessResult r in Failures()) Out.Warn(SearchCommand.WrapPaths(r.Message, "         ", new[] { r.Path }));
                Out.Info(nobodyToAsk
                    ? "  --dry-run does not stop for this; a real run would be refused here (exit 3), because nothing is "
                      + "reading the keyboard" + (json ? " (--json)." : " (stdin is redirected).")
                    : "  --dry-run does not stop for this; a real run would stop here and ask.");
                return null;
            }

            if (nobodyToAsk)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("vseed " + command + ": REFUSED - SeedLab cannot use a file this run needs.");
                foreach (AccessResult r in Failures()) Console.Error.WriteLine("  " + SearchCommand.WrapPaths(r.Message, "  ", new[] { r.Path }));
                Console.Error.WriteLine();
                Console.Error.WriteLine("  Nothing is reading the keyboard" + (json ? " (--json)" : " (stdin is redirected)")
                                        + ", so this is not asked. Fix it and run the command again.");
                Console.Error.WriteLine("  " + nothingDone);
                _rt.Log.Error("refused  vseed " + command + ": a file or folder this run needs failed its access check (exit "
                              + ExitCodes.NotFound + ")");
                return ExitCodes.NotFound;
            }

            while (true)
            {
                Console.Error.WriteLine();
                Console.Error.WriteLine("SeedLab cannot use a file this run needs:");
                foreach (AccessResult r in Failures()) Console.Error.WriteLine("  " + SearchCommand.WrapPaths(r.Message, "  ", new[] { r.Path }));
                Console.Error.WriteLine();
                Console.Error.WriteLine("  Fix that, then type r and press Enter to check again - or a to stop here.");

                string answer = Ask("[r]etry / [a]bort? ", "r", "a");
                if (answer != "r")
                {
                    Console.Error.WriteLine("not started. " + nothingDone);
                    _rt.Log.Warn("refused  vseed " + command + ": stopped at the access check by the user");
                    return ExitCodes.NotFound;
                }

                _rt.Log.Info("access   checking again, as the user asked");
                Run();
                if (Ok)
                {
                    Out.Info(AccessCheck.Summary(_results) + " (checked again)");
                    return null;
                }
            }
        }

        private IEnumerable<AccessResult> Failures()
        {
            foreach (AccessResult r in _results)
            {
                if (!r.Ok) yield return r;
            }
        }

        /// <summary>
        /// A one-letter question at the terminal. Returns the first letter of an answer that is one of
        /// <paramref name="letters"/>; an answer that is none of them is asked again (a stray Enter must
        /// not decide anything); the end of input is the LAST letter - the answer that does nothing. A
        /// Ctrl-C that a caller's handler cancels can also end the read with no line; it is taken as the
        /// same answer.
        /// </summary>
        public static string Ask(string prompt, params string[] letters)
        {
            while (true)
            {
                Console.Error.Write(prompt);
                string? line = Console.ReadLine();
                if (line == null)
                {
                    // End of input, or Ctrl-C at the question: the answer that does nothing, on a line of its own.
                    Console.Error.WriteLine();
                    return letters[letters.Length - 1];
                }

                string a = line.Trim().ToLowerInvariant();
                foreach (string l in letters)
                {
                    if (a.Length > 0 && a[0] == l[0]) return l;
                }
            }
        }
    }
}
