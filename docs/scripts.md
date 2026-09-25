# The SeedLab scripts: install, run, stop and remove SeedLab without typing commands

SeedLab is a command-line tool, but you do not need to be at home in a terminal to use it. The files
in the SeedLab folder described here do the setup, start and stop the web page, and take everything
away again - and they ask before they change anything.

| On | Use |
| --- | --- |
| Windows | `SeedLab.bat` (a menu) or the numbered one-click files `SeedLab 1 - ...` to `SeedLab 6 - ...` |
| macOS | double-click `SeedLab.command`, or run `sh seedlab.sh` in Terminal |
| Linux | `sh seedlab.sh` in a terminal |

The one-click files are in the order you need them: install first, then open the web page, and so
on. **Every one of them first does any earlier step that has not happened yet, and says so** - open
the web page before anything is installed and it offers to install first. Every step can be run
again at any time; running it again when there is nothing to do says so and changes nothing.

Please read the next section once before you start. It is short, and two of its points are about
your computer's safety.

## Before you start

**Do not run any of these as administrator (Windows) or with sudo / as root (macOS, Linux).** The
scripts refuse every action but Help, which only prints text (Stop and Status included). As administrator, Windows may run a script as a *different* account - the one
whose password was typed into the prompt - and SeedLab would then be registered for that account
instead of yours. Nothing SeedLab does needs administrator rights, with one exception: installing
Microsoft's .NET SDK for the whole computer. On Windows, when you choose that, Windows itself asks
for permission with its own prompt. On macOS and Linux, you do it yourself if you choose it; the
script never runs sudo.

**Windows: "this file came from the internet".** If you downloaded SeedLab as a ZIP, Windows marks
every file in it as coming from the internet, and asks before it runs one. You will see one of:

- a blue **"Windows protected your PC"** window (SmartScreen). Click **More info**, check that the
  file name is the SeedLab file you double-clicked, then **Run anyway**;
- an **"Open File - Security Warning"** window saying "The publisher could not be verified". Click
  **Run**.

Only do this for files that came from SeedLab's own GitHub page. To stop Windows asking for every
file, do this once, *before* you unpack the ZIP: right-click the ZIP file, choose **Properties**,
tick **Unblock** at the bottom, click **OK**, then unpack it. (A copy made with `git clone` is not
marked and does not ask.)

**macOS: "cannot be opened because it is from an unidentified developer".** macOS's Gatekeeper says
this about a `.command` file that came from the internet. Right-click (or Control-click)
`SeedLab.command`, choose **Open**, then **Open** again in the warning; macOS remembers the answer.
If it then says you do not have permission, open Terminal in the SeedLab folder and run
`chmod +x SeedLab.command seedlab.sh` - or simply run `sh seedlab.sh` there instead.

**Where to keep the SeedLab folder.** Anywhere in your own user folders (Documents, a games folder,
a second drive). Not in `C:\Program Files`, which needs administrator rights to write to. If you
move the folder later, run **Install or update** again from its new place: it registers the new
place and offers to remove the old entry.

## Windows

| File | What it does |
| --- | --- |
| `SeedLab 1 - Install or update.bat` | checks for the .NET 10 SDK, builds SeedLab, and makes `vseed` a command you can type |
| `SeedLab 2 - Open web page.bat` | the map and the search in your browser; starts SeedLab's web server if it is not running |
| `SeedLab 3 - Stop web page.bat` | stops the web server |
| `SeedLab 4 - Command window.bat` | opens a PowerShell window in the SeedLab folder in which `vseed` works |
| `SeedLab 5 - Uninstall (keeps the build).bat` | removes everything SeedLab put outside its own folder |
| `SeedLab 6 - Remove the build.bat` | runs the uninstall if it has not been done, then deletes the build inside the SeedLab folder |
| `SeedLab.bat` | all of the above as a numbered menu, plus **S**tatus and **H**elp |

`SeedLab.bat` also takes the action by name, from a Command Prompt:
`SeedLab.bat install | web | stop | status | shell | uninstall | remove-build | help`.
Options after `web` go to `vseed serve` (for example `SeedLab.bat web --port 0`), options after
`stop` go to `vseed serve --stop`. A double-clicked window waits for Enter at the end so it does not
vanish before you can read it; `--no-pause` turns that off for scripts that call these files.

All the `.bat` files hand their work to one PowerShell script, `scripts\windows\seedlab.ps1`. It
works with the Windows PowerShell 5.1 that comes with Windows 10 and 11; nothing extra is needed.

### 1. Install or update

Run it once to set SeedLab up, and again after every update (a `git pull`, or a new ZIP unpacked
over the folder). It does four steps:

1. **The .NET 10 SDK.** SeedLab is built from its source code on your own computer, and the .NET SDK
   is Microsoft's free toolkit that does the building (it also brings the runtimes `vseed` needs).
   If a 10.x SDK is not found, the script explains this and offers three choices:
   - **W - install it with winget**, Windows' own package manager
     (`winget install --id Microsoft.DotNet.SDK.10 --exact --source winget`, the package Microsoft
     names on its "Install .NET on Windows" page). **Before you choose this, know that it downloads
     about 200 MB from Microsoft, and that Windows will show an administrator (User Account Control)
     prompt**, because the SDK is installed for every account on the computer, in
     `C:\Program Files\dotnet`. Answer Yes to continue. This is the only step of SeedLab that needs
     administrator rights. winget may first ask you to accept its source agreements; that question
     is winget's, not SeedLab's. If winget is not on your computer, this choice is shown as not
     available (winget comes with "App Installer" from the Microsoft Store).
   - **B - the browser**: opens <https://dotnet.microsoft.com/download/dotnet/10.0>; install the SDK
     for Windows x64 (Arm64 on an ARM PC) yourself, then run Install again.
   - **C - cancel**: nothing is changed.
2. **The build**: `dotnet build src\SeedLab.Cli\SeedLab.Cli.csproj -c Release`. The first build takes
   a minute or two. **Telemetry:** the .NET SDK sends usage data to Microsoft by default. SeedLab's
   scripts turn that off for the builds they run (`DOTNET_CLI_TELEMETRY_OPTOUT=1`); your other uses
   of `dotnet` are not changed. If a `vseed` from this build is running (a web server or a search),
   Windows will not let the build replace its files, so the script lists it and offers to stop it.
3. **The `vseed` command**: the build folder, `<SeedLab folder>\src\SeedLab.Cli\bin\Release\net10.0`,
   is added to **your own** Path (the user Path, in `HKCU\Environment`). Nothing outside your account
   is changed. The other entries of your Path are kept exactly as they were, including any
   `%VARIABLES%` in them (the script does not use `setx`, which cuts a Path at 1,024 characters and
   mixes the system Path into it). Windows is then told the Path changed, so **new** windows see
   it; windows that were already open do not. If your Path still lists a SeedLab folder that no
   longer exists (a folder you moved or deleted), the script offers to remove that entry.
4. **A check**: runs `vseed --version`, and tells you whether a new window will run this build when
   you type `vseed` (or a different `vseed` that comes earlier on the Path).

### 2. Open web page

If SeedLab's web server is already running, this just opens the page in your browser (it is
`vseed serve` itself that answers: "SeedLab is already running at ... - opened it in your
browser"). Otherwise it starts the server **in a window of its own, titled "SeedLab web server"**,
in which `vseed.exe` is the only program, and your browser opens the page by itself a few seconds
later. The window you double-clicked closes by itself once that has happened.

**That window is the server** - it says so at the top. Minimise it, but do not close it while you
use the page. To stop it: **Stop SeedLab** on the page (top right, or on the Help panel), **SeedLab 3 -
Stop web page**, or press **Ctrl+C twice** in that window (the first press stops nothing and says what
is running; the second, within 10 seconds, stops it). Closing the window stops it too, and so do
signing out of Windows and shutting it down. Whichever way, a search that is running is stopped with
its checkpoint saved - except one still in the first stage of a two-stage (funnel) search, which has
no resume point yet and loses that stage's work (every warning says so) - and the page asks you first
if one is running. The window closes by itself when the server has stopped - unless a search it
stopped left a checkpoint: then it waits for Enter (at most 10 minutes), so you can read or copy the
command that continues the search. If the server could not start, it also stays open until you press
Enter, so you can read why.

If nobody has used the page for 60 minutes, the page and the window suggest stopping SeedLab. They
never stop it by themselves; "Keep running" (or ignoring it) asks again after another 60 minutes.

The page's results files are saved in the `seedlab-results` folder inside the SeedLab folder.

### 3. Stop web page

Stops SeedLab's web server, through `vseed serve --stop`.

- The script first asks `vseed serve --status` which servers are running, in each cache folder one
  can be registered in: `%LOCALAPPDATA%\SeedLab`, and the folder `SEEDLAB_CACHE_DIR` names if it is
  set. With none running it says "SeedLab's web server is not running - nothing to stop."
- With no search running, the server stops at once, without a question: you asked for it by
  double-clicking. If a search is running, `vseed` says which one and how far it got and, for that
  search, what stopping does: that the finished part is saved, with the command that continues it -
  or, for a funnel search still in its first stage, that stopping loses its work so far - and asks
  "Stop anyway? [y/N]". Answering no (or just Enter) leaves everything running. A stopped search is
  stopped at once with its checkpoint saved, and `vseed` prints where the checkpoint is and the
  command that continues it; that command names the search's query file, which SeedLab keeps beside
  the checkpoint, so it works as printed.
- From a Command Prompt, options after `stop` go straight to `vseed serve --stop`:
  `SeedLab.bat stop --yes` stops it even though a search is running (without asking);
  `SeedLab.bat stop --force` ends the server's process if it does not answer within 10 seconds (a
  running search then loses what its last checkpoint did not hold); `SeedLab.bat stop --cache-dir
  <folder>` stops a server that was started with that `--cache-dir`.
- A file in the cache folder that says a web server is running when none is - left behind by one that
  was ended without stopping (Task Manager, a power cut) - is named, with its path, as safe to
  delete; it is ignored, and the next web server removes it. (It used to make SeedLab believe a
  server was running, which nothing could then stop.) Status names it too.
- A `vseed` web server that no cache folder here knows about - started by hand with another
  `--cache-dir`, or by a SeedLab from before 2026-09-24 - is listed with how to stop it (Ctrl+C
  twice in its window, closing that window, or `SeedLab.bat stop --cache-dir <that folder>`). The
  script does not stop it: nothing proves which cache folder it uses.
- With a SeedLab build from before `vseed serve --stop` existed (an update you have not built yet),
  the script ends the server's process directly instead. That is abrupt: a search running in the
  page stops too, and loses what its last checkpoint did not hold. The script shows what it will stop
  and asks first. A web server started from a *different* SeedLab folder is listed, and only stopped
  if you say so.

### 4. Command window

Opens a PowerShell window in the SeedLab folder in which `vseed` works (even in the moment before a
new Path is seen), prints two lines of examples and `vseed --help`. Type `exit` to close it. Run
SeedLab's commands from the SeedLab folder or anywhere else: `vseed` finds its `data\` folder next to
its build either way.

### Status

Menu choice **S**, or `SeedLab.bat status`: whether the SDK is found, whether SeedLab is built and up
to date with its source, whether `vseed` is registered, whether the web server is running (where,
since when, and whether a search is running in it - what `vseed serve --status` reports), where the
two session logs are, and how much the cache folder holds.

### 5. Uninstall (keeps the build)

Removes everything SeedLab put **outside** its own folder. It first lists what it will do, and asks
once. Then, in this order:

1. stops SeedLab's web server through `vseed serve --stop`. Your "go ahead" covers that, **except
   when a search is running in it**: then it names the search and asks separately, because stopping
   saves the search's checkpoint in the very cache folder the next step moves to the Recycle Bin -
   the search could not be continued afterwards. Answer no and the server keeps running, and the
   cache folder is left in place. A web server of this folder's build that no cache folder here
   knows about has its process ended (your "go ahead" covers that too); one from another SeedLab
   folder is asked about on its own. It then lists any other running `vseed` (a search in a
   terminal) and asks before stopping it. While `vseed` is still running, the cache folder is left
   in place and the uninstall says so. A `vseed` that was started with `--cache-dir` naming another
   folder does not use the cache folders uninstall removes, so it is neither stopped nor waited for;
2. moves SeedLab's **cache folder**, `%LOCALAPPDATA%\SeedLab`, to the **Recycle Bin**. It holds
   rendered maps, map tiles, search checkpoints, **both session logs** (`logs\vseed.log` and
   `logs\vseed-prev.log`), the self-test stamp and - while a web server runs - its small
   `serve\server-<pid>.json` file: all rebuilt when needed, except that a search you have not
   finished can no longer be resumed, and the last two sessions' logs are gone.
   **If Windows cannot put it
   in the Recycle Bin** (a folder too big for it, or a drive that has none), Windows itself warns
   that it would be deleted permanently and lets you say no;
3. if `SEEDLAB_CACHE_DIR` names a cache folder you chose yourself, asks about it **separately**. If
   that folder also holds things SeedLab did not make, only SeedLab's own folders in it (among them
   `logs` and `serve`) are offered;
4. takes `vseed` off your Path (exactly the entry Install added, and any entry for a SeedLab folder
   that no longer exists). Windows that are already open still know `vseed` until they are closed;
5. lists SeedLab settings for your account (environment variables named `SEEDLAB_...`) and offers to
   remove them. Settings made for all accounts are only mentioned: changing them needs an
   administrator;
6. looks for **SeedLab's dumper plugin** in Valheim (`BepInEx\plugins\...SeedLabDumper...` and its
   `BepInEx\config\DoomMachine.SeedLabDumper.cfg`) and, if it finds it, offers to move it to the
   Recycle Bin. Valheim must not be running. It looks in `SEEDLAB_VALHEIM_DIR`, in a folder above the
   SeedLab folder, and in your Steam libraries. **It does not search mod-manager profiles** (r2modman,
   Thunderstore Mod Manager and the like keep their own BepInEx folders): if you installed the
   dumper through one, remove it there. How the dumper is used: [`dumper.md`](dumper.md);
7. offers to move the **dumper's output folder** (`%USERPROFILE%\AppData\valheim-dumper`, the raw game
   data it wrote) to the Recycle Bin. SeedLab's `data\` folder holds a copy of what you imported
   from it; if you have deleted `data\`, or not imported the last run yet, it may be the only copy.

**What uninstall deliberately keeps:**

- the SeedLab folder and its build (6 removes the build);
- `data\` (the game data), and your results: `seedlab-results` folders and any file you named with
  `--out`;
- the .NET SDK, because other programs may use it. To remove it: Windows **Settings > Apps**, find
  "Microsoft .NET SDK 10..." and uninstall it (or `winget uninstall Microsoft.DotNet.SDK.10` if you
  installed it with winget).

Run it a second time and it finds nothing to do, and says so.

### 6. Remove the build

The last step. If the uninstall has not been done yet (the command is still registered, the cache
folder is still there, or the web server is running), it offers to run the uninstall first, and
does nothing if you say no - removing the build first would leave a `vseed` command that points at
nothing. Then it lists any program running from the SeedLab folder and asks before stopping it, and
lists the build folders with their sizes: the `bin\` and `obj\` folders of every project under
`src\`, `tests\` and `tools\`, and `tools\SeedLab.Dumper\build\`. They are **deleted, not moved to
the Recycle Bin**: they are rebuilt exactly by Install. (The dumper's build can only be rebuilt with
Valheim installed.) Nothing else in the folder is touched.

To remove SeedLab completely, delete the SeedLab folder itself afterwards: close the window, then
in File Explorer right-click the folder and choose **Delete**. A script cannot delete the folder it
is running from. Save its `seedlab-results` folder first if you want your results.

### If a step fails: doing it by hand

| Step | By hand |
| --- | --- |
| SDK | install the .NET 10 SDK from <https://dotnet.microsoft.com/download/dotnet/10.0> |
| Build | in the SeedLab folder: `dotnet build src\SeedLab.Cli\SeedLab.Cli.csproj -c Release` |
| Command | Start menu, "Edit environment variables for your account", **Path**, **New**, paste the build folder; or type the full path of `vseed.exe` |
| Web page | `vseed serve` (`vseed serve --stop`, or Ctrl+C twice in its window, stops it; `vseed serve --status` says whether it runs) |
| Uninstall | `vseed serve --stop`, then delete `%LOCALAPPDATA%\SeedLab`; take the build folder off your Path the same way |
| Remove the build | delete the `bin` and `obj` folders under `src`, `tests` and `tools` |

## macOS and Linux

**Say it plainly: `seedlab.sh` has not been tested on macOS - no Mac was available - and has not yet
been tested on a real Linux system (that is planned, in WSL).** It was written to run under any
POSIX shell and checked under `dash`, the strict shell Debian and Ubuntu use as `/bin/sh`; its own
logic was exercised there against a Windows build of SeedLab. Treat it as a careful first version,
and use the table at the end of this section if a step fails.

Run it from a terminal in the SeedLab folder: `sh seedlab.sh` for the menu, or
`sh seedlab.sh install | web | stop | status | shell | uninstall | remove-build | help`. On a Mac,
double-clicking `SeedLab.command` in Finder opens the same menu in Terminal. It refuses to run as
root, and never runs sudo.

- **Install**: the same four steps as on Windows.
  - If no .NET 10 SDK is found, it offers **P**: install it for your account only with Microsoft's
    installer script. That downloads <https://dot.net/v1/dotnet-install.sh> and runs it with bash
    (`--channel 10.0 --install-dir ~/.dotnet`); the SDK is about 200 MB. It needs no password and no
    sudo, is not added to your PATH (SeedLab's scripts find it in `~/.dotnet`), and deleting
    `~/.dotnet` removes it again. On Linux, if `vseed` then fails to start with a message about
    ICU or libssl, your distribution needs its `libicu` and `libssl` packages (that needs sudo).
    The other choices: on **Linux**, your distribution's package, which you install yourself with
    sudo - on Ubuntu 24.04 and later `sudo apt-get update && sudo apt-get install -y dotnet-sdk-10.0`
    (Ubuntu 22.04 first needs `sudo add-apt-repository ppa:dotnet/backports`; other distributions:
    <https://learn.microsoft.com/dotnet/core/install/linux>). On **macOS**, Microsoft's installer
    (`.pkg`) from <https://dotnet.microsoft.com/download/dotnet/10.0> - Arm64 for Apple processors,
    x64 for Intel - which asks for your administrator password. Homebrew can also install .NET, but
    Microsoft's own instructions do not cover it.
  - The build turns telemetry off the same way.
  - **The command**: `~/.local/bin/vseed`, a small script that runs this folder's build. (It is a
    script rather than a symbolic link so that it can tell `vseed` where a per-user SDK is - .NET
    does not look in `~/.dotnet` by itself - and so that it says clearly what to do if the SeedLab
    folder has moved.) If `~/.local/bin` is not on your PATH, the script offers to add three marked
    lines, between `# >>> SeedLab >>>` and `# <<< SeedLab <<<`, to the file your terminal reads at
    start: `~/.zshrc` on macOS (or `~/.bash_profile` if your shell is bash), `~/.bashrc` for bash on
    Linux, otherwise `~/.profile`. Terminals that are already open do not see it.
- **Web**: starts the server in the terminal you ran it from (on a Mac, double-clicking
  `SeedLab.command` opens a new Terminal window for it); if one is already running, `vseed serve`
  opens its page instead. Leave that terminal open; press Ctrl+C twice in it to stop the server, or
  use **Stop SeedLab** on the page, or `sh seedlab.sh stop` from another terminal (which uses
  `vseed serve --status` and `--stop` exactly as on Windows, and passes its options on:
  `sh seedlab.sh stop --yes`). Closing the terminal or a SIGTERM also stops it, and a running search
  is saved first - that part is not tested on macOS or Linux yet. `vseed` opens the browser itself
  (`open` on macOS, `xdg-open` on Linux).
- **Shell**: prints the examples and `vseed --help`, then starts a shell in the SeedLab folder in
  which `vseed` works; `exit` returns.
- **Uninstall**: as on Windows, including the separate question for a running search. The cache
  folder, with both session logs in its `logs` folder, is `~/Library/Caches/SeedLab` on macOS and
  `${XDG_CACHE_HOME:-~/.cache}/seedlab` on Linux. A web server no cache folder here knows about is
  sent SIGTERM, which a `vseed` from 2026-09-24 or later answers with the same graceful stop. Things are moved to the Trash when the system has
  one the script can use (macOS; Linux with `gio` or `trash-put`), and otherwise **deleted for
  good** - the script says which before it asks. It removes `~/.local/bin/vseed` (only if SeedLab
  made it) and exactly its own marked lines. `SEEDLAB_...` settings in your startup files are listed
  but not edited, because you wrote them. It looks for the dumper in Linux Steam libraries; it does
  not search mod-manager profiles or Proton prefixes.
- **Remove the build**: as on Windows. To remove SeedLab completely afterwards, delete the folder
  (`rm -rf` followed by its path; the script prints the exact command).

**Apple Silicon and other ARM processors.** SeedLab has only ever been proven on x64 processors. On
anything else it can only prove its arithmetic with the ground-truth recordings (`groundtruth/natives`),
which are not published, so `vseed` refuses to answer unless you add `--accept-unverified-platform` -
and then its answers may differ from the game's. The scripts say this, and `sh seedlab.sh web
--accept-unverified-platform` passes it on.

**WSL (Linux inside Windows).** Keep one SeedLab folder per system. A build made by Windows and one
made by Linux in the same folder overwrite each other's files in `obj/`, and each will want to
rebuild. Cloning SeedLab inside the Linux file system (not under `/mnt/c` or `/mnt/e`) is also much
faster to build.

| Step | By hand |
| --- | --- |
| SDK | see the choices above |
| Build | in the SeedLab folder: `dotnet build src/SeedLab.Cli/SeedLab.Cli.csproj -c Release` |
| Command | run `src/SeedLab.Cli/bin/Release/net10.0/vseed` by its path, or link it into a folder on your PATH |
| Web page | `vseed serve` (`vseed serve --stop`, or Ctrl+C twice in its terminal, stops it) |
| Uninstall | `vseed serve --stop`, then delete the cache folder above, `~/.local/bin/vseed`, and the marked lines |
| Remove the build | delete the `bin` and `obj` folders under `src`, `tests` and `tools` |

## For maintainers

**What the scripts rely on in `vseed`:** `vseed --version`; `vseed serve` in the foreground (it opens
the browser, and `--no-browser` / `--port` are passed through); `vseed serve --help`, read to find
out whether this build has `--status` and `--stop` (read again after a build). Where they are listed
there (every build since 2026-09-24):

- **`vseed serve --status --cache-dir <folder>`** is asked of each cache folder a server can be
  registered in - `%LOCALAPPDATA%\SeedLab` (or its test stand-in), `SEEDLAB_CACHE_DIR`'s folder, and
  vseed's own default - because a server registers in the cache folder it was started with. Exit 0
  and one line per server (`running at http://127.0.0.1:<port> since HH:mm (pid N); ...`), 1 when
  none runs. The Windows script reads `--json`; `seedlab.sh` reads the text. It starts nothing and
  creates nothing - not even the cache folder - so the scripts ask it at any time, including after
  an uninstall has removed that folder (tested: after uninstall, `status`, `stop` and a bare
  `vseed serve --status` / `--stop` left it absent).
- **`vseed serve --stop --cache-dir <folder>`** (exit 0 stopped or nothing to stop, 1 not stopped).
  "Stop web page" runs it without `--yes`, so `vseed` asks its own "Stop anyway?" when a search is
  running; uninstall asks its own question first (the checkpoint goes with the cache folder) and
  then passes `--yes`; install passes `--yes` after it has shown the search and asked. With nobody
  to answer (a redirected input), `vseed` refuses rather than stop a search, and says so.
- **A second `vseed serve`** opens the running server's page and exits 0: "Open web page" runs it in
  its own window when a server of the same cache folder (and port, if one was given) is running,
  and lets `vseed` say where that server is.
- A `vseed` that lists `--stop` prints its own banner and titles its own window, so the scripts no
  longer print theirs, and the Windows **web** action starts `vseed.exe` as the only program in its
  new window: Ctrl+C and the close button reach `vseed` alone (no cmd.exe "Terminate batch job
  (Y/N)?", no PowerShell in between).

Where they are not listed (a build from before 2026-09-24), the scripts fall back on the list of
running programs and on asking the page's own port, and only ask `vseed` about a server when a
`vseed serve` program is actually running: an older `vseed` created its cache folder (and ran its
self-test) for any `vseed serve` question - the folder uninstall has just removed. The same process
list finds a web server that no cache folder here knows about.

**Only programs from this folder's own build are ever stopped** without a separate question; one
from another SeedLab folder is listed and asked about on its own. A `vseed` started with
`--cache-dir` naming another folder is left out of uninstall altogether.

**Test hooks.** Both scripts read `SEEDLAB_SCRIPT_TEST_*` environment variables, listed at the top of
each script, that redirect everything they would change (the user environment key or home folder,
the cache folder, the Valheim folder, the dumper's output) to scratch folders, supply answers to
their questions, and simulate running as administrator or root and a missing SDK. When any is set
the script runs in test mode and refuses to start unless the redirections that protect the real
account are set too. In test mode the scripts also set `SEEDLAB_CACHE_DIR` to the test cache folder
when a test has not set it, so every `vseed` they start writes its cache, its session logs and its
server file into scratch; `SEEDLAB_SCRIPT_TEST_NO_WINDOWS=1` also gives `vseed serve` `--no-browser`.
What was tested, and what was not, is listed in `CHANGES-FOR-THE-USER.md` (2026-09-24, "Starting and
stopping the web page").

**Line endings.** `.bat` and `.cmd` files are CRLF (cmd.exe misreads parts of a batch file with
LF-only endings), `.sh` and `.command` files are LF; `.gitattributes` enforces both.
`scripts\windows\seedlab.ps1` must stay ASCII: Windows PowerShell 5.1 reads a script without a byte
order mark in the ANSI code page. `seedlab.sh` and `SeedLab.command` need the executable bit in git.
