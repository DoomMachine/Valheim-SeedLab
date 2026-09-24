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

Useful options: `--port 0` (let the OS choose), `--no-browser`, `--idle-reminder <minutes>`
(default 60, 0 = never), `--tile-cache <MiB>`, `--worlds <n>` (how many seeds keep a constructed
generator alive), `--selftest`, and `--status` / `--stop` (below).

## Starting and stopping it (decided 2026-09-24)

**In short, for using it.** Start the page with `SeedLab 2 - Open web page` (Windows), `sh seedlab.sh
web` (macOS, Linux) or `vseed serve`. The server is the window titled **SeedLab web server** (on
macOS and Linux, the terminal you started it in): keep it open, minimised if you like, while you use
the page. It never stops by itself and never starts by itself. Stop it with any of:

| Way | What happens to a running search |
| --- | --- |
| **Stop SeedLab** on the page (top bar, or the Help panel) | a second warning names it and shows how to continue it; then **Stop anyway** or **Keep running** |
| `SeedLab 3 - Stop web page`, `sh seedlab.sh stop` or `vseed serve --stop` | `vseed` names it and asks `Stop anyway? [y/N]` (`--yes` answers yes) |
| **Ctrl+C twice** within 10 seconds in the server's window | the first press stops nothing and says what is running |
| closing the server's window, or signing out of / shutting down Windows | no question: it is stopped with its checkpoint saved before the program ends (the same stop as the others; see Traps for what was tested) |

Whichever way, a running search is stopped at once and its checkpoint saved, so it can be continued
from a terminal with the command shown - except a search still in a funnel's first stage, which has
no resume point yet and loses that stage's work; every warning says so for that search. The command
names the query file saved beside the checkpoint (`<checkpoint>.query.json`), so it works as printed;
it is also in the session log. Pressing **Find seeds** again on a query a stop left a resume point
for asks first (below). After 60 minutes with nobody using the page, the page and
the window suggest stopping SeedLab, and ask again every 60 minutes; they never stop it.
`vseed serve --status` says whether it is running. The README's
[How to use it](../README.md#the-web-page) says the same with more words; the scripts are in
[`scripts.md`](scripts.md).

The user asked for a server that is never left running without anyone knowing: *"I don't find it
prudent to have something permanently running. There should be scripts and/or options and commands
for starting and stopping the web server"*, and then for "a very serious mix" of ways to stop it. The
design they confirmed, as built:

- **The window is the server.** `vseed serve` runs in the window it was started from, titles it
  **SeedLab web server**, and prints first - before the runtime's own lines, however it was started -

  ```
  ==============================================================================
   This window IS SeedLab's web server. Minimise it - do not close it - while
   you use the page. To stop it: Stop SeedLab on the page, 'SeedLab 3 - Stop
   web page' (or vseed serve --stop), or press Ctrl+C in THIS window twice.
  ==============================================================================
  ```

  (`'sh seedlab.sh stop'` on macOS and Linux). Its startup block ends with `SeedLab is ready. To stop
  it: ...`. It is never hidden, never a service and never started at login. The Windows script's
  **web** action starts `vseed.exe` as the only program in a new console window, so Ctrl+C and the
  close button reach vseed alone (no `Terminate batch job (Y/N)?`, no PowerShell in between);
  `seedlab.sh` runs it in the terminal it was started from.
- **Stop SeedLab on the page** - a button in the top bar and on the Help panel. The first dialog says
  what stopping does ("This page stops working until you start SeedLab again with 'SeedLab 2 - Open web
  page' or vseed serve. Nothing is lost ..."). If a search is running the server answers 409 and the
  page shows the **second warning**: which search, how many seeds it has checked, and - said for
  THAT search - the command that continues it, and **Stop anyway** / **Keep running**. A funnel still
  in its first stage has no resume point (a stop throws its survivors away), and there the dialog,
  `--status`, `--stop` and the first Ctrl+C say that stopping loses its work, and never that anything
  is saved (review of 2026-09-25: one shared sentence used to promise both). After a stop the page
  covers itself with "SeedLab has stopped. You can close this tab." and what became of each search,
  with its checkpoint and the command; every control behind it is inert. Another tab, or a stop from
  the window or a script, is found by the page's polling: "SeedLab is not answering" after two failed
  polls - up to about 20 seconds - with a Reload button, and, when this tab's own search was stopped,
  that search's checkpoint and command from its last `done` event. A **Save the query file** button
  appears only when the query file could not be written beside the checkpoint, so the command says
  `<this query file>`.
- **The query file beside the checkpoint** (2026-09-25). When a page search starts, the server writes
  its query - the exact text it compiled - to `<cache root>\checkpoints\<hash>.ckpt.query.json`, beside
  the checkpoint it will save, and every resume command names that file:
  `vseed search "<...>.ckpt.query.json" --resume --checkpoint "<...>.ckpt"`. It goes with the
  checkpoint: a finished run retires both, a run that leaves no checkpoint (a funnel stopped in stage
  one, a failure before the first save) deletes it, and `vseed clean` counts it under checkpoints. It
  used to say `<this query file>`, a file only the page's Export or Save button could make - and after
  a stop from the window or a script nothing could make it any more.
- **A resume point is never replaced without asking** (2026-09-25). A checkpoint is named by the
  query's hash, so pressing Find seeds again on a query a stop had just ended started at the first seed
  and overwrote the resume point every stop had promised to keep. Now `POST /api/search` answers 400
  with `kind: "checkpoint-exists"`, the checkpoint's path and the command that continues it, when a
  readable checkpoint of THIS query is there; the page asks ("This search was stopped before, and can
  be continued" - **Start again from the first seed** / **Cancel**) and only a start again sends
  `replaceCheckpoint: true`. Not `confirmed`, which the page sends with every run. A run of this
  server that is using the same checkpoint right now is refused by name. (`vseed search` without
  `--resume` still starts over at its checkpoint path without asking - not changed here.)
- **The idle reminder, never an automatic stop.** After `--idle-reminder` minutes (60) in which nobody
  used the page, the page shows "SeedLab hasn't been used for 60 minutes. Stop it now?" with **Stop
  SeedLab** and **Keep running**, the window prints the same, and its title becomes `SeedLab web server
  - idle 60 min`. Keep running starts another period; ignored, the reminder comes again after every
  further period ("2 hours", "3 hours" ...). Use is what a PERSON does - tiles, seed reports, points,
  places, a search started or looked at - and never the page's own polling (`/api/runtime`, a search's
  event stream, `/api/server/*`); a running search counts as use for as long as it runs. The server
  keeps the clock, so every tab agrees.
- **`vseed serve --status`** prints one line per server of this cache root - `SeedLab's web server is
  running at http://127.0.0.1:8731 since 14:03 (pid 1234); no search is running.` (or `a search is
  running: "gentle-start", 12.3 % done (49,152 of 400,000 seeds).`) - exit 0; `SeedLab's web server is
  not running.` - exit 1. `--json` for scripts.
- **`vseed serve --stop`** asks each server of this cache root to stop. When a search is running and
  someone can answer, it asks: `A search is running: "..." ... Stopping SeedLab stops it:` and, for each
  search, `"..." stops at once; the part it has finished is saved, and it can be continued later from a
  terminal with: <the resume command>` - or, for a funnel in its first stage, `"..." has no resume point
  yet (it is in its first stage): stopping it now loses the work it has done so far.` - then
  `Stop anyway? [y/N]`. With nothing
  to answer (a script, stdin redirected) it refuses - exit 1, "Add --yes to stop it anyway" - and never
  guesses yes. `--yes` answers yes. A server that does not answer within 10 seconds is said, and
  `--force` then ends its process after checking its pid AND start time (the search loses what its last
  checkpoint did not hold). Exit 0 when stopped or nothing was running, 1 when not stopped.
- **`--status` and `--stop` start nothing.** They are answered before the command's runtime: no cache
  root is created, no session log is opened, no self-test is run and nothing is reaped - an uninstall
  runs them after deleting the cache root. They find the servers of ONE cache root: a server started
  with `--cache-dir X` is found with the same `--cache-dir X` (or `SEEDLAB_CACHE_DIR`). On Windows they
  also list a `vseed serve` the registry does not know - read from its command line
  (`NtQueryInformationProcess`, not WMI, which would be a NuGet package) - with how to find and stop
  it; nothing here stops such a process, because nothing here can prove it is SeedLab's.
- **A second `vseed serve`** while one of this cache root runs opens the running one's page and exits
  0 - `SeedLab is already running at http://127.0.0.1:8731 (since 14:03) - opened it in your browser.` -
  without starting a runtime, so the running server's session log is not touched. An explicit,
  different `--port` starts another server; `--port 0` always does.
- **Ctrl+C counts only in the server's own window.** In the browser it is Copy. The first press stops
  nothing: `Ctrl+C - nothing has been stopped yet.`, what is running, `Press Ctrl+C again within 10
  seconds to stop SeedLab.` and, when a search is running, that it stops with it and how it is
  continued. A second press within 10 seconds is the graceful stop; a press while that stop is under
  way says `Ctrl+C - SeedLab is already stopping; wait a moment.` (it used to count as a new first
  press); a press after it is a new first press. **Ctrl+Break** is a stop already confirmed. **Closing the window** (Windows' `CTRL_CLOSE_EVENT`,
  which .NET reports as `SIGHUP`), a Unix hang-up and **SIGTERM** cannot be refused: the graceful stop
  runs at once, inside the handler, because Windows ends the process about five seconds after its window
  is closed. The handlers are installed when the banner is printed, so "twice" is true while the data is
  still loading too; a stop asked for then is applied before the port is ever bound.
- **Signing out, shutting down, restarting (Windows).** These never reach a console handler here:
  Windows sends no `CTRL_LOGOFF_EVENT` or `CTRL_SHUTDOWN_EVENT` to a console program that has loaded
  user32.dll, and a running `vseed serve` has (Microsoft's `SetConsoleCtrlHandler` documentation says
  so, and recommends a hidden window). So the server keeps a hidden top-level window
  (`SessionEndWindow`, class `SeedLabWebServerSessionEnd`, never shown): `WM_QUERYENDSESSION` is
  answered "yes" at once and stops nothing (another program may still cancel), and `WM_ENDSESSION` with
  "the session is ending" runs the graceful stop inside the message, before Windows ends the process.
  Found by review 2026-09-25: before it, a shutdown ended the server abruptly - up to 30 s of a search
  lost, the registry file left behind.
- **After the stop, the window waits** when a search it stopped left a checkpoint: `The command above
  continues the stopped search. It is kept in the session log too (...). Press Enter to close this
  window (it closes by itself in 10 minutes).` - only in a window vseed owns alone, with a keyboard.

**One graceful stop, whoever asks** (`WebServerControl`, `WebServer.BeginStop`). Every running search
is stopped **at once**: `SearchRun.Abandon` makes each worker leave its block between two seeds and
throws that half block away (a pure function of its seeds; the resume recomputes it), and the run ends
as any stopped run does - its checkpoint saved at the last block written, on the quick retry schedule
(~1.6 s) rather than the patient one (~15 s), because a closed window leaves about five. The server
waits for the searches 3.5 s at most, each search's stream ends with a `done` event that says why
(`stopped because SeedLab's web server was stopped (Stop SeedLab was pressed on the page); every seed
below the count above was evaluated, and the checkpoint keeps them`), the registry file is deleted, and
`RunAsync` returns 0. Measured 2026-09-24: `vseed serve --stop --yes` with a one-worker search running
took 222 ms end to end; the self-test's stop with a search running answered in 9 ms and 5 ms in two
runs. The web host's own
console lifetime is replaced by one that does nothing: it used to stop the server on the first Ctrl+C
and end the process under a running search, which lost everything since the last 30 s checkpoint.

**The registry** (`SeedLab.Runtime.Storage.ServerRegistry`): `<cache root>\serve\server-<pid>.json`,
written atomically once the port is bound (before the first line that says the server is up) and
deleted when it stops - `{pid, process_started_utc, port, url, started_utc, version, token}`. A file
counts as a running server only while its pid is alive AND that process's start time can be read AND
matches; a start time this account cannot read means the pid now belongs to a system process or
another account's, never to the user's own server (review of 2026-09-25: the lenient check the scratch
reaper uses called such a file "live", which locked the user out - "already running", a `--stop` that
could not stop it, an uninstall that refused). A server that answers must also say its own pid, and a
file is read only when its `url` is exactly `http://127.0.0.1:<its port>` - the url is opened in the
browser and asked for the server's state. `--status` and `--stop` name a left-over file as safe to
delete; the next server removes it. `serve\` is not a `vseed clean` category (a live server's file must
not be cleaned); an uninstall removes the cache root with it. On Linux and macOS the folder and the
file are created owner-only (0700, 0600) - which keeps the file private, not the token (below).

**The token.** `POST /api/server/stop` and `POST /api/server/keep-alive` need BOTH the cross-site
guard below and `X-SeedLab-Token` equal to the server's token - 32 random bytes as hex, new every
start. The page reads it from `/api/meta` (`server.token`), which another web page cannot read (no
CORS); `vseed serve --stop` reads it from the registry file. Without it: 401; another token: 403; the
right token from another page: 403 from the guard, before the route. A custom header on a
cross-origin request also needs a CORS preflight, which this server never answers. **What it guards
against is other web pages, and only them**: any program on this computer, or another account on it,
that can reach 127.0.0.1 can read `/api/meta` as the page does and stop the server (review of
2026-09-25). That is no more than closing the window, which the same account can do anyway.

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

Re-run on **2026-09-24** with the lifecycle (`--selftest` on a scratch `--cache-dir`): **exit 0, 17
PASS rows and 3 `MEASURED`**. Two rows are new; the last one ends the server:

```
  Stop SeedLab / Keep running need the token and the page's origin  PASS  /api/meta carries a 64-hex token; 10 POSTs, 10 answered as expected (no token 401, another token 403, another page 403 with the token, the page's own Keep running 200); the server still answers (200)
  Stop SeedLab with a search running: 409, then stopped with its checkpoint saved  PASS  without stopSearch: 409 naming 'selftest-stop' and its resume command; with it: 200 in 9 ms, checkpoint at block 46 (the path the 409 named), the stream's done 'cancelled', the server returned 0 and no longer answers (the checkpoint was then deleted)
```

The self-test's own server is not registered (a `--status` run at the same moment must not mistake
it for yours) and deletes the checkpoint its stop check leaves.

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
  compression, the ceiling and `on_limit` are flags about a file. There are exactly five places, all
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
  4. **the session logs**, `<cache root>\logs\vseed.log` (or `vseed.log.1` .. `.4` while another
     `vseed` runs), written for the server's whole life - every web search's access checks, warnings,
     retries and any exception in full, the idle reminders and the stop - and renamed `vseed-prev.log`
     when the next `vseed` command starts, so the last session's log is always kept beside the
     current one and never more (the user's "two logs" decision, 2026-09-24; the SeedLab.Runtime
     README has the rules and the size cap). They hold your folder paths (on Windows usually
     `C:\Users\<your account name>\...`); read them before posting them anywhere public.
  5. **the registry file** `<cache root>\serve\server-<pid>.json` (above), deleted when it stops.

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

New endpoints: `GET /api/runtime` (mode, throttle, workers, cache root, self-test, and since
2026-09-24 `idle` and `stopping`), `POST /api/search/preflight` (the whole pre-run report without
touching a seed), `POST /api/search/{id}/retry-save` (Retry saving), and the lifecycle's
`GET /api/server/state` (pid, address, idle clock, the running searches with their resume commands),
`POST /api/server/keep-alive` and `POST /api/server/stop` (`{stopSearch, by}`; both need the token).
A search asked for while the server is stopping is refused with 503 `kind: "stopping"`.

## Traps

- **A running `vseed serve` locks `src\SeedLab.Cli\bin\Release\`.** A build then fails with MSB3027 /
  MSB3021. Stop the server first (`vseed serve --stop`). Same for a running `vseed search`.
- **Not tested** (2026-09-25): clicking the window's close button (a review posted `WM_CLOSE` to a
  server's console window once, by hand: it stopped in 23 ms with its search's checkpoint saved and no
  registry file left - but no automated test does that); a REAL sign-out or shutdown (section 18 sends
  the server's hidden window `WM_QUERYENDSESSION` and `WM_ENDSESSION` itself, and the stop runs - that
  Windows delivers them at a real sign-out is Microsoft's documented behaviour, not watched here);
  typing an answer at `Stop anyway? [y/N]`; `--force`; two servers in one cache root at once; a stop
  during the measurement a funnel makes between its stages; and everything on Linux and macOS
  (SIGTERM, SIGHUP, the terminal title, file modes, `xdg-open` and `open`). Tested for real: Ctrl+C
  sent to the server's own console (`GenerateConsoleCtrlEvent`, `tests\SeedLab.Search.Tests` section
  18) once, once more after 10 s, twice, and a third time while the stop runs; a stop during a funnel's
  first stage (the warning says the work is lost; nothing of it is left behind).
- **A stop from outside reaches a tab late.** The page notices only through its polling: `stopping`
  on `/api/runtime`, or two polls in a row that get no answer, 5 s apart after polls up to 15 s apart -
  so up to about 20 seconds.
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
