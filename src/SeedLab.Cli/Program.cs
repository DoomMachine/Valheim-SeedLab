using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using SeedLab.Cli.Commands;
using SeedLab.Cli.Infra;
using SeedLab.Runtime.Storage;

namespace SeedLab.Cli
{
    /// <summary>
    /// <c>vseed</c> - type a seed, see the world.
    ///
    /// <para>Two rules run through the whole tool. Every number carries the grid it was measured on,
    /// because a figure from a coarser grid is a different measurement and not an approximation of the
    /// fine one. And nothing is printed that has not been checked: the seed inverse re-hashes what it
    /// returns, the published seed-space figures are recomputed before they are quoted, and
    /// <c>vseed selftest</c> re-runs the acceptance gate against what the game itself wrote.</para>
    /// </summary>
    public static class Program
    {
        /// <summary>
        /// Built rather than const so the thread cap and the core count in it are this machine's,
        /// not a number copied into a string literal.
        /// </summary>
        private static string Usage => @"vseed - offline Valheim world generation, for Valheim 1.0.15 (worldGenVersion 2)

usage: vseed <command> [options]

  seed <text-or-int>        resolve the seed both ways and summarise the world it makes
  map <text-or-int>         render the world to a PNG
  at <text-or-int> <x> <z>  what the generator says about one point
  locations <text-or-int>   where the bosses, traders and dungeons of a seed are
  hash <text>               seed text -> int32 (the game's GetStableHashCode)
  invert <int32>            int32 -> a typeable seed text, re-hashed before it is printed
  space                     the size and shape of the seed space, re-verified now
  serve                     open the local web UI (map, seed panel, search) on 127.0.0.1
  clean                     what SeedLab is using on disk, and remove what it no longer needs
  search <query|preset>     scan the seed space for worlds that match a declarative query
  explain <seed> <query>    why one seed does or does not match that query
  presets list|show <name>  the shipped queries, in the criteria language
  data                      the game data this build ships, and whether it matches the install
  worlds                    the Valheim worlds on this machine (read-only)
  world <name>              one save: seed, world-gen version, modifiers, contents
  selftest                  re-check this build against the bundled ground truth
  bench                     measured throughput of each stage on this machine
  profile                   where one seed's time goes, phase by phase, on this machine now

global options (accepted before or after the command name):
  --json                    machine-readable output on stdout (warnings go to stderr)
  --mode <m>                background | balanced | full   (DEFAULT balanced)
                            background ~25 % of cores at BelowNormal - safe while you play;
                            balanced   ~50 % - the machine stays usable;
                            full       every core, still capped by the memory guard.
  --threads <n>             worker threads, 1.." + Args.MaxThreads + @"; omit it and --mode chooses
                            (this machine has " + Environment.ProcessorCount + @" logical cores).
                            Whatever is chosen is still capped by free memory, and the
                            arithmetic is printed.
  --cache-dir <dir>         where checkpoints, rendered maps and the self-test stamp live
                            (default: %LOCALAPPDATA%\SeedLab, or $SEEDLAB_CACHE_DIR)
  --ignore-running-game     do not drop to background mode when Valheim is running
  --skip-self-test          do not check this machine against the recorded goldens.
                            The run then says out loud that it is unverified.
  --accept-unverified-platform
                            proceed on an architecture SeedLab has never had its gates run on
  --debug                   print a stack trace if something unexpected goes wrong
  -h, --help                this text, or 'vseed <command> --help'
  -V, --version

exit codes:
  0 ok   1 a check failed   2 bad command line   4 internal error
  3 not found - or a file or folder SeedLab needs is in use, read-only, not allowed or on a
    full drive; also a finished rotated search whose manifest could not be saved

session log: every command but hash, invert, space, presets, data, worlds and world
  rewrites %LOCALAPPDATA%\SeedLab\logs\vseed.log (or <cache-dir>\logs\vseed.log) as it
  starts - copy it first if you need the last one. A second vseed running at the same
  time writes vseed.log.1 (up to .4) instead.

A seed token that parses as an int32 is read as the INT; pass --text to read it as a seed text.
";

        public static int Main(string[] rawArgs)
        {
            // FIRST, before anything can touch a generator type: 'vseed profile --counters' switches the
            // per-point counters on through an environment variable that is read exactly once, in a type
            // initialiser. Nothing else in this method may run before it.
            ProfileCommand.ApplyCountersSwitch(rawArgs);

            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;

            bool debug = Array.IndexOf(rawArgs, "--debug") >= 0;
            List<string> argv = new List<string>();
            foreach (string s in rawArgs)
            {
                if (s != "--debug") argv.Add(s);
            }

            if (argv.Count == 0)
            {
                Console.Out.Write(Usage);
                return ExitCodes.Usage;
            }

            // The global options are documented as global, so they are accepted on either side of the
            // command name. Anything else that starts with '-' is left where it is, so '--help',
            // '--version' and a mistyped command still reach the handling below.
            // Each one is rewritten to its --name=value form before it is moved, so that moving it
            // can never make it swallow a token that was not its value ('vseed --json hash abc' must
            // not hand "abc" to --json).
            List<string> globals = new List<string>();
            List<string> asTyped = new List<string>();
            int at = 0;
            while (at < argv.Count && Args.IsGlobalOption(argv[at]))
            {
                string tok = argv[at++];
                asTyped.Add(tok);
                if (tok.IndexOf('=') >= 0) globals.Add(tok);
                else if (!Args.GlobalOptionTakesValue(tok)) globals.Add(tok + "=true");
                else if (at < argv.Count) { asTyped.Add(argv[at]); globals.Add(tok + "=" + argv[at++]); }
                else globals.Add(tok);           // no value left; the command reports it
            }

            if (at >= argv.Count)
            {
                Console.Error.WriteLine("vseed: " + string.Join(" ", asTyped)
                                        + " is an option, not a command - name a command as well.");
                Console.Error.WriteLine("       run 'vseed --help' to see the commands.");
                return ExitCodes.Usage;
            }

            argv.RemoveRange(0, at);
            argv.InsertRange(1, globals);   // right after the command, never past a '--'

            string cmd = argv[0];
            if (cmd == "-h" || cmd == "--help" || cmd == "help")
            {
                if (argv.Count > 1 && HelpFor(argv[1]) != null)
                {
                    Console.Out.WriteLine(HelpFor(argv[1]));
                    return ExitCodes.Ok;
                }

                Console.Out.Write(Usage);
                return ExitCodes.Ok;
            }

            if (cmd == "-V" || cmd == "--version" || cmd == "version")
            {
                Console.Out.WriteLine("vseed " + Verified.EngineVersion
                                      + "  (SeedLab; verified against Valheim " + Verified.GameVersion
                                      + ", network " + Verified.NetworkVersion
                                      + ", worldGenVersion " + Verified.WorldGenVersion + ")");
                return ExitCodes.Ok;
            }

            string? help = HelpFor(cmd);
            if (help == null)
            {
                Console.Error.WriteLine("vseed: unknown command '" + cmd + "'.");
                Console.Error.WriteLine("       run 'vseed --help' to see the commands.");
                return ExitCodes.Usage;
            }

            List<string> rest = argv.GetRange(1, argv.Count - 1);
            if (cmd == "profile") rest = ProfileCommand.RenameThreadsOption(rest);
            if (rest.Contains("--help") || rest.Contains("-h"))
            {
                Console.Out.WriteLine(help);
                return ExitCodes.Ok;
            }

            Args a = new Args(rest);

            // Declared out here, and ended in the finally, so that every catch below can still write
            // to the session log and the log's last line carries the exit code. It used to be a
            // `using` inside the try, which disposed it before any catch ran (2026-09-24).
            CliRuntime? rt = null;
            int exit = ExitCodes.Internal;
            try
            {
                // Both are read inside the try, so a bad --json or --threads is one error line like
                // every other bad option rather than an unhandled exception and a stack trace.
                bool json = a.Flag("json");

                // ONE RuntimeContext per command, for every command that does real work. It probes
                // the machine, opens the cache root, reaps what a killed run left behind, looks for a
                // running game and runs the self-test gate - all of it milliseconds, and all of it
                // things that used to be decided (or not decided at all) at the point of use.
                //
                // The pure-arithmetic commands - hash, invert, space, presets, data, worlds, world -
                // deliberately do NOT start one: they touch no grid, spawn no worker and write
                // nothing, so probing memory and listing processes for them would be cost with no
                // answer attached. They still accept and VALIDATE every global option, which is what
                // ConsumeGlobals is for.
                bool needsRuntime = cmd is "seed" or "map" or "at" or "locations" or "search"
                                        or "explain" or "serve" or "selftest" or "bench" or "clean"
                                        or "profile";
                rt = needsRuntime ? CliRuntime.Start(a, cmd, AsTyped(rawArgs)) : null;
                if (rt == null) a.ConsumeGlobals();

                // Fail closed before anything a user would act on. 'selftest' is exempt because it IS
                // the diagnostic - refusing to run the thing that explains the refusal helps nobody -
                // and so are 'bench' and 'profile' (they time, they do not answer) and 'clean' (it
                // moves no numbers). 'profile' prints the self-test's status in its machine block.
                if (rt != null && cmd is "seed" or "map" or "at" or "locations" or "search"
                                     or "explain" or "serve")
                {
                    rt.RequireVerified();
                }

                using Out o = new Out(json);
                int code = cmd switch
                {
                    "seed" => SeedCommand.Run(a, o, rt!),
                    "map" => MapCommand.Run(a, o, rt!),
                    "at" => AtCommand.Run(a, o),
                    "locations" => LocationsCommand.Run(a, o, rt!),
                    "hash" => HashCommand.Run(a, o),
                    "invert" => InvertCommand.Run(a, o),
                    "space" => SpaceCommand.Run(a, o),
                    "search" => SearchCommand.Run(a, o, rt!),
                    "explain" => ExplainCommand.Run(a, o, rt!),
                    "presets" => PresetsCommand.Run(a, o),
                    "data" => DataCommand.Run(a, o),
                    "worlds" => WorldsCommand.Run(a, o),
                    "world" => WorldCommand.Run(a, o),
                    "serve" => ServeCommand.Run(a, o, rt!),
                    "selftest" => SelfTestCommand.Run(a, o, rt!),
                    "bench" => BenchCommand.Run(a, o, rt!),
                    "profile" => ProfileCommand.Run(a, o, rt!),
                    "clean" => CleanCommand.Run(a, o, rt!),
                    _ => throw new CliException("unknown command '" + cmd + "'."),
                };

                o.Flush();
                exit = code;
                return code;
            }
            catch (CliException ex)
            {
                Console.Error.WriteLine("vseed " + cmd + ": " + ex.Message);
                if (ex.Hint != null) Console.Error.WriteLine(ex.Hint);
                if (debug) Console.Error.WriteLine(ex.StackTrace);
                Logged(rt, cmd, ex.Message, ex, ex.ExitCode == ExitCodes.Internal);
                return exit = ex.ExitCode;
            }
            catch (SeedLab.Data.GameDataException ex)
            {
                // Missing, stale or unreadable game data is a condition the user can act on - it has
                // its own exit code and its own sentence. Falling through to the general handler
                // printed "this is a bug", which sent the reader looking for a fault in the tool.
                Console.Error.WriteLine("vseed " + cmd + ": " + ex.Message);
                if (debug) Console.Error.WriteLine(ex.StackTrace);
                Logged(rt, cmd, ex.Message, ex, false);
                return exit = ExitCodes.NotFound;
            }

            catch (SeedLab.Data.StaleGameDataException ex)
            {
                // The same condition one type along: the data loaded and describes ANOTHER build, so
                // DataPolicy refused the answer. 'vseed locations' and 'vseed explain' reach this
                // directly. It is a refusal the user can act on - the message already names the fix -
                // and printing "this is a bug" for it sent the reader hunting a fault in the tool.
                Console.Error.WriteLine("vseed " + cmd + ": " + ex.Message);
                if (debug) Console.Error.WriteLine(ex.StackTrace);
                Logged(rt, cmd, ex.Message, ex, false);
                return exit = ExitCodes.NotFound;
            }
            catch (FileAccessException ex)
            {
                // A file another program kept busy for the whole wait, a read-only file, a folder this
                // account may not write: the retry has already named the file and the probable cause,
                // in words. It is a condition the user can fix, never "this is a bug" (2026-09-24).
                Console.Error.WriteLine("vseed " + cmd + ": " + SearchCommand.WrapPaths(ex.Message, "       ", new[] { ex.Path }));
                if (debug) Console.Error.WriteLine(ex.ToString());
                Logged(rt, cmd, ex.Message, ex, true);
                return exit = ExitCodes.NotFound;
            }
            catch (System.IO.FileNotFoundException ex)
            {
                Console.Error.WriteLine("vseed " + cmd + ": " + ex.Message);
                if (debug) Console.Error.WriteLine(ex.StackTrace);
                Logged(rt, cmd, ex.Message, ex, false);
                return exit = ExitCodes.NotFound;
            }
            catch (System.IO.DirectoryNotFoundException ex)
            {
                Console.Error.WriteLine("vseed " + cmd + ": " + ex.Message);
                if (debug) Console.Error.WriteLine(ex.StackTrace);
                Logged(rt, cmd, ex.Message, ex, false);
                return exit = ExitCodes.NotFound;
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException
                                       || (ex is IOException && FileRetry.IsTransient(ex)))
            {
                // The same conditions, from a file operation that did not go through the retry: a raw
                // sharing violation (its message names the file) or an access denial (whose message
                // often does not). Diagnosed from the file itself when the message names it; otherwise
                // said as plainly as it can be, with the log that holds the rest.
                FileDiagnosis? d = FileRetry.DiagnoseEscaped(ex);
                if (d != null)
                {
                    Console.Error.WriteLine("vseed " + cmd + ": " + SearchCommand.WrapPaths(d.Message, "       ", new[] { d.Path }));
                }
                else
                {
                    Console.Error.WriteLine("vseed " + cmd + ": Windows refused SeedLab access to a file or folder it needed ("
                                            + ex.Message + ").");
                    Console.Error.WriteLine("       Another program may have it open, or it is read-only, or this account may not "
                                            + "change it. Close other programs that use SeedLab's files and try again.");
                    if (rt?.Log.Path != null) Console.Error.WriteLine("       the session log has the details: " + rt.Log.Path);
                }

                if (debug) Console.Error.WriteLine(ex.ToString());
                Logged(rt, cmd, d?.Message ?? ex.Message, ex, true);
                return exit = ExitCodes.NotFound;
            }
            catch (IOException ex) when (FileRetry.IsDiskFull(ex))
            {
                // A full drive is a condition the user can fix, not a fault in the tool - it used to fall
                // to the handler below and print "IOException: There is not enough space on the disk"
                // above "this is a bug" (review of 2026-09-24). Named by the file the message names.
                FileDiagnosis? d = FileRetry.DiagnoseEscaped(ex);
                Console.Error.WriteLine("vseed " + cmd + ": " + (d != null
                    ? SearchCommand.WrapPaths(d.Message, "       ", new[] { d.Path })
                    : "the drive SeedLab was writing to is full. Free some space on it - 'vseed clean' shows what "
                      + "SeedLab itself keeps in its cache folder - then try again."));
                if (rt?.Log.Path != null) Console.Error.WriteLine("       the session log has the details: " + rt.Log.Path);
                if (debug) Console.Error.WriteLine(ex.ToString());
                Logged(rt, cmd, d?.Message ?? ex.Message, ex, true);
                return exit = ExitCodes.NotFound;
            }
            catch (Exception ex)
            {
                // No stack trace unless asked: a wall of frames is not a message to a user. The log
                // always gets it, so the one file a user can hand over holds what --debug would show.
                Console.Error.WriteLine("vseed " + cmd + ": " + ex.GetType().Name + ": " + ex.Message);
                Console.Error.WriteLine("       this is a bug; re-run with --debug for the stack trace.");
                if (rt?.Log.Path != null) Console.Error.WriteLine("       the session log has it too: " + rt.Log.Path);
                if (debug) Console.Error.WriteLine(ex.ToString());
                Logged(rt, cmd, ex.Message, ex, true);
                return exit = ExitCodes.Internal;
            }
            finally
            {
                rt?.End(exit);
            }
        }

        /// <summary>
        /// A failure, in the session log: the sentence the user saw, and - when it is not an ordinary
        /// refusal - the exception's type, HResult and stack trace, which the terminal shows only with
        /// --debug.
        /// </summary>
        private static void Logged(CliRuntime? rt, string cmd, string message, Exception ex, bool withStack)
        {
            if (rt == null) return;
            rt.Log.Error("error    vseed " + cmd + ": " + message);
            if (withStack) rt.Log.Exception("the command failed", ex);
        }

        /// <summary>The command line as typed, for the session log: "vseed search axe-heads --seeds 400".</summary>
        private static string AsTyped(string[] rawArgs)
        {
            List<string> parts = new List<string> { "vseed" };
            foreach (string arg in rawArgs)
            {
                parts.Add(arg.Length == 0 || arg.IndexOfAny(new[] { ' ', '\t', '"' }) >= 0
                    ? "\"" + arg.Replace("\"", "\\\"") + "\""
                    : arg);
            }

            return string.Join(" ", parts);
        }

        private static string? HelpFor(string cmd) => cmd switch
        {
            "seed" => SeedCommand.Help,
            "map" => MapCommand.Help,
            "at" => AtCommand.Help,
            "locations" => LocationsCommand.Help,
            "hash" => HashCommand.Help,
            "invert" => InvertCommand.Help,
            "space" => SpaceCommand.Help,
            "search" => SearchCommand.Help,
            "explain" => ExplainCommand.Help,
            "presets" => PresetsCommand.Help,
            "data" => DataCommand.Help,
            "worlds" => WorldsCommand.Help,
            "world" => WorldCommand.Help,
            "serve" => ServeCommand.Help,
            "selftest" => SelfTestCommand.Help,
            "bench" => BenchCommand.Help,
            "profile" => ProfileCommand.Help,
            "clean" => CleanCommand.Help,
            _ => null,
        };
    }
}
