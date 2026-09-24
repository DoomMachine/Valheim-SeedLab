# The web UI — `src\SeedLab.Web`, `vseed serve`

## What it does

```
vseed serve
```

opens a local page on `http://127.0.0.1:8731`: a pan-and-zoom slippy map of any seed, a seed panel
with the same figures `vseed seed` prints, a point panel (click anywhere for the generator's answer
at that exact point), a ruler, location markers, and a search panel.

`vseed serve` owns the process and the numbers; `SeedLab.Web` owns the server. The seam is
`SeedReportProvider`: the web project never learns what an island is — it asks, and the analysis in
`SeedLab.Cli.Analysis`, the same code behind `vseed seed`, answers. The page and the terminal
therefore cannot disagree.

Useful options: `--port 0` (let the OS choose), `--no-browser`, `--tile-cache <MiB>`,
`--worlds <n>` (how many seeds keep a constructed generator alive), `--selftest`.

## What proves it correct

`vseed serve --selftest` checks the server against the ground truth and against the engines behind
it, and prints every result. Re-run on **2026-09-23 after the wiring pass**: **exit 0, all checks
passed.**

```
  asdasdasd z=3 tiles vs game texture                   PASS  262,144 of 262,144 pixels identical
  testworldclaude z=3 tiles vs game texture (hold-out)  PASS  262,144 of 262,144 pixels identical
  2x2 tile mosaic vs one render                         PASS  262,144 of 262,144 pixels identical (hillshade apron)
  static allowlist rejects traversal                    PASS  7 probes, 0 reachable; the table answers exactly 4 names
  bound to loopback only                                PASS  1 non-loopback address tried, 0 answered
  Host header refuses a rebinding name                  PASS  8 hosts, 8 answered as expected
  no CORS, and a CSP on every reply                     PASS  12 foreign-Origin requests, 0 CORS headers back; 6 of 6 carried a CSP
  nothing outside the content root                      PASS  13 escapes tried, 0 answered; 6 alternative spellings returned the canonical asset byte for byte
  location markers vs the placement engine              PASS  60 of 60 instances bit-identical; 7 unique types, 0 not flagged as candidates
  search panel vs the engine                            PASS  384 seeds, 200 hits from the page vs 200 from a direct SearchRun, identical in order and score
```

Re-run on **2026-09-24** with the cross-site guard, Retry saving and the replay check: **exit 0, 15
PASS rows and 3 `MEASURED`**. The rows above all passed again, with their same numbers, and three
are new:

```
  search replay past its cap keeps every warning and the end  PASS  4,001 results, 41 tables, a warning, done -> replayed 4,000 results (1 dropped), 1 table (the latest), 1 warning, 1 done, last event 'done'
  a POST from another web page is refused               PASS  11 POSTs to Stop, Retry saving and the search, 11 answered as expected (7 refused with 403, 4 reaching their route)
  search panel: Retry saving, from the page only        PASS  the page's own request on a finished run -> 200 saved (nothing was missing); the same request from another origin -> 403
```

(Mid-way through 2026-09-23 this suite reported `search panel vs the engine` as FAILED, and it was
failing *correctly*: its fixture posted a query whose grid the policy now raises, which is a
confirmation both front ends require. On the build measured above it passes. If it ever fails again,
read the detail line before assuming the server is wrong — a new confirmation the CLI raises will
stop that fixture too.)

It also prints timings marked `MEASURED` rather than `PASS` — a cold seed's first tile, further
tiles, PNG compression levels — because they are measurements of this machine, not checks.

Note what the tile check does and does not cover: it compares one zoom level (z=3, 512 × 512 pixels
per world) against the game's own texture, plus a mosaic check that tiles seam correctly. It is not a
claim that every tile at every zoom was compared.

## Privacy and safety, by construction

- Bound to **127.0.0.1 only**, and the server refuses any `Host` header other than
  `127.0.0.1` / `localhost` — both tested by the selftest above.
- The front end is **four files embedded in `vseed.exe`** (`index.html`, `app.css`, `app.js`,
  `favicon.svg`), served from a fixed allowlist. No CDN, no web font, no external request of any
  kind — which is why "serves nothing outside its own content root" is true by construction rather
  than by a path check somebody has to get right.
- **What it writes, and nowhere else.** This line used to say "it writes nothing". That stopped
  being true when decision 12 made every `vseed search` flag reachable from the page: rotation,
  compression, the ceiling and `on_limit` are flags about a file. There are exactly four places, all
  bounded and the first three named on the Search panel:
  1. **the results file**, only when the page names one. The browser names a *file*, never a path:
     `QueryTranslator.Clean` refuses a separator, a drive, a `..`, a Windows device name (`CON.jsonl`
     opens the console, whatever extension follows it) and any extension but `.jsonl`, `.csv` and
     `.json`, and the server resolves the name inside one results directory it owns — by default
     `<working directory>\seedlab-results`. Probed over HTTP: 11 escape attempts, 0 reachable.
  2. **the checkpoint** and its kept-set snapshot, in the runtime cache root's `checkpoints`
     (`%LOCALAPPDATA%\SeedLab`, or `vseed serve --cache-dir`, or `$SEEDLAB_CACHE_DIR`), never in the
     working directory — a funnel's stage 2 included. It is retired when a run completes.
  3. **the tile cache's second tier**, under the same cache root's `tiles` — a cache of a pure
     function of (seed, gen version, style, z, x, y), byte-bounded, deleteable at any moment.
  4. **the session log**, `<cache root>\logs\vseed.log` (or `vseed.log.1` .. `.4` while another
     `vseed` runs), written for the server's whole life - every web search's access checks, warnings,
     retries and any exception in full - and emptied when the next `vseed` command starts. It holds
     your folder paths (on Windows usually `C:\Users\<your account name>\...`); read it before
     posting it anywhere public.

  `vseed serve` hands the server its own runtime since 2026-09-24, so the process has one cache root,
  one auto-throttle and one self-test, and what the page writes follows `--cache-dir`. Before, the
  server started a second runtime that had never seen `--cache-dir`, and a funnel's stage 2
  checkpointed in the default cache root even under `$SEEDLAB_CACHE_DIR`.

  Your saves and Steam Cloud folders are still never touched, and nothing is written beside them.
- **A change sent by another web page is refused** (2026-09-24). Loopback and the `Host` check do not
  stop it: a page on any site you visit can send a form or a `fetch` to `127.0.0.1`, and the browser
  puts this server's own host in `Host`. Stop and Retry saving take no body, so nothing else stood in
  the way. One middleware, for every `POST` and before any route: a request whose `Sec-Fetch-Site` is
  present and is not `same-origin` or `none`, or whose `Origin` is present and is not this server's own
  (`Origin: null` included), gets 403 `{"kind":"refused"}`. The page's own requests carry
  `same-origin` and its origin; curl and scripts send neither header and work as before. GETs are not
  touched — they change nothing and are unreadable cross-site without CORS. `vseed serve --selftest`
  proves it with 11 POSTs to Stop, Retry saving and the search: 7 refused, 4 reaching their route.

## The search panel is the CLI

Decision 12: *"everything must also be doable from the web-interface (GUI) as opposed to having
CLI-only operations."* The panel is not a simplified front end onto a subset:

- The page's goals become a **query file** (`QueryTranslator`), read back by `QueryReader`, compiled
  by `CompiledQuery`, planned by `SearchSession` and gated by `SearchPreflightCheck` — the same five
  steps `vseed search` takes. **Export** downloads exactly that file; `vseed search <that file>`
  reproduces the run. Measured: a 2,000-seed query built in the panel and the same file run in a
  terminal produced result files with the **same SHA-256**, 71,588 bytes, 154 records.
- `keep N` is a **real cap on the file** here as it is in the terminal, and the report says
  "top 200 of 2,802 matches" rather than "200 matches".
- **The refusal rule and the confirmations are enforced on the server**, not in the page: a `POST`
  from curl that skips them is refused with `kind: "refused"` or `kind: "confirm"` and the whole
  preflight report. The page's dialog is the courtesy, not the check.
- A goal's value control is **range-limited to what the generator can produce**, from
  `LocationFeasibility` and `BiomeGeometry` — the same functions the engine refuses with. A one-line
  hint names the binding constraint ("Eikthyrnir is always within 1 km of the centre
  (m_maxDistance 1000)") and the evidence sits behind an ⓘ that starts closed.
- Workers, the tile renderer's degree and the render slots all come from the runtime `WorkerPlan`, so
  the mode, the memory guard and the auto-throttle govern the whole process. A mode change while the
  page is open becomes a banner: the panel polls `/api/runtime` at the throttle's own interval.

- **A file in the way** (2026-09-24), as in the terminal (`docs\search.md`, "A file another program
  holds"). Before a run starts, the results file, its folder and the checkpoint folder are checked;
  one that cannot be used refuses the search with `kind: "refused"` and a sentence that names the
  file, says what probably has it open and ends "press Find seeds again once the file is free" —
  pressing it again is the page's retry. During the run each checkpoint save that fails, and the save
  that works again, is a `warning` event; the page **adds** each one to a list under the button
  (the single message line used to be replaced by the next). The `done` event carries
  `checkpointError` when the last save of a run that stopped early failed — with the sentence the
  terminal prints — and `failedSaves`, `retireWarning` and `checkpointLeftovers`. The page then shows
  the error and a **Retry saving** button, which calls `POST /api/search/{id}/retry-save`: the same
  save again, as often as it is pressed, answered with `{saved, message, checkpointPath,
  resumeCommand, checkpointError}` (the run's stream has closed by then). While a run is still going
  it answers 409 and tries nothing. `checkpointError.problem` is spelled as the terminal's `--json`
  spells it (`in_use`, `read_only`, ...), and `onDiskUnreadable` is true when the checkpoint on disk
  is there but was held so that it could not be read: it is still the resume point, and the resume
  command is kept.
- **What a tab that comes back is shown** (review of 2026-09-24). The server replays a run to every
  tab that opens its stream. Only the `result` lines are capped (4,000); every `warning`, the latest
  best-of table and progress, and the `done` event - always last - are replayed however long the run
  was (the cap used to drop them all after 4,000 events, and such a tab reconnected for as long as it
  was open). The page lists each warning once, however often its stream reconnects.
  `GET /api/search/{id}` carries `save` - `{checkpointError, checkpointPath, resumeCommand}` as they
  stand now, null while the run goes on - and a tab that rejoins an ended run draws the save box
  from it rather than from the `done` event, which a later Retry saving does not change. A second
  Retry saving after one that worked answers "saved". When the server no longer knows the run (it was
  restarted, or has run newer searches since), the page says so and stops reconnecting, and Retry
  saving says the same instead of "the server did not answer".
- A rotated run (`keep all` with rotation) whose manifest is held when it finishes is published as
  `done`, not failed: every record is on disk. The wait and the failed manifest are `warning` events,
  and `done` carries `resultsError`.
- A search that fails is described in words, never with an exception type: a file another program
  held, a read-only file or one this account may not change is named with its cause (an access
  denial whose message names no file says "Windows refused SeedLab access to a file or folder it
  needed" and points at the session log); a full drive is said as one; any other input/output error
  says SeedLab could not read or write a file it needed; anything else says it is SeedLab's fault.
  Each names the session log.
- The server's session log is the command's (`vseed serve` hands the server its runtime), so the
  page's warnings, errors and retries are in `<cache root>\logs\vseed.log` too. `vseed serve` also
  checks the tile cache and the results folder at start (without creating the results folder; the
  checkpoints folder is checked with the rest of the cache root) and adds `file access checked: ...;
  integrity ...` to its startup block, after the location data has loaded. A failure there is a
  warning, not a question: the map needs none of them, and every search checks again.

New endpoints: `GET /api/runtime` (mode, throttle, workers, cache root, self-test),
`POST /api/search/preflight` (the whole pre-run report without touching a seed) and
`POST /api/search/{id}/retry-save` (Retry saving).

## Traps

- **A running `vseed serve` locks `src\SeedLab.Cli\bin\Release\`.** A build then fails with MSB3027 /
  MSB3021. Stop the server first. Same for a running `vseed search`.
- The tile cache's **memory** tier is measured in MiB of rendered tiles, not tiles — a big
  `--tile-cache` on a small machine is not free. Behind it is a disk tier under the cache root, with
  its own bound; `/api/stats` reports both. A disk tile is written through `DurableWrite` (since
  2026-09-24), tried once — a scanner holding it only means that tile stays in memory — and a temp
  file a kill leaves is one the cache root's reaper removes. Earlier builds left `<hash>.png.tmp`
  behind whenever a write failed; those older than a minute are swept when the server starts.
- Tiles are rendered from the generator on demand. The first view of a new seed pays for constructing
  the generator and pre-generating its rivers (382 ms in the run above); that is the `--worlds`
  cache's whole job.
- The map uses SeedLab's readable palette, not the game's. `vseed map --palette game --plain` is the
  thing that is pixel-identical to `Minimap`; the web tiles recolour Deep North and depth-shade water
  so the three whites can be told apart.
- `--selftest` binds an ephemeral port to test loopback behaviour and then exits; it does not leave a
  server running.
