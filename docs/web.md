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

(Mid-way through the same day this suite reported `search panel vs the engine` as FAILED, and it was
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
  compression, the ceiling and `on_limit` are flags about a file. There are exactly three places, all
  bounded and all named on the Search panel:
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

  `vseed serve` hands the server its own runtime since 2026-09-24, so the process has one cache root,
  one auto-throttle and one self-test, and what the page writes follows `--cache-dir`. Before, the
  server started a second runtime that had never seen `--cache-dir`, and a funnel's stage 2
  checkpointed in the default cache root even under `$SEEDLAB_CACHE_DIR`.

  Your saves and Steam Cloud folders are still never touched, and nothing is written beside them.

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

New endpoints: `GET /api/runtime` (mode, throttle, workers, cache root, self-test) and
`POST /api/search/preflight` (the whole pre-run report without touching a seed).

## Traps

- **A running `vseed serve` locks `src\SeedLab.Cli\bin\Release\`.** A build then fails with MSB3027 /
  MSB3021. Stop the server first. Same for a running `vseed search`.
- The tile cache's **memory** tier is measured in MiB of rendered tiles, not tiles — a big
  `--tile-cache` on a small machine is not free. Behind it is a disk tier under the cache root, with
  its own bound; `/api/stats` reports both.
- Tiles are rendered from the generator on demand. The first view of a new seed pays for constructing
  the generator and pre-generating its rivers (382 ms in the run above); that is the `--worlds`
  cache's whole job.
- The map uses SeedLab's readable palette, not the game's. `vseed map --palette game --plain` is the
  thing that is pixel-identical to `Minimap`; the web tiles recolour Deep North and depth-shade water
  so the three whites can be told apart.
- `--selftest` binds an ephemeral port to test loopback behaviour and then exits; it does not leave a
  server running.
