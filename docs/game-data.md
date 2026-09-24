# Getting the game data: the dumper, step by step

This page is for anyone who cloned SeedLab and wants **location answers**: where the bosses, traders
and dungeons of a seed are. It assumes no modding experience. Every command is written out, and every
step says what you should see.

You do **not** need any of this for **terrain answers**. Biomes, heights, rivers, map images, seed
text arithmetic and terrain searches work straight after building SeedLab. They are computed from the
seed alone.

**Tested on Windows only.** The dumper has never been run on Linux or macOS. See
[section 16](#16-linux-and-macos).

The full technical manual is [`tools\SeedLab.Dumper\README.md`](../tools/SeedLab.Dumper/README.md).
If this page and that manual ever disagree, the manual is the authority.

## Contents

1. [What the dumper is, and why you need it](#1-what-the-dumper-is-and-why-you-need-it)
2. [What you need before you start](#2-what-you-need-before-you-start)
3. [Install BepInEx](#3-install-bepinex)
4. [Turn on the game's console](#4-turn-on-the-games-console)
5. [Open a command window and name your folders](#5-open-a-command-window-and-name-your-folders)
6. [Build the dumper](#6-build-the-dumper)
7. [Run the safety check (the preflight)](#7-run-the-safety-check-the-preflight)
8. [Install the dumper](#8-install-the-dumper)
9. [Arm it](#9-arm-it)
10. [Start Valheim and make sure it is armed](#10-start-valheim-and-make-sure-it-is-armed)
11. [At the main menu: two console commands](#11-at-the-main-menu-two-console-commands)
12. [In a fresh, throwaway, solo world: press F4](#12-in-a-fresh-throwaway-solo-world-press-f4)
13. [Quit, and find the output](#13-quit-and-find-the-output)
14. [Copy it into SeedLab and check it](#14-copy-it-into-seedlab-and-check-it)
15. [Remove the dumper](#15-remove-the-dumper)
16. [Linux and macOS](#16-linux-and-macos)
17. [After a Valheim update](#17-after-a-valheim-update)
18. [If something goes wrong](#18-if-something-goes-wrong)

---

## 1. What the dumper is, and why you need it

Some of what SeedLab needs is not in Valheim's program code. It is data inside the game's assets: the
list of every location type with its placement rules, the vegetation list, the alternative biomes, a
few map constants, and the game's English names for places. That data belongs to Iron Gate, the
game's developer, so it is **not included** in this repository.

The dumper (`tools\SeedLab.Dumper`) gets it from **your own copy** of Valheim. It is a small
BepInEx plugin, meaning a mod that the BepInEx mod loader loads into the game. While the game runs,
the dumper reads those tables from memory and writes them to JSON files in
`%USERPROFILE%\AppData\valheim-dumper\`. You then copy that folder into SeedLab's `data\` folder.

Without that folder:

- **terrain answers work**: biome, height, rivers, maps, seed text arithmetic and terrain searches;
- **location answers refuse** with exit code 3 and a message saying the data is missing. That is
  anything that names a location, a dungeon or a trader, such as `vseed locations`.

**The dumper is a capture tool, not a mod to play with.** You install it, run it once, check the
result, and remove it. [Section 15](#15-remove-the-dumper) says why that matters.

What it will and will not do:

- It writes only to `%USERPROFILE%\AppData\valheim-dumper\`. Every write goes through a guard that
  refuses the game folder, your world and character save folders, and any Steam Cloud folder.
- It does nothing until you arm it with a file and then press a key or type a command.
- It refuses to run in multiplayer: as a client, on a dedicated server, or with anyone connected to
  you.
- **The asset dump makes the game stutter** for a while. In the author's run on 2026-09-24 it took
  under a minute: it started just after 16:15:46 and had finished before 16:16:35, by the game's own
  log timestamps.
- **The output is large**, about 150 MB for a complete run. The asset dump alone wrote 119 MB in that
  run. By the sizes of the author's files, the two world-generator seeds add about 13 MB each and the
  native-function records about 5 MB.

## 2. What you need before you start

1. **Valheim for Windows, from Steam.**
2. **The right game version.** SeedLab reproduces **Valheim 1.0.15**, the build whose
   `assembly_valheim.dll` has a SHA-256 beginning `59f53fb5`. From the SeedLab folder, run:

   ```
   powershell -ExecutionPolicy Bypass -File tools\check-game-version.ps1
   ```

   If it cannot find the game, add `-ValheimDir "<your game folder>"` to the end of that line.

   - `OK - the installed game is the build SeedLab was verified against.` (exit code 0): carry on.
   - `GAME CHANGED` (exit code 2): **read [section 17](#17-after-a-valheim-update) first.** The
     dumper can still run, but read what a dump can and cannot fix before you spend time on it.
   - `Valheim not found` (exit code 3): pass `-ValheimDir` as above.
3. **SeedLab itself, built**, with the .NET 10 SDK installed. See [Build it](../README.md#build-it)
   in the main README. The same SDK builds the dumper. Nothing is downloaded during the build.
4. **BepInEx for Valheim**, installed ([section 3](#3-install-bepinex)).
5. **About 300 MB of free disk space**: about 150 MB for the dumper's output and the same again for
   the copy in `data\`.
6. **Preferably, the game set to English.** The dumper records the game's place names in the language
   the game is set to, and SeedLab has only been used with English names. **Unverified:** how SeedLab
   behaves with a dump in another language.

## 3. Install BepInEx

BepInEx is the mod loader that runs the dumper inside the game. The Valheim edition is called
**BepInExPack_Valheim** (by denikson) and is published on Thunderstore:
https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/

There are two ways to install it. **Only the first has been tested with the dumper.**

### Way A: installed by hand into the game folder (tested)

1. Find your game folder. In Steam: right-click **Valheim** > **Manage** > **Browse local files**. The
   folder that opens holds `valheim.exe`. The usual location is
   `C:\Program Files (x86)\Steam\steamapps\common\Valheim`, but a second Steam library on another
   drive is common, such as `D:\SteamLibrary\steamapps\common\Valheim`.
2. Download BepInExPack_Valheim from the page above and open the zip. It contains a folder called
   `BepInExPack_Valheim`. Copy **the contents of that folder** into the game folder, so that a folder
   called `BepInEx` sits beside `valheim.exe`. (**Unverified:** the layout of the zip, which was not
   opened while this page was written. What was checked is the result: a working hand install has
   `BepInEx`, `doorstop_libs`, `doorstop_config.ini` and `winhttp.dll` beside `valheim.exe`. If the
   package's own instructions differ from this step, follow them.)
3. Start Valheim once, go to the main menu, and quit.
4. Check that the file `BepInEx\LogOutput.log` now exists in the game folder. Its first line names
   BepInEx, for example `[Message:   BepInEx] BepInEx 5.4.23.3 - valheim`.

**Your BepInEx folder is the game folder.** Plugins go in `<game folder>\BepInEx\plugins\`, the log is
`<game folder>\BepInEx\LogOutput.log`, and you start the game from Steam as usual.

If Windows asks for administrator permission when you copy into the game folder, it is because the
game sits in a protected folder such as `C:\Program Files (x86)`. The copy needs that permission. The
dumper itself never needs administrator rights, and you should not run Valheim as administrator.

### Way B: through a mod manager (r2modman or Thunderstore Mod Manager)

A mod manager keeps BepInEx and your mods in a **profile folder** outside the game folder, and starts
the game with them. Install BepInExPack_Valheim into a profile from inside the manager.

**Make a separate profile just for the dump**, holding only BepInExPack_Valheim. That keeps the
dumper out of the profile you play with, and it keeps other mods out of the data (see
[section 12](#12-in-a-fresh-throwaway-solo-world-press-f4)).

**Your BepInEx folder is the profile folder.** Plugins go in `<profile folder>\BepInEx\plugins\` and
the log is `<profile folder>\BepInEx\LogOutput.log`. You must start the game with the manager's
**Start modded** button. Starting it from Steam runs the game without BepInEx, and the dumper never
loads.

Where the profile folder usually is:

| Manager | Profile folder |
|---|---|
| Thunderstore Mod Manager | `%APPDATA%\Thunderstore Mod Manager\DataFolder\Valheim\profiles\<profile name>` |
| r2modman | `%APPDATA%\r2modmanPlus-local\Valheim\profiles\<profile name>` |

**Unverified:** these two paths for Valheim. The Thunderstore Mod Manager layout above
(`DataFolder\<game>\profiles\<profile>\BepInEx\plugins`, `config`, `core`, `LogOutput.log`) was
checked on a Windows machine, but for a different game. The r2modman path and the **Start modded**
label were not checked at all. If a path does not exist, look in the manager's settings for an option
that opens the profile folder. **Unverified:** that option's name, and whether both managers have it.

**Two things are harder with a mod manager**, because the dumper's build and its safety check both
expect BepInEx inside the game folder. Both have a workaround below that was tested:
[section 6](#6-build-the-dumper) (one extra setting) and [section 7](#7-run-the-safety-check-the-preflight)
(a temporary stand-in folder). Nothing else changes.

## 4. Turn on the game's console

Two of the dumper's steps are typed into Valheim's console (F5), and **the console is off by
default**. Turn it on in one of two ways:

- **In the game:** Settings > **Gameplay** > **Enable Console**. (These labels are the game's own
  English text, read from its localization table.)
- **Or in Steam:** right-click **Valheim** > **Properties** > **General** > **Launch Options**, and type
  `-console`. **Unverified:** whether Steam's launch options still apply when a mod manager starts the
  game. With a manager, use the in-game setting.

## 5. Open a command window and name your folders

Every command on this page is typed into a **Command Prompt** window whose current folder is the
SeedLab folder (the one that holds `README.md`, `src` and `tools`).

1. Open the SeedLab folder in File Explorer.
2. Click the address bar, type `cmd`, and press Enter. A black window opens in that folder.

Now tell the window where your game and your BepInEx are. Type these lines, with your own folder in
the first one:

```
set "VALHEIM=C:\Program Files (x86)\Steam\steamapps\common\Valheim"
set "BEPINEX=%VALHEIM%"
```

**With a mod manager**, the second line points at the profile folder instead, for example:

```
set "BEPINEX=%APPDATA%\r2modmanPlus-local\Valheim\profiles\SeedLab dump"
```

Check both. Each command must print one file name, not `File Not Found`:

```
dir /b "%VALHEIM%\valheim.exe"
dir /b "%BEPINEX%\BepInEx\core\BepInEx.dll"
```

These names last only as long as this window is open. If you close it, open a new one the same way
and type the two `set` lines again before you continue.

**If a command later prints `Access is denied`**, the folder it writes to is protected. This can
happen in sections 8, 9 and 15 when the game is under `C:\Program Files (x86)`. A command window
does not ask for permission the way File Explorer does; it just fails. Open a second window **as
administrator**: open the Start menu, type `cmd`, right-click **Command Prompt**, and choose **Run as
administrator**. That window starts in `C:\Windows\system32`, so first type
`cd /d "<your SeedLab folder>"`, then the `set` lines again (including `set "DUMPER=..."` from section
8 if you are past it). Use that window only for the command that failed, and do everything else in
the normal one. Nothing else on this page needs administrator rights, and you should never run
Valheim itself as administrator.

## 6. Build the dumper

Type:

```
dotnet build tools\SeedLab.Dumper\SeedLab.Dumper.csproj -c Release -p:DeployToGame=false -p:ValheimDir="%VALHEIM%" -p:BepInExCore="%BEPINEX%\BepInEx\core" -v quiet --nologo
```

You should see `Build succeeded.` and `0 Error(s)`. The result is two files in
`tools\SeedLab.Dumper\build\`: **`SeedLab.Dumper.dll`** and **`SeedLab.Contracts.dll`**. (A
`.pdb` and a `.deps.json` appear beside them. You do not need those.)

How the build finds the game: the plugin is compiled against the game's own DLLs, which it reads from
`ValheimDir\valheim_Data\Managed`, and against BepInEx's DLLs, which it reads from `BepInExCore`. It
copies none of them. Without `-p:ValheimDir`, the project looks for the game in the folder where it
is installed on the author's machine (`E:\SteamLibrary\steamapps\common\Valheim`). So always pass it.

`-p:BepInExCore` is the one setting that is **not** in the plugin README's build command. With BepInEx
in the game folder it changes nothing. With a mod manager it points the build at the profile's
BepInEx. Tested: without it, a build against a game folder that has no BepInEx fails with
`error CS0246: The type or namespace name '...' could not be found`. With it, the same build succeeds.

`-p:DeployToGame=false` means the build does **not** copy anything into the game. You install it
yourself in [section 8](#8-install-the-dumper), so you always know when it is there.

## 7. Run the safety check (the preflight)

The preflight reads the built plugin and the game's DLLs and checks, without starting the game, that:

- every game field and method the dumper reads still exists in your copy of the game. If one is
  missing, the dump would be silently wrong;
- the plugin contains no code that deletes or moves files, every file it writes goes through its
  guard, and the guard refuses the game folder and every save folder;
- the protections behind the incident described in [section 15](#15-remove-the-dumper) are in place.

It checks the code, not a run: it cannot prove that a dump will be correct.

**With BepInEx in the game folder (Way A):**

```
powershell -ExecutionPolicy Bypass -File tools\SeedLab.Dumper\preflight.ps1 -ValheimDir "%VALHEIM%"
```

**With a mod manager (Way B)**, the preflight looks for BepInEx only inside the folder it is given, and
it has no setting for a profile folder. Give it a temporary stand-in folder that holds copies of both,
then delete the stand-in:

```
set "STANDIN=%TEMP%\seedlab-preflight"
xcopy /E /I /Q "%VALHEIM%\valheim_Data\Managed" "%STANDIN%\valheim_Data\Managed"
xcopy /E /I /Q "%BEPINEX%\BepInEx\core" "%STANDIN%\BepInEx\core"
powershell -ExecutionPolicy Bypass -File tools\SeedLab.Dumper\preflight.ps1 -ValheimDir "%STANDIN%"
rmdir /S /Q "%STANDIN%"
```

Tested: on the same built plugin, the stand-in folder and the real game folder gave identical
preflight output. (The test used a copy of a hand-installed BepInEx `core` folder in place of a
profile's.)

**The last line must say `PASS  <number> checks, 0 failures.`** The number changes as the dumper
changes. On 2026-09-24 it was 464.

If it says **`FAIL`**, **stop here**. Do not install the dumper. A `FAIL` on a game that
`check-game-version.ps1` called OK means something is wrong with the build. After a game update it
means the game changed something the dumper reads, and only a new version of the dumper can fix that.
The lines above the `FAIL` name what it found.

## 8. Install the dumper

The plugin goes in a folder of its own inside BepInEx's `plugins` folder. Type:

```
set "DUMPER=%BEPINEX%\BepInEx\plugins\DoomMachine-SeedLabDumper"
mkdir "%DUMPER%"
copy /Y tools\SeedLab.Dumper\build\SeedLab.Dumper.dll "%DUMPER%\"
copy /Y tools\SeedLab.Dumper\build\SeedLab.Contracts.dll "%DUMPER%\"
```

Both files are needed. `SeedLab.Contracts.dll` holds the shapes of the data, and the plugin does not
load without it.

## 9. Arm it

On its own, the installed plugin does nothing. It logs one line saying it is idle and applies no
changes to the game. It runs only when a file called **`dumper.enable`** sits next to it, holding one
word.

Type:

```
echo all> "%DUMPER%\dumper.enable"
type "%DUMPER%\dumper.enable"
dir /b "%DUMPER%"
```

`type` must print `all`, and `dir` must list exactly `dumper.enable`, `SeedLab.Contracts.dll` and
`SeedLab.Dumper.dll`.

Write it this way, not with Notepad. Notepad saves `dumper.enable.txt`, and Explorer hides the `.txt`
by default, so the file looks right and the plugin never sees it.

The word decides what the dumper is allowed to do:

| Word | Allows |
|---|---|
| `all` | everything below. **Use this.** |
| `assets` | the asset dump (F4) only |
| `natives` | the `seedlab_natives` console command only |
| `worldgen` | the `seedlab_worldgen` console command only |

**Use `all`.** An `assets`-only run does not write `seed-input.json`, which only `seedlab_natives`
writes, and only at the main menu. `vseed data`, the command that checks the folder in
[section 14](#14-copy-it-into-seedlab-and-check-it), reads that file every time it runs, so without it
the check cannot run. (Verified: the output of an `assets`-only run on 2026-09-24 has no
`seed-input.json`.)

Capital letters and a trailing new line are fine; the plugin trims the word and lower-cases it. Any
other word leaves it idle, and the log says which word it found.

## 10. Start Valheim and make sure it is armed

1. Start Valheim: from Steam with BepInEx in the game folder (Way A), or with the manager's
   **Start modded** (Way B).
2. At the main menu, switch back to the command window and type:

   ```
   findstr /C:"SeedLab.Dumper" "%BEPINEX%\BepInEx\LogOutput.log"
   ```

   Among the lines it prints you should see:

   ```
   [Warning:SeedLab.Dumper] SeedLab.Dumper 1.0.0 is ARMED in mode 'all'. Remove dumper.enable to make it inert again.
   [Info   :SeedLab.Dumper] Output folder: <your user folder>\AppData\valheim-dumper
   ```

   If you see `no dumper.enable next to the plugin, idle` instead, the file is missing or misnamed
   (see [section 9](#9-arm-it)). Quit the game, fix it, and start again.

   The log can be read while the game is running: BepInEx opens it with shared reading allowed. It
   writes to the file on a timer, so a line can take a moment to appear. If it prints nothing, wait
   a few seconds and type the command again.

**While the game is running with the dumper armed, the session is not a normal one:**

- **F4 belongs to the dumper.** In a solo world, one press starts the asset dump and the game
  stutters. (At the main menu, F4 only writes a "not now" line to the log.) If another of your mods
  uses F4, the key can be changed in `BepInEx\config\DoomMachine.SeedLabDumper.cfg`, which BepInEx
  creates the first time the plugin starts armed.
- The console gains four commands: `seedlab_status`, `seedlab_dump`, `seedlab_natives` and
  `seedlab_worldgen`. `seedlab_status` prints the mode, the output folder, and whether each dump is
  allowed right now and why not.

## 11. At the main menu: two console commands

Stay at the main menu (any screen before you enter a world). Press **F5** to open the console.

1. Type `seedlab_natives` and press Enter.

   It records how the game's random number generator, noise function and number conversions behave
   on your machine, and what the new-world seed box accepts. It takes a few seconds. Wait for:

   ```
   SeedLab.Dumper: native-function dump DONE. Files are in <your user folder>\AppData\valheim-dumper\<version>-<code>
   ```

2. Type `seedlab_worldgen MWd8eV6svz hnBd9gJf2G` and press Enter.

   It records the world generator's internal state for those two seeds, which is what SeedLab's own
   tests compare against. **The menu's background landscape flickers and changes while it runs.** That
   is expected: the dumper swaps the menu's world generator and puts it back at the end, whether the
   dump succeeds or fails. It took about 2 to 3 seconds per seed in the author's 2026-09-22 run. Wait
   for `world-generator dump DONE`.

3. Press **F5** again to close the console.

`seedlab_natives` must be run **here, at the main menu**, or it skips `seed-input.json`.
`seedlab_worldgen` refuses to run once a world is loaded, because swapping the generator under a
loaded world would corrupt it.

## 12. In a fresh, throwaway, solo world: press F4

### Why a fresh, throwaway, solo world

- **Fresh**, because the data is only clean in a brand-new world. As you walk around, the game marks
  pieces of its shared location templates as used, and a dump taken after exploring can record fewer
  pieces than the templates really have. A new world does this only for the area around the spawn
  point, and the dumper reports which templates were touched.
- **Throwaway**, because you should never point a tool like this at a world you would miss. The dumper
  writes nothing into your saves; its guard refuses those folders. The game saves the new world as it
  saves any world. The rule costs one new world, and the world can be deleted afterwards.
- **Solo**, because the dumper refuses to run with anyone else in the session. That refusal is on
  purpose. If a player connects while a dump is running, it stops, says `STOPPED, not finished`,
  disables itself until you restart the game, and leaves a folder you must not use.

**Other mods:** the dumper records what the game has loaded, mods included. If a mod changes world
generation, locations or vegetation, the data describes that modded game, not Valheim. Dump with such
mods switched off. A separate mod-manager profile with only BepInExPack_Valheim is the simplest way.
**Unverified:** which mods, if any, change these tables.

### The steps

1. At the main menu, choose any character (a new one if you prefer), then **New World**. Give it a
   name you will recognise as a throwaway, such as `SeedLabDump`. Leave the seed as offered. **If the
   seed box offers `aaaaaaaaaa`, stop and quit the game**; see
   [section 18](#18-if-something-goes-wrong).
2. Before you start the world, make sure **Start Server** is **not** ticked. That keeps the session
   solo.
3. Start the world. Wait until you are standing in it, not on the loading screen.
4. Press **F4** once.

What you will see, in the top-left corner of the screen:

- `SeedLab.Dumper: asset dump started. The game will hitch; do not quit until it says DONE.`
- then the game stutters, with progress lines counting up, such as `prefab walk: 20/186` and
  `room walk: 20/358`;
- then one line for each name it was asked to look for, each saying `FOUND` or `NOT FOUND`;
- and finally: `SeedLab.Dumper: asset dump DONE. Files are in <your user folder>\AppData\valheim-dumper\<version>-<code>`

**Do not quit until you see `DONE`.** A dump that is interrupted leaves a folder that must not be used.

### How to be sure it finished

The top-left message goes away after a while. The log is the record. In the command window, type:

```
findstr /C:"dump DONE" "%BEPINEX%\BepInEx\LogOutput.log"
```

After a complete run it prints (with your user folder in place of `%USERPROFILE%`, and your game's
version and code in place of `1.0.15-59f53fb5`):

```
[Warning:SeedLab.Dumper] SeedLab.Dumper: native-function dump DONE -> %USERPROFILE%\AppData\valheim-dumper\1.0.15-59f53fb5
[Warning:SeedLab.Dumper] SeedLab.Dumper: world-generator dump DONE -> %USERPROFILE%\AppData\valheim-dumper\1.0.15-59f53fb5
[Warning:SeedLab.Dumper] SeedLab.Dumper: asset dump DONE -> %USERPROFILE%\AppData\valheim-dumper\1.0.15-59f53fb5
```

(Each `DONE` also appears once more on an `[Info` line ending `Files are in ...`.)

The words that mean it did **not** work are **`FAILED`**, **`STOPPED`** and **`REFUSED`**. Search for
them the same way. Warning lines on their own are not failures. The author's 2026-09-24 run logged 19
`SoftReference unreadable` warnings and still ended `DONE`. **Unverified:** whether those warnings
lose any data. Anything the dumper is unsure about is written into the `notes` list at the end of the
output's `manifest.json`.

**Read the log before you start Valheim again.** BepInEx rewrites `LogOutput.log` every time the game
starts.

## 13. Quit, and find the output

1. **Quit to the desktop**, not just to the main menu. The dumper borrows parts of the game's state
   while it works and puts them back. A fresh start of the game is the simple way to be sure nothing
   borrowed is still in play.
2. **Do not start Valheim again until you have removed the dumper** ([section 15](#15-remove-the-dumper)).

The output is in a folder named after the game build: the game version, a dash, and the first 8
characters of the SHA-256 of the game's `assembly_valheim.dll`. For Valheim 1.0.15 that is
`1.0.15-59f53fb5`. To see it, type:

```
dir /b "%USERPROFILE%\AppData\valheim-dumper"
```

Or paste `%USERPROFILE%\AppData\valheim-dumper` into File Explorer's address bar. Note that this is
**not** `%APPDATA%`, which is a different folder (`AppData\Roaming`).

You never have to compute that name. The dumper chose it, and SeedLab expects exactly that name.

## 14. Copy it into SeedLab and check it

### Copy

In the command window (still in the SeedLab folder), set `DUMP` to the name you saw in section 13,
then copy the whole folder, sub-folders included:

```
set "DUMP=1.0.15-59f53fb5"
if exist "data\%DUMP%" echo STOP - data\%DUMP% already exists. Read "Replacing a folder" below.
if not exist "data\%DUMP%" xcopy /E /I "%USERPROFILE%\AppData\valheim-dumper\%DUMP%" "data\%DUMP%"
```

The copy only happens when `data\` does not already hold a folder of that name. If it printed `STOP`,
nothing was copied; read "Replacing a folder" below. Otherwise it lists the files and ends with
`<number> File(s) copied`.

`xcopy` creates `data\` if it does not exist yet. Copy the folder **as it is**. Do not rename it, and
do not open and save any file in it.

### Why a data folder is never edited

`manifest.json` in that folder records the SHA-256 of every other file in it, and SeedLab checks them
before it reads anything. **Any change to any file, even re-saving it unchanged in an editor, is
indistinguishable from corruption and is treated as corruption.** For the same reason, never mix files
from two different runs in one folder.

**Replacing a folder:** if `data\` already holds a folder with the same name (for example because you
dumped twice), move the old one **out of `data\`** first, then copy the new one in:

```
move "data\%DUMP%" "..\SeedLab-old-data-%DUMP%"
```

That puts it in the folder that holds the SeedLab folder, outside the repository, so it can never be
published with it by mistake (it is the game's content, like `data\`). Then run the two `if` lines
above again. If the name is already taken, choose another one.

A folder for a **different** game build can stay where it is. When `data\` holds several folders,
SeedLab uses the one whose name ends with the first 8 characters of the installed game's hash. If none
of them matches, it refuses and asks you to choose one with the `SEEDLAB_DATA_DIR` environment
variable.

### Your folder will be smaller than the author's, and that is expected

The author's `data\1.0.15-59f53fb5\` also holds files the dumper does not write: a hand-written
`README.md`, and `constraint-atlas.json`, `count-sample.bin` and `count-sample.json`, which were built
by one-off studies. No command in this repository rebuilds them. Only the search's pre-flight checker
reads them, and it says so when they are missing:

- without `constraint-atlas.json`, a search goal that the checker would have refused as impossible
  gets a **warning** instead, and the search still runs;
- without `count-sample.*`, two warnings about goals that nearly every seed meets (rules D4 and D5)
  stay silent.

Location answers themselves do not use these files.

### Check it

Run these from the SeedLab folder. If `vseed` is not a command on your machine, type
`src\SeedLab.Cli\bin\Release\net10.0\vseed.exe` in its place.

```
vseed data
```

Look for these three lines:

```
  verdict               MATCH - this data describes the installed game
  ...
  location answers (locations, dungeons, traders, resources): allowed
```

- `MISMATCH` means the folder is from a different game build than the one installed. You copied the
  wrong folder, or the game has updated since the dump.
- `UNVERIFIED - no Valheim install found to compare against` means SeedLab could not find your game.
  Tell it where the game is, in this window, then run `vseed data` again:

  ```
  set "SEEDLAB_VALHEIM_DIR=%VALHEIM%"
  ```

- If it stops with an error instead of a report, read the error: it names the file it could not use.
  If that file is `seed-input.json`, `seedlab_natives` was skipped or was typed inside a world. Run
  sections 9 to 11 again, then copy the folder again (see "Replacing a folder").

Then:

```
vseed data --verify
```

This runs 8 checks. **On a copy of SeedLab cloned from GitHub, 2 of them always fail. That is not your
dump's fault.** They compare the data with a log the game wrote on the author's machine,
`groundtruth\location-names.csv`, which is not in the repository:

| Check | Expected result on a clone |
|---|---|
| manifest files present, sized and hashed | pass |
| every field of every row present (strict load) | pass |
| prefab names + name hashes vs the game's log | **FAIL**: `groundtruth\location-names.csv was not found` |
| m_quantity vs the quantities the game logged | **FAIL**: it reports `0 types compared` |
| name hashes vs goldens\natives-hash.json | pass |
| prefab constants vs what the tool measured independently | pass |
| every enabled location has its prefab walked | pass |
| placement order vs the game's own ordered list | pass |

Because of those two, the command ends with `SOME CHECKS FAILED` and exit code 1. **The six other
checks must say pass.** If any of those six fails, the dump is not usable. Remove the folder from
`data\` (move it out, as above) and dump again.

**Unverified:** this table is worked out from the source of `vseed data --verify` (the check names,
and what each one needs). It has not been run on a freshly cloned copy.

## 15. Remove the dumper

Do this now, before you start Valheim again for any reason, even if a check above failed. Putting it
back takes a minute with sections 8 and 9.

**Why:** the dumper is a capture tool, and a session with it installed is not a normal game session.
In September 2026 an earlier build of it, still installed after its dumps, silently wiped the game's
random number generator in the sessions it dumped in: every new world was offered the seed
`aaaaaaaaaa`, and everything random in those sessions (weather, effects, growth) stopped being random,
with no error on screen. That bug is fixed and the preflight now checks for it (the full account is in
the plugin manual, [2026-09-23: it zeroed `UnityEngine.Random`](../tools/SeedLab.Dumper/README.md#2026-09-23-it-zeroed-unityenginerandom-and-what-was-done-about-it)),
but it is the reason this page tells you to remove the dumper, not merely leave it idle.

Type:

```
del "%DUMPER%\dumper.enable"
if exist "%BEPINEX%\BepInEx\SeedLabDumper-removed" rmdir /S /Q "%BEPINEX%\BepInEx\SeedLabDumper-removed"
move "%DUMPER%" "%BEPINEX%\BepInEx\SeedLabDumper-removed"
del "%BEPINEX%\BepInEx\config\DoomMachine.SeedLabDumper.cfg"
```

1. The first line disarms it. From the next start the plugin would apply nothing and F4 would be free,
   even if you stopped here.
2. The second line only matters the second time you do this: it deletes the copy that an earlier
   removal put aside, so that the next line has somewhere to move to. Nothing is lost; the same two
   files are still in `tools\SeedLab.Dumper\build\`.
3. The third line moves the plugin out of `BepInEx\plugins\`, so BepInEx no longer loads it at all.
   It goes to a folder beside `plugins`, which BepInEx does not load from. That folder is on the same
   drive, which `move` needs. You may delete it later.
4. The fourth line deletes the dumper's settings file. It does nothing on its own, so this is optional.
   If the file was never created, `del` says `Could Not Find`, which is fine.

**To confirm**, start Valheim once more, quit, and type:

```
findstr /C:"SeedLab.Dumper" "%BEPINEX%\BepInEx\LogOutput.log"
```

It must print nothing.

Your dump in `%USERPROFILE%\AppData\valheim-dumper\` is not touched by any of this. Once
`data\<your folder>\` passes its checks, that original folder is only a spare copy. SeedLab never
deletes it for you (`vseed clean` reports it and leaves it alone), so delete it yourself when you no
longer want it.

## 16. Linux and macOS

**The dumper has been run on Windows only.** On Linux (a native build or Proton) or on a Mac, nothing
on this page has been tested. What is known:

- The plugin declares that it is for the process `valheim.exe`. BepInEx compares that name without
  its `.exe` ending against the running program's name, so this is a name filter, not a Windows
  filter. **Unverified:** the name of the game's program on Linux and macOS, and therefore whether the
  plugin loads there at all.
- The output folder is `AppData/valheim-dumper` inside the home folder the game sees. **Unverified:**
  where that is on each system. Under Proton it is probably inside the game's Wine prefix rather than
  your Linux home folder.
- The commands on this page are Windows Command Prompt commands. The same steps apply, but every
  command would need its Linux or macOS equivalent, and none of those has been tried.

One route that avoids running the dumper on those systems: dump on a Windows PC and copy the folder
across. **Unverified:** whether the Windows and Linux/macOS builds of the game have the same
`assembly_valheim.dll`. If they do not, SeedLab on the other system reports `MISMATCH` and refuses
location answers, which is the correct and safe outcome.

## 17. After a Valheim update

When Valheim updates, `tools\check-game-version.ps1` says `GAME CHANGED` (exit code 2) and `vseed
data` says `MISMATCH`. From then on, **location answers are refused, and terrain answers continue with
a warning.** That is deliberate: a location table from another build gives coordinates that look right
and are not.

**What a new dump does not fix, said plainly:** SeedLab is a copy of **Valheim 1.0.15's** world
generation, checked against that version only. A new dump makes `vseed data` say `MATCH` again, and
location answers come back. It does **not** check that SeedLab still generates worlds the way the new
version does. If the update changed how the game places locations, the answers will be wrong and will
look right. Only the maintainer's tests can re-check that, and they need files that are not in this
repository. Until the project says it has been checked against your version, treat every location
answer as unverified. `check-game-version.ps1` keeps saying `GAME CHANGED` even after your new dump,
because it compares the game with the build SeedLab was verified against, not with your data.

If you want to dump anyway:

1. Pull the latest SeedLab, if there is a newer one, and build it again.
2. Do this page again from [section 6](#6-build-the-dumper). **If the preflight says `FAIL`, stop.**
   The update changed something the dumper reads, and a dump would be silently wrong.
3. The dumper writes the new build's folder under a **new name**. Copy it into `data\` **beside** the
   old one. Never edit or overwrite the old folder. SeedLab picks the folder that matches the
   installed game.
4. Remove the dumper again ([section 15](#15-remove-the-dumper)).

If the game did **not** update but `vseed data` says `MISMATCH`, the wrong folder is being used. Check
whether `SEEDLAB_DATA_DIR` is set (`echo %SEEDLAB_DATA_DIR%`) before you dump anything.

## 18. If something goes wrong

| What you see | What it means | What to do |
|---|---|---|
| F5 does nothing | the console is off | [Section 4](#4-turn-on-the-games-console) |
| The console does not know `seedlab_natives`, or the log has no `SeedLab.Dumper` lines | the plugin did not load | check the files ([section 8](#8-install-the-dumper)), and that the game was started with BepInEx (with a mod manager: **Start modded**) |
| `no dumper.enable next to the plugin, idle` in the log | not armed | [Section 9](#9-arm-it); the file may be called `dumper.enable.txt` |
| `dumper.enable contains '...', which is not one of assets / natives / worldgen / all` | a wrong word in the file | write it again with the `echo` line in section 9 |
| F4 does nothing, and the log says `SeedLab.Dumper: not now - no world is loaded ...` | F4 was pressed at the menu or while loading | nothing is wrong; press it again once you are standing in the world |
| `REFUSED ... The dumper is now disabled for this session` | you are a client, or someone is connected | quit, restart the game, and use a solo world with **Start Server** unticked |
| `STOPPED, not finished` or `FAILED` | the dump did not complete | do not use that output. Quit, then in File Explorer rename the folder in `%USERPROFILE%\AppData\valheim-dumper\` by adding `-incomplete` to its name, so the next run starts in an empty folder. Run sections 10 to 12 again |
| The new-world seed box offers `aaaaaaaaaa` | the game's random number generator has been wiped, as in the 2026-09-23 incident | quit the game at once; it does not recover within a session. Remove the dumper (section 15), restart, and check that the box offers a random seed. Delete any world created in that session: its seed was not random. Report it, because the preflight is meant to make this impossible |
| `vseed data` says `MISMATCH` | data and installed game are different builds | [Section 14](#check-it) and [section 17](#17-after-a-valheim-update) |
| `vseed data --verify`: a check other than the two log checks says `FAIL` | the copy is incomplete or was changed | move that folder out of `data\` and copy it again from `%USERPROFILE%\AppData\valheim-dumper\`; if it still fails, dump again |
