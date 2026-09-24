# SeedLab

**Valheim 1.0.15 world generation, offline and exact.** Type a seed and see the world without
launching the game; or scan all 4,294,967,296 of them for one that suits you, with no 20,000-seed cap.

Two things make it worth having rather than another seed viewer:

- **The terrain is not an approximation.** On two worlds the game itself generated and wrote to disk,
  every biome pixel matches and every height matches *as a binary16 bit pattern* — 4,194,304 of
  4,194,304 codes per world, 0 differing, worst difference 0 m. The location placement reproduces
  12,228 of 12,228 instances of a fresh world bit-identically, including the `x/y/z` float bits.
- **It never rounds an answer into a claim.** Every figure carries the grid it was measured on, a
  thing that cannot be predicted is labelled as unpredictable instead of being printed as a
  coordinate, and a search that could not answer a goal refuses rather than returning seeds it never
  tested.

Everything runs locally. Nothing is uploaded, and nothing in your save folders is ever written.

---

**In a hurry?** [How to use it](#how-to-use-it) gets SeedLab running without typing a command: on
Windows, double-click `SeedLab 1 - Install or update`, then `SeedLab 2 - Open web page`. After that,
[`docs\finding-a-seed.md`](docs/finding-a-seed.md) is the one page to read: a first search, reading
the result and opening it on the map.

## Contents

- [Cloned from GitHub? Two folders are not here](#cloned-from-github-two-folders-are-not-here)
- [How to use it](#how-to-use-it)
- [What it can and cannot tell you](#what-it-can-and-cannot-tell-you)
- [Build it](#build-it)
- [The commands](#the-commands)
- [The session logs, and a file another program holds](#the-session-logs-and-a-file-another-program-holds)
- [The web UI](#the-web-ui)
- [Searching, and what it really costs](#searching-and-what-it-really-costs)
- [Seeds: 853 quadrillion texts, 4.29 billion worlds](#seeds-853-quadrillion-texts-429-billion-worlds)
- [The game data, and what to do after a Valheim update](#the-game-data-and-what-to-do-after-a-valheim-update)
- [Where the evidence lives](#where-the-evidence-lives)
- [Layout](#layout)
- [Credits](#credits)

---

## Cloned from GitHub? Two folders are not here

This repository holds the source, the tests, the tools and the documentation. Two folders the rest of
this README mentions are kept on the author's machine on purpose:

- **`data\`** - the game data snapshot (`data\1.0.15-59f53fb5\`). It is read out of the running game
  by `tools\SeedLab.Dumper`, so it is Iron Gate's content - location tables, prefab constants, the
  game's own English text - and it is not redistributed. **Without it, terrain answers work** (biome,
  height, rivers, maps, seed arithmetic: they need only the seed), and **location answers refuse**
  with exit 3 and a message saying so, exactly as they do after a game update. **Searches refuse
  too, today - even one that asks only about terrain**: the search's checker reads the constraint
  atlas (`constraint-atlas.json`) from `data\`, and without it `vseed search` and the page's Search
  panel refuse every run and name that file (checked 2026-09-24 on a copy without `data\`). To get
  it, follow [`docs\game-data.md`](docs/game-data.md), which walks through running the dumper against
  your own copy of Valheim step by step.
- **`groundtruth\`** - two worlds the game generated on the author's machine, their map caches, a game
  log and the recorded native-function values. It is the evidence the gates compare against, so
  `vseed selftest`, `tests\SeedLab.Acceptance.Tests`, the location gate and the tile check in
  `vseed serve --selftest` say that it is missing rather than pass. It is **not** needed to run
  `vseed`: on x64 the built-in machine self-test is self-contained, and only another CPU architecture
  would need `groundtruth\natives` to prove itself.

Paths in the documentation such as `E:\SteamLibrary\steamapps\common\Valheim\_ModSource\SeedLab` are
where the project lives on the author's machine; SeedLab itself does not depend on them.

---

## How to use it

SeedLab is one program, `vseed`, used two ways: as a **web page** in your own browser (a map of any
world, and a search for worlds you would like), and as a **command line** in a terminal window. Both
run the same engine, so anything the page can do, the terminal can do too, and the other way round.

Everything below is tested on Windows 10 with an ordinary x64 (Intel or AMD) processor. For macOS
and Linux, see [On macOS and Linux](#on-macos-and-linux) at the end of this section.

### On Windows: the one-click files

The SeedLab folder has seven small files that do the setup, start and stop the web page, and take
everything away again. Double-click them in File Explorer - the first time, in this order:

| File | What it does |
|---|---|
| `SeedLab 1 - Install or update.bat` | checks for Microsoft's .NET 10 SDK (and helps you install it), builds SeedLab, and makes `vseed` a command you can type |
| `SeedLab 2 - Open web page.bat` | opens the map and the search in your browser, starting SeedLab's web server first if it is not running |
| `SeedLab 3 - Stop web page.bat` | stops the web server |
| `SeedLab 4 - Command window.bat` | opens a PowerShell window in the SeedLab folder in which `vseed` works |
| `SeedLab 5 - Uninstall (keeps the build).bat` | removes everything SeedLab put outside its own folder |
| `SeedLab 6 - Remove the build.bat` | runs the uninstall if it has not been done, then deletes the build inside the SeedLab folder |
| `SeedLab.bat` | all of the above as one numbered menu, plus **S**tatus (what is installed and what is running) and **H**elp |

Each of them first does any earlier step that has not happened yet, and says so - open the web page
before anything is installed and it offers to install first. **Nothing is installed or removed
without asking.** Nothing is stopped without asking either, with one exception you ask for by name:
`SeedLab 3 - Stop web page` stops the web server at once when no search is running in it (and asks
first when one is). When there is nobody to answer, the answer is always "no". The window
stays open at the end until you press Enter, so you can read what happened (except `SeedLab 2`'s,
which closes by itself once the web server's own window has opened). All of them hand their
work to one PowerShell script, `scripts\windows\seedlab.ps1`; [`docs\scripts.md`](docs/scripts.md)
explains every step and every question.

**Before the first run:**

1. **Get the SeedLab folder.** On SeedLab's GitHub page choose **Code > Download ZIP**. Before you
   unpack it, right-click the ZIP file, choose **Properties**, tick **Unblock** at the bottom and
   click **OK** - that stops Windows from asking about every file in it (below). Unpack it somewhere
   in your own folders: Documents, a games folder, a second drive. Not in `C:\Program Files`, which
   needs administrator rights to write to. (Or clone it with git; a clone is not marked as coming
   from the internet.)
2. **"This file came from the internet."** If you did not unblock the ZIP, Windows asks before it
   runs each file. You will see either a blue **"Windows protected your PC"** window (SmartScreen):
   click **More info**, check that the file name is the SeedLab file you double-clicked, then
   **Run anyway** - or an **"Open File - Security Warning"** window ("The publisher could not be
   verified"): click **Run**. Only do this for files from SeedLab's own GitHub page.
3. **Do not use "Run as administrator".** Every one of the files refuses to run that way, and says why: "Run as
   administrator" can run them as a *different* Windows account - the one whose password was typed
   into the prompt - and SeedLab would then be set up for that account instead of yours. Double-click
   them normally.

### What "Install or update" does

It works in four steps and says each one as it goes:

1. **The .NET 10 SDK.** SeedLab is built from its source code, here on your PC, and the .NET SDK is
   Microsoft's free toolkit that does the building. It also brings the runtimes `vseed` needs - every
   `vseed` command needs its ASP.NET Core runtime, not only the web page. If it is missing, the script
   explains this and offers:
   **W** - install it with winget, Windows' own package manager (about 200 MB, downloaded from
   Microsoft); **B** - open Microsoft's download page, so you install it yourself; **C** - cancel.
   **Installing the SDK is the only step of SeedLab that needs administrator rights**, because it is
   installed for every account on the PC: Windows shows its own **User Account Control** prompt, and
   you answer **Yes** to continue. Nothing else asks for those rights.
2. **The build.** The first build takes a minute or two. **Telemetry:** the .NET SDK sends usage data
   to Microsoft by default. SeedLab's scripts turn that off for the builds they run
   (`DOTNET_CLI_TELEMETRY_OPTOUT=1`); your other uses of `dotnet` are not changed - to turn it off
   for those too, set that variable for your account yourself. When the build is already up to date
   with the source, nothing is built and nothing is stopped. Otherwise, if SeedLab's web page is still
   open, Windows will not let the build replace the running program; the script says so and offers to
   stop it first (a search running in it is stopped with its checkpoint saved).
3. **The `vseed` command.** The build folder is added to **your own** Path, so that typing `vseed`
   works in every Command Prompt or PowerShell window you open from now on (windows that are already
   open do not see it). Nothing outside your account is changed.
4. **A check.** It runs `vseed --version`, and tells you whether a new window will find this build.

Run it again after every update of SeedLab. When there is nothing to do, it says so and changes
nothing.

### The web page

Start it with **`SeedLab 2 - Open web page`** (or type `vseed serve` in a terminal). A few seconds
later your browser opens `http://127.0.0.1:8731` by itself. The page works only on this computer:
nothing is sent anywhere, and no other computer can reach it.

**The server window.** The page comes from a small program on your PC, SeedLab's web server, and it
runs in a window of its own titled **SeedLab web server**. The window says so first:

```
==============================================================================
 This window IS SeedLab's web server. Minimise it - do not close it - while
 you use the page. To stop it: Stop SeedLab on the page, 'SeedLab 3 - Stop
 web page' (or vseed serve --stop), or press Ctrl+C in THIS window twice.
==============================================================================
```

Keep it open (minimised is fine) while you use the page. **It never stops by itself, and it never
starts by itself**: it is not hidden, not a service, and not started when you log in. It runs until
you stop it.

**Using the page.** Type a seed - the text you would type in the game, or its number - into the box
at the top and press **Open** (or **Random**); drag to pan, scroll to zoom. The panels: **Seed**
(land, biomes, islands, spawn), **Places** (location markers, once you have the game data),
**Search** (find seeds), **Point** (click anywhere on the map for the exact values there) and
**Help** (keys, and what the map and its markers mean). To find seeds, open **Search**, pick a
**Preset** or **Add goal**, then **Find seeds**; matches appear as they are found. Name a **Results
file** to keep them: it is written to the `seedlab-results` folder inside the SeedLab folder, and
the page says where. **Export** saves the search as a query file that `vseed search` runs the same
way.

**Stopping it** - whichever of these is handy:

- **Stop SeedLab** on the page: a button at the top, and another on the **Help** panel. It first
  says what stopping does and asks. If a search is running, a **second** warning names it, says how
  far it got and what stopping does to it, with the command that continues it later - then **Stop
  anyway** or **Keep running**.
- **`SeedLab 3 - Stop web page`**, or `vseed serve --stop` in a terminal. If a search is running,
  it says which one and asks `Stop anyway? [y/N]` first.
- **Ctrl+C twice in the server window**, within 10 seconds. The first press stops nothing: it says
  what is running and what a second press would do. Ctrl+C counts only in that window - in the
  browser, Ctrl+C is Copy.
- **Close the server window.** That stops it at once, with no question - but a running search is
  still stopped properly, with its checkpoint saved, before the program ends. **Signing out of
  Windows, or shutting it down,** does the same.

Every way stops a running search the same way: at once, with the part it finished saved in a
checkpoint, so it can be continued from a terminal - **except a search still in the first stage of a
two-stage ("funnel") search**, which has no resume point yet and loses that stage's work; every
warning says so for that search. The command that continues a stopped search looks like this:

```
vseed search "C:\Users\...\SeedLab\checkpoints\675dc42410485df8.ckpt.query.json" --resume --checkpoint "C:\Users\...\SeedLab\checkpoints\675dc42410485df8.ckpt"
```

It works as printed: SeedLab keeps the search's query file beside its checkpoint, so there is nothing
to save first. It is shown where the stop happened - in the page, in the terminal that ran
`SeedLab 3` or `vseed serve --stop`, or in the server window, which then stays open until you press
Enter (at most 10 minutes) - and it is always in the session log, `logs\vseed.log`, which becomes
`vseed-prev.log` the next time SeedLab starts (see [The session logs](#the-session-logs)). The page
then says **"SeedLab has stopped. You can close this tab."**, with the same command. After a stop from
outside the page (the window, a script), the page takes up to about 20 seconds to notice.

**Starting a stopped search again.** If you press **Find seeds** on a search that was stopped and can
still be continued, the page says so first, shows the command that continues it, and asks: **Start
again from the first seed** replaces that resume point; **Cancel** keeps it.

Three different things are called "stop" - worth keeping apart: the **Stop** button on the Search
panel ends one search and leaves SeedLab running; **Stop SeedLab** ends the web server itself; and
**Ctrl+C** stops a `vseed search` running in a terminal (once), but the web server only when pressed
twice in its own window.

**The 60-minute reminder.** If nobody has used the page for 60 minutes, the page and the server
window suggest stopping SeedLab, with a **Stop SeedLab** button. "Used" means something you did -
the page's own automatic refreshing does not count, and a running search does. **Keep running**, or
simply ignoring it, asks again after another 60 minutes, and so on. It never stops SeedLab by
itself. `vseed serve --idle-reminder 30` makes it 30 minutes; `0` turns it off.

**Opening it again** while it is running - `SeedLab 2` again, or a second `vseed serve` - opens the
page of the server that is already running; it does not start a second one. If the browser does not
open, go to the address the server window printed. If another program already uses port 8731,
`vseed serve --port 0` lets Windows pick a free port and prints it.

**Not yet tested for real** (2026-09-25): closing the server window with its **X** (it was tested
once by sending the window its close message: the search's checkpoint was saved and nothing was left
behind); a real Windows sign-out or shutdown (a test sends the server the same two messages Windows
sends, and it stops properly - but nobody has yet watched Windows deliver them at a real sign-out);
typing an answer at `vseed`'s own `Stop anyway? [y/N]` (only the refusal with nobody to answer, and
`--yes`, were run); two web servers running in one cache folder at the same time; a stop during the
short measurement a funnel search makes between its two stages; and all of this on macOS and Linux.
A stop during a funnel's first stage **was** tested: every warning says that search's work is lost,
and nothing of it is left behind.

### The command line

**`SeedLab 4 - Command window`** opens a PowerShell window in the SeedLab folder in which `vseed`
works. After Install, `vseed` also works in any **new** Command Prompt or PowerShell window.
`vseed --help` lists the commands, and `vseed <command> --help` explains one. (`vseed.exe` is a
console program: double-clicking it opens a window that prints the command list and closes at once.)
The everyday commands:

| To | Type |
|---|---|
| see everything about one world | `vseed seed MWd8eV6svz` |
| read one exact point (x, z in metres) | `vseed at MWd8eV6svz 120 -340` |
| list where the places are (needs the game data) | `vseed locations MWd8eV6svz` |
| save a map picture | `vseed map MWd8eV6svz --rivers -o world.png` |
| see the ready-made searches | `vseed presets list` |
| find seeds (needs the game data, below) | `vseed search gentle-start --seeds 4000 --keep 20 --out hits.jsonl` |
| ask why one seed passed or failed (needs the game data) | `vseed explain -1957974196 gentle-start` |
| turn a seed number into text you can type | `vseed invert -1957974196 --alphabet game` |
| list your own worlds (read-only) | `vseed worlds` |
| is the web page running? stop it | `vseed serve --status`, `vseed serve --stop` |

A search prints its plan and its cost before it scans anything, and asks before anything expensive
(`--yes` answers for you). **Ctrl+C stops a search at the next block and writes a checkpoint**; run
the same command again with `--resume` to continue where it stopped.
[`docs\finding-a-seed.md`](docs/finding-a-seed.md) walks through a first search with real output.

### The session logs

Every `vseed` command that does real work keeps a log of what it did - and SeedLab keeps exactly
two of them:

```
%LOCALAPPDATA%\SeedLab\logs\vseed.log        this session: the last vseed command, or the web server that is running
%LOCALAPPDATA%\SeedLab\logs\vseed-prev.log   the session before it
```

When a command starts, `vseed.log` becomes `vseed-prev.log` (replacing the older one) and a new
`vseed.log` begins - the way Valheim's own `BepInEx\LogOutput.log` is rewritten at every start. So
the last two sessions are always there, and never more. If you want to keep one, copy it before you
run two more `vseed` commands. While another `vseed` is running - the web server, say - a second
command cannot take over those two, so it writes `vseed.log.1` (up to `.4`) instead, and a numbered
log nobody is using is deleted by a later command. **After the web server stops**, its log - with
every search it stopped, where that search's checkpoint is and the command that continues it - is
`vseed.log` until the next `vseed` command, then `vseed-prev.log`, and gone after one more: copy the
command if you mean to continue the search later. A log that passes 4 MiB keeps only warnings and
errors from then on, and one that passes 16 MiB stops, so the logs cannot fill a drive; a normal
session is a few kilobytes. **The logs contain your folder paths** (on Windows usually
`C:\Users\<your account name>\...`) - read them before you post them anywhere public. **S**tatus in
`SeedLab.bat` shows where they are. More about them:
[The session logs, and a file another program holds](#the-session-logs-and-a-file-another-program-holds).

### Where things go

- **Your results:** where `--out` says (terminal), or the `seedlab-results` folder inside the SeedLab
  folder (the web page; to be exact, inside the folder `vseed serve` was started from).
- **Everything else** goes to SeedLab's cache folder, `%LOCALAPPDATA%\SeedLab`: search checkpoints,
  map pictures made without `-o`, the web page's map tiles, the self-test stamp, the two session logs,
  and - while the web server runs - a small file that says where it is (`serve\`). Deleting the
  folder loses nothing you asked to keep, except that an unfinished search can then no longer be
  continued. `vseed clean` shows what is there and removes it with `--yes`. `--cache-dir <folder>` or
  the `SEEDLAB_CACHE_DIR` setting puts it somewhere else.
- **Your Valheim saves are never written.** `vseed worlds` and `vseed world` only read them.

### Locations and searches need the game data

**Terrain answers work straight after Install**: biomes, heights, rivers, maps, seed arithmetic,
and the land and island figures of `vseed seed`. **Bosses, traders, dungeons and every other
location need the `data\` folder**, which you make from your own copy of Valheim:
[`docs\game-data.md`](docs/game-data.md) walks through it step by step. Until then those answers
stop with a message that says so (exit code 3). **Searching and `vseed explain` need it too, for
now - even for a search that only asks about terrain**: the search's checker reads one file of the
game data (`constraint-atlas.json`), and without it every search and every explain is refused, on
the page and in the terminal, with a message naming that file. `vseed data` shows what data SeedLab found and whether it matches
your installed game.

### Updating

Stop the web page if it is open (the build cannot replace a running `vseed`; Install offers to stop
it for you). Then:

- **With git:** `git pull`, then run **`SeedLab 1 - Install or update`** again.
- **With a ZIP:** do **not** unpack the new ZIP over the old folder - a file that a new version
  deleted or renamed would stay behind, still be built, and could break the build or change what it
  does. Unblock the new ZIP first (right-click, **Properties**, **Unblock**, as the first time),
  unpack it into a **new** folder, and copy your `data\` folder and your `seedlab-results` folder (if
  you have one) from the old folder into the new one. Then delete the old folder, and run
  **`SeedLab 1 - Install or update`** in the new one: it notices that the `vseed` command still points
  at the old folder, offers to take that off your Path, and registers the new one.

`SeedLab 2` and `SeedLab 4` also notice when the source has changed since the last build and offer
to rebuild first. After a **Valheim** update, see
[The game data, and what to do after a Valheim update](#the-game-data-and-what-to-do-after-a-valheim-update).

### Uninstalling

**`SeedLab 5 - Uninstall (keeps the build)`** lists everything it will do and asks once. It stops
the web server (and asks separately if a search is running, because that search's checkpoint goes
with the cache folder), moves the cache folder `%LOCALAPPDATA%\SeedLab` - both session logs
included - to the **Recycle Bin** (if Windows would have to delete it permanently instead, Windows
itself warns first), and takes `vseed` off your Path. Then it asks separately about a cache folder
you chose with `SEEDLAB_CACHE_DIR`, your `SEEDLAB_...` settings, SeedLab's dumper plugin in Valheim
and the dumper's output folder. It keeps, on purpose: the SeedLab folder and its build, `data\`,
your results, and the .NET SDK, which other programs may use (Windows **Settings > Apps** removes
it). Run it again and it says there is nothing left to do.

**`SeedLab 6 - Remove the build`** runs the uninstall first if it has not been done, then deletes the
build folders inside the SeedLab folder (not to the Recycle Bin: Install rebuilds them exactly). To
remove SeedLab completely, delete the SeedLab folder itself afterwards - save its `seedlab-results`
folder first if you want your results.

### Doing it by hand

If you prefer a terminal, or a script step fails, this is what the scripts do. In PowerShell:

1. **Install the .NET 10 SDK** from <https://dotnet.microsoft.com/download/dotnet/10.0> (the SDK
   installer for Windows x64). It includes the ASP.NET Core runtime that every `vseed` command needs.
   Nothing else is downloaded: SeedLab uses no NuGet packages.
2. **Open a terminal in the SeedLab folder.** In File Explorer, open the folder, click the address
   bar, type `powershell` and press Enter.
3. **Build it:** `dotnet build src\SeedLab.Cli\SeedLab.Cli.csproj -c Release`. The program is
   `src\SeedLab.Cli\bin\Release\net10.0\vseed.exe`. Leave it where the build put it: that is how it
   finds the `data\` folder on its own, whichever folder you run it from.
4. **Make `vseed` a command** for this terminal:
   `Set-Alias vseed "$PWD\src\SeedLab.Cli\bin\Release\net10.0\vseed.exe"`. For every terminal from
   now on, add that `bin\Release\net10.0` folder to your user **Path** (Start menu, *Edit environment
   variables for your account*, **Path**, **Edit**, **New**), then open a new terminal.
5. **Try it:** `vseed seed MWd8eV6svz`. The web page is `vseed serve`; `vseed serve --stop`, or
   Ctrl+C twice in its window, stops it.
6. **To remove it by hand:** delete `%LOCALAPPDATA%\SeedLab`, take the build folder off your Path,
   and delete the SeedLab folder.

### On macOS and Linux

The same actions are in **`seedlab.sh`**: run `sh seedlab.sh` in a terminal in the SeedLab folder for
the menu, or `sh seedlab.sh install | web | stop | status | shell | uninstall | remove-build`. On a
Mac, double-clicking **`SeedLab.command`** in Finder opens the menu in Terminal (the first time,
macOS may say the file is from an unidentified developer: right-click it, choose **Open**, then
**Open** again). If it says instead that you do not have permission to open it, the file lost its
"may be run" mark on the way (a ZIP can do that): run `chmod +x SeedLab.command seedlab.sh` once in a
terminal in the SeedLab folder, or simply use `sh seedlab.sh`.

**Say it plainly: these have not been tested on macOS at all - no Mac was available - and not yet on
a real Linux system** (that is planned, in WSL). They were checked with `dash`, the strict shell
Ubuntu uses, running against a Windows build of SeedLab. Treat them as a careful first version;
[`docs\scripts.md`](docs/scripts.md) lists the manual steps each action automates.

What differs from Windows:

- They refuse to run as root, and never run `sudo`. If the .NET 10 SDK is missing they offer to
  install it **for your account only** with Microsoft's installer script (into `~/.dotnet`, no
  password needed), or tell you how to install it yourself (your distribution's package, or the
  macOS installer - those ask for an administrator password).
- The `vseed` command is a small script at `~/.local/bin/vseed`; if that folder is not on your PATH,
  the script offers to add three marked lines to your shell's startup file, and uninstall takes
  exactly those lines out again.
- **The web server runs in the terminal you started it from** (on a Mac, the Terminal window
  `SeedLab.command` opened). Keep it open while you use the page; stop it with Stop SeedLab on the
  page, `sh seedlab.sh stop` from another terminal, or Ctrl+C twice in that terminal.
- The cache folder, with the two session logs in its `logs` folder, is `~/Library/Caches/SeedLab`
  on macOS and `~/.cache/seedlab` on Linux. Uninstall moves things to the Trash where the system has
  one the script can use (macOS; Linux with `gio` or `trash-put`) and otherwise **deletes them for
  good** - it says which before it asks.
- **Apple Silicon and other ARM processors:** SeedLab has only ever been proven on x64 processors.
  On ARM, `vseed` stops with `SELF-TEST UNPROVEN` and answers nothing unless you add
  `--accept-unverified-platform` - and then its answers may differ from the game's. (Intel Macs are
  x64.)

---

## What it can and cannot tell you

### Exact — bit-for-bit against what the game wrote

| | |
|---|---|
| Biome at any point | `GetBiome`, verified over ~5.1 M minimap pixels on two worlds, 0 mismatches |
| Terrain height at any point | verified as the stored binary16 code, 4,194,304/4,194,304 per world |
| Rivers, streams and lakes | the generator's own point lists, 2.1 M rendered river points in order, bit-exact on three seeds |
| The biome map | `vseed map --plain --palette game` is pixel-identical to the game's own texture |
| Where every location instance is | 12,228/12,228 on a fresh world; 12,314/12,314 and 12,287/12,287 against two played worlds' `.db2` |
| How many of each type got placed | the game's own 29 `placed N out of M` counters, all reproduced exactly |
| Seed text ↔ int32, both directions | every returned text is re-hashed before it is printed |

### True, but with a resolution attached

Anything counted over an area — biome share, land area, island counts, "nearest Swamp" — is counted
on a **sampling grid**, and the grid is part of the number. `vseed` prints it with every figure. A
count at 24 m is a *different measurement* from the same count at 12 m, not an approximation of it:
the raw island component count moves about 45× over a 16× change of spacing. Do not compare figures
across grids.

Distances to a biome are "the centre of the nearest cell of that biome", so they carry half a cell
diagonal of slack — 8.485 m on the game's own 12 m grid — and the output says so. When a distance is
at or below that floor the record says `censored` rather than printing a number that looks measured.

Eight metrics cannot be compared across grids at all; each carries `grid_comparable: false` and a
sentence saying why. The worst is `spawn_island_area`: median |relative error| against the game's
own grid is 14.0 % at 24 m and 70.8 % at 384 m, because coarse grids merge islands across straits.

### True, but bounded rather than proven

Some answers are statements about *all* seeds that only a sample supports, and SeedLab says which
kind it is holding. The seven boss altars never fell short in 5,000 uniformly drawn seeds, which
**bounds** the shortfall rate at ≤ 0.060 % (95 %, rule of three) and does **not** make the count
fixed — and `DN_Bossroom`, a boss altar in the same catalogue, *does* fall short, in 26 of 5,000
seeds (0.520 %, CI 0.355–0.761 %). Full list and the proofs behind it:
[`docs\limits.md`](docs/limits.md).

### Not predictable at all — by anyone, from the seed

- **Which candidate of a unique location survives.** Vendor_BlackForest has ten candidate positions.
  The game keeps exactly **one**, and which one depends on the first zone a player or a peer
  generates, not on the seed. SeedLab lists all ten and refuses to name one. The seed sites that show
  you "the" trader are showing you a candidate.
- **The rotation of anything.** `Quaternion.Euler(0, Random.Range(0,16)*22.5, 0)` is drawn from the
  ambient `UnityEngine.Random` stream at spawn time, which nothing seeds.
- **The interior of a dungeon whose generator sits off its location's axis**, because the unseeded
  rotation moves the generator and its position feeds its seed.
- **The ground a building finally rests on.** `gen y` is `WorldGenerator.GetHeight`, which is what the
  save stores; the real ground comes from the built heightmap and the location's own terrain edits.
- **Anything about a world that has already been played.** Generated zones are excluded from a re-run,
  placed instances are kept, and the game's own `genloc` is not deterministic either.

### Simply not modelled

Creatures and spawns, loot, ore under the ground, dungeon room contents, world modifiers' effect on
anything but the flags in the save, and anything a player did.

`vseed locations` prints the unpredictable list in full every time it runs. It is not a footnote.

**[`docs\limits.md`](docs/limits.md) is the long version of this section** — every grid-relative
metric with its measured error, the answers that are bounded rather than exact, the shortfall rates,
what a single untested architecture means and what the startup self-test does about it, and what is
simply not implemented (`--strategy funnel|sample`, the D4/D5 count rules, GPU).

---

## Build it

(`SeedLab 1 - Install or update` does all of this for you - see [How to use it](#how-to-use-it).
This is the same by hand.)

You need the **.NET 10 SDK**. Every `vseed` command also needs the ASP.NET Core 10 shared runtime
(the web server is built into the same program), which ships in the same install. **There are no NuGet packages** — the whole thing builds offline.

```
cd <your SeedLab folder>
dotnet build src\SeedLab.Cli\SeedLab.Cli.csproj -c Release
```

The tool is `src\SeedLab.Cli\bin\Release\net10.0\vseed.exe`. Put it on your PATH or alias it - in
PowerShell, from the SeedLab folder:

```powershell
Set-Alias vseed "$PWD\src\SeedLab.Cli\bin\Release\net10.0\vseed.exe"
```

Run it from inside the SeedLab folder, or set `SEEDLAB_DATA_DIR` to `data\1.0.15-59f53fb5\` — that is
where the location table lives, and without it the location commands exit 3 and say so.

Check the build against the game before trusting it:

```
vseed selftest                    # full: every cell of both ground-truth worlds
vseed selftest --quick            # 1 cell in 16, about 4 s
```

**If a build fails with MSB3027 or MSB3021**, a `vseed serve` or `vseed search` is still running and
holding `bin\Release\`. Stop it (`vseed serve --stop` for the web server) and build again.

---

## The commands

```
vseed --help                 the command list
vseed <command> --help       one command's options
```

`--json` gives every data command a machine-readable form on stdout, with warnings on stderr, so it
pipes into `jq` cleanly. Exit codes: `0` ok, `1` a check failed, `2` bad command line, `3` not found -
or a file or folder SeedLab needs is in use, read-only or not allowed - and `4` internal fault. Global
options work on either side of the command name:

| | |
|---|---|
| `--mode background\|balanced\|full` | how much of the machine to use. **Default `balanced`** — about 50 % of the logical cores at Normal priority. `background` is ~25 % at BelowNormal, for while you play; `full` is every core, still capped by the memory guard. Every run prints the arithmetic: `workers 8 (balanced mode = 50 % of 16 logical cores -> 8; memory allowed 840)`. |
| `--threads <n>` | override the worker count (1..64). Still capped by free memory, and the cap is printed. |
| `--cache-dir <dir>` | where checkpoints, rendered maps, web tiles, the self-test stamp, the two session logs and a running web server's file live. Default `%LOCALAPPDATA%\SeedLab`, or `$SEEDLAB_CACHE_DIR`. A web server started with `--cache-dir <dir>` is found by `vseed serve --status` / `--stop` with the same `--cache-dir <dir>`. |
| `--ignore-running-game` | do not drop to `background` when Valheim is running. |
| `--skip-self-test` | do not check this machine against the recorded goldens. `seed`, `at`, `map`, `locations` and `search` then say `warning: the machine self-test was turned off: SeedLab's bit-exactness is UNVERIFIED on this run` (commands that do not build a world stay quiet). |
| `--accept-unverified-platform` | proceed on an architecture the gates have never run on (see [`docs\limits.md`](docs/limits.md)). |
| `--simd auto\|scalar\|avx2\|avx512` | the widest vector path the generator may use. **Default `auto`**: the widest this CPU and the .NET runtime allow (AVX2 today; AVX-512 is detected but has no kernel yet). Every path is proved to give the same bits, so this changes only speed - `scalar` can be faster on CPUs with slow gathers. See [`docs\cpu-compatibility.md`](docs/cpu-compatibility.md). |

**Auto-throttle.** If Valheim is running when a command starts, SeedLab drops to background mode by
itself and prints one line saying so and how to override it:

```
vseed: valheim is running - dropping to background mode (~25 % of cores, BelowNormal).
       Override with --mode full --ignore-running-game.
```

**The machine self-test.** On a cold cache, the first command that builds a world re-checks this
build's arithmetic against the corpora the game itself produced — 271 numerics checks and 263,780
recorded native values — and **fails closed**: one divergent value and the command exits 1, naming
the platform, the suite, the first failing case with both numbers, and what to do. Demonstrated by
altering one recorded hash by 1 in a copy of `groundtruth\natives`, which made `vseed seed 12345`
exit 1 with `seedlab/natives: 263779/263780 exact`. A pass writes a stamp in `<cache root>\selftest`
and costs nothing again - until the processor, the vector path, a .NET runtime switch or the C
runtime's `ucrtbase.dll` changes, each of which is part of the stamp and re-runs the test.

**`vseed clean`** reports what SeedLab is holding on disk, per category, with the volume's free
space, and removes the caches with `--yes` (`--what checkpoints,maps,tiles,scratch,runs,selftest,logs,all`).
"Freed" counts only files that were really deleted; one another program has open is left alone and
listed with the probable cause, and the command's own session log is always kept. The table's "this
run" column says `removed` only for a category whose every file went — `partly removed`, or
`kept (in use)` when none did (the `logs` row usually, since the command keeps its own). It also compares
the dumper's raw output folder against `data\` and tells you when it is redundant — without ever
deleting it.

### `vseed seed` — everything about one world

```
$ vseed seed MWd8eV6svz

Seed
----------------
  as typed              "MWd8eV6svz"  (seed text)
  int32                 -1772362158
  shortest text         Hi9L9a  (6 chars, alphanumeric)
  game-style text       5R3inNYZse  (10 chars, the 59 the game's own generator uses - ONE of the many texts for this seed)
  worldGenVersion       2

  The text is hashed to the int by the game's World constructor (World..ctor) and never
  looked at again, so the int IS the world.

Measurement
-----------------------
  grid                  G12 (2048 x 2048 @ 12 m, the grid the game itself samples)
  cells in world        2,405,324 of 4,194,304   (DUtils.Length(x,z) <= 10500 m)
  area sampled          346.37 km2   (cell 144 m2)
  time                  5.48 s field, 1.98 s analysis, 16 threads

Land and water  (land = height >= 30.0 m, the game's water level)
-----------------------------------------------------------------------------
  land                  117.60 km2   33.95 %
  water                 228.76 km2   66.05 %

Biomes  (share of the sampled in-world area)
--------------------------------------------------------
  biome         area km2      %  land km2  nearest m  nearest land m
  ------------  --------  -----  --------  ---------  --------------
  Meadows          11.56   3.34      5.72          8              19
  Black Forest     44.34  12.80     19.38        508             508
  Swamp             9.02   2.60      3.04       2000            2000
  Mountain         11.39   3.29     11.18        628             628
  Plains           47.16  13.61     21.09       2902            2902
  Mistlands        57.98  16.74     32.02       5900            5900
  Ashlands         43.03  12.42      6.98       7909            8443
  Deep North       23.91   6.90     18.19       7915            8078
  Ocean            97.98  28.29      0.00        102               -

  'nearest' is the centre of the closest cell of that biome to (0,0), so it is within 8.5 m
  (half a cell diagonal) of the true nearest point.
```

`World..ctor` in that output is not a typo or a cut-off word: it is the real name of the game's
`World` constructor, the code that runs when the game creates a world. `World` is the class and
`.ctor` is the name .NET gives every constructor, hence the two dots. It is where the text you type
in the new-world seed box becomes a 32-bit number, and where an empty box becomes seed 0.

The output goes on to islands (with a per-seed measurement of how much of the count survives the
binary16 precision the game's own map cache stores), the spawn area, the extremes, and:

```
Landmarks  (the game's own location placement, run for this seed)
-----------------------------------------------------------------------------
  game data             1.0.15, dumped 2026-09-23
  types run             23 of 183   (0.58 s biome grid, 0.09 s placement)
  spawn (StartTemple)   (71, -3)   this is Game.FindSpawnPoint's anchor, not (0, 0)
  name                prefab                         kind        count  nearest m  dir      at              biome         one position?
  ------------------  -----------------------------  ----------  -----  ---------  -------  --------------  ------------  -------------
  Eikthyr             Eikthyrnir                     boss altar      3        131  ESE 123  (110, -71)      Meadows       yes
  The Elder           GDKing                         boss altar      4       2757  S 176    (188, -2751)    Black Forest  yes
  Moder               Dragonqueen                    boss altar      3       3050  ENE 59   (2625, 1554)    Mountain      yes
  Bonemass            Bonemass                       boss altar      5       3087  NE 34    (1723, 2561)    Swamp         yes
  Yagluth             GoblinKing                     boss altar      4       5234  ENE 78   (5120, 1088)    Plains        yes
  The Queen           Mistlands_DvergrBossEntrance1  boss altar      5       6341  SE 137   (4288, -4672)   Mistlands     yes
  Fader               FaderLocation                  boss altar      3       9097  S 187    (-1152, -9024)  Ashlands      yes
  Kall Fimbulbringer  DN_Bossroom                    boss altar      3       9440  N 352    (-1344, 9344)   Deep North    yes
  Haldor              Vendor_BlackForest             trader         10       2516  NE 38    (1545, 1986)    Black Forest  1 of 10
  Hildir              Hildir_camp                    trader         10       3017  SSW 192  (-639, -2949)   Meadows       1 of 10
  The Bog Witch       BogWitch_Camp                  trader         10       3136  NNW 327  (-1726, 2618)   Swamp         1 of 10
```

Two columns carry the honesty. `name` is the game's own string for the place, from the dumped
localization table - a dash there means the dump names it nothing and the prefab is what it is
called, which is the case for 152 of the 183 placed types. `one position?` answers `1 of 10` for a
`m_unique` type, because the game keeps exactly one of those candidates and which one is not a
function of the seed.

Useful options: `--grid <m>`, `--islands <n>`, `--no-landmarks` (much faster), `--dungeons` (runs all
183 location types), `--json`.

### `vseed at` — one point, exactly

No grid, no snapping — the coordinates go straight into the generator.

```
$ vseed at MWd8eV6svz 0 0

Point  (0.00, 0.00)  in seed -1772362158
----------------------------------------------------
  biome                 Meadows   (Heightmap.BiomeIndex 1)
  height                23.619 m
  vs sea level          6.381 m below the 30 m water line (underwater)
  base height           0.08344   (normalised; x200 before biome shaping)
  river / stream        no  (weight 0)
  forest factor         1.0189   in forest (< 1.15)
  zone                  (0, 0)   64 m zone, centre (0, 0)
  from the centre       0.0 m, bearing 0.0 deg N
  geometry              inside the 10500 m water edge
```

`--ascii` draws a terrain sketch around the point.

### `vseed locations` — where things are

```
$ vseed locations MWd8eV6svz

Locations
---------------------
  as typed              "MWd8eV6svz"  (seed text)
  int32                 -1772362158
  game data             1.0.15, dumped 2026-09-23 (59f53fb55d99)
  selection             --type boss,trader  ->  11 of 183 location types
  names                 English, from the 1.0.15 dump
  placement run         23 of 183 types (0.74 s world, 0.12 s placement)
  
  Everything before the selection has to run too: one location per zone, globally, so an
  earlier type can take a later one's zone. The prefix is never shortened past that.

  Eikthyr  (Eikthyrnir)   [boss altar]
    3 shown of 3 placed, m_quantity 3, biome Meadows
     x     z  dist m  bearing        biome    gen y  zone
  ----  ----  ------  -------------  -------  -----  --------
   110   -71     131  122.7 deg ESE  Meadows   43.1  (2, -1)
  -597  -174     621  253.8 deg WSW  Meadows   33.1  (-9, -3)
  -779   428     889  298.8 deg WNW  Meadows   51.5  (-12, 7)

  Haldor  (Vendor_BlackForest)   [trader, m_unique]
    10 shown of 10 placed, m_quantity 10, biome Black Forest
    m_unique: the game keeps exactly ONE of these 10 candidates. Which one is NOT a
    function of the seed - the first candidate whose zone a player (or a peer)
    generates wins, and ZoneSystem.RemoveUnplacedLocations deletes the others.
    All of them are listed; none of them is 'the' position.
  ...
```

`--type boss|trader|dungeon|unique|all`, `--name <prefab>`, `--top <n>` (`0` for every one),
`--max-distance <m>`, `--json`. A full `--type all` run took **26.4 s** for one seed here (4.7 s world
grid on 16 threads, 21.7 s placement).

### `vseed map` — a PNG

```
vseed map MWd8eV6svz                                  # 2048 x 2048, the game's own grid
vseed map MWd8eV6svz --px 4096 --rivers --grid
vseed map MWd8eV6svz --zoom 0,0,2000 --px 1024        # 2 km around spawn
vseed map MWd8eV6svz --plain --palette game -o exact.png   # pixel-identical to the game's texture
```

The footer records the seed, the `worldGenVersion`, the grid, the area covered, the engine version
and the biome shares, so a PNG that has been sitting in a folder for a month still says what it is.
It lays itself out to the image width, down to `--px 256`.

**With no `-o` the render goes to the cache root** (`<cache>\maps`) and the path is printed — it is
not dropped into whatever directory you happened to be standing in, and `vseed clean` empties it.

### `vseed hash`, `vseed invert`, `vseed space` — the seed arithmetic

```
$ vseed hash MWd8eV6svz
  text                  "MWd8eV6svz"
  int32 seed            -1772362158
  even lane             210405865
  odd lane              223983285
  combiner              1566083941   seed = even + combiner * odd (checked)

$ vseed invert -1772362158 --alphabet game --length 10 --count 3
  text                  B2JpDNUCMg   (10 chars, re-hashed and checked)
  text                  9dEJ2Gsby3   (10 chars, re-hashed and checked)
  text                  pQscvHcbVt   (10 chars, re-hashed and checked)
```

Every text is re-hashed before it is printed. An unverified preimage is never returned.

### `vseed worlds`, `vseed world` — your saves, read-only

```
$ vseed worlds
Worlds  (2)
  world                seed text    int32 seed  gen  map cache  note
  -------------------  ----------  -----------  ---  ---------  ---------------
  asdasdasd            MWd8eV6svz  -1772362158    2  yes
  testworldclaude      hnBd9gJf2G    319486907    2  yes
```

`vseed world <name>` reads one save's seed, world-gen version, modifiers and contents;
`--locations` counts the location instances stored in the `.db2`. **Nothing is ever written to a save
folder or to Steam Cloud.**

### `vseed data`, `vseed selftest`, `vseed bench`

`data` reports the shipped game data and whether it matches your install. `selftest` re-checks this
build against the ground truth. `selftest --report` is the **machine report** - the processor, its
instruction sets, the vector path SeedLab chose and why, the C runtime's version, the machine
self-test run there and then, and world fingerprints of 8 seeds compared with the reference machine's.
It needs neither `groundtruth\` nor `data\`, contains no machine name, user name or path, and is what
to send when SeedLab runs on a CPU it has not been tested on ([`docs\cpu-compatibility.md`](docs/cpu-compatibility.md)).
If SeedLab stops at start-up because its AVX2 path disagrees with the reference on your CPU, it still
runs bit-exactly on the scalar path: send `vseed --simd scalar selftest --report` instead, which then
starts, proves the AVX2 path separately and prints where it differs. `--report` runs the self-test
even when `--skip-self-test` is given.
`bench` measures each stage on your machine, so any throughput estimate is anchored to a number you
watched being produced:

```
$ vseed bench --no-map
Bench  (16 logical cores, .NET 10.0.12)
  stage                              measured                       rate
  ---------------------------------  -----------------------------  ----------------------
  generator construct + pregenerate  949.2 ms per world (8 worlds)  1.1 worlds/s, 1 thread
  GetBiome                           2,000,000 samples in 3.479 s   0.57 M/s, 1 thread
  GetBiomeHeight                     2,000,000 samples in 3.955 s   0.51 M/s, 1 thread
  GetStableHashCode (10 chars)       1,000,000 in 0.074 s           13.57 M/s, 1 thread
  shortest-text inverse              2,000 in 1.612 s               1241 seeds/s, 1 thread
```

### `vseed profile`

Where one seed's time goes, phase by phase: the generator's constructor, the lake/river/stream
pre-generation and its nine steps, the biome and height passes per sampling grid, the structure
counts, and the location world build (the 2048^2 point grid, the sectors, the alt biomes, the
placement). With no options it runs a fixed battery; `--tier`, `--grid`, `--prefix`, `--seeds` and
`--threads 1,8,16` narrow or widen it, `--counters` also counts per-point events (base heights, world
angles, river lookups), and `--out profile.json` keeps the result (`seedlab-profile/1`, with
`--per-seed` a CSV beside it). `vseed profile --help` has the rest.

- **It changes no answer.** Timestamps are taken only at phase boundaries, generator code can write
  the profiler but never read it (an IL check in the tests enforces that), and the world fingerprints
  of 64 seeds - every biome, height, river point and placed location - are bit-identical with the
  profiler off, on, and on with counters.
- **It says when its numbers are not measurements.** It watches the machine for 30 s first and the
  whole time it runs; another `vseed` or SeedLab test, a running Valheim, a busy build or a heavy
  background load marks the run **TAINTED**, naming what it saw. The load is judged on the whole
  machine, so a protected process whose own CPU time Windows will not show (an antivirus scan, the
  search indexer, an update) counts too, and a baseline that was itself busy does not raise the limit.
  A tainted profile is a smoke test, not a figure to quote - `docs\measurements.md` stays the only
  source of throughput numbers.
- `--overhead` measures what the profiler itself costs (and, with `--baseline <another vseed.exe>`,
  what this build costs with the profiler off against another build). It times the counters itself, so
  it refuses `--counters` and a `SEEDLAB_PROFILE_COUNTERS=1` left in the environment; every other
  command prints a warning when that variable is set, because it slows every world.

---

## The session logs, and a file another program holds

Every command that starts SeedLab's runtime — `seed`, `at`, `map`, `locations`, `search`, `explain`,
`serve`, `selftest`, `bench`, `profile` and `clean` — keeps a log of what it did, and SeedLab keeps two
of them, this session's and the last one's:

```
%LOCALAPPDATA%\SeedLab\logs\vseed.log         this session's      (with --cache-dir: <that folder>\logs\)
%LOCALAPPDATA%\SeedLab\logs\vseed-prev.log    the last session's
```

- **At the start of every session `vseed.log` is renamed `vseed-prev.log`**, replacing the one before
  it, and a new `vseed.log` begins - the way the game's BepInEx rewrites `BepInEx\LogOutput.log` each
  time Valheim starts, with one session more kept. So the logs always describe the **last two**
  commands - if you want to keep one or send it to someone, **copy it before you run two more `vseed`
  commands.** (Before 2026-09-24 there was only `vseed.log`, rewritten every time.) If another
  program holds `vseed-prev.log` so that it cannot be replaced, it is kept as it is and the new
  session writes on after the last one's lines in `vseed.log` (unless those are already past 4 MiB),
  and says so.
- **Bounded:** past 4 MiB a log keeps only warnings and errors (it says so once, and counts what it
  left out); past 16 MiB it stops. A normal session is a few kilobytes - only a fault that repeats
  could get there - so the two logs together can never grow past a few tens of MiB.
- **It holds your folder paths** — on Windows usually `C:\Users\<your account name>\...` — on almost
  every line, and the command exactly as you typed it. Read it, or replace the name, before you post
  it anywhere public. (It records no computer name, no user name as such and no keys; of the
  environment, only the name of the variable the cache folder came from. It does record the process
  ID - the PID the system gave that `vseed` run - on its first lines; the number changes every run
  and identifies no one, but take it out too if you would rather not share it.)
- **What is in it:** when the session started (your local time and UTC), the vseed version, the
  command exactly as you typed it, the process ID with the operating system and .NET version it ran
  on, the machine, the cache folder, an access check of every SeedLab folder, the self-test result,
  the warnings and errors the command printed (a question you answered
  "no" shows only as the exit code), the retries behind a file that was busy, the full detail of an
  unexpected error (the terminal shows that only with `--debug`), an integrity line (the self-test,
  how many game-data files matched their SHA-256 in `manifest.json`, and whether the data's
  DATA-STAMP matches the installed game), and at the end the exit code and how long it took.
- **Two at once:** while one `vseed` is still running — a `vseed serve` you left open, a long search —
  a second one does not touch its log or `vseed-prev.log`: it writes `vseed.log.1` instead (then
  `.2`, up to `.4`; a sixth at once runs without a log and says so). A numbered log nobody is using
  is deleted by the next command that starts. (The first line of every log names the process that
  writes it, which is how a later command tells a log in use from one another program holds.)
- `hash`, `invert`, `space`, `presets`, `data`, `worlds` and `world` start no runtime, keep no log,
  and leave the last ones alone; so do `--help`, `--version` and `vseed serve --status` / `--stop`.

### When a file is in use, read-only or not allowed

Windows will not let a program replace a file while **any** other program has it open — a virus
scanner checking it, OneDrive or Dropbox syncing it, a search indexer, a spreadsheet with the
results open, an image viewer showing the last map. A search used to die of that with a bare
`Access to the path is denied.` that named no file. Now:

- **Before a run starts**, `vseed search` checks the results file (and a rotated run's manifest), the
  checkpoint's folder and, with `--resume`, the checkpoint, its snapshot and the survivor list;
  `vseed map` checks `-o`. A file that
  cannot be used is named, with what probably has it and what to do. In a terminal you are asked
  `[r]etry / [a]bort` — close the program, type `r`, press Enter. With `--json`, or when nothing is
  reading the keyboard, the command stops with exit 3 and writes nothing. `--dry-run` says so and goes
  on. The startup block sums it up: `file access checked: 11 paths OK; integrity confirmed (...)`.
- **That check describes the moment it was made. It cannot stop another program from opening a file
  an hour later** — that is what the retries are for. Every save is tried again for about 1.6 s; a
  checkpoint save that still fails is a `warning:` that names the file, and the run goes on and tries
  again at the next checkpoint. The warning says "It passed SeedLab's access check at <time>, so
  something changed after that" only when **the file itself** passed that check and fails it now. A
  file whose folder alone was checked (a checkpoint without `--resume`) gets no such line, and a
  program that has the file open while letting others write to it — which passes the check and still
  blocks the save — is said to be one the check cannot see.
- The **last** save of a run that stops early waits about 15 s, and says so as the wait begins. If it
  still fails, an `error:` says what is on disk and what `--resume` will do. That includes a
  checkpoint that is there but could not be read either, because the same program holds it: it is
  still the resume point, and `--resume` continues from it once that program lets go. A terminal
  then offers `[r]etry / [g]ive up`. Answer `g` to give up: Ctrl-C at that question does not end
  `vseed` before the results file is finished and the report printed. Giving up costs time, not
  results — the older checkpoint still resumes to the same bytes — but **at worst, when no save of
  the run ever worked, that time is the whole run**: there is then nothing on disk to resume from,
  and the error says so.
- `vseed map` waits the same ~15 s for a viewer that opened the output after the check, says so,
  and in a terminal asks `[r]etry / [g]ive up` with the finished image kept, so a retry is a rename,
  not a render. A `--keep all --rotate` search that finishes while its manifest is held prints its
  report, says that every record is written and only the manifest is not, and exits 3 (a terminal
  offers `[r]etry / [g]ive up` first).
- A full drive is named as one, with exit 3 — it used to print "this is a bug". It still ends the
  command: waiting does not free space.
- On the web page the same check refuses a search by name ("press Find seeds again once the file is
  free"), warnings are listed under the Find seeds button, and a last save that failed gets a
  **Retry saving** button. A page that is reloaded, or opened again on a run that has ended, is shown
  every warning and the save as it stands after any retry. `docs\search.md` and `docs\web.md` have
  the details.

---

## The web UI

```
vseed serve
```

A pan-and-zoom map of any seed on `http://127.0.0.1:8731`, with the seed panel, a click-anywhere
point panel, a ruler, location markers and a search panel. It is bound to loopback only, refuses any
`Host` header that is not `127.0.0.1`/`localhost`, refuses a change (a `POST` — start, Stop, Retry
saving) sent by any other web page you have open, and serves four files embedded in `vseed.exe`
itself — no CDN, no web font, no external request of any kind.

**The search panel is the CLI, not a subset of it.** The page's goals become the same query file
`vseed search` reads, planned by the same `SearchSession` and gated by the same preflight, so
`keep` is a real cap on the file there too, and the refusal rule and the confirmations are enforced
**on the server**: a `POST` from curl that skips them is refused with `kind: "refused"` or
`kind: "confirm"` and the whole report. Export downloads exactly that query file; running it in a
terminal reproduces the run.

**What it writes** (it used to say "nothing", which stopped being true when every search flag became
reachable from the page): the results file you name on the Search panel, inside one results
directory the server owns; the checkpoint and its kept-set snapshot, in the cache root; the tile
cache's disk tier, also in the cache root; the session log, `<cache root>\logs\vseed.log`, which
becomes `vseed-prev.log` at the next start; and, while it runs, `<cache root>\serve\server-<pid>.json`,
which says where it is and is deleted when it stops. Your saves and Steam Cloud folders are still
never touched. `docs\web.md` has the exact rules.

**Starting and stopping it** (decided with the user on 2026-09-24: nothing of SeedLab's may be left
running without anyone knowing). The server runs in a window of its own, titled **SeedLab web
server**, that says what it is and how to stop it; it never stops by itself and never starts by
itself. It stops by **Stop SeedLab** on the page (which warns first, and warns again if a search is
running), by `vseed serve --stop` or `SeedLab 3 - Stop web page`, by **Ctrl+C twice** within 10
seconds in its own window (the first press stops nothing and says what is running), or by closing
its window, and on Windows by signing out or shutting down (a hidden window hears those; a console
program that has loaded user32.dll is sent no console event for them). Every way stops a running
search at once with its checkpoint saved - except one still in a funnel's first stage, which has no
resume point yet and loses that stage's work (the warnings say so) - and prints the command that
continues it, which names the query file saved beside the checkpoint. After 60 minutes
with nobody using the page (`--idle-reminder`), the page and the window suggest stopping it, and
again after every further 60 minutes; they never stop it. `vseed serve --status` says whether it is
running, where, and whether a search is running in it; a second `vseed serve` opens the running
one's page. [How to use it](#the-web-page) has the user's view; `docs\web.md` has the design, the
endpoints and the token that keeps other web pages from pressing Stop (it keeps out web pages, not
other programs on the same computer).

`vseed serve --selftest` checks the server against the ground truth and prints every result. Run on
2026-09-23: **all PASS**, exit 0 — tiles against the game's own texture (262,144 of 262,144 pixels
on both worlds), the mosaic, the markers (60 of 60 bit-identical), the search panel against a direct
engine run (200 hits identical in order and score), and loopback, `Host`, CORS, CSP and
path-traversal behaviour. (This line used to say 13 checks. The code of the commit before
2026-09-24's file-access work has 12 PASS rows, the tile row counted once per world; what the
2026-09-23 build printed was not kept.) Re-run on
2026-09-24 with the cross-site guard, Retry saving and the replay check added: **15 PASS rows and 3
`MEASURED`**, exit 0. Re-run again after the web server's start-and-stop work (2026-09-24/25), with two
new rows - Stop SeedLab refused without the server's token, and Stop SeedLab with a search running
(409, then stopped with its checkpoint saved, in 6 ms): **17 PASS rows and 3 `MEASURED`**, exit 0.
See `docs\web.md`.

---

## Searching, and what it really costs

You write what you want as a query; the engine scans the seed space and writes the best matches to
disk. It is deterministic (same query + key + range = the same seed sequence, however many threads
ran), resumable (Ctrl-C writes a checkpoint, `--resume` continues) and it tells you what fraction of
the space it is going to cover **before it starts**.

```
vseed presets list                             what ships, and what each one costs
vseed presets show gentle-start > mine.json    a working query to edit
vseed search mine.json --dry-run               estimate the cost (an upper bound - see below)
vseed search mine.json --seeds 200000 --keep 20 --out hits.jsonl --yes
vseed explain -1772362158 mine.json            why that one seed passed or failed
```

New to it? [`docs\finding-a-seed.md`](docs/finding-a-seed.md) walks the whole path once, with real
output.

### Before every run, not only `--dry-run`

Every `vseed search` prints a plan first: the grid and why, the region, the tier each goal is
answered at, the coverage as a fraction of 2³², the worker count with its arithmetic, the block
size (and why, when it is not the default 256), the memory budget, the **ceiling on the output
file**, the free space, the checkpoint path, and — when `--budget` is set — how far past it a run
can go. Then one of three things happens.

- **It runs.**
- **It asks**, when the run is expensive or surprising — the whole space, over an hour, over 100 M
  seeds, over 1 GB of output or 10 % of the volume, an evicting ceiling, or a grid the engine had to
  raise. With stdin redirected it does not start: it prints what needs confirming and tells you to
  pass `--yes` or use `--dry-run`.
- **It refuses**, when the run could not answer the question honestly — or, on `--resume`, could not
  continue the checkpoint that is there (a different `--block-size`, say) — and names the fix. Nothing
  is scanned and nothing is written. Real examples from this build: a query whose goals are *all*
  must-haves (nothing to rank by, so "top 1,000" would be an arbitrary sample — fix it with a
  nice-to-have goal or `--accept-scan-order`); `--keep all` on a query that matches every seed
  (*"it WILL write about 818 GB and the volume has 599 GB free"*); a goal no seed can satisfy; a
  goal that excludes nothing (`Eikthyrnir` is always within 1,000 m, so "within 1,200 m" is a
  presence test, not a distance filter); and a metric that was retired, with the measurement that
  retired it and its replacement.

### Disk is bounded by what you asked for, not by what it finds

`keep` (default 1000) is **a real cap on the results file**: only those records are ever written, so
a whole-space run writes about 200 KB, and the report says `top 10 of 82 matches; 72 were not kept`
rather than `10 matches`. This is the headline change from earlier builds, where `--keep` capped an
in-memory table while every match was streamed to disk — `vseed search custom --all` would have
written a measured **7.36 TB**.

Ask for everything with `--keep all` and the output rotates into gzipped 1 GB segments (measured
10.0× compression) beside a `results.manifest.json`, with an optional `--reduce top:N` that reduces
each segment as it closes and deletes the raw one. `--max-bytes` sets a hard ceiling;
`--on-limit stop` (the default) stops cleanly at it and prints the resume command, `--on-limit evict`
keeps going and drops the lowest-scoring records, and has to be confirmed.

Checkpoints live in the cache root, keyed by the query hash — never in the directory you are
standing in — and a completed run deletes its own. A hard kill costs at most one block, and the
resumed file is **byte-identical** to an uninterrupted run's, verified by killing runs at three
points, bounded and streaming, and comparing SHA-256.

One worker computes a whole block, so the block size is **automatic**: 256 seeds, or smaller when a
short run would otherwise leave workers with nothing to do (`--seeds 512` on 8 workers is 32 blocks
of 16, where a fixed 256 was 2 blocks and 6 idle workers), and the plan says which and why. The web
page uses the same rule when its Block size box is empty. `--block-size` still pins it — for a
finer resume point on an expensive query — and is warned about when it leaves workers idle. A
resumed run keeps its checkpoint's size on any thread count, because a resume point is a block
number. None of this changes a result: a completed run's file is the same bytes at every block size
(`proof blocks`). A `--budget` is checked only when a worker takes a block, so a run can end up to
one block of work past it, plus the workers' start-up and the final write — the plan prints that
bound with this run's numbers, and a funnel's gate prints stage 2's.

### The grid is part of the answer, and the engine will raise it

A figure measured at 24 m is a *different measurement* from the same figure at 12 m. Eight metrics
cannot be compared across grids at all, and for island, spawn-island, peak and shore goals no coarse
grid has a margin that is both safe and selective. So under `--screen auto` (the default) the engine
**screens coarsely with a measured margin and re-measures every survivor at the definitional grid**
before writing it, raises the grid for a must-have goal no coarse grid can decide (and says that the
run "really is compiled and measured at G12, not only described as it"), and stamps every record
with `screened_at_grid_m` beside the fine-grid numbers. `--screen off` measures once;
`--screen-grid <m>` picks the coarse grid by hand.

### The measured table

Measured on 2026-09-23 on an 8-core / 16-thread Ryzen 9800X3D at the shipped default `--mode
balanced` (8 workers). Every row is a real run's own `measured rate`. Provenance, machine load, the
`--mode full` comparison, the per-seed cost breakdown and the preset hit rates are in
[`docs\measurements.md`](docs/measurements.md), which is the single source for every cost number in
this repository.

| query | grid the run used | region | top tier | seeds/s | whole space |
|---|---|---|---|---|---|
| one biome goal (`custom`) | G12 | 1.0 km disc | T2 biome | 2,046 | **24.3 days** |
| `mountain-home` | G24 screen → G12 verify | whole world | T3 height | 1,736 | **28.6 days** |
| `coastal-builder` | G24 screen → G12 verify | 1.1 km disc | T3 shore | 19.5 | **7.0 years** |
| `balanced-biomes` | G192 | whole world | T3 height | 38.7 | **3.5 years** |
| `gentle-start` | G24 screen → G12 verify | whole world | T3 height | 7.3 | **18.6 years** |
| `all-traders` | G384 | whole world | T5 locations | 5.7 | **23.9 years** |
| `archipelago` | G12 | whole world | T3 height | 5.6 | **24.3 years** |

**Days for a biome question inside a disc; years for anything that needs heights, islands, shore or
a location.** `--mode full` (16 workers) buys about 1.5×, not 2×.

`--dry-run` will project a better number than any of these: it times a tight single-thread loop and
scales it, running about 1.5× ahead of a real run on a heavy T3 query and about **35× ahead** on the
cheapest biome-only one. It says so in its own output. Treat it as an upper bound and confirm with
`--seeds 20000`.

### Why the spread

**Cost per seed is how much of the world the goal forces you to evaluate, plus a fixed cost for
every new seed.** A biome question over a small disc is a few thousand cheap `GetBiome` calls on top
of that fixed cost. A height question adds `GetBiomeHeight` to every sample, and heights need the
lake/river/stream pre-generation — about 99.5 % of the cost of building a world — which is why the
engine defers it until a tier actually reaches it. A location question needs the 2048 × 2048
biome-and-height point grid that `GetRandomPointByBiomes` draws from, about **1.9 s per seed per
core** before a single candidate zone is tried, plus the ordered placement prefix.

The largest honest lever is the **region**: one biome goal at the game's own 12 m grid costs
7.90 ms/seed inside a 1 km disc against 314.40 ms/seed over the whole world — 39.8× — and the disc
decides a disc-bounded goal *exactly*.

None of that is fixable by writing faster code; it is the generator's own cost multiplied by 4.29
billion. So the realistic shape of a session is: **scan the whole space on something cheap, then
re-check the survivors with something expensive.**

A goal no seed can satisfy is refused before a single seed is touched:

```
$ vseed search q.json --dry-run
vseed search: goal 'swamp-near' can never be satisfied by any seed.
  Swamp can never be closer than 2,000 m to the centre, so '1,500 m or nearer' is impossible
  for every seed. GetBiome test 'dist > 2000 && dist < m_maxMarshDistance',
  maxMarshDistance = 6000 (worldGenVersion 2)

  Refusing to scan 4.29 billion worlds for something the generator cannot make.
```

More in [`docs\search.md`](docs/search.md); what the numbers do *not* mean is in
[`docs\limits.md`](docs/limits.md).

---

## Seeds: 853 quadrillion texts, 4.29 billion worlds

The game's new-world box takes a **text**. When the game creates the world, its `World` constructor
(`World..ctor`, the name explained under [`vseed seed`](#vseed-seed--everything-about-one-world))
runs `string.GetStableHashCode` on that text and keeps only the resulting **int32**; generation never
sees the text again. So the int *is* the world, and the whole space of worlds is exactly 2³² =
**4,294,967,296**.

The seed field allows **10 characters** and validates them as **Alphanumeric** — measured from the
live UI component (`FejdStartup.m_newWorldSeed`: `characterLimit` 10, `Alphanumeric`), not guessed
from the code, because nothing in the game's code enforces a seed length at all. That makes

> 62¹ + 62² + … + 62¹⁰ = **853,058,371,866,181,866** typeable seed texts

collapsing onto 4,294,967,296 worlds — about **198.6 million texts per world** on average. Two
different-looking seeds giving the same world is not a bug; it is arithmetic.

Going the other way is exact, and `vseed space` re-verifies the whole thing before it prints it:

```
$ vseed space
  seed texts            853,058,371,866,181,866
  distinct worlds       4,294,967,296   = 2^32, because the game's World constructor (World..ctor) keeps only the int
  texts per world       198,618,130   (a mean, not a guarantee)

  reachable at <= 5     142,962,629   3.33 %
  reachable at <= 6     3,310,424,872   77.08 %
  reachable at <= 7     4,294,967,296   100.00 %  (complete)
  unreachable at 6      984,542,424   includes seed 0 itself

  shortest is <= 5      3.33 %
  shortest is 6         73.75 %
  shortest is 7         22.92 %

Re-verified now  (0.23 s)
  |E_1| computed from the lane tables = 62 vs published 62  ok
  |E_2| computed from the lane tables = 2,097 vs published 2,097  ok
  |E_3| computed from the lane tables = 66,014 vs published 66,014  ok
  |E_4| computed from the lane tables = 2,058,466 vs published 2,058,466  ok
  inverse round trips: 200/200 texts re-hashed to their seed  ok
  seeds published as unreachable at 6 characters: 12/12 have no 6-char text and do have a 7-char one  ok

  all checks passed
```

**Seven characters reach every one of the 4,294,967,296 worlds.** Six reach 77.08 % of them; the
remaining 984,542,424 — including seed 0 itself — have no six-character alphanumeric text. Since the
field allows ten, any world SeedLab finds can always be typed back into the game.

One consequence worth knowing: **an empty seed box is not random.** The `World` constructor
(`World..ctor`) maps it straight to 0, which is one specific world — the same one the main menu
background uses.

A token on the command line that parses as an int32 is read as the **int**, because that is what a
script emits; the seed-text reading is always reported as well, and `--text` / `--int` force either
one.

---

## The game data, and what to do after a Valheim update

Some of what SeedLab needs is not in Valheim's code — it is serialized asset data, and the code
defaults are wrong: `Minimap.m_textureSize` is 256 in the IL and **2048** in the prefab,
`m_pixelSize` is 64 and **12**, `ZoneSystem.m_locationVersion` is 1 and **32**.

So `data\1.0.15-59f53fb5\` holds what the running game was actually holding, read out of its loaded
objects by `tools\SeedLab.Dumper`, a BepInEx plugin: 232 `ZoneLocation` entries in list order, 257
vegetation entries, 32 alt biomes, 186 location prefabs, the prefab and version constants, the
seed-field limits, the constraint atlas and the native goldens. Every file carries a `DATA-STAMP`
naming the game build and the SHA-256 of its `assembly_valheim.dll`, and every dumped file the tool
reads has its SHA-256 in `manifest.json`, verified before it is parsed.

**The dumper is installed only for a named capture and retired after each.** The current data came out
of runs 4 and 5 on 2026-09-23 and run 6 on 2026-09-24: run 4 captured the whole dungeon surface after
a field-coverage audit found the earlier dumps had been carrying 10 of `DungeonGenerator`'s 24 public
instance fields and no `RoomConnection` data at all, run 5 the localization table that lets the map
say "The Elder" rather than `GDKing`, and run 6 the captions on the dungeon doors
(`Teleport.m_enterText`) that let it say "Burial Chambers" rather than `Crypt2`, with the Vegvisir
pins. All three copies were retired to `_ModSource\_retired\`; the plugin is not installed and **F4**
is free. Making this data from your own copy of the game, step by step: [`docs\game-data.md`](docs/game-data.md).

**SeedLab itself needs no plugin and no console command.** The dumper is a capture tool, not a runtime
dependency: terrain answers (biome, height, rivers, maps, seed arithmetic) are computed from the seed
and `worldGenVersion` alone and need nothing from the dump, and location answers read the shipped
tables. The one thing that brings the dumper back is a **game update**, which makes location answers
refuse rather than quietly produce coordinates from a stale table. `docs\dumper.md` has the procedure.

`vseed data` prints the stamp and the verdict:

```
Installed game
--------------------------
  verdict               MATCH - this data describes the installed game
  install               E:\SteamLibrary\steamapps\common\Valheim
  assembly_valheim      59f53fb55d99d22a33e8ed094eec8d21e9f133543bce92bc3d80dce44033adb1
  the installed game is the build this data was dumped from (Valheim 1.0.15, assembly_valheim 59f53fb5).

  terrain answers (at, map, seed, search on terrain): allowed
  location answers (locations, dungeons, traders, resources): allowed
```

### After a Valheim update

The stamp stops matching, and SeedLab **fails closed in the direction that matters**:

- **terrain answers keep working with a warning** — they come from the seed and `worldGenVersion`
  alone and use nothing in `data\`;
- **location answers are refused** — a location table from another build produces coordinates that
  look right and are not.

What to do, in order. **If you cloned this repository**, the first two steps are yours:

1. `powershell -ExecutionPolicy Bypass -File tools\check-game-version.ps1` — exit 0 means the
   installed game is the build SeedLab was verified on, exit 2 means the game moved, exit 3 means the
   game was not found (name it with `-ValheimDir <folder>` or `SEEDLAB_VALHEIM_DIR`).
2. Re-run the dumper (`docs\dumper.md`, and the full manual in `tools\SeedLab.Dumper\README.md`) and
   copy its output into a **new** folder `data\<game version>-<first 8 hex of the assembly sha256>\`.
   **Never edit the old folder** — an edit is indistinguishable from corruption and is treated as
   corruption.

The rest needs `groundtruth\`, which is not in the repository (see
[Cloned from GitHub?](#cloned-from-github-two-folders-are-not-here)), so it is **the maintainer's**:

3. `vseed selftest`. If the terrain checks still pass against the old ground truth, the generator
   itself did not change; if they fail, the port needs re-verifying against the new build before any
   number from it is trusted.
4. Capture fresh ground truth: a world generated by the new build, its `.fwl2`/`.db2`/map cache, into
   `groundtruth\`.
5. Re-run the two gates below. They are the definition of "SeedLab still matches the game".

---

## Where the evidence lives

Nothing in this README is a claim you have to take on trust. These are the things that check it:

```
dotnet run --project tests\SeedLab.Acceptance.Tests -c Release
```
32 checks, two to three and a half minutes. Regenerates both ground-truth worlds cell by cell and compares them
against what the game itself wrote — biome, height as binary16 codes, the world-edge constant, the
river pass, and the float32 `y` of every location instance in the saves. Last run: **32 passed, 0
failed**, 4,194,304/4,194,304 height codes exact per world, 0 biome mismatches, 12,314/12,314 and
12,287/12,287 location heights bit-exact.

```
dotnet run -c Release --project tools\SeedLab.LocationLab -- gate
```
The location gate. Fresh world: 12,228/12,228 instances bit-identical in zone, prefab and x/y/z, 178
of 178 prefabs exact, every per-type count equal. Played worlds: 12,314/12,314 and 12,287/12,287
reproduced from the `.db2`. Alt biomes 32/32, and all 29 of the game's own `placed N out of M` log
lines reproduced exactly. Last run: **GATE: PASS**.

```
dotnet run --project tests\SeedLab.Tests -c Release -- natives
```
The three Unity functions the port had to re-implement, against corpora captured from the running
game. 11 checks: **262,780/262,780** `Mathf.PerlinNoise` samples bit-exact, 268/268 `InitState`
seeds, **276/276** `UnityEngine.Random` traces (1,980 draws, both the result bits and the state after
each), `Mathf.FloatToHalf` resolved as ties-**away**-from-zero (.NET's `(Half)f` gets 2 of the 4
midpoints wrong), 93/93 libm results identical between Mono and .NET 10, 429/429 `GetStableHashCode`
vectors. Last run: **ALL 11 NATIVE CHECKS PASSED**.

```
dotnet run -c Release --project tools\SeedLab.GoldenCheck
```
The generator's *private* state against what the game's own generator was holding, for three seeds:
the five offsets, the two river seeds, the constructor's RNG draws, the lakes / rivers / streams in
order, the full rendered river-point grid (2.1 M points, 8.5 M float32 comparisons), and
`GetHeight` as float32 for 12,228 location instances. Last run: **VERDICT: PASS - every internal
field is bit-identical**.

```
vseed selftest          # the fast subset, any time
vseed serve --selftest  # the web server's tiles and its security properties
vseed space             # the seed-space arithmetic, recomputed before it is printed
```

The raw material:

| | |
|---|---|
| `groundtruth\decoded\*.biome.u8`, `*.height.f32` | the game's own minimap cache, decoded — bytes the game wrote |
| `groundtruth\worlds\` | the `.fwl2` / `.db2` of two worlds the game generated |
| `groundtruth\*-locations.csv`, `LogOutput-*.log` | the game's own location dump and its log lines |
| `groundtruth\natives\` | 262,780 real `Mathf.PerlinNoise` samples, 268 `Random` states + 276 traces, `Mathf.FloatToHalf` over an adversarial float set, Mono's libm, 429 hash vectors |
| `data\1.0.15-59f53fb5\goldens\` | the same natives corpora, plus the generator's private state per seed (with the full river-point grids) and 12,228 `LocationInstance`s of a fresh world |

One of the two ground-truth worlds, `testworldclaude` (seed 319486907), is a **hold-out**: it was
never used while porting the biome and height code. It matched blind on biome and to 99.9998 % on
height, and a last one-ulp residual was then diagnosed on both worlds and closed. The fully
independent check is the third, fresh seed 75539276 (GoldenCheck).

The name is the one DoomMachine gave that world in game, as a test world for Claude (see
[Credits](#credits)). It is stored inside the game's own save, which SeedLab never edits, and the
gates key on it, so it stays.

---

## Layout

```
src\SeedLab.WorldGen     the ported WorldGenerator + the Unity natives       docs\generator.md
src\SeedLab.Render       sampling grids, the map renderer, the PNG encoder   docs\generator.md
src\SeedLab.Locations    the ported ZoneSystem location placement            docs\locations.md
src\SeedLab.Search       the query language and the scan engine              docs\search.md
src\SeedLab.Web          the local web UI                                    docs\web.md
src\SeedLab.Data         the dumped game data, and the DATA-STAMP policy     docs\data.md
src\SeedLab.Seeds        GetStableHashCode and its inverse
src\SeedLab.Saves        read-only .fwl2 / .db2 / map-cache readers
src\SeedLab.Runtime      hardware probe, modes, memory guard, cache root, machine self-test
src\SeedLab.LocationOracle  the feasibility oracle the search checker refuses with
src\SeedLab.Cli          vseed itself
tools\SeedLab.Dumper     the BepInEx plugin that captured data\              docs\dumper.md
tools\SeedLab.LocationLab   the location gate
tools\SeedLab.GoldenCheck   the generator's private state against the game's
tools\check-game-version.ps1  is the installed game the build SeedLab was verified on?
tools\decompile.ps1           one game type as C#, for checking a spec's citation (needs ILSpy or ilspycmd)
SeedLab.bat, SeedLab 1..6 - *.bat   the Windows menu and one-click files                  docs\scripts.md
scripts\windows\seedlab.ps1         the PowerShell script they all run
seedlab.sh, SeedLab.command         the same for macOS and Linux (not tested there yet)   docs\scripts.md
tests\SeedLab.Acceptance.Tests   the 32-check gate against the game's own output
tests\SeedLab.Search.Tests       the query language, the tiers and prefilter parity
tests\SeedLab.Search.Safety.Tests  the output layer: bounds, rotation, kills and resumes
tests\SeedLab.Runtime.Tests      the runtime layer (122 checks)
tests\SeedLab.Tests              the library-level checks, incl. the natives gate
data\1.0.15-59f53fb5\    the captured game data (its own README is the authority)
groundtruth\             what the game itself wrote
docs\                    one short page per subsystem, plus docs\specs\
.claude\                 Claude Code skills and a research agent for Valheim modding and SeedLab
                         (.claude\README.md)
```

`docs\` in reading order:
[`scripts.md`](docs/scripts.md) (install, start, stop, remove) ·
[`finding-a-seed.md`](docs/finding-a-seed.md) (start here) ·
[`game-data.md`](docs/game-data.md) (the game data, step by step) ·
[`search.md`](docs/search.md) · [`measurements.md`](docs/measurements.md) (every cost number) ·
[`limits.md`](docs/limits.md) (what is not true of it) · [`generator.md`](docs/generator.md) ·
[`locations.md`](docs/locations.md) · [`data.md`](docs/data.md) · [`dumper.md`](docs/dumper.md) ·
[`web.md`](docs/web.md).

`docs\specs\` holds the eight design documents the source cites by name and section
(`07-features.md section 2.2` and the like). They lived in a session scratchpad that does not
survive; they were copied in on 2026-09-23 so those citations still resolve. Where a spec and the
code disagree, the code and the goldens are the evidence.

House rules, in case you come back to this and wonder: **no NuGet packages**; port rather than
improve, and cite the decompiled member for anything ported; keep the numerics discipline (the game
evaluates in `double` and truncates to `float` per statement — do not tidy an expression); never write
to a save folder or to Steam Cloud; and every number printed either is exact by construction or
carries its resolution.

---

## Credits

SeedLab was conceived, directed and tested by DoomMachine, who also captured - in their own copy
of Valheim - the worlds, game logs and game-data dumps it is verified against.

The code, tests and documentation were written by Claude, Anthropic's AI model, working in Claude
Code under DoomMachine's direction. Where docs\ mention "sessions" or "agents", they mean that work.
Commits are authored by DoomMachine; Claude is credited here rather than as a co-author.

Valheim is a trademark of Iron Gate AB. SeedLab is an independent project, not affiliated with or
endorsed by Iron Gate or Coffee Stain. Third-party code: THIRD-PARTY-NOTICES.md (FastNoise, MIT).
