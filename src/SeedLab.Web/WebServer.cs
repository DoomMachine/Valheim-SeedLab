using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SeedLab.Runtime;
using SeedLab.Runtime.Execution;
using SeedLab.Runtime.Throttle;
using SeedLab.WorldGen;
using SeedLab.Web.Api;
using SeedLab.Web.Locations;
using SeedLab.Web.Search;
using SeedLab.Web.Tiles;

namespace SeedLab.Web
{
    /// <summary>How <c>vseed serve</c> configures the server.</summary>
    public sealed class WebServerOptions
    {
        /// <summary>0 lets the operating system pick a free port; the chosen one is reported.</summary>
        public int Port { get; set; }

        public bool OpenBrowser { get; set; } = true;

        public int WorldGenVersion { get; set; } = 2;

        public string EngineVersion { get; set; } = "";

        public string GameVersion { get; set; } = "";

        public long TileCacheBytes { get; set; } = 128L * 1024 * 1024;

        public int WorldCacheSeeds { get; set; } = 4;

        /// <summary>Where the seed panel's figures come from. Required.</summary>
        public SeedReportProvider SeedReport { get; set; } = null!;

        /// <summary>
        /// Where the map's location markers come from. Optional: null means this build cannot place
        /// locations, which the page reports as such rather than drawing an empty map and implying
        /// the world has none.
        /// </summary>
        public LocationsProvider? Locations { get; set; }

        /// <summary>How many seeds keep a computed location set in memory.</summary>
        public int LocationCacheSeeds { get; set; } = 4;

        /// <summary>
        /// How the location listing is grouped and every spelling that means a place, for
        /// <c>/api/meta</c>. Optional, and null in exactly the case
        /// <see cref="Locations"/> is: this project can read neither the dumped location table nor
        /// the dumped names, so both arrive already decided from the CLI.
        /// </summary>
        public LocationVocabulary? LocationVocabulary { get; set; }

        /// <summary>
        /// The location oracle the SEARCH engine talks to. Unavailable by default, which is what
        /// <c>vseed search</c> also uses: a location goal then refuses with its reason instead of
        /// scoring every seed zero.
        /// </summary>
        public SeedLab.Search.Locations.ILocationOracle? SearchLocationOracle { get; set; }

        /// <summary>
        /// A CEILING on the workers a search may take; 0 for none.
        ///
        /// <para>It used to mean "the thread count to use when the query asks for 0", which stopped
        /// being the right shape when the worker count became the runtime's
        /// <see cref="SeedLab.Runtime.Execution.WorkerPlan"/>: a fixed number cannot be lowered by the
        /// auto-throttle or by the memory guard, so a host that passed one pinned the page to a figure
        /// the rest of the process had already moved away from. A caller that shares its
        /// <see cref="Runtime"/> does not need this at all.</para>
        /// </summary>
        public int SearchThreads { get; set; }

        /// <summary>
        /// The runtime layer this server plans against: the hardware probe, the worker plan, the cache
        /// root, the auto-throttle, the estimator and the self-test.
        ///
        /// <para>Null means "start one for me", which is what keeps this server usable from a host that
        /// has not been taught about the runtime yet. A caller that owns one - <c>vseed serve</c>, once
        /// it passes its own - shares it, and then the terminal and the page are literally the same
        /// probe, the same cache root and the same throttle rather than two that happen to agree.</para>
        /// </summary>
        public RuntimeContext? Runtime { get; set; }

        /// <summary>
        /// Where a results file the page names is written.
        ///
        /// <para>The browser names a FILE; this names the only directory those files can land in. It is
        /// deliberately not the cache root - the cache root's contract is that deleting it loses nothing
        /// the user asked to keep, and a results file is exactly that - and deliberately not a path the
        /// browser can choose.</para>
        ///
        /// <para>Null means the default, <c>&lt;working directory&gt;\seedlab-results</c>: explicit,
        /// next to the process the way <c>vseed search --out</c> writes next to the terminal, shown on
        /// the page, and created only when a run actually names a file. Set it to the empty string to
        /// turn the output controls off entirely and have the panel say why.</para>
        /// </summary>
        public string? ResultsDirectory { get; set; }

        /// <summary>The directory a results file lands in, after the default has been applied.</summary>
        public string ResolvedResultsDirectory =>
            ResultsDirectory ?? System.IO.Path.Combine(Environment.CurrentDirectory, "seedlab-results");

        /// <summary>Lines for the terminal. Defaults to <see cref="Console.Out"/>.</summary>
        public Action<string>? Log { get; set; }

        /// <summary>
        /// Called with the bound URL once the socket is listening, before the run blocks.
        ///
        /// <para>It exists so a caller that started the server on port 0 can find out which port the
        /// operating system gave it - which is what <c>vseed serve --selftest</c> needs in order to
        /// check the Host-header refusal, the absence of CORS and the content-root containment
        /// against a REAL socket rather than against the code that is supposed to implement them.</para>
        /// </summary>
        public Action<string>? OnStarted { get; set; }
    }

    /// <summary>
    /// The local web UI's server.
    ///
    /// <para><b>Local means local.</b> Kestrel is told to listen on
    /// <see cref="IPAddress.Loopback"/> and nothing else, so the socket is not reachable from the
    /// network whatever <c>ASPNETCORE_URLS</c> or any other environment variable says. There is no
    /// CORS policy - the absence of one is what stops another origin from reading these responses -
    /// and every reply carries a Content-Security-Policy that forbids the page from contacting
    /// anything but this server, so "works offline" is enforced rather than hoped for. The Host
    /// header is checked as well, which is what closes the DNS-rebinding hole that loopback binding
    /// alone leaves open.</para>
    ///
    /// <para><b>What it writes, and nowhere else.</b> This used to say "it never writes anything", and
    /// that stopped being true when decision 12 made every CLI flag reachable from the page: a results
    /// file, its rotation, its compression and its ceiling are flags about a file. Three places, all
    /// named and all bounded:</para>
    /// <list type="number">
    /// <item>the <b>results file</b>, only when the page names one, only inside
    /// <see cref="WebServerOptions.ResultsDirectory"/>, and only as a bare file name -
    /// <see cref="SeedLab.Web.Search.QueryTranslator.Clean"/> refuses a separator, a drive, a
    /// <c>..</c> and any extension but <c>.jsonl</c>, <c>.csv</c> and <c>.json</c>;</item>
    /// <item>the <b>checkpoint</b> and its kept-set snapshot, in the runtime cache root, which is tool
    /// state rather than the user's data;</item>
    /// <item>the <b>tile cache's</b> second tier, also in the cache root, which is a cache of a pure
    /// function and can be deleted at any moment.</item>
    /// </list>
    /// <para>Nothing is written beside the working directory, nothing is written to a path a browser
    /// chose, and the only file-shaped thing SERVED is the four embedded assets in
    /// <see cref="StaticAssets"/>. The user's saves and Steam Cloud folders are not read either: the
    /// generator is the only source of world data here.</para>
    /// </summary>
    public sealed class WebServer
    {
        private readonly WebServerOptions _options;
        private readonly WorldCache _worlds;
        private readonly TileRenderer _tiles;
        private readonly TileCache _tileCache;
        private readonly SemaphoreSlim _renderSlots;
        private readonly SearchRegistry _searches = new SearchRegistry();
        private readonly ISeedSearchEngine _engine;
        private readonly ReportCache _reports;
        private readonly LocationsCache _locations;
        private readonly DateTime _started = DateTime.UtcNow;
        private readonly RuntimeContext _runtime;
        private readonly bool _ownsRuntime;
        private readonly EngineSearchEngine _engineImpl;
        private readonly object _throttleLock = new object();
        private readonly List<object> _throttleChanges = new List<object>();

        private static readonly JsonSerializerOptions Json = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals,
        };

        public WebServer(WebServerOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            if (_options.SeedReport == null) throw new ArgumentException("SeedReport provider is required.", nameof(options));

            _ownsRuntime = _options.Runtime == null;
            _runtime = _options.Runtime ?? RuntimeContext.Start(new RuntimeOptions
            {
                Log = line => (_options.Log ?? Console.Out.WriteLine)("  " + line),
            });

            _worlds = new WorldCache(_options.WorldCacheSeeds);

            // Rendering used to size itself from Environment.ProcessorCount, which is a number about
            // the machine and not about what SeedLab has been allowed to take. The renderer and the
            // search are in ONE process: at ProcessorCount they fight, and the map freezes while a
            // search runs. Both now come out of the same WorkerPlan, so the mode the user chose - and
            // the auto-throttle that drops it while Valheim is running - governs the whole process.
            WorkerPlan render = _runtime.PlanWorkers(WorkTier.BiomeGrid, WorkerFootprints.CellsForSpacing(96));
            _tiles = new TileRenderer(_worlds, Math.Max(1, Math.Min(4, render.Workers)));
            _tileCache = new TileCache(_options.TileCacheBytes,
                                       System.IO.Path.Combine(_runtime.Cache.Tiles, "v" + _options.WorldGenVersion));
            _renderSlots = new SemaphoreSlim(Math.Max(1, render.Workers));

            _reports = new ReportCache(_options.SeedReport);
            _locations = new LocationsCache(_options.Locations, _options.LocationCacheSeeds);
            _engineImpl = new EngineSearchEngine(
                _options.SearchLocationOracle ?? SeedLab.Search.Locations.UnavailableLocationOracle.Instance,
                _options.EngineVersion,
                _runtime,
                string.IsNullOrEmpty(_options.ResultsDirectory) && _options.ResultsDirectory != null
                    ? null
                    : _options.ResolvedResultsDirectory,
                _options.SearchThreads);
            _engine = _engineImpl;

            // Decision 7 asks for any change of mode to be ANNOUNCED, not applied in silence. The page
            // has no socket of its own to be pushed down, so every change is kept here and handed to
            // /api/runtime, which the panel polls at the throttle's own interval and turns into a banner.
            _runtime.Throttle.Changed += change =>
            {
                lock (_throttleLock)
                {
                    _throttleChanges.Add(new
                    {
                        atUtc = DateTime.UtcNow,
                        from = ResourceModes.Name(change.From),
                        to = ResourceModes.Name(change.To),
                        because = change.Reason,
                        line = change.Message,
                    });
                    while (_throttleChanges.Count > 8) _throttleChanges.RemoveAt(0);
                }
            };
        }

        /// <summary>The URL the server ended up on, once it has started.</summary>
        public string Url { get; private set; } = "";

        public static async Task<int> RunAsync(WebServerOptions options, CancellationToken ct = default)
        {
            WebServer server = new WebServer(options);
            return await server.StartAndWaitAsync(ct).ConfigureAwait(false);
        }

        private async Task<int> StartAndWaitAsync(CancellationToken ct)
        {
            Action<string> log = _options.Log ?? Console.Out.WriteLine;

            WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = "vseed",
                EnvironmentName = Environments.Production,
            });

            builder.Logging.ClearProviders();
            builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = null; });
            builder.Logging.SetMinimumLevel(LogLevel.Warning);

            // A startup failure - a taken port, above all - is already raised as an exception out of
            // RunAsync and printed by the caller as one readable line. Left alone, the host ALSO dumps
            // the whole three-deep Kestrel stack trace to the console first, so the user reads 2 KB of
            // framework noise before the sentence that tells them what to do. Silence this one category;
            // nothing is lost, because the exception it is describing is still thrown.
            builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", LogLevel.None);

            builder.WebHost.ConfigureKestrel(k =>
            {
                // Loopback and only loopback. Listen() is used rather than UseUrls() precisely because
                // it cannot be overridden by configuration or by an environment variable.
                k.Listen(IPAddress.Loopback, _options.Port);
                k.AddServerHeader = false;
                k.Limits.MaxRequestBodySize = 1L << 20;
            });

            WebApplication app = builder.Build();
            Configure(app);

            await app.StartAsync(ct).ConfigureAwait(false);

            IServerAddressesFeature? addresses = app.Services
                .GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>()
                .Features.Get<IServerAddressesFeature>();
            string address = "http://127.0.0.1:" + _options.Port;
            if (addresses != null)
            {
                foreach (string a in addresses.Addresses) { address = a; break; }
            }

            Url = address.Replace("[::1]", "127.0.0.1").Replace("localhost", "127.0.0.1");
            log("SeedLab is serving at " + Url);
            log("  bound to 127.0.0.1 only; no CORS, no external requests");
            log("  cache       " + _runtime.Cache.Path);
            log("  results     " + (_options.ResultsDirectory == ""
                ? "(off - the page cannot keep a results file)"
                : _options.ResolvedResultsDirectory + " (created when a run names a file)"));
            log("  mode        " + SeedLab.Runtime.Execution.ResourceModes.Describe(_runtime.EffectiveMode)
                + (_runtime.Throttle.IsThrottled ? "  [auto-throttled by " + _runtime.Throttle.ThrottledBy + "]" : ""));
            foreach (string line in _runtime.StartupLines()) log("  " + line);
            log("  press Ctrl+C to stop");

            _options.OnStarted?.Invoke(Url);
            if (_options.OpenBrowser) TryOpenBrowser(Url, log);

            // The token stops the server as well as the start: a caller that owns the lifetime - the
            // self-test - cancels and gets its port back. Ctrl+C still works through the host's own
            // lifetime when the token is 'none', which is the ordinary 'vseed serve' case.
            await app.WaitForShutdownAsync(ct).ConfigureAwait(false);
            _searches.CancelAll();
            if (_ownsRuntime) _runtime.Dispose();
            return 0;
        }

        private void Configure(WebApplication app)
        {
            app.Use(async (ctx, next) =>
            {
                // Host check: a loopback socket can still be reached by a page on any site if the
                // browser is pointed at a hostname that resolves to 127.0.0.1. Only the two names the
                // user can have typed are accepted.
                string host = ctx.Request.Host.Host;
                if (!string.Equals(host, "127.0.0.1", StringComparison.Ordinal)
                    && !string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(host, "::1", StringComparison.Ordinal))
                {
                    ctx.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                    await ctx.Response.WriteAsync("SeedLab only answers to 127.0.0.1.").ConfigureAwait(false);
                    return;
                }

                ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
                ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
                ctx.Response.Headers["Content-Security-Policy"] =
                    "default-src 'none'; script-src 'self'; style-src 'self'; "
                    + "img-src 'self' data: blob:; connect-src 'self'; base-uri 'none'; "
                    + "form-action 'none'; frame-ancestors 'none'";
                await next().ConfigureAwait(false);
            });

            MapStatic(app);
            MapMeta(app);
            MapRuntime(app);
            MapSeed(app);
            MapTiles(app);
            MapLocations(app);
            MapSearch(app);

            app.MapFallback(async ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                ctx.Response.ContentType = "text/plain; charset=utf-8";
                await ctx.Response.WriteAsync(
                    "Not found. This server has no file system: it serves four embedded assets and its API, "
                    + "and nothing else exists to be reached.").ConfigureAwait(false);
            });
        }

        // -------------------------------------------------------------------------------------------
        private static void MapStatic(WebApplication app)
        {
            foreach (StaticAssets.Asset a in StaticAssets.All)
            {
                StaticAssets.Asset asset = a;
                RequestDelegate handler = async ctx =>
                {
                    ctx.Response.ContentType = asset.ContentType;
                    ctx.Response.Headers.ETag = asset.ETag;
                    ctx.Response.Headers.CacheControl = "no-cache";
                    if (ctx.Request.Headers.IfNoneMatch.ToString() == asset.ETag)
                    {
                        ctx.Response.StatusCode = StatusCodes.Status304NotModified;
                        return;
                    }

                    await ctx.Response.Body.WriteAsync(asset.Bytes).ConfigureAwait(false);
                };

                app.MapGet(asset.Path, handler);
                if (asset.Path == "/") app.MapGet("/index.html", handler);
            }
        }

        private void MapMeta(WebApplication app)
        {
            app.MapGet("/api/meta", () => Results.Json(new
            {
                engine = _options.EngineVersion,
                game = _options.GameVersion,
                worldGenVersion = _options.WorldGenVersion,
                startedUtc = _started,
                tiles = new
                {
                    worldSpanM = TileGrid.WorldSpanM,
                    minXZ = TileGrid.MinXZ,
                    maxXZ = TileGrid.MaxXZ,
                    tilePixels = TileGrid.TilePixels,
                    maxZoom = TileGrid.MaxZoom,
                    gameGridZoom = TileGrid.GameGridZoom,
                    metresPerPixelAtZoom0 = TileGrid.MetresPerPixel(0),
                },
                world = new
                {
                    worldSizeM = SeedLab.WorldGen.WorldGeneratorPort.WorldSize,
                    waterEdgeM = SeedLab.WorldGen.WorldGeneratorPort.WaterEdge,
                    waterLevelM = SeedLab.Render.MapPalette.WaterLevel,
                    ashlandsMinDistanceM = SeedLab.WorldGen.WorldGeneratorPort.AshlandsMinDistance,
                    ashlandsYOffsetM = SeedLab.WorldGen.WorldGeneratorPort.AshlandsYOffset,
                },
                palette = Palette(),
                search = _engine.Describe(),
                locations = new
                {
                    available = _locations.Available,
                    sets = new[] { "core", "all" },
                    // Seed-independent, so it is here rather than on every report: the page builds
                    // its grouped dropdown and canonicalises a typed name before any placement run
                    // has finished. Empty, not absent, when this build has no dumped names.
                    groups = _options.LocationVocabulary?.Groups ?? new List<LocationGroupRow>(),
                    names = _options.LocationVocabulary?.Names
                            ?? new Dictionary<string, string>(StringComparer.Ordinal),
                    namesLanguage = _options.LocationVocabulary?.NamesLanguage,
                },
            }, Json));

            app.MapGet("/api/stats", () => Results.Json(new
            {
                tiles = new
                {
                    rendered = _tiles.TilesRendered,
                    renderSeconds = _tiles.TotalRenderSeconds,
                    cacheEntries = _tileCache.Count,
                    cacheBytes = _tileCache.Bytes,
                    cacheCapacityBytes = _tileCache.CapacityBytes,
                    hits = _tileCache.Hits,
                    misses = _tileCache.Misses,
                    evictions = _tileCache.Evictions,
                    diskDirectory = _tileCache.DiskDirectory,
                    diskBytes = _tileCache.DiskBytes(),
                    diskCapacityBytes = _tileCache.DiskCapacityBytes,
                    diskHits = _tileCache.DiskHits,
                    diskWrites = _tileCache.DiskWrites,
                    diskEvictions = _tileCache.DiskEvictions,
                },
                worldsConstructed = _worlds.Constructed,
                uptimeS = (DateTime.UtcNow - _started).TotalSeconds,
            }, Json));
        }

        private static object Palette()
        {
            List<object> rows = new List<object>();
            foreach (SeedLab.WorldGen.Biome b in SeedLab.Render.MapPalette.LegendOrder)
            {
                rows.Add(new
                {
                    name = SeedLab.Render.MapPalette.Name(b),
                    index = b.ToGameIndex(),
                    seedlab = SeedLab.Render.MapPalette.LandColor(b).Hex,
                    game = SeedLab.Render.MapPalette.GameColor(b).Hex,
                });
            }

            return new
            {
                biomes = rows,
                waterShallow = SeedLab.Render.MapPalette.WaterShallow.Hex,
                waterDeep = SeedLab.Render.MapPalette.WaterDeep.Hex,
                voidColor = SeedLab.Render.MapPalette.Void.Hex,
                lava = SeedLab.Render.MapPalette.Lava.Hex,
            };
        }

        // -------------------------------------------------------------------------------------------
        // The machine, the mode and the auto-throttle - polled by the page at the throttle's own
        // interval so that "Valheim started, the next search runs in background mode" is a banner
        // rather than a surprise in a log the user never reads.
        // -------------------------------------------------------------------------------------------
        private void MapRuntime(WebApplication app)
        {
            app.MapGet("/api/runtime", () =>
            {
                object[] changes;
                lock (_throttleLock) changes = _throttleChanges.ToArray();

                SeedLab.Runtime.Execution.WorkerPlan plan = _runtime.PlanWorkers(
                    SeedLab.Runtime.Execution.WorkTier.BiomeGrid,
                    SeedLab.Runtime.Execution.WorkerFootprints.CellsForSpacing(96));

                return Results.Json(new
                {
                    mode = SeedLab.Runtime.Execution.ResourceModes.Name(_runtime.EffectiveMode),
                    modeDescription = SeedLab.Runtime.Execution.ResourceModes.Describe(_runtime.EffectiveMode),
                    requestedMode = SeedLab.Runtime.Execution.ResourceModes.Name(_runtime.Throttle.RequestedMode),
                    throttled = _runtime.Throttle.IsThrottled,
                    throttledBy = _runtime.Throttle.IsThrottled ? _runtime.Throttle.ThrottledBy : null,
                    pollIntervalS = _runtime.Options.ThrottlePollInterval.TotalSeconds,
                    changes,
                    defaultThreads = plan.Workers,
                    workerLines = plan.Lines(),
                    logicalCores = _runtime.Hardware.LogicalCores,
                    physicalCores = _runtime.Hardware.PhysicalCores,
                    totalMemoryBytes = _runtime.Hardware.TotalMemoryBytes,
                    availableMemoryBytes = _runtime.Hardware.AvailableMemoryBytes,
                    cacheRoot = _runtime.Cache.Path,
                    resultsDirectory = _options.ResultsDirectory == "" ? null : _options.ResolvedResultsDirectory,
                    selfTest = _runtime.SelfTestOutcome?.Message,
                    hardware = _runtime.Hardware.Lines(),
                }, Json);
            });
        }

        // -------------------------------------------------------------------------------------------
        private void MapSeed(WebApplication app)
        {
            app.MapGet("/api/seed/resolve", (string? q, string? read) =>
            {
                string token = q ?? "";
                bool isInt = int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int asInt);
                int asTextSeed = SeedLab.Seeds.StableHash.SeedFromText(token);
                string mode = read == "text" ? "text" : read == "int" ? "int" : (isInt ? "int" : "text");
                if (mode == "int" && !isInt) return Results.BadRequest(new { error = "'" + token + "' is not an int32." });

                int seed = mode == "int" ? asInt : asTextSeed;
                string shortest = SeedLab.Seeds.SeedText.Invert(seed, SeedLab.Seeds.SeedAlphabet.Alnum62) ?? "";
                string gameStyle = SeedLab.Seeds.SeedText.GenerateGameStyle(seed);
                (uint even, uint odd) = SeedLab.Seeds.StableHash.ComputeLanes(token);
                return Results.Json(new
                {
                    input = token,
                    readAs = mode,
                    seed,
                    asText = asTextSeed,
                    asInt = isInt ? asInt : (int?)null,
                    ambiguous = isInt && asTextSeed != asInt,
                    shortestText = shortest,
                    gameStyleText = gameStyle,
                    laneEven = even,
                    laneOdd = odd,
                    laneCombiner = SeedLab.Seeds.StableHash.LaneCombiner,
                    emptyTextIsSeedZero = token.Length == 0,
                }, Json);
            });

            app.MapGet("/api/seed/report", async (HttpContext ctx, int seed, double? grid, double? minIsland, int? top) =>
            {
                double spacing = grid ?? 12.0;
                if (!double.IsFinite(spacing) || spacing < 12 || spacing > 1024)
                {
                    return Results.BadRequest(new { error = "grid must be between 12 and 1024 m." });
                }

                SeedReportRequest req = new SeedReportRequest
                {
                    Seed = seed,
                    GridSpacingM = spacing,
                    MinIslandAreaM2 = minIsland is > 0 ? minIsland.Value : 10_000.0,
                    TopIslands = Math.Clamp(top ?? 5, 0, 25),
                    Threads = 0,
                    WorldGenVersion = _options.WorldGenVersion,
                };

                try
                {
                    SeedReport r = await Task.Run(() => _reports.Get(req, ctx.RequestAborted), ctx.RequestAborted)
                                             .ConfigureAwait(false);
                    return Results.Json(r, Json);
                }
                catch (OperationCanceledException)
                {
                    return Results.StatusCode(499);
                }
            });

            app.MapGet("/api/at", (HttpContext ctx, int seed, float x, float z) =>
            {
                if (!float.IsFinite(x) || !float.IsFinite(z))
                {
                    return Results.BadRequest(new { error = "x and z must be finite." });
                }

                // Fork per request: the shared handle owns a single-entry river cache and is not safe
                // to sample from two threads at once.
                SeedLab.WorldGen.WorldGeneratorPort gen = _worlds.Get(seed, _options.WorldGenVersion).Fork();
                SeedLab.WorldGen.Biome biome = gen.GetBiome(x, z);
                float h = gen.GetBiomeHeight(biome, x, z, out SeedLab.WorldGen.Unity.ColorRGBA mask);
                gen.GetRiverWeightPublic(x, z, out float rw, out float rwidth);
                float forest = SeedLab.WorldGen.WorldGeneratorPort.GetForestFactor(x, 0f, z);
                double dist = Math.Sqrt((double)x * x + (double)z * z);
                bool outside = SeedLab.WorldGen.DUtils.Length(x, z) > SeedLab.Render.WorldField.WaterEdgeRadius;
                double bearing = (Math.Atan2(x, z) * 180.0 / Math.PI % 360 + 360) % 360;

                return Results.Json(new
                {
                    seed,
                    x,
                    z,
                    biome = SeedLab.Render.MapPalette.Name(biome),
                    biomeIndex = biome.ToGameIndex(),
                    heightM = h,
                    aboveSeaM = h - SeedLab.Render.MapPalette.WaterLevel,
                    underwater = h < SeedLab.Render.MapPalette.WaterLevel,
                    forestFactor = forest,
                    inForest = forest < 1.15f,
                    riverWeight = rw,
                    riverWidth = rwidth,
                    distanceM = dist,
                    bearingDeg = bearing,
                    outsideWaterEdge = outside,
                    isAshlandsGeometry = SeedLab.WorldGen.WorldGeneratorPort.IsAshlands(x, z),
                    isDeepNorthGeometry = SeedLab.WorldGen.WorldGeneratorPort.IsDeepnorth(x, z),
                    zoneX = (int)Math.Floor(((double)x + 32.0) / 64.0),
                    zoneZ = (int)Math.Floor(((double)z + 32.0) / 64.0),
                    ashlandsMaskA = mask.a,
                }, Json);
            });
        }

        // -------------------------------------------------------------------------------------------
        private void MapTiles(WebApplication app)
        {
            app.MapGet("/tiles/{seed:int}/{z:int}/{x:int}/{y:int}.png",
                async (HttpContext ctx, int seed, int z, int x, int y) =>
            {
                if (!TileGrid.InRange(z, x, y))
                {
                    return Results.NotFound(new { error = $"tile {z}/{x}/{y} is outside the scheme (max zoom {TileGrid.MaxZoom})." });
                }

                TileStyle style = StyleFrom(ctx.Request.Query);
                string key = TileCache.Key(seed, _options.WorldGenVersion, style, z, x, y);
                CancellationToken ct = ctx.RequestAborted;

                try
                {
                    await _renderSlots.WaitAsync(ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return Results.StatusCode(499);
                }

                try
                {
                    byte[] png = await Task.Run(
                        () => _tileCache.GetOrRender(key, () => _tiles.Render(seed, _options.WorldGenVersion, z, x, y, style, ct), ct),
                        ct).ConfigureAwait(false);

                    // A tile is a pure function of (seed, gen version, style, z, x, y), so it can be
                    // cached hard by the browser. 'private' keeps it out of any shared cache, which
                    // on a loopback-only server is belt and braces.
                    ctx.Response.Headers.CacheControl = "private, max-age=86400, immutable";
                    ctx.Response.ContentType = "image/png";
                    await ctx.Response.Body.WriteAsync(png, ct).ConfigureAwait(false);
                    return Results.Empty;
                }
                catch (OperationCanceledException)
                {
                    // The user panned away. Nothing was wasted past this point and nothing is logged:
                    // an abandoned tile is the normal case, not an error.
                    return Results.StatusCode(499);
                }
                finally
                {
                    _renderSlots.Release();
                }
            });
        }

        private static TileStyle StyleFrom(IQueryCollection q)
        {
            bool Flag(string name, bool fallback)
            {
                string? v = q[name];
                if (string.IsNullOrEmpty(v)) return fallback;
                return v == "1" || v.Equals("true", StringComparison.OrdinalIgnoreCase);
            }

            double Num(string name, double fallback, double lo, double hi)
            {
                string? v = q[name];
                if (string.IsNullOrEmpty(v)) return fallback;
                return double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out double d)
                       && double.IsFinite(d)
                    ? Math.Clamp(d, lo, hi)
                    : fallback;
            }

            return new TileStyle
            {
                GamePalette = string.Equals(q["p"], "game", StringComparison.OrdinalIgnoreCase),
                Plain = Flag("plain", false),
                Water = Flag("water", true),
                Lava = Flag("lava", true),
                Shade = Num("shade", 0.35, 0, 1),
                Exaggeration = Num("exag", 1.5, 0.1, 8),
            };
        }

        // -------------------------------------------------------------------------------------------
        // Locations. One GET, three answers: here it is, it is being computed, or it cannot be.
        //
        // There is no second endpoint to "start" a run and no job id to keep track of. A GET with
        // start=1 is idempotent: it returns the answer if there is one, otherwise it makes sure exactly
        // one computation is under way for that seed and set and reports its phase. Two tabs asking at
        // once share the one run; a tab that reloads rejoins it.
        // -------------------------------------------------------------------------------------------
        private void MapLocations(WebApplication app)
        {
            app.MapGet("/api/locations", (HttpContext ctx, int seed, string? set, int? start) =>
            {
                if (!_locations.Available)
                {
                    return Results.Json(new
                    {
                        status = "unavailable",
                        error = "this build cannot place locations: it was started without a placement "
                                + "provider, so nothing about bosses, traders or dungeons can be shown.",
                    }, Json, statusCode: StatusCodes.Status501NotImplemented);
                }

                LocationSet which = string.Equals(set, "all", StringComparison.OrdinalIgnoreCase)
                    ? LocationSet.All
                    : LocationSet.Core;
                bool begin = start is null or not 0;

                LocationsEntry e = _locations.Get(seed, _options.WorldGenVersion, which, begin);
                switch (e.State)
                {
                    case LocationsState.Ready:
                        return Results.Json(new { status = "ready", elapsedS = e.ElapsedS, report = e.Report }, Json);

                    case LocationsState.Failed:
                        // 409, not 500: the data is in a state the user can fix, and it is not a bug in
                        // the server. The hint says which one it is.
                        return Results.Json(new { status = "failed", error = e.Error, hint = e.Hint }, Json,
                                            statusCode: StatusCodes.Status409Conflict);

                    case LocationsState.Computing:
                        return Results.Json(new
                        {
                            status = "computing",
                            phase = e.Phase,
                            fraction = e.Fraction,
                            elapsedS = e.ElapsedS,
                            seed,
                            set = which == LocationSet.All ? "all" : "core",
                        }, Json);

                    default:
                        return Results.Json(new { status = "absent", seed }, Json);
                }
            });
        }

        // -------------------------------------------------------------------------------------------
        private void MapSearch(WebApplication app)
        {
            app.MapPost("/api/search", async (HttpContext ctx) =>
            {
                SearchQuery? q;
                try
                {
                    q = await ctx.Request.ReadFromJsonAsync<SearchQuery>(Json).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    return Results.BadRequest(new { error = "the query is not valid JSON: " + ex.Message });
                }

                if (q == null) return Results.BadRequest(new { error = "empty query." });

                try
                {
                    ISearchRun run = _engine.Start(q);
                    _searches.Add(run);
                    return Results.Json(new { id = run.Id }, Json);
                }
                catch (SearchRefusedException ex)
                {
                    // Two different answers wearing one status code would be a trap for the page: a
                    // refusal is a dead end with a named fix, a confirmation is a dialog. 'kind' says
                    // which, and the whole pre-run report travels with it so the dialog can show the
                    // same numbers the terminal would have printed.
                    return Results.Json(new { error = ex.Message, kind = ex.Kind, preflight = ex.Report },
                                        Json, statusCode: StatusCodes.Status400BadRequest);
                }
                catch (ArgumentException ex)
                {
                    // A refused query is the normal way this endpoint says "that cannot be answered
                    // exactly" - an unknown metric, a goal no seed can satisfy, a goal that needs the
                    // dumped location table. The message is the whole point, so it is the whole body.
                    return Results.BadRequest(new { error = ex.Message, kind = "refused" });
                }
            });

            // The live verdict beside the Find button, and the content of the confirm dialog. It runs
            // exactly what POST /api/search runs and then stops: no seed is touched and nothing is
            // created on disk, so the page can call it on every keystroke of the editor.
            app.MapPost("/api/search/preflight", async (HttpContext ctx) =>
            {
                SearchQuery? q;
                try
                {
                    q = await ctx.Request.ReadFromJsonAsync<SearchQuery>(Json).ConfigureAwait(false);
                }
                catch (JsonException ex)
                {
                    return Results.BadRequest(new { error = "the query is not valid JSON: " + ex.Message });
                }

                if (q == null) return Results.BadRequest(new { error = "empty query." });

                try
                {
                    SeedLab.Web.Search.SearchPlanner.Planned p = _engineImpl.Preflight(q);
                    return Results.Json(p.Report, Json);
                }
                catch (ArgumentException ex)
                {
                    return Results.Json(new { error = ex.Message, kind = "refused" }, Json,
                                        statusCode: StatusCodes.Status400BadRequest);
                }
            });

            // The run's live state without an EventSource: for a page that lost its stream, and for
            // checking the panel against the terminal from a shell.
            app.MapGet("/api/search/{id}", (string id) =>
            {
                if (!_searches.TryGet(id, out ISearchRun run)) return Results.NotFound(new { error = "no run called '" + id + "'." });
                return Results.Json(new { id, progress = run.Progress }, Json);
            });

            // The shipped query files, exactly as 'vseed search <name>' would run them.
            app.MapGet("/api/search/presets", () => Results.Json(new { presets = _engine.Describe().Presets }, Json));

            app.MapGet("/api/search/{id}/stream", async (HttpContext ctx, string id) =>
            {
                if (!_searches.TryGet(id, out ISearchRun run))
                {
                    ctx.Response.StatusCode = StatusCodes.Status404NotFound;
                    return;
                }

                ctx.Response.ContentType = "text/event-stream";
                ctx.Response.Headers.CacheControl = "no-store";
                ctx.Response.Headers["X-Accel-Buffering"] = "no";
                await ctx.Response.Body.FlushAsync().ConfigureAwait(false);

                try
                {
                    await foreach (SearchEvent e in run.ReadEvents(ctx.RequestAborted).ConfigureAwait(false))
                    {
                        string payload = JsonSerializer.Serialize(e.Payload, Json);
                        await ctx.Response.WriteAsync("event: " + e.Type + "\ndata: " + payload + "\n\n", ctx.RequestAborted)
                                 .ConfigureAwait(false);
                        await ctx.Response.Body.FlushAsync(ctx.RequestAborted).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    // The page navigated away or the user closed the tab.
                }
            });

            app.MapPost("/api/search/{id}/cancel", (string id) =>
            {
                if (!_searches.TryGet(id, out ISearchRun run)) return Results.NotFound();
                run.Cancel();
                return Results.Json(new { id, cancelled = true }, Json);
            });
        }

        // -------------------------------------------------------------------------------------------
        private static void TryOpenBrowser(string url, Action<string> log)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
                }
                else if (OperatingSystem.IsMacOS())
                {
                    System.Diagnostics.Process.Start("open", url);
                }
                else
                {
                    System.Diagnostics.Process.Start("xdg-open", url);
                }
            }
            catch (Exception ex)
            {
                log("  (could not open a browser: " + ex.Message + " - open " + url + " yourself)");
            }
        }

        /// <summary>
        /// A tiny bounded cache of measured worlds, so re-opening the seed panel or re-running a
        /// search over the same seed does not pay the whole field sweep again.
        /// </summary>
        private sealed class ReportCache
        {
            private readonly SeedReportProvider _inner;
            private readonly object _lock = new object();
            private readonly Dictionary<string, SeedReport> _map = new Dictionary<string, SeedReport>(StringComparer.Ordinal);
            private readonly LinkedList<string> _lru = new LinkedList<string>();

            public ReportCache(SeedReportProvider inner) { _inner = inner; }

            /// <summary>The provider shape, for callers that want the cache transparently.</summary>
            public SeedReportProvider Provider => Get;

            public SeedReport Get(SeedReportRequest req, CancellationToken ct)
            {
                string key = req.Seed + "/" + req.WorldGenVersion + "/"
                             + req.GridSpacingM.ToString("R", CultureInfo.InvariantCulture) + "/"
                             + req.MinIslandAreaM2.ToString("R", CultureInfo.InvariantCulture) + "/"
                             + req.TopIslands;
                lock (_lock)
                {
                    if (_map.TryGetValue(key, out SeedReport? hit))
                    {
                        _lru.Remove(key);
                        _lru.AddFirst(key);
                        return hit;
                    }
                }

                SeedReport r = _inner(req, ct);
                lock (_lock)
                {
                    if (_map.TryAdd(key, r))
                    {
                        _lru.AddFirst(key);
                        while (_lru.Count > 16)
                        {
                            string last = _lru.Last!.Value;
                            _lru.RemoveLast();
                            _map.Remove(last);
                        }
                    }
                }

                return r;
            }
        }
    }
}
