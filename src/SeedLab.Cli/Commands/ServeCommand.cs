using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using SeedLab.Cli.Analysis;
using SeedLab.Cli.Infra;
using SeedLab.Contracts.Dump;
using SeedLab.Data;
using SeedLab.Locations;
using SeedLab.Render;
using SeedLab.Render.Png;
using SeedLab.Runtime.Execution;
using SeedLab.Search.Criteria;
using SeedLab.Search.Evaluation;
using SeedLab.Search.Execution;
using SeedLab.Seeds;
using SeedLab.Web;
using SeedLab.Web.Api;
using SeedLab.Web.Tiles;
using SeedLab.WorldGen;

namespace SeedLab.Cli.Commands
{
    /// <summary>
    /// <c>vseed serve</c> - the local web UI.
    ///
    /// <para>This command owns the process and the numbers; <c>SeedLab.Web</c> owns the server. The
    /// seam between them is <see cref="SeedReportProvider"/>: the web project never learns what an
    /// island is, it asks for one of these and the analysis in <c>SeedLab.Cli.Analysis</c> - the same
    /// code behind <c>vseed seed</c> - answers. So the page and the terminal cannot disagree.</para>
    /// </summary>
    public static class ServeCommand
    {
        public const string Help = @"vseed serve [options]

  Starts the local web UI: a pan-and-zoom map of any seed, the seed panel, the location
  markers and the search panel, served on 127.0.0.1 and nowhere else.

  The search panel runs SeedLab.Search itself - the same engine, criteria language and
  query hash as 'vseed search' - and streams its hits as they are found. The map's
  markers are the placement engine's own output; a m_unique type with more than one
  surviving candidate is drawn as a CANDIDATE SET and never as a position.

  Nothing leaves this machine. The page is four files embedded in vseed itself - no CDN,
  no web font, no external request of any kind - and the server refuses any Host header
  other than 127.0.0.1 / localhost. It writes three things and nothing else: a results
  file you name on the Search panel (inside .\seedlab-results), and a search's checkpoint
  and the tile cache, in the cache root (--cache-dir). Your saves are never touched.

Options:
  --port <n>           port to bind (default 8731; 0 lets the OS pick a free one)
  --no-browser         do not open a browser
  --tile-cache <MiB>   memory budget for rendered tiles (default 128)
  --worlds <n>         how many seeds keep a constructed generator alive (default 4)
  --selftest           check the tiles against the game's own texture, start a real server
                       and check that it is on loopback only, refuses a foreign Host, sends
                       no CORS header, refuses a POST from another web page, serves nothing
                       outside its content root, and that its markers and search results
                       match the engines - then exit
  --json               with --selftest, machine-readable output

Examples:
  vseed serve
  vseed serve --port 0 --no-browser
  vseed serve --selftest";

        public static int Run(Args a, Out o, CliRuntime rt)
        {
            if (a.Flag("selftest")) return SelfTest(a, o, rt);

            int port = a.Int("port", 8731);
            if (port < 0 || port > 65535) throw new CliException("--port must be between 0 and 65535.");
            bool browser = a.Flag("browser", true) && !a.Flag("no-browser");
            int cacheMiB = a.Int("tile-cache", 128);
            if (cacheMiB < 1 || cacheMiB > 4096) throw new CliException("--tile-cache must be between 1 and 4096 MiB.");
            int worlds = a.Int("worlds", 4);
            if (worlds < 1 || worlds > 32) throw new CliException("--worlds must be between 1 and 32.");
            a.RejectUnknown();

            // The tile renderer and the search panel share this process, so the search is planned
            // with the tile cache reserved out of the memory budget rather than pretending it is free.
            WorkerPlan searchPlan = rt.Plan(WorkTier.HeightsRivers, 12.0,
                                            reservedBytes: (long)cacheMiB * 1024 * 1024);

            WebServerOptions opt = new WebServerOptions
            {
                Port = port,
                OpenBrowser = browser,
                WorldGenVersion = Verified.WorldGenVersion,
                EngineVersion = Verified.EngineVersion,
                GameVersion = Verified.GameVersion,
                TileCacheBytes = (long)cacheMiB * 1024 * 1024,
                WorldCacheSeeds = worlds,
                SeedReport = BuildReport,
                Locations = BuildLocations,
                LocationCacheSeeds = Math.Max(2, worlds),
                // Seed-independent, and the page wants it before the first placement finishes: the
                // grouped type list and the name->prefab map it canonicalises a typed name with.
                LocationVocabulary = Vocabulary(),
                SearchLocationOracle = SearchOracle(out string? oracleProblem),
                // The page's search panel used to size itself at ProcessorCount - 2, decided inside
                // SeedLab.Web with no knowledge of the mode, the memory or the game running. It is now
                // the same plan the terminal uses, so the two front ends cost the same.
                SearchThreads = searchPlan.Workers,
                // This command's own runtime, so the process has ONE: one cache root, one throttle, one
                // reap and one self-test. Left null, the server started a second RuntimeContext with
                // none of the global options, so '--cache-dir' reached only this command's runtime,
                // which the page never used - the tile cache and every search checkpoint went to the
                // default cache root, and the process ran two throttles, two reaps and two self-tests
                // (2026-09-24). The server does not dispose a runtime it was given; CliRuntime does,
                // once, when the command returns.
                Runtime = rt.Context,
                Log = Console.Out.WriteLine,
            };

            // ---- the folders the page will write in, checked at start (2026-09-24) -----------------
            //
            // The tile cache and the results folder a run names a file in; the checkpoints folder is one
            // of the cache root's own, already checked when the session started. A failure here is a
            // WARNING, not a question: the map and the seed panel need none of these, a tile cache that
            // cannot be written runs from memory, and every search checks again before it starts and
            // refuses by name. The results folder is not created - the server promises to create it only
            // when a run names a file - so the folder above it is checked instead. Made after the
            // options, because building them loads the location data, and the integrity half of the
            // line counts the data files that load verified.
            AccessGate access = new AccessGate(rt)
                .Folder(System.IO.Path.Combine(rt.Cache.Tiles, "v" + Verified.WorldGenVersion), create: true)
                .Folder(opt.ResolvedResultsDirectory)
                .Run();
            List<string> startLines = new List<string> { access.Summary() };
            rt.Log.Info("start    " + startLines[0]);
            foreach (SeedLab.Runtime.Storage.AccessResult r in access.Results)
            {
                if (r.Ok) continue;
                startLines.Add("warning: " + r.Message + " "
                               + (r.Path.StartsWith(rt.Cache.Tiles, StringComparison.OrdinalIgnoreCase)
                                   ? "The map still works; its tiles are kept in memory only."
                                   : "The map still works; a search that writes a results file is refused until it is fixed."));
                rt.Log.Warn("start    " + r.Message);
            }

            opt.StartupLines = startLines;

            Console.Out.WriteLine();
            Console.Out.WriteLine("vseed " + Verified.EngineVersion + " - verified against Valheim "
                                  + Verified.GameVersion + ", worldGenVersion " + Verified.WorldGenVersion);
            if (oracleProblem != null)
            {
                Out.Warn("location goals in the search panel are unavailable: " + oracleProblem);
            }

            // A spelling two prefabs claim is dropped rather than given to one of them, and the user
            // hears about it here as well as on every report - otherwise the only symptom is a name
            // that quietly stops working in the page while it still works in the terminal.
            foreach (string problem in _vocabularyNotes) Out.Warn(problem);
            try
            {
                return WebServer.RunAsync(opt).GetAwaiter().GetResult();
            }
            // Kestrel wraps a bind failure THREE deep: IOException -> AddressInUseException ->
            // SocketException(10048). Catching only the outer two let a plain "port is taken" print
            // as "this is a bug; re-run with --debug", which is what it actually did the first time
            // a second server was started. So walk the whole chain instead of guessing its depth.
            catch (Exception ex) when (FindSocketError(ex) is System.Net.Sockets.SocketException se)
            {
                throw new CliException(
                    "could not bind 127.0.0.1:" + port + " - " + se.Message,
                    ExitCodes.NotFound,
                    port == 0
                        ? "the OS refused even an ephemeral port; check a firewall or security tool"
                        : "another program is already on port " + port
                          + " (a vseed serve you left running?); try 'vseed serve --port 0' to let the OS pick one");
            }
        }

        /// <summary>
        /// The oracle the SEARCH panel talks to - the same one <c>vseed search</c> uses, so a goal that
        /// can be answered in the terminal can be answered on the page and a goal that cannot is
        /// refused with the same sentence in both.
        ///
        /// <para>It never throws: a missing or stale dump leaves the refusing oracle in place and hands
        /// back the reason, which is printed once at startup and repeated on the page. The map's own
        /// markers come from <see cref="BuildLocations"/> and are independent of this.</para>
        /// </summary>
        private static SeedLab.Search.Locations.ILocationOracle SearchOracle(out string? problem)
        {
            try
            {
                return SeedLab.LocationOracle.DumpedLocationOracle.Create(out problem);
            }
            catch (Exception ex)
            {
                // Create swallows the failures it expects; a stale DATA-STAMP raises its own type and
                // must not take the whole server down with it - the map, the seed panel and every
                // terrain goal are still exact.
                problem = ex.Message;
                return SeedLab.Search.Locations.UnavailableLocationOracle.Instance;
            }
        }

        /// <summary>The first <see cref="System.Net.Sockets.SocketException"/> anywhere in an exception's
        /// chain, including inside an <see cref="AggregateException"/>, or null if there is none.</summary>
        private static System.Net.Sockets.SocketException? FindSocketError(Exception? ex)
        {
            for (int depth = 0; ex != null && depth < 16; depth++)
            {
                if (ex is System.Net.Sockets.SocketException se) return se;
                if (ex is AggregateException agg)
                {
                    foreach (Exception inner in agg.InnerExceptions)
                    {
                        System.Net.Sockets.SocketException? found = FindSocketError(inner);
                        if (found != null) return found;
                    }
                    return null;
                }
                ex = ex.InnerException;
            }
            return null;
        }

        // -------------------------------------------------------------------------------------------
        // The seam: the web server asks, the CLI's own analysis answers.
        // -------------------------------------------------------------------------------------------
        private static SeedReport BuildReport(SeedReportRequest req, CancellationToken ct)
        {
            FieldGrid grid = Grids.ForSpacing(req.GridSpacingM);
            Stopwatch sw = Stopwatch.StartNew();
            WorldField field = WorldField.Sample(req.Seed, grid, sampleLava: false, req.Threads, req.WorldGenVersion);
            double fieldS = sw.Elapsed.TotalSeconds;
            ct.ThrowIfCancellationRequested();

            sw.Restart();
            WorldSummary s = WorldSummary.Compute(field, req.MinIslandAreaM2);
            double analysisS = sw.Elapsed.TotalSeconds;

            List<SeedReport.BiomeRow> biomes = new List<SeedReport.BiomeRow>();
            foreach (Biome b in MapPalette.LegendOrder)
            {
                int bi = b.ToGameIndex();
                biomes.Add(new SeedReport.BiomeRow
                {
                    Name = MapPalette.Name(b),
                    Index = bi,
                    Color = MapPalette.LandColor(b).Hex,
                    Cells = s.BiomeCells[bi],
                    AreaM2 = s.BiomeCells[bi] * grid.CellArea,
                    Share = (double)s.BiomeCells[bi] / Math.Max(1, s.InWorldCells),
                    LandCells = s.BiomeLandCells[bi],
                    LandM2 = s.BiomeLandCells[bi] * grid.CellArea,
                    NearestM = Finite(s.NearestBiomeM[bi]),
                    NearestLandM = Finite(s.NearestBiomeLandM[bi]),
                });
            }

            IslandAnalysis isl = s.Islands;
            List<SeedReport.IslandRow> top = new List<SeedReport.IslandRow>();
            for (int i = 0; i < isl.Top.Count && i < req.TopIslands; i++) top.Add(Row(isl.Top[i]));

            string? shortest = null;
            string? gameStyle = null;
            try
            {
                shortest = SeedText.Invert(req.Seed, SeedAlphabet.Alnum62);
                gameStyle = SeedText.GenerateGameStyle(req.Seed);
            }
            catch (InvalidOperationException)
            {
                // SeedText verifies every preimage it returns and throws rather than hand back an
                // unverified one. A report without a typeable text is still a correct report.
            }

            return new SeedReport
            {
                Seed = req.Seed,
                AsTyped = req.AsTyped,
                ShortestText = shortest ?? "",
                GameStyleText = gameStyle ?? "",
                WorldGenVersion = field.WorldGenVersion,
                Grid = new SeedReport.GridInfo
                {
                    SpacingM = grid.Spacing,
                    Size = grid.Size,
                    IsGameGrid = grid.IsGameGrid,
                    CellAreaM2 = grid.CellArea,
                    CellsTotal = grid.Count,
                    CellsInWorld = s.InWorldCells,
                    CellsOutside = s.OutsideCells,
                    AreaSampledM2 = s.InWorldAreaM2,
                    DistanceUncertaintyM = s.GridDistanceUncertaintyM,
                },
                Land = new SeedReport.LandInfo
                {
                    WaterLevelM = MapPalette.WaterLevel,
                    LandCells = s.LandCells,
                    LandM2 = s.LandAreaM2,
                    WaterCells = s.WaterCells,
                    WaterM2 = s.WaterAreaM2,
                },
                Biomes = biomes,
                Islands = new SeedReport.IslandInfo
                {
                    MinAreaM2 = isl.MinAreaM2,
                    CountAtLeastMin = isl.ComponentsAtLeastMin,
                    ComponentsAll = isl.ComponentsAll,
                    LargestAreaM2 = isl.Largest?.AreaM2,
                    LargestNearestM = isl.Largest == null ? null : Finite(isl.Largest.NearestToOriginM),
                    NearestLandM = Finite(isl.NearestLandM),
                    NearestLandX = isl.NearestLandX,
                    NearestLandZ = isl.NearestLandZ,
                    CentreIsland = isl.CentreIsland == null ? null : Row(isl.CentreIsland),
                    Rule = "rule 2 (the land component whose nearest cell is closest to the origin). "
                           + "Rule 1 anchors the spawn island on the StartTemple instance, which needs "
                           + "location placement - not in this build - so the StartTemple anchor was NOT used.",
                    Top = top,
                },
                Origin = new SeedReport.OriginInfo
                {
                    Biome = MapPalette.Name(s.OriginBiome),
                    HeightM = s.OriginHeight,
                    AboveWaterM = s.OriginHeight - MapPalette.WaterLevel,
                    ForestFactor = s.OriginForestFactor,
                    InForest = WorldGeneratorPort.InForest(0f, 0f, 0f),
                },
                Highest = new SeedReport.PointInfo
                {
                    HeightM = s.PeakHeight,
                    X = s.PeakX,
                    Z = s.PeakZ,
                    Biome = MapPalette.Name(s.PeakBiome),
                },
                Lowest = new SeedReport.PointInfo
                {
                    HeightM = s.DeepestHeight,
                    X = s.DeepestX,
                    Z = s.DeepestZ,
                    Biome = "",
                },
                Timing = new SeedReport.TimingInfo
                {
                    FieldS = fieldS,
                    AnalysisS = analysisS,
                    Threads = field.Workers,
                },
            };
        }

        // -------------------------------------------------------------------------------------------
        // The second seam: the map asks where the places are, the CLI's own placement run answers.
        //
        // The web project deliberately never learns what a boss is. "Boss altar" and "trader camp" are
        // CURATED lists (LocationCatalog) - nothing on ZoneLocation or Location marks an altar or a
        // vendor - and the placement engine needs the dumped asset table and its version gate. All of
        // that stays here, exactly as the island analysis does, so the map and "vseed locations" cannot
        // disagree about a single coordinate.
        // -------------------------------------------------------------------------------------------

        private static readonly object CatalogLock = new object();
        private static LocationCatalog? _catalog;
        private static Exception? _catalogError;

        private static readonly object NamesLock = new object();
        private static SeedLab.Search.Locations.ILocationOracle? _names;

        /// <summary>
        /// The oracle the MAP borrows its names from, built once and kept for the life of the process.
        ///
        /// <para><b>Why a second oracle and not the search panel's.</b>
        /// <see cref="SearchOracle"/> hands out a fresh <c>DumpedLocationOracle</c>, which is
        /// <c>IDisposable</c> and owns a thread-local worker per calling thread; handing the same
        /// instance to the server, to <c>--selftest</c>'s own search run and to this provider would
        /// make one caller's <c>Dispose</c> everyone else's crash. Building a second one is cheap
        /// where it matters: <c>GameData</c> is a per-process singleton, so the 177 ms name table and
        /// the parsed dump are already in memory and only the table wrapper is rebuilt.</para>
        ///
        /// <para>It never throws and it never fails the map. An unavailable oracle means the markers
        /// are placed exactly as before and simply carry no names - which is why every caller tests
        /// <c>Available</c> rather than assuming a table is there.</para>
        /// </summary>
        private static SeedLab.Search.Locations.ILocationOracle Names()
        {
            lock (NamesLock)
            {
                return _names ??= SearchOracle(out _);
            }
        }

        /// <summary>
        /// The naming oracle as its concrete type, for the two things the interface does not carry -
        /// the language of the dumped strings and the notes the derivation wants printed - or null
        /// when this build has no dumped names.
        /// </summary>
        private static SeedLab.LocationOracle.DumpedLocationOracle? NamesDump()
            => Names() as SeedLab.LocationOracle.DumpedLocationOracle;

        /// <summary>
        /// The shipped location table, loaded once.
        ///
        /// <para>The load is what enforces the DATA-STAMP: <c>RequireUsableForAssetData</c> throws when
        /// the installed <c>assembly_valheim.dll</c> is not the build the table was dumped from, because
        /// a location answer from a different build would be confidently wrong. The failure is cached
        /// alongside the success so the server does not retry the load on every request, and it reaches
        /// the page with the one action that fixes it.</para>
        /// </summary>
        private static LocationCatalog Catalog()
        {
            lock (CatalogLock)
            {
                if (_catalog != null) return _catalog;
                if (_catalogError != null) throw _catalogError;
                try
                {
                    _catalog = LocationCatalog.Load();
                    return _catalog;
                }
                catch (Exception ex) when (ex is GameDataException or StaleGameDataException
                                           or System.IO.IOException or InvalidOperationException)
                {
                    _catalogError = ex;
                    throw;
                }
            }
        }

        /// <summary>
        /// One seed's locations for the map.
        ///
        /// <para><b>Called one at a time.</b> <see cref="SeedLab.Web.Locations.LocationsCache"/> holds a
        /// single slot, which matters here for a reason beyond load: the alt-biome runtime objects on
        /// the catalogue are per-WORLD state - <c>AltBiomeAssignment.Generate</c> resets and refills
        /// their sector lists - so two placement runs sharing one catalogue at the same time would be
        /// the bug the game itself hides behind a scene reload.</para>
        /// </summary>
        private static LocationsReport BuildLocations(LocationsRequest req, CancellationToken ct)
        {
            LocationCatalog cat = Catalog();
            SeedLab.Search.Locations.ILocationOracle names = Names();

            List<ZoneLocationEntry> wanted = req.Set == LocationSet.All
                ? cat.Select(LocationKind.None)
                : cat.Select(LocationKind.Boss | LocationKind.Trader);
            if (wanted.Count == 0) throw new InvalidOperationException("the placement list is empty in this build.");

            // Everything before the selection has to run too: one location per zone, globally, so an
            // earlier type can take a later type's zone. The prefix is never shortened past that.
            int prefix = cat.PrefixFor(wanted);

            req.OnPhase?.Invoke("world", 0.0);
            Stopwatch sw = Stopwatch.StartNew();
            WorldLocations wl = WorldLocations.Build(req.Seed, req.WorldGenVersion, cat.AltBiomes,
                                                     req.Threads == 0 ? -1 : req.Threads);
            double worldS = sw.Elapsed.TotalSeconds;
            ct.ThrowIfCancellationRequested();

            req.OnPhase?.Invoke("placement", 0.55);
            sw.Restart();
            PlacementResult res = LocationPlacementEngine.Run(wl.Generator, wl.Field, cat.Table,
                new PlacementOptions { StopAfterOrderedIndex = prefix - 1, AltBiomesComputed = true });
            double placeS = sw.Elapsed.TotalSeconds;

            req.OnPhase?.Invoke("biomes", 0.9);

            // ---- the types, in the order the placement list runs them ---------------------------------
            Dictionary<string, int> index = new Dictionary<string, int>(StringComparer.Ordinal);
            HashSet<string> candidateSets = new HashSet<string>(StringComparer.Ordinal);
            foreach (UniqueCandidateSet u in res.UniqueCandidates)
            {
                if (!u.WinnerIsPredictable) candidateSets.Add(u.Location.PrefabName);
            }

            LocationsReport report = new LocationsReport
            {
                Seed = req.Seed,
                WorldGenVersion = res.WorldGenVersion,
                Set = req.Set == LocationSet.All ? "all" : "core",
                TypesRun = prefix,
                TypesTotal = cat.Table.Ordered.Count,
                SecondsWorld = worldS,
                SecondsPlacement = placeS,
                GameVersion = cat.Data.Stamp.GameVersion,
                DataStamp = cat.Data.Stamp.AssemblyValheimSha256,
                DataDumped = cat.Data.Stamp.Dumped,
                NamesLanguage = NamesDump()?.DisplayNames?.Language,
            };

            // The catalogue's notes, the name derivation's own caveats and the vocabulary's dropped
            // spellings, DE-DUPLICATED: the catalogue already republishes LocationDisplayNames.Notes
            // and the oracle republishes them again beside its fold-collision notes, so the same
            // sentence reaches this list from two directions. Printing the Bog Witch's caveat twice
            // makes a reader doubt the one time it matters.
            HashSet<string> said = new HashSet<string>(StringComparer.Ordinal);
            void Say(string note)
            {
                if (said.Add(note)) report.Notes.Add(note);
            }

            foreach (string n in cat.Notes) Say(n);

            // EXPECTED to be non-empty: the Bog Witch's name is joined through the $npc_ token
            // because the dumped Trader carries none, and that note is permanent until a dump makes
            // the join itself.
            SeedLab.LocationOracle.DumpedLocationOracle? dump = NamesDump();
            if (dump != null)
            {
                foreach (string n in dump.NameNotes) Say(n);
            }

            Vocabulary();
            foreach (string n in _vocabularyNotes) Say(n);
            foreach (string n in PlacementResult.NotPredictable) report.NotPredictable.Add(n);

            foreach (ZoneLocationEntry e in wanted)
            {
                LocationTypeResult? t = null;
                foreach (LocationTypeResult x in res.Types)
                {
                    if (string.Equals(x.Location.PrefabName, e.PrefabName, StringComparison.Ordinal)) { t = x; break; }
                }

                // Null when this build has no dumped names: the row then carries the prefab and no
                // name at all, which is the one honest answer. It is never filled in with the prefab.
                SeedLab.Search.Locations.LocationPresentation? p =
                    names.Available ? names.TypeOf(e.PrefabName)?.Presentation : null;

                // Keyed by PREFAB, so a house that is only ever reached by clicking its marker gets
                // the same warning a group: query would have printed. The row carries the FIRST
                // feature: no prefab is in two of them in this build (there is one feature), and a
                // second would want a list rather than a silently concatenated sentence.
                IReadOnlyList<SeedLab.Search.Locations.WorldFeature> feats =
                    SeedLab.Search.Locations.WorldFeatures.ForPrefab(e.PrefabName);

                index[e.PrefabName] = report.Types.Count;
                report.Types.Add(new LocationTypeRow
                {
                    Prefab = e.PrefabName,
                    Label = cat.LabelOf(e.PrefabName),
                    DisplayName = p?.DisplayName,
                    DisplayNameSource = p?.DisplayNameSource,
                    NameToken = p?.NameToken,
                    Aliases = p == null ? new List<string>() : new List<string>(p.Aliases),
                    GroupKey = p?.GroupKey ?? "",
                    GroupHeading = p?.GroupHeading ?? "",
                    GroupOrder = p?.GroupOrder ?? -1,
                    SortIndex = p?.SortIndex ?? -1,
                    Category = CategoryOf(cat, e.PrefabName),
                    Unique = e.Unique,
                    CandidateSet = candidateSets.Contains(e.PrefabName),
                    Quantity = e.Quantity,
                    Placed = t?.Placed ?? 0,
                    Shortfall = t != null && t.Shortfall,
                    DungeonGenerators = cat.GeneratorsOf(e.PrefabName),
                    BiomeMask = BiomeMask(e.Biome),
                    BiomeBits = (int)e.Biome,
                    FeatureName = feats.Count > 0 ? feats[0].Name : null,
                    ContentsNote = feats.Count > 0 ? feats[0].Note : null,
                });
            }

            // ---- the instances --------------------------------------------------------------------------
            foreach (LocationInstanceResult i in res.Instances)
            {
                if (!index.TryGetValue(i.PrefabName, out int ti)) continue;
                report.Instances.Add(new LocationInstanceRow
                {
                    T = ti,
                    X = i.X,
                    Z = i.Z,
                    Y = i.Y,
                    B = wl.Generator.GetBiome(i.X, i.Z).ToGameIndex(),
                    Zx = i.Zone.x,
                    Zz = i.Zone.y,
                });

                report.Types[ti].Count++;
            }

            req.OnPhase?.Invoke("done", 1.0);
            return report;
        }

        /// <summary>
        /// What <c>/api/meta</c> says about location NAMES and grouping: the ordered group list and
        /// every spelling that means a place.
        ///
        /// <para><b>Why the page needs the map of names.</b> It builds a goal target from what the
        /// user typed, and a goal's id is derived from that raw string - so <c>location:The Elder</c>
        /// would become a results column headed with a space in it. The page canonicalises to the
        /// prefab first, and this is what it canonicalises with.</para>
        ///
        /// <para><b>All known types, not only the placed ones.</b> A name that resolves to a type
        /// this build never places should reach the server and get its own "in the table, never
        /// placed" answer, rather than being rejected in the browser as if it did not exist.</para>
        ///
        /// <para>A spelling two different prefabs claim is given to NEITHER - it is dropped and said
        /// out loud, because silently picking one would make the same typed word mean different
        /// things in the page and in the terminal. There are none in this dump.</para>
        /// </summary>
        private static LocationVocabulary? _vocabulary;
        private static List<string> _vocabularyNotes = new List<string>();

        /// <summary>The vocabulary, built once. Its problems are kept beside it so that every report
        /// can repeat them - a dropped spelling is something the user finds out by typing it.</summary>
        private static LocationVocabulary Vocabulary()
        {
            lock (NamesLock)
            {
                if (_vocabulary != null) return _vocabulary;
                _vocabulary = BuildVocabulary(out List<string> problems);
                _vocabularyNotes = problems;
                return _vocabulary;
            }
        }

        private static LocationVocabulary BuildVocabulary(out List<string> problems)
        {
            problems = new List<string>();
            LocationVocabulary vocab = new LocationVocabulary();

            foreach (SeedLab.Search.Locations.LocationGroupHeading g in
                     SeedLab.Search.Locations.LocationGroupTaxonomy.All)
            {
                vocab.Groups.Add(new LocationGroupRow { Key = g.Key, Heading = g.Heading, Order = g.Order });
            }

            SeedLab.Search.Locations.ILocationOracle names = Names();
            if (!names.Available) return vocab;

            vocab.NamesLanguage = NamesDump()?.DisplayNames?.Language;

            HashSet<string> dropped = new HashSet<string>(StringComparer.Ordinal);
            foreach ((string prefab, string? _) in names.LocationNames)
            {
                SeedLab.Search.Locations.LocationTypeInfo? info = names.TypeOf(prefab);
                IReadOnlyList<string> spellings = info?.Presentation?.Aliases ?? new[] { prefab };
                foreach (string s in spellings)
                {
                    if (string.IsNullOrEmpty(s) || dropped.Contains(s)) continue;
                    if (!vocab.Names.TryGetValue(s, out string? owner))
                    {
                        vocab.Names[s] = prefab;
                        continue;
                    }

                    if (string.Equals(owner, prefab, StringComparison.Ordinal)) continue;

                    vocab.Names.Remove(s);
                    dropped.Add(s);
                    problems.Add("the name '" + s + "' is claimed by both " + owner + " and " + prefab
                                 + ", so the page will not canonicalise it; type either prefab instead.");
                }
            }

            return vocab;
        }

        /// <summary>
        /// One category per prefab, most specific first. A Mistlands boss entrance is a boss AND a
        /// dungeon; the map draws it as a boss, because that is what the user is looking for.
        /// </summary>
        private static string CategoryOf(LocationCatalog cat, string prefab)
        {
            LocationKind k = cat.KindOf(prefab);
            if ((k & LocationKind.Boss) != 0) return "boss";
            if ((k & LocationKind.Trader) != 0) return "trader";
            if ((k & LocationKind.Dungeon) != 0) return "dungeon";
            return "feature";
        }

        private static string BiomeMask(Biome b)
        {
            List<string> p = new List<string>();
            foreach (Biome one in MapPalette.LegendOrder)
            {
                if (((int)b & (int)one) != 0) p.Add(MapPalette.Name(one));
            }

            return p.Count == 0 ? "(none)" : string.Join("/", p.ToArray());
        }

        private static SeedReport.IslandRow Row(Island i) => new SeedReport.IslandRow
        {
            AreaM2 = i.AreaM2,
            Cells = i.Cells,
            NearestToOriginM = i.NearestToOriginM,
            NearestX = i.NearestX,
            NearestZ = i.NearestZ,
            MinX = i.MinX,
            MaxX = i.MaxX,
            MinZ = i.MinZ,
            MaxZ = i.MaxZ,
        };

        private static double? Finite(double v) => double.IsFinite(v) ? v : (double?)null;

        // -------------------------------------------------------------------------------------------
        // --selftest
        // -------------------------------------------------------------------------------------------

        /// <summary>
        /// Checks the tile pipeline itself, offline, with no browser and no server.
        ///
        /// <list type="number">
        /// <item><b>Exactness.</b> Zoom 3 has a 12 m pixel and its sample points are bit-identical to
        /// G12's, so a z=3 tile in plain + game-palette mode must equal the game's own
        /// <c>cacheMinimapBiome</c> texture pixel for pixel. Checked against both ground-truth worlds.</item>
        /// <item><b>Seamlessness.</b> A 2 x 2 block of shaded tiles must equal a single render of the
        /// same region at the same resolution, exactly - which is only true if the one-pixel apron
        /// gives every tile pixel its real neighbours for the hillshade.</item>
        /// <item><b>Containment.</b> The static allowlist answers four names and nothing else, so no
        /// spelling of a traversal can reach a file. (Kestrel normalises a request path before
        /// routing sees it, so <c>/%2e%2e/app.js</c> arrives as <c>/app.js</c> and is served - as
        /// <c>/app.js</c>, an asset inside the root. Nothing outside it is reachable either way,
        /// because nothing outside it is in the table.)</item>
        /// </list>
        /// </summary>
        /// <summary>
        /// The worker count the serve selftest's own engine runs at, planned once from the runtime
        /// context rather than taken as half of ProcessorCount in two unrelated places. Static
        /// because the live-server checks below are static helpers the selftest calls.
        /// </summary>
        private static int SelfTestWorkers = 1;

        private static int SelfTest(Args a, Out o, CliRuntime rt)
        {
            SelfTestWorkers = rt.Plan(WorkTier.HeightsRivers, 12.0).Workers;
            a.Flag("selftest");
            a.RejectUnknown();

            string? gt = Verified.FindGroundTruth();
            int failures = 0;
            List<string[]> rows = new List<string[]>();

            WorldCache worlds = new WorldCache(4);
            TileRenderer renderer = new TileRenderer(worlds);
            TileStyle exact = new TileStyle { GamePalette = true, Plain = true };
            TileStyle shaded = new TileStyle();

            // ---- 1. exactness against the game's own biome texture ---------------------------------
            if (gt == null)
            {
                Out.Warn("groundtruth\\decoded was not found; the exactness check was skipped.");
                rows.Add(new[] { "tiles vs game biome texture", "SKIPPED", "groundtruth not found" });
            }
            else
            {
                foreach (Verified.Fixture f in Verified.Fixtures)
                {
                    string path = Path.Combine(gt, "decoded", f.Name + ".biome.u8");
                    if (!File.Exists(path))
                    {
                        rows.Add(new[] { f.Name + " z=3 tiles", "SKIPPED", "no " + path });
                        continue;
                    }

                    byte[] oracle = File.ReadAllBytes(path);
                    if (oracle.Length != 2048 * 2048)
                    {
                        rows.Add(new[] { f.Name + " z=3 tiles", "FAIL", "oracle is " + oracle.Length + " bytes, expected 4,194,304" });
                        failures++;
                        continue;
                    }

                    long compared = 0, bad = 0;
                    // Four tiles spread over the map: the centre, a coast, and two far corners.
                    (int X, int Y)[] tiles = { (4, 4), (3, 3), (1, 6), (6, 1) };
                    foreach ((int tx, int ty) in tiles)
                    {
                        byte[] png = renderer.Render(f.Seed, Verified.WorldGenVersion, 3, tx, ty, exact, CancellationToken.None);
                        DecodedPng img = PngDecoder.Decode(png);
                        for (int py = 0; py < 256; py++)
                        {
                            int gridRow = 2047 - (ty * 256 + py);       // row 0 of the cache is SOUTH
                            for (int px = 0; px < 256; px++)
                            {
                                int gridCol = tx * 256 + px;
                                byte dense = oracle[gridRow * 2048 + gridCol];
                                Rgb want = dense == 255
                                    ? new Rgb(255, 255, 255)
                                    : MapPalette.GameColor(BiomeExtensions.FromDenseIndex(dense));
                                int i = (py * 256 + px) * 3;
                                compared++;
                                if (img.Rgb[i] != want.R || img.Rgb[i + 1] != want.G || img.Rgb[i + 2] != want.B) bad++;
                            }
                        }
                    }

                    bool ok = bad == 0;
                    if (!ok) failures++;
                    rows.Add(new[]
                    {
                        f.Name + " z=3 tiles vs game texture" + (f.HoldOut ? " (hold-out)" : ""),
                        ok ? "PASS" : "FAIL",
                        Out.N(compared - bad) + " of " + Out.N(compared) + " pixels identical",
                    });
                }
            }

            // ---- 2. seams ---------------------------------------------------------------------------
            {
                int seed = Verified.Fixtures[0].Seed;
                const int z = 5, tx = 14, ty = 17;
                byte[][] tile = new byte[4][];
                tile[0] = renderer.Render(seed, Verified.WorldGenVersion, z, tx, ty, shaded, CancellationToken.None);
                tile[1] = renderer.Render(seed, Verified.WorldGenVersion, z, tx + 1, ty, shaded, CancellationToken.None);
                tile[2] = renderer.Render(seed, Verified.WorldGenVersion, z, tx, ty + 1, shaded, CancellationToken.None);
                tile[3] = renderer.Render(seed, Verified.WorldGenVersion, z, tx + 1, ty + 1, shaded, CancellationToken.None);
                DecodedPng[] img = { PngDecoder.Decode(tile[0]), PngDecoder.Decode(tile[1]),
                                     PngDecoder.Decode(tile[2]), PngDecoder.Decode(tile[3]) };

                // One render of the same 1536 m square at the same 3 m resolution, with its own apron.
                double span = TileGrid.TileSpanM(z);
                float cx = (float)(TileGrid.MinXZ + (tx + 1) * span);
                float cz = (float)(TileGrid.MaxXZ - (ty + 1) * span);
                FieldGrid big = new FieldGrid(514, (float)TileGrid.MetresPerPixel(z), cx, cz);
                WorldField bigField = WorldField.Sample(seed, big, sampleLava: true, 0, Verified.WorldGenVersion);
                RenderResult bigR = MapRenderer.Render(bigField, shaded.ToMapOptions());

                long diff = 0;
                for (int Y = 0; Y < 512; Y++)
                {
                    for (int X = 0; X < 512; X++)
                    {
                        DecodedPng t = img[(Y >= 256 ? 2 : 0) + (X >= 256 ? 1 : 0)];
                        int ti = ((Y & 255) * 256 + (X & 255)) * 3;
                        Rgb b = bigR.Canvas.Get(1 + X, 1 + Y);
                        if (t.Rgb[ti] != b.R || t.Rgb[ti + 1] != b.G || t.Rgb[ti + 2] != b.B) diff++;
                    }
                }

                bool ok = diff == 0;
                if (!ok) failures++;
                rows.Add(new[]
                {
                    "2x2 tile mosaic vs one render",
                    ok ? "PASS" : "FAIL",
                    Out.N(262144 - diff) + " of 262,144 pixels identical (hillshade apron)",
                });
            }

            // ---- 3. nothing outside the content root -----------------------------------------------
            {
                string[] probes =
                {
                    "/../Program.cs", "/../../SeedLab.Cli.csproj", "/%2e%2e/app.js", "/wwwroot/app.js",
                    "/C:/Windows/win.ini", "//etc/passwd", "/app.js/../../secret",
                };
                int reachable = 0;
                foreach (string p in probes)
                {
                    if (StaticAssets.TryGet(p, out _)) reachable++;
                }

                bool ok = reachable == 0;
                if (!ok) failures++;
                rows.Add(new[]
                {
                    "static allowlist rejects traversal",
                    ok ? "PASS" : "FAIL",
                    probes.Length + " probes, " + reachable + " reachable; the table answers exactly "
                    + CountAssets() + " names",
                });
            }

            // ---- 4. cost ----------------------------------------------------------------------------
            {
                // A seed nothing above has touched, so the generator really is built from scratch.
                const int coldSeed = 1234567;
                Stopwatch sw = Stopwatch.StartNew();
                renderer.Render(coldSeed, Verified.WorldGenVersion, 3, 4, 4, shaded, CancellationToken.None);
                double cold = sw.Elapsed.TotalSeconds;
                sw.Restart();
                for (int i = 0; i < 8; i++)
                {
                    renderer.Render(coldSeed, Verified.WorldGenVersion, 6, 32 + i, 32, shaded, CancellationToken.None);
                }

                double warm = sw.Elapsed.TotalSeconds / 8.0;
                rows.Add(new[] { "first tile of a cold seed", "MEASURED", Out.F(cold * 1000, 1) + " ms, including the generator pregeneration" });
                rows.Add(new[] { "further tiles, same seed", "MEASURED", Out.F(warm * 1000, 1) + " ms each, 8 tiles at z=6" });

                // The encoder level the tile path uses, measured rather than assumed.
                FieldGrid tg = TileGrid.SamplingGrid(3, 4, 4);
                WorldField tf = WorldField.Sample(coldSeed, tg, sampleLava: true, 0, Verified.WorldGenVersion);
                RenderResult tr = MapRenderer.Render(tf, shaded.ToMapOptions());
                byte[] rgb = new byte[256 * 256 * 3];
                for (int row = 0; row < 256; row++)
                {
                    Buffer.BlockCopy(tr.Canvas.Pixels, ((row + 1) * tg.Size + 1) * 3, rgb, row * 256 * 3, 256 * 3);
                }

                long[] sizes = new long[2];
                double[] times = new double[2];
                System.IO.Compression.CompressionLevel[] levels =
                {
                    System.IO.Compression.CompressionLevel.Fastest,
                    System.IO.Compression.CompressionLevel.Optimal,
                };
                for (int li = 0; li < 2; li++)
                {
                    sw.Restart();
                    using MemoryStream ms = new MemoryStream();
                    for (int rep = 0; rep < 20; rep++)
                    {
                        ms.SetLength(0);
                        PngEncoder.Write(ms, rgb, 256, 256, levels[li]);
                    }

                    times[li] = sw.Elapsed.TotalSeconds / 20.0;
                    sizes[li] = ms.Length;
                }

                rows.Add(new[]
                {
                    "png level: Fastest vs Optimal",
                    "MEASURED",
                    Out.F(times[0] * 1000, 2) + " ms / " + Out.N(sizes[0]) + " B   vs   "
                    + Out.F(times[1] * 1000, 2) + " ms / " + Out.N(sizes[1]) + " B",
                });
            }

            // ---- the replay a tab that rejoins a run is handed -----------------------------------------
            failures += ReplayCheck(rows);

            // ---- 5. the live server: the security properties, and the two new surfaces -------------
            failures += LiveChecks(rows, rt);

            if (o.Json)
            {
                var j = o.J;
                j.WriteStartObject();
                j.WriteString("command", "serve --selftest");
                j.WriteString("engine", Verified.EngineVersion);
                j.WriteNumber("failures", failures);
                j.WriteStartArray("checks");
                foreach (string[] r in rows)
                {
                    j.WriteStartObject();
                    j.WriteString("check", r[0]);
                    j.WriteString("result", r[1]);
                    j.WriteString("detail", r[2]);
                    j.WriteEndObject();
                }

                j.WriteEndArray();
                j.WriteEndObject();
            }
            else
            {
                o.Header("vseed serve --selftest");
                o.Table(new[] { "check", "result", "detail" }, rows);
                o.Line();
                // What was compared is one zoom level of two worlds plus a seam check, not every tile
                // of every zoom - so the sentence says which tiles it is talking about.
                o.Note(failures == 0
                    ? "All checks passed. The z=3 tiles compared above are the game's own texture, pixel for "
                      + "pixel, and the mosaic check says neighbouring tiles agree where they meet."
                    : failures + " check(s) FAILED.");
                o.Line();
            }

            return failures == 0 ? ExitCodes.Ok : ExitCodes.CheckFailed;
        }

        /// <summary>
        /// The events a tab that rejoins a run is handed: a run past the replay cap still replays every
        /// warning and its end (review of 2026-09-24). The cap used to apply to every event, so a run that
        /// had streamed 4,000 results - under three minutes of a cheap query - lost its later warnings and
        /// its <c>done</c> from the replay, and a page that reloaded never saw the failed last save and
        /// reconnected for as long as it stayed open.
        /// </summary>
        private static int ReplayCheck(List<string[]> rows)
        {
            SeedLab.Web.Search.SearchEventHub hub = new SeedLab.Web.Search.SearchEventHub();
            hub.Publish(new SeedLab.Web.Search.SearchEvent("started", new { id = "s0" }));
            for (int i = 0; i <= SeedLab.Web.Search.SearchEventHub.MaxHistory; i++)
            {
                hub.Publish(new SeedLab.Web.Search.SearchEvent("result", new { seed = i }));
                if (i % 100 == 0) hub.Publish(new SeedLab.Web.Search.SearchEvent("top", new { upTo = i }));
            }

            hub.Publish(new SeedLab.Web.Search.SearchEvent("warning", new { message = "the checkpoint could not be saved" }));
            hub.Publish(new SeedLab.Web.Search.SearchEvent("progress", new { scanned = 1 }));
            hub.Publish(new SeedLab.Web.Search.SearchEvent("done", new { status = "done" }));
            hub.Close();

            int results = 0, tops = 0, warnings = 0, dones = 0;
            string first = "", last = "";
            foreach (SeedLab.Web.Search.SearchEvent e in hub.Read(CancellationToken.None).ToBlockingEnumerable())
            {
                if (first.Length == 0) first = e.Type;
                last = e.Type;
                if (e.Type == "result") results++;
                else if (e.Type == "top") tops++;
                else if (e.Type == "warning") warnings++;
                else if (e.Type == "done") dones++;
            }

            bool ok = first == "started" && last == "done" && dones == 1 && warnings == 1 && tops == 1
                      && results == SeedLab.Web.Search.SearchEventHub.MaxHistory && hub.DroppedFromHistory == 1;
            rows.Add(new[]
            {
                "search replay past its cap keeps every warning and the end",
                ok ? "PASS" : "FAIL",
                Out.N(SeedLab.Web.Search.SearchEventHub.MaxHistory + 1) + " results, 41 tables, a warning, done -> replayed "
                + Out.N(results) + " results (" + hub.DroppedFromHistory + " dropped), " + tops + " table (the latest), "
                + warnings + " warning, " + dones + " done, last event '" + last + "'",
            });
            return ok ? 0 : 1;
        }

        // -------------------------------------------------------------------------------------------
        // The live server.
        //
        // Everything above this point tests code that is SUPPOSED to implement the server's promises.
        // These start a real Kestrel on a real loopback socket and check the promises themselves:
        // that nothing but 127.0.0.1 can reach it, that a foreign Host is refused, that no CORS header
        // is ever sent, that no spelling of a path reaches a file, and that the two new surfaces - the
        // location markers and the search panel - return what the engines return.
        // -------------------------------------------------------------------------------------------
        private static int LiveChecks(List<string[]> rows, CliRuntime rt)
        {
            int failures = 0;
            using CancellationTokenSource cts = new CancellationTokenSource();
            TaskCompletionSource<string> ready = new TaskCompletionSource<string>(
                TaskCreationOptions.RunContinuationsAsynchronously);

            WebServerOptions opt = new WebServerOptions
            {
                Port = 0,
                OpenBrowser = false,
                WorldGenVersion = Verified.WorldGenVersion,
                EngineVersion = Verified.EngineVersion,
                GameVersion = Verified.GameVersion,
                TileCacheBytes = 16L * 1024 * 1024,
                WorldCacheSeeds = 2,
                SeedReport = BuildReport,
                Locations = BuildLocations,
                LocationCacheSeeds = 2,
                LocationVocabulary = Vocabulary(),
                SearchLocationOracle = SearchOracle(out _),
                SearchThreads = SelfTestWorkers,
                // The same runtime as 'vseed serve' itself, so the checked server is the shipped one:
                // its search writes its checkpoint under --cache-dir, not the default cache root.
                Runtime = rt.Context,
                Log = _ => { },
                OnStarted = url => ready.TrySetResult(url),
            };

            Task<int> server = Task.Run(() => WebServer.RunAsync(opt, cts.Token));
            string baseUrl;
            try
            {
                if (!ready.Task.Wait(TimeSpan.FromSeconds(20)))
                {
                    rows.Add(new[] { "live server", "FAIL", "it did not start within 20 s" });
                    return 1;
                }

                baseUrl = ready.Task.Result.TrimEnd('/');
            }
            catch (Exception ex)
            {
                rows.Add(new[] { "live server", "FAIL", ex.GetType().Name + ": " + ex.Message });
                return 1;
            }

            Uri bound = new Uri(baseUrl);
            using HttpClient http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };

            try
            {
                failures += CheckLoopbackOnly(rows, bound.Port);
                failures += CheckHostHeader(rows, http, baseUrl);
                failures += CheckNoCors(rows, http, baseUrl);
                failures += CheckCrossSitePost(rows, http, baseUrl);
                failures += CheckContentRoot(rows, http, baseUrl);
                failures += CheckLocations(rows, http, baseUrl);
                failures += CheckSearch(rows, http, baseUrl);
            }
            catch (Exception ex)
            {
                rows.Add(new[] { "live checks", "FAIL", ex.GetType().Name + ": " + ex.Message });
                failures++;
            }
            finally
            {
                cts.Cancel();
                try { server.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { /* shutting down */ }
            }

            return failures;
        }

        /// <summary>
        /// The socket is on 127.0.0.1 and on nothing else. Kestrel is told <c>Listen(IPAddress.Loopback)</c>
        /// rather than <c>UseUrls</c> precisely so that no environment variable can widen it - this
        /// check is the proof, made by trying to reach the same port on this machine's own LAN address.
        /// </summary>
        private static int CheckLoopbackOnly(List<string[]> rows, int port)
        {
            List<System.Net.IPAddress> outside = new List<System.Net.IPAddress>();
            try
            {
                foreach (System.Net.IPAddress a in System.Net.Dns.GetHostAddresses(System.Net.Dns.GetHostName()))
                {
                    if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork
                        && !System.Net.IPAddress.IsLoopback(a))
                    {
                        outside.Add(a);
                    }
                }
            }
            catch (System.Net.Sockets.SocketException)
            {
                // No name resolution on this machine; the check simply has no address to try.
            }

            if (outside.Count == 0)
            {
                rows.Add(new[] { "bound to loopback only", "SKIPPED", "this machine has no non-loopback IPv4 address to try" });
                return 0;
            }

            int reachable = 0;
            foreach (System.Net.IPAddress a in outside)
            {
                using System.Net.Sockets.TcpClient c = new System.Net.Sockets.TcpClient();
                try
                {
                    if (c.ConnectAsync(a, port).Wait(TimeSpan.FromMilliseconds(700)) && c.Connected) reachable++;
                }
                catch (Exception)
                {
                    // Refused, unreachable or timed out: all of them mean "not listening there".
                }
            }

            bool ok = reachable == 0;
            rows.Add(new[]
            {
                "bound to loopback only",
                ok ? "PASS" : "FAIL",
                outside.Count + " non-loopback address(es) tried on port " + port + ", " + reachable + " answered",
            });
            return ok ? 0 : 1;
        }

        /// <summary>
        /// DNS rebinding: a loopback socket is still reachable from any web page if the browser is
        /// pointed at a hostname that resolves to 127.0.0.1. Only the names the user can have typed
        /// are accepted, and everything else gets 421 before a route is ever reached.
        /// </summary>
        private static int CheckHostHeader(List<string[]> rows, HttpClient http, string baseUrl)
        {
            (string Host, bool Allow)[] probes =
            {
                ("127.0.0.1", true), ("localhost", true), ("LOCALHOST", true),
                ("evil.example", false), ("seedlab.attacker.test", false), ("127.0.0.1.nip.io", false),
                ("0.0.0.0", false), ("[::ffff:127.0.0.1]", false),
            };

            int wrong = 0;
            List<string> detail = new List<string>();
            foreach ((string host, bool allow) in probes)
            {
                using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/meta");
                req.Headers.Host = host;
                using HttpResponseMessage res = http.Send(req);
                bool served = res.StatusCode != System.Net.HttpStatusCode.MisdirectedRequest;
                if (served != allow)
                {
                    wrong++;
                    detail.Add(host + " -> " + (int)res.StatusCode);
                }
            }

            bool ok = wrong == 0;
            rows.Add(new[]
            {
                "Host header refuses a rebinding name",
                ok ? "PASS" : "FAIL",
                probes.Length + " hosts, " + (probes.Length - wrong) + " answered as expected"
                + (detail.Count > 0 ? "; wrong: " + string.Join(", ", detail) : ""),
            });
            return ok ? 0 : 1;
        }

        /// <summary>
        /// There is no CORS policy, and the ABSENCE of one is what stops another origin reading these
        /// replies. A single <c>Access-Control-Allow-*</c> header anywhere would undo it, so every
        /// endpoint is asked with a foreign Origin and the headers are read back.
        /// </summary>
        private static int CheckNoCors(List<string[]> rows, HttpClient http, string baseUrl)
        {
            string[] paths = { "/", "/app.js", "/api/meta", "/api/stats", "/api/seed/resolve?q=1", "/api/at?seed=1&x=0&z=0" };
            List<string> bad = new List<string>();
            int csp = 0;

            foreach (string path in paths)
            {
                foreach (HttpMethod method in new[] { HttpMethod.Get, HttpMethod.Options })
                {
                    using HttpRequestMessage req = new HttpRequestMessage(method, baseUrl + path);
                    req.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
                    req.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "GET");
                    using HttpResponseMessage res = http.Send(req);
                    foreach (KeyValuePair<string, IEnumerable<string>> h in res.Headers)
                    {
                        if (h.Key.StartsWith("Access-Control-", StringComparison.OrdinalIgnoreCase))
                        {
                            bad.Add(path + " " + method + " " + h.Key);
                        }
                    }

                    if (method == HttpMethod.Get && res.Headers.TryGetValues("Content-Security-Policy", out _)) csp++;
                }
            }

            bool ok = bad.Count == 0 && csp == paths.Length;
            rows.Add(new[]
            {
                "no CORS, and a CSP on every reply",
                ok ? "PASS" : "FAIL",
                paths.Length * 2 + " requests with a foreign Origin, " + bad.Count + " CORS headers back; "
                + csp + " of " + paths.Length + " carried a Content-Security-Policy"
                + (bad.Count > 0 ? "; " + string.Join(", ", bad) : ""),
            });
            return ok ? 0 : 1;
        }

        /// <summary>
        /// A POST from another web page is refused before any route runs (2026-09-24). Loopback and the
        /// Host check do not stop it - a page on any site the user visits can send a form or a fetch to
        /// 127.0.0.1, and the browser puts the target's own host in the Host header - and Stop and
        /// "Retry saving" take no body, so nothing else stood in its way.
        ///
        /// <para>The discriminator is 403 against 404: the run id asked for does not exist, so a request
        /// the guard lets through reaches its route and gets 404, and one it refuses never gets there.
        /// Refused: a foreign Origin, "Origin: null", and <c>Sec-Fetch-Site: cross-site</c> or
        /// <c>same-site</c> with no Origin, on Stop, "Retry saving" and the search itself. Let through:
        /// this server's own Origin with <c>same-origin</c> (the page), and a request with neither header
        /// (curl, a script, this self-test's other checks).</para>
        /// </summary>
        private static int CheckCrossSitePost(List<string[]> rows, HttpClient http, string baseUrl)
        {
            (string Path, string? Origin, string? Site, bool Refuse)[] probes =
            {
                ("/api/search/s0/cancel", "https://evil.example", null, true),
                ("/api/search/s0/retry-save", "https://evil.example", "cross-site", true),
                ("/api/search/s0/retry-save", "null", null, true),
                ("/api/search/s0/cancel", null, "cross-site", true),
                ("/api/search/s0/retry-save", null, "same-site", true),
                ("/api/search/s0/cancel", "http://127.0.0.1:1", null, true),
                ("/api/search", "https://evil.example", "cross-site", true),
                ("/api/search/s0/cancel", baseUrl, "same-origin", false),
                ("/api/search/s0/retry-save", baseUrl, "same-origin", false),
                ("/api/search/s0/retry-save", null, null, false),
                ("/api/search/s0/cancel", null, "none", false),
            };

            int wrong = 0;
            List<string> detail = new List<string>();
            foreach ((string path, string? origin, string? site, bool refuse) in probes)
            {
                using HttpRequestMessage req = new HttpRequestMessage(HttpMethod.Post, baseUrl + path)
                {
                    Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json"),
                };
                if (origin != null) req.Headers.TryAddWithoutValidation("Origin", origin);
                if (site != null) req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", site);
                using HttpResponseMessage res = http.Send(req);
                bool refused = res.StatusCode == System.Net.HttpStatusCode.Forbidden;
                if (refused != refuse)
                {
                    wrong++;
                    detail.Add(path + " Origin " + (origin ?? "-") + " Sec-Fetch-Site " + (site ?? "-") + " -> " + (int)res.StatusCode);
                }
            }

            bool ok = wrong == 0;
            rows.Add(new[]
            {
                "a POST from another web page is refused",
                ok ? "PASS" : "FAIL",
                probes.Length + " POSTs to Stop, Retry saving and the search, " + (probes.Length - wrong)
                + " answered as expected (7 refused with 403, 4 reaching their route)"
                + (detail.Count > 0 ? "; wrong: " + string.Join(", ", detail) : ""),
            });
            return ok ? 0 : 1;
        }

        /// <summary>
        /// Nothing outside the content root, over the wire this time.
        ///
        /// <para>The server has no file system at all: a request path is looked up in a fixed table of
        /// four names, so every traversal spelling is simply a name that is not in a dictionary.</para>
        ///
        /// <para><b>An alias is not an escape.</b> ASP.NET routing matches a route case-insensitively
        /// and tolerates one trailing slash, and Kestrel normalises <c>%2e%2e</c> before routing ever
        /// sees it - so <c>/APP.JS</c>, <c>/app.css/</c> and <c>/%2e%2e/app.js</c> all answer. They are
        /// other spellings of a file that is INSIDE the root, which is why this check does not simply
        /// count 200s: it demands that each of them return the canonical asset byte for byte, and that
        /// nothing naming something outside the root return anything at all.</para>
        /// </summary>
        private static int CheckContentRoot(List<string[]> rows, HttpClient http, string baseUrl)
        {
            string[] mustMiss =
            {
                "/../Program.cs", "/../../src/SeedLab.Cli/Program.cs", "/wwwroot/app.js",
                "/app.js/../../secret", "/.env", "/data/locations.json", "/appsettings.json",
                "/%2e%2e%2fProgram.cs", "/..%5cProgram.cs", "/app.js.map",
                "/app.js/extra", "/C:/Windows/win.ini", "//etc/passwd",
            };
            string[] mustHit = { "/", "/index.html", "/app.css", "/app.js", "/favicon.svg" };

            // (spelling, the canonical asset it must be byte-identical to)
            (string Alias, string Canonical)[] aliases =
            {
                ("/APP.JS", "/app.js"), ("/App.Css", "/app.css"), ("/app.css/", "/app.css"),
                ("/%2e%2e/app.js", "/app.js"), ("/index.html", "/"),
                // The HTTP CLIENT collapses this one before it is sent, so the server is asked for
                // "/app.js" and answers with it. Listing it as an escape would be testing System.Uri.
                ("/../app.js", "/app.js"),
            };

            byte[] Body(string path)
            {
                using HttpResponseMessage r = http.Send(new HttpRequestMessage(HttpMethod.Get, baseUrl + path));
                return r.IsSuccessStatusCode ? r.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult() : Array.Empty<byte>();
            }

            List<string> leaked = new List<string>();
            foreach (string path in mustMiss)
            {
                using HttpResponseMessage res = http.Send(new HttpRequestMessage(HttpMethod.Get, baseUrl + path));
                if (res.IsSuccessStatusCode) leaked.Add(path + " -> " + (int)res.StatusCode);
            }

            int served = 0;
            foreach (string path in mustHit)
            {
                if (Body(path).Length > 0) served++;
            }

            List<string> wrongAlias = new List<string>();
            foreach ((string alias, string canonical) in aliases)
            {
                byte[] a = Body(alias);
                byte[] c = Body(canonical);
                if (a.Length == 0 || !a.AsSpan().SequenceEqual(c)) wrongAlias.Add(alias);
            }

            bool ok = leaked.Count == 0 && served == mustHit.Length && wrongAlias.Count == 0;
            rows.Add(new[]
            {
                "nothing outside the content root",
                ok ? "PASS" : "FAIL",
                mustMiss.Length + " escapes tried, " + leaked.Count + " answered; the " + mustHit.Length
                + " real names all served; " + aliases.Length + " alternative spellings all returned the "
                + "canonical asset byte for byte"
                + (leaked.Count > 0 ? "; LEAKED: " + string.Join(", ", leaked) : "")
                + (wrongAlias.Count > 0 ? "; WRONG BODY: " + string.Join(", ", wrongAlias) : ""),
            });
            return ok ? 0 : 1;
        }

        /// <summary>
        /// The markers the map draws are the placement engine's own answer, unchanged by the provider,
        /// the cache, the JSON and the transport.
        ///
        /// <para>The comparison is on the float BITS of x, y and z, not on a rounded print: a single
        /// precision lost between the engine and the browser would move a marker and nobody would
        /// notice. It is the same check the acceptance gate makes against the game's own .db2, applied
        /// to the last few metres of the pipeline.</para>
        /// </summary>
        private static int CheckLocations(List<string[]> rows, HttpClient http, string baseUrl)
        {
            int seed = Verified.Fixtures[0].Seed;
            System.Text.Json.JsonElement report;
            Stopwatch sw = Stopwatch.StartNew();
            while (true)
            {
                using HttpResponseMessage res = http.Send(new HttpRequestMessage(
                    HttpMethod.Get, baseUrl + "/api/locations?seed=" + seed + "&set=core&start=1"));
                string body = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(body);
                string status = doc.RootElement.GetProperty("status").GetString() ?? "";
                if (status == "ready") { report = doc.RootElement.GetProperty("report").Clone(); break; }
                if (status != "computing")
                {
                    rows.Add(new[] { "location markers vs the placement engine", "SKIPPED", status + ": "
                        + (doc.RootElement.TryGetProperty("error", out System.Text.Json.JsonElement e) ? e.GetString() : "") });
                    return 0;
                }

                if (sw.Elapsed > TimeSpan.FromSeconds(180))
                {
                    rows.Add(new[] { "location markers vs the placement engine", "FAIL", "still computing after 180 s" });
                    return 1;
                }

                Thread.Sleep(200);
            }

            // The same run, straight from the engine, exactly as 'vseed locations --type boss,trader'
            // makes it.
            LocationCatalog cat = Catalog();
            List<ZoneLocationEntry> wanted = cat.Select(LocationKind.Boss | LocationKind.Trader);
            int prefix = cat.PrefixFor(wanted);
            WorldLocations wl = WorldLocations.Build(seed, Verified.WorldGenVersion, cat.AltBiomes, -1);
            PlacementResult truth = LocationPlacementEngine.Run(wl.Generator, wl.Field, cat.Table,
                new PlacementOptions { StopAfterOrderedIndex = prefix - 1, AltBiomesComputed = true });

            HashSet<string> names = new HashSet<string>(StringComparer.Ordinal);
            foreach (ZoneLocationEntry e in wanted) names.Add(e.PrefabName);
            List<LocationInstanceResult> expect = new List<LocationInstanceResult>();
            foreach (LocationInstanceResult i in truth.Instances)
            {
                if (names.Contains(i.PrefabName)) expect.Add(i);
            }

            System.Text.Json.JsonElement types = report.GetProperty("types");
            System.Text.Json.JsonElement got = report.GetProperty("instances");
            long compared = 0, bad = 0;
            if (got.GetArrayLength() != expect.Count)
            {
                bad = Math.Abs(got.GetArrayLength() - expect.Count);
            }
            else
            {
                for (int k = 0; k < expect.Count; k++)
                {
                    System.Text.Json.JsonElement g = got[k];
                    LocationInstanceResult w = expect[k];
                    string prefab = types[g.GetProperty("t").GetInt32()].GetProperty("prefab").GetString() ?? "";
                    compared++;
                    if (!string.Equals(prefab, w.PrefabName, StringComparison.Ordinal)
                        || BitConverter.SingleToInt32Bits(g.GetProperty("x").GetSingle()) != BitConverter.SingleToInt32Bits(w.X)
                        || BitConverter.SingleToInt32Bits(g.GetProperty("z").GetSingle()) != BitConverter.SingleToInt32Bits(w.Z)
                        || BitConverter.SingleToInt32Bits(g.GetProperty("y").GetSingle()) != BitConverter.SingleToInt32Bits(w.Y)
                        || g.GetProperty("zx").GetInt32() != w.Zone.x
                        || g.GetProperty("zz").GetInt32() != w.Zone.y)
                    {
                        bad++;
                    }
                }
            }

            // Every m_unique type with more than one surviving candidate must be flagged as a candidate
            // set. A marker that claimed to BE Haldor would be the one lie this tool must not tell.
            int unflagged = 0;
            foreach (UniqueCandidateSet u in truth.UniqueCandidates)
            {
                if (u.WinnerIsPredictable || !names.Contains(u.Location.PrefabName)) continue;
                bool flagged = false;
                foreach (System.Text.Json.JsonElement t in types.EnumerateArray())
                {
                    if (t.GetProperty("prefab").GetString() == u.Location.PrefabName
                        && t.GetProperty("candidateSet").GetBoolean())
                    {
                        flagged = true;
                    }
                }

                if (!flagged) unflagged++;
            }

            bool ok = bad == 0 && unflagged == 0;
            rows.Add(new[]
            {
                "location markers vs the placement engine",
                ok ? "PASS" : "FAIL",
                Out.N(compared - bad) + " of " + Out.N(compared) + " instances bit-identical in prefab, "
                + "x/y/z and zone; " + truth.UniqueCandidates.Count + " unique types, " + unflagged + " not flagged as candidates",
            });
            return (ok ? 0 : 1) + CheckLocationNames(rows, http, baseUrl, types);
        }

        /// <summary>
        /// The names the map draws are honest about being names.
        ///
        /// <para>Two assertions on the rows, and they are the ones that catch the failure mode this
        /// whole change exists to avoid - a tool that stops saying <c>GDKing</c> and starts saying
        /// something it made up:</para>
        /// <list type="number">
        /// <item>a row that HAS a display name states where the name came from. A caption with no
        /// provenance cannot be checked by the reader, and the card's "name from" row would be
        /// blank.</item>
        /// <item>no row's display name equals its prefab - <b>except <c>Bonemass</c></b>, where the
        /// game genuinely localizes <c>$enemy_bonemass</c> to the same string the prefab is spelled
        /// with. The exemption is that one literal name and not "any row with a real source",
        /// because the rule it is protecting is precisely that an unnamed place must come back with
        /// <c>displayName: null</c> and never with its own prefab dressed up as a name.</item>
        /// </list>
        ///
        /// <para>Then the vocabulary on <c>/api/meta</c>, without which the page has nothing to turn
        /// a typed "The Elder" into a prefab with.</para>
        /// </summary>
        private static int CheckLocationNames(List<string[]> rows, HttpClient http, string baseUrl,
                                              System.Text.Json.JsonElement types)
        {
            const string BonemassPrefab = "Bonemass";

            List<string> sourceless = new List<string>();
            List<string> echoed = new List<string>();
            int named = 0;
            foreach (System.Text.Json.JsonElement t in types.EnumerateArray())
            {
                string prefab = t.GetProperty("prefab").GetString() ?? "";
                string? display = t.GetProperty("displayName").GetString();
                if (display == null) continue;

                named++;
                if (string.IsNullOrEmpty(t.GetProperty("displayNameSource").GetString())) sourceless.Add(prefab);
                if (string.Equals(display, prefab, StringComparison.Ordinal)
                    && !string.Equals(prefab, BonemassPrefab, StringComparison.Ordinal))
                {
                    echoed.Add(prefab);
                }
            }

            int groups = 0, spellings = 0;
            string? elder = null;
            string metaError = "";
            try
            {
                using HttpResponseMessage res = http.Send(new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/meta"));
                using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(
                    res.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                System.Text.Json.JsonElement loc = doc.RootElement.GetProperty("locations");
                groups = loc.GetProperty("groups").GetArrayLength();
                System.Text.Json.JsonElement map = loc.GetProperty("names");
                foreach (System.Text.Json.JsonProperty _ in map.EnumerateObject()) spellings++;
                if (map.TryGetProperty("The Elder", out System.Text.Json.JsonElement e)) elder = e.GetString();
            }
            catch (Exception ex)
            {
                metaError = ex.GetType().Name + ": " + ex.Message;
            }

            // 12 groups today: bosses, traders, the nine legend biomes and the several-biome residue.
            // The Ocean group is one of the twelve and is EMPTY in this build, which is why it is
            // shipped and omitted by the renderer rather than left out here. The count is read from
            // the taxonomy rather than written down, so adding a legend biome moves both sides at
            // once instead of failing this check for a reason that is not a defect.
            int expectGroups = SeedLab.Search.Locations.LocationGroupTaxonomy.All.Count;
            bool metaOk = metaError.Length == 0 && groups == expectGroups && spellings > 0
                          && string.Equals(elder, "GDKing", StringComparison.Ordinal);
            bool ok = sourceless.Count == 0 && echoed.Count == 0 && metaOk;
            rows.Add(new[]
            {
                "location names say where they came from",
                ok ? "PASS" : "FAIL",
                named + " of " + types.GetArrayLength() + " rows named, all with a source"
                + (sourceless.Count > 0 ? " EXCEPT " + string.Join(", ", sourceless) : "")
                + "; no name echoes its own prefab (Bonemass exempt - the game uses that string)"
                + (echoed.Count > 0 ? "; ECHOED: " + string.Join(", ", echoed) : "")
                + "; /api/meta ships " + groups + " of " + expectGroups + " groups and " + Out.N(spellings)
                + " spellings, 'The Elder' -> " + (elder ?? "(absent)")
                + (metaError.Length > 0 ? "; META FAILED: " + metaError : ""),
            });
            return ok ? 0 : 1;
        }

        /// <summary>
        /// The search panel's results are the engine's results.
        ///
        /// <para>A query is posted exactly as the page posts it; the run's own <c>started</c> event
        /// hands back the query FILE it became, and that file is then read with
        /// <see cref="QueryReader"/> and run through <see cref="SearchRun"/> in this process - the four
        /// steps <c>vseed search</c> takes. The two seed lists have to be identical, in order,
        /// including the scores, or the page and the terminal are answering different questions.</para>
        /// </summary>
        private static int CheckSearch(List<string[]> rows, HttpClient http, string baseUrl)
        {
            const string body = @"{
              ""name"": ""selftest"",
              ""goals"": [
                { ""target"": ""biome:Swamp"", ""metric"": ""nearest_distance"", ""test"": ""near"",
                  ""value"": 2500, ""importance"": ""must"" },
                { ""target"": ""world:ocean_share"", ""metric"": ""ocean_share"", ""test"": ""at_most"",
                  ""value"": 0.7, ""importance"": ""nice"" }
              ],
              ""budgetSeeds"": 384, ""budgetSeconds"": 0, ""keep"": 200, ""gridSpacingM"": 192,
              ""order"": ""shuffled"", ""rangeStart"": -2147483648, ""rangeEnd"": 2147483647,
              ""blockSize"": null, ""screen"": ""off"", ""confirmed"": true, ""acceptScanOrder"": true
            }";
            // "blockSize": null is what the page sends for its empty Block size box (2026-09-24): the
            // block size is then decided by the same rule as the terminal's, and the query file the
            // page produces must say nothing about it - checked below, strictly, for the comma that
            // omitting the last key of "search" could leave behind.
            // The last three fields are what the PAGE sends once the user has answered its dialogs,
            // and they are here because the server now runs the whole preflight and refuses a run
            // that has not been confirmed - a hand-written POST cannot walk past it, which is the
            // point. Without them this check got a 400 'kind: confirm' instead of a search.
            //
            // "screen": "off" is not about the dialog. This check compares the page's seed list with
            // one produced HERE by a direct SearchRun at the grid the query names, so the two sides
            // have to be measuring on the same grid. Left on "auto", the policy raises this query's
            // grid (its nearest_distance must-have is one no coarse grid measures safely), and the
            // two sides would then differ for a reason that has nothing to do with what this check
            // exists to catch. The raise itself is covered by 'vseed search', where it is confirmed
            // and applied.

            using HttpRequestMessage post = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/search")
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            };
            using HttpResponseMessage started = http.Send(post);
            string startedBody = started.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!started.IsSuccessStatusCode)
            {
                rows.Add(new[] { "search panel vs the engine", "FAIL", (int)started.StatusCode + ": " + startedBody });
                return 1;
            }

            string id;
            using (System.Text.Json.JsonDocument d = System.Text.Json.JsonDocument.Parse(startedBody))
            {
                id = d.RootElement.GetProperty("id").GetString() ?? "";
            }

            // Read the whole stream. It ends when the run does, so this is also the cancellation and
            // completion path being exercised.
            string stream;
            using (HttpResponseMessage sr = http.Send(new HttpRequestMessage(HttpMethod.Get, baseUrl + "/api/search/" + id + "/stream")))
            {
                stream = sr.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            }

            string? queryJson = null;
            List<int> pageSeeds = new List<int>();
            List<double> pageScores = new List<double>();
            string status = "";
            long scanned = 0;
            foreach (string chunk in stream.Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            {
                int dataAt = chunk.IndexOf("\ndata: ", StringComparison.Ordinal);
                if (dataAt < 0) continue;
                string name = chunk.Substring("event: ".Length, dataAt - "event: ".Length);
                string data = chunk.Substring(dataAt + "\ndata: ".Length);
                using System.Text.Json.JsonDocument d = System.Text.Json.JsonDocument.Parse(data);
                if (name == "started") queryJson = d.RootElement.GetProperty("queryJson").GetString();
                else if (name == "done")
                {
                    status = d.RootElement.GetProperty("status").GetString() ?? "";
                    scanned = d.RootElement.GetProperty("scanned").GetInt64();
                }
                else if (name == "top")
                {
                    pageSeeds.Clear();
                    pageScores.Clear();
                    foreach (System.Text.Json.JsonElement r in d.RootElement.GetProperty("results").EnumerateArray())
                    {
                        pageSeeds.Add(r.GetProperty("seed").GetInt32());
                        pageScores.Add(r.GetProperty("score").GetDouble());
                    }
                }
            }

            if (queryJson == null || status != "done")
            {
                rows.Add(new[] { "search panel vs the engine", "FAIL", "the stream did not finish cleanly (status '" + status + "')" });
                return 1;
            }

            // "Retry saving" on this real, finished run: the page's own request (its Origin, same-origin)
            // is answered - there is nothing to save again, so "saved" - and the same request from
            // another page is refused before it reaches the run.
            int retryStatus, foreignStatus;
            bool savedFlag = false;
            using (HttpRequestMessage mine = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/search/" + id + "/retry-save"))
            {
                mine.Headers.TryAddWithoutValidation("Origin", baseUrl);
                mine.Headers.TryAddWithoutValidation("Sec-Fetch-Site", "same-origin");
                using HttpResponseMessage res = http.Send(mine);
                retryStatus = (int)res.StatusCode;
                string b = res.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                try
                {
                    using System.Text.Json.JsonDocument d = System.Text.Json.JsonDocument.Parse(b);
                    savedFlag = d.RootElement.TryGetProperty("saved", out System.Text.Json.JsonElement sv) && sv.GetBoolean();
                }
                catch (System.Text.Json.JsonException)
                {
                }
            }

            using (HttpRequestMessage theirs = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/search/" + id + "/retry-save"))
            {
                theirs.Headers.TryAddWithoutValidation("Origin", "https://evil.example");
                using HttpResponseMessage res = http.Send(theirs);
                foreignStatus = (int)res.StatusCode;
            }

            bool retryOk = retryStatus == 200 && savedFlag && foreignStatus == 403;
            rows.Add(new[]
            {
                "search panel: Retry saving, from the page only",
                retryOk ? "PASS" : "FAIL",
                "the page's own request on a finished run -> " + retryStatus + (savedFlag ? " saved (nothing was missing)" : " NOT saved")
                + "; the same request from another origin -> " + foreignStatus,
            });
            if (!retryOk) return 1;

            // An empty Block size box must reach the query file as NO block_size - so 'vseed search' on
            // the exported file sizes it automatically too, and a --resume keeps the checkpoint's - and
            // leave no dangling comma after "threads". QueryReader accepts trailing commas, so it cannot
            // catch one; a strict parse (no trailing commas, no comments) can.
            bool strictOk;
            try
            {
                using System.Text.Json.JsonDocument strict = System.Text.Json.JsonDocument.Parse(queryJson);
                strictOk = true;
            }
            catch (System.Text.Json.JsonException)
            {
                strictOk = false;
            }

            bool autoOk = strictOk && !queryJson.Contains("block_size", StringComparison.Ordinal);
            rows.Add(new[]
            {
                "search panel: automatic block size",
                autoOk ? "PASS" : "FAIL",
                autoOk
                    ? "an empty Block size box leaves block_size out of the query file, which parses strictly"
                    : (strictOk ? "the query file names a block_size the page never set" : "the query file is not strict JSON (a dangling comma?)"),
            });
            if (!autoOk) return 1;

            // ---- the same query, the terminal's way ---------------------------------------------------
            Query q = QueryReader.Parse(queryJson, "selftest");
            SeedLab.Search.Locations.ILocationOracle oracle = SearchOracle(out _);
            CompiledQuery cq = CompiledQuery.Compile(q, oracle);
            string hash = QueryReader.Hash(q);
            ulong key = q.Search.Key ?? ScanPlan.KeyFromHash(hash);

            // The block size by the rule the session uses: the query file carries none, so it is the
            // automatic size for this side's worker count, which need not be the page's. Any size would
            // do for this comparison - a completed run's results do not depend on it (measured
            // 2026-09-24) - so the two sides must agree whichever sizes they chose.
            long limit = new ScanPlan(q.Search.Order, q.Search.From, q.Search.To, key,
                                      SearchSpec.DefaultBlockSize, q.Search.Seeds).Limit;
            int blockSize = BlockSizing.Decide(q.Search.BlockSize, limit, SelfTestWorkers).Size;
            ScanPlan plan = new ScanPlan(q.Search.Order, q.Search.From, q.Search.To, key, blockSize, q.Search.Seeds);
            SearchRun run = new SearchRun(cq, plan, oracle, SelfTestWorkers);
            // The parameters are (startBlock, writer, checkpoint, checkpointPath, checkpointInterval,
            // wallBudget, onProgress). They used to be given as (..., FromSeconds(30), Zero, null),
            // which set a 30 s CHECKPOINT interval on a run with no checkpoint path - a no-op - and
            // a wall budget of Zero, which SearchRun reads as "no time limit at all". A selftest
            // check that cannot time out is not a check; the 30 s belongs to the wall budget.
            SearchOutcome outcome = run.Run(0, null, null, null, TimeSpan.Zero, TimeSpan.FromSeconds(30), null);

            List<int> cliSeeds = new List<int>();
            List<double> cliScores = new List<double>();
            foreach (SeedResult r in outcome.Top) { cliSeeds.Add(r.Seed); cliScores.Add(r.Score); }

            bool same = pageSeeds.Count == cliSeeds.Count && scanned == outcome.Evaluated;
            if (same)
            {
                for (int i = 0; i < cliSeeds.Count; i++)
                {
                    if (pageSeeds[i] != cliSeeds[i] || Math.Abs(pageScores[i] - cliScores[i]) > 1e-12) { same = false; break; }
                }
            }

            rows.Add(new[]
            {
                "search panel vs the engine",
                same ? "PASS" : "FAIL",
                Out.N(scanned) + " seeds, " + pageSeeds.Count + " hits from the page vs " + cliSeeds.Count
                + " from a direct SearchRun of the query file the page produced"
                + (same ? ", identical in order and score" : " - THEY DIFFER"),
            });
            return same ? 0 : 1;
        }

        private static int CountAssets()
        {
            int n = 0;
            foreach (StaticAssets.Asset _ in StaticAssets.All) n++;
            return n;
        }
    }
}
