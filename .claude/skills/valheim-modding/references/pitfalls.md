# Pitfalls and hard-won lessons

Each entry is something that actually went wrong (or nearly did) in this project, why, and what to do
instead. Newest lessons are added at the end of their section. Keep entries short and concrete.

Contents: 1. Process · 2. Shell and file tooling · 3. Build · 4. Game code and Harmony · 5. Input, cursor, UI ·
6. Map and pins · 7. Multiplayer · 8. Game lifecycle · 9. Reading decompiled game code and game data ·
10. Offline tools, CLIs and the local web UI

---

## 1. Process

- **A session's scratchpad lives in `%LOCALAPPDATA%\Temp`, and the user clears Temp** (2026-09-24, before
  closing Claude Code). Two long sessions had left 50 critical items only there, 184 more entries were cited as
  evidence, and about 15 durable citations already pointed at files that had been overwritten or deleted.
  - **The critical items:** design documents, decisions, the only copies of the mutation drivers and the
    release scanners.
  - **A dead citation:** `wg.txt:924` was overwritten with a different dump three days later.
  - **A deleted folder:** a live session removed a folder while it was being triaged.

  The rescue is in `_ModSource\_retired\scratchpad-rescue-20260924\` (README, a re-runnable `rescue-copy.py`, a SHA-256 manifest).
  **Instead:**
  - put a tool that a checklist relies on into a skill's `scripts\` folder when it is written;
  - put a design document or a decision into the repository's docs or the KB before the session ends;
  - write every fact itself into the KB, citing a scratchpad file only as supporting evidence;
  - never overwrite a scratch file that something cites, and write new output to a new name.
- **What is installed, released or armed changes while you work - check the disk, not the knowledge
  base.** Two sessions share this game folder (SeedLab's and TomTom's). On 2026-09-24 the knowledge base
  said the SeedLab dumper was uninstalled; the other session had reinstalled and armed it hours earlier, and
  an agent's "live install not updated" was stale an hour after it was written because the build was then
  deployed. Before stating what is in `BepInEx\plugins`, what is live or what a release holds, look (plugins
  folder, `LogOutput.log`, GitHub), and date what you record.
- **A rule the user chose must be tested against its own wording, not against the code that claims to
  implement it.** Delete precedence (a), "a marker of ours within reach always wins", passed a 73-agent
  review because the verifiers tested the patch's default logic; one layout (a saved pin, a followed pin and
  a mod marker at increasing distances) showed a player's pin being deleted. Build the concrete scenario.
- **Verify game facts against the shipped DLLs, never from memory.** Training data about Valheim is
  years stale for this build (Unity 6, new Input System, `AddPin` gained a `PlatformUserID` argument).
  `scripts/decompile.ps1` settles most questions in seconds.
- **Assumptions that sounded right and were wrong** — each caught only by reading decompiled code:
  `OnMapLeftClick` places pins (it toggles a pin's checkmark); markers with `save:false` behave like
  other pins (every vanilla hit test skips them); "no local player" means "world changed" (it also
  means "just died"); showing coordinates is harmless in a no-coordinates mod (a readout turns any
  free map click into coordinate entry).
- **A golden that your implementation passes has not necessarily *discriminated* anything.** SeedLab's
  `insideUnitCircle` port uses `(float)Math.Cos((double)a)`; the game's 8 recorded draws matched it bit
  for bit, which looked like the open variant was closed. It was not: `MathF.Cos(a)` matches those same
  8 too, because the two forms differ on only 0.2586 % of the possible angles, so 8 samples had a 98 %
  chance of agreeing by coincidence. **Before calling a variant settled, measure the oracle's power:**
  evaluate every rival form on the recorded inputs and check that at least one of them actually fails.
  If none does, say "confirmed, not discriminated" and state how many samples would be needed.
  The cheap general form, when the thing under test is a whole algorithm rather than one function, is a
  **defect census**: plant each ported detail's plausible "fix" one at a time, rebuild, re-run the
  oracle, and record the score. SeedLab's location engine scored 9 caught of 12 planted (3.3 % to
  95.5 % remaining agreement for the nine), and the three misses were each explained by the *data* --
  no vanilla location has `m_exteriorRadius == 0`, none has a radius over 32, and no alt-biome sets
  `m_requireNeighbor` -- so those three claims rest on IL reading and are labelled as such rather than
  as "validated". See `valheim-worldgen/references/zones-locations-vegetation.md` section 3.7.
- **Replay a recorded trace including its implicit prelude.** The dumper's `D9-long-run` records one
  `Random.value` and the state after it - but the dumper had burned 100 000 draws first, which only the
  call's name (`value (after 100000 discarded)`) says. A harness that replayed the single recorded draw
  reported a port defect that did not exist. When a golden's own note describes state the writer built
  up, the reader has to rebuild it.
- **Check a key against vanilla, not just other mods.** F9 was shipped as a default after checking
  mod configs only — vanilla uses F9 to cycle the gamepad layout. Use `scripts/find-key-usage.ps1`.
- **Read `BepInEx\LogOutput.log` before changing a build the user has played.** It is the only record of
  how a build behaved live; it exposed the death/respawn bug that no review had found.
- **Adversarial review earns its cost.** Independent refute-by-default reviewers found every fatal
  defect in this project (an unclickable window, markers invisible to vanilla hit tests, a coordinate
  readout that defeated Wayfinder). Review compiled output, not just source.
- **Test the tripwire, not just the code.** A safety check that has never failed is unproven: break the
  build on purpose (without deploying) and confirm the check fires. Same for detectors like
  `check-game-version.ps1`, and for unit tests (next entry).
- **A test can pass for the wrong reason - plant defects to find out which.** TomTom 1.1.1's "refuses
  while the only copy is locked" still passed with the rename-back fix removed, because Windows refuses
  every write to an exclusively held file anyway (2026-09-24 re-check). The proof added then was not enough
  either: **a held handle cannot tell "renamed back" from "deleted".** On this Windows 10 NTFS, `File.Delete`
  of a file held open with `FileShare.Delete` frees the name at once and the handle keeps reading the old
  bytes, so "the held handle still reads the original" also passed with the survivor deleted; it proves only
  that the file was never opened for writing. **Mutation testing** found this and more: 45 mutants of
  `SafeFile.cs`, planted one at a time and run on .NET and on Mono (identical kill matrix), left **14
  surviving a suite that looked complete**, nine of them real regressions (the TomTom project's own history,
  `2a63e30` entry). What made the checks discriminate:
  - **Prove an ordering by making the next step fail deterministically**, then assert the earlier state is
    intact. A *directory* squatting on the target name does it: `File.Exists` is false for it, and a
    `FileStream` or `File.Move` onto it throws on .NET and on Mono.
  - **Prove "renamed, not rewritten"** by setting `File.SetLastWriteTimeUtc` to an old stamp first: a rename
    keeps it, a rewrite does not.
  - **An exception can escape the check it was meant to fail.** Against a writer that opens with
    `FileShare.None`, the planted "no rename-back" defect showed only as an `IOException` caught by the test
    method and reported as `FAIL SafeFile tests`, not as the check's own FAIL. Catch inside the check and
    report the exception under the check's name, or read the last `ok` line before the failure.
  - Keep the kill matrix (mutant x runtime x the check that killed it) and give every survivor a reason:
    after the fix 39 of 45 fail a named check; the six left are flush durability, equivalent mutants and a
    race, none visible to a unit test.
  (Correction, 2026-09-24: this entry advised a `FileShare.ReadWrite | FileShare.Delete` lock plus "assert
  that the held handle still reads the original" as the proof; it is not one.)
- **A Mono.Cecil safety scan that walks `ModuleDefinition.Types` is blind to most of the code.**
  That property lists only TOP-LEVEL types; every iterator body (`<Run>d__0`) and every capturing
  lambda (`<>c__DisplayClass7_0`) is a NESTED type. In the SeedLab dumper this hid 33 of the shipped build's 59 types (26 top-level, 33 nested; 34 of 61 after the 2026-09-23 fixes - the count moves with every build, so the preflight prints it rather than asserting it) -
  including the bodies of `ModeAssets.Run`, `ModeNatives.Run`, `ModeWorldGen.Run`, `Plugin.Drive` and
  `DumpWriter`'s streaming writer - from the scan that was supposed to prove the plugin has no
  file-deleting code. Recurse through `type.NestedTypes`, and ask ownership questions ("is this write
  inside DumpWriter?") of the OUTERMOST declaring type, or the recursion will flag a class's own
  iterator as foreign code. Proven 2026-09-23 by planting `File.Delete` in a lambda in a scratch copy:
  the top-level-only scan found 0, the recursive scan named
  `ModeAssets/<>c__DisplayClass7_0.<Hashes>b__0` and exited 1.
- **A tripwire list narrower than the claim it backs is worse than no list.** The SeedLab dumper's preflight scanned for 6 destructive and 6 write file APIs while its README said it proved "no method anywhere calls a file-deleting API" and "every file write is inside DumpWriter". `File.Copy` (overwrites its destination), `Directory.CreateDirectory`, and the whole `FileInfo`/`DirectoryInfo` surface were unlisted, so a violation using them would have passed while reading as proven. Fixed 2026-09-23 to 18 destructive + 20 write APIs, and proved twice: a scratch copy with five planted calls (`File.Copy`, `File.Delete`, `Directory.CreateDirectory`, a `StreamWriter` ctor, a `FileStream` ctor outside DumpWriter) was named call-by-call and exited 1, and `-SelfTest` now runs the same signature strings over `assembly_valheim.dll` - which really does delete, move and create files - so a typo in a signature cannot masquerade as a clean scan. Either widen the check to the claim or narrow the claim to the check.
- **Moving beats deleting** for superseded installs and user data — `_ModSource\_retired\` exists for that.
- **A commit trailer can make an assistant a public contributor, and removing it rewrites history.**
  `Co-Authored-By: Claude <noreply@anthropic.com>` on three commits listed Claude as a contributor on the
  user's GitHub repository (2026-09-24). Fixing it meant rewriting and force-pushing `main` and the release
  tag - and because the .NET SDK stamps `HEAD` into every DLL (`AssemblyInformationalVersion` =
  `1.0.0+<sha>`), the already-published DLLs now name a commit that no ref reaches. Do not add the trailer
  for this user (a standing instruction of theirs). If history ever has to be rewritten, use `git commit-tree` with the
  original tree, author and committer so only the message changes, compare the old and new trees before
  pushing, and plan the rebuild of any artifact that embeds the old hash. **Rewriting does not scrub what is
  already public:** after the force-push GitHub's `/contributors` API listed only the user, yet the
  repository page's Contributors sidebar still showed "claude" (cached; `/stats/contributors` answered 202),
  and the unreachable old commit is still served by SHA with its trailer, named by the release notes and
  the published DLLs (checked 2026-09-24; recorded in the TomTom project's own history). What finally cleared it: the user
  deleted the repository and created a new one, and the history was rebuilt with `git commit-tree` on the new
  repository's own `Initial commit` and pushed as a fast-forward; its page then showed one contributor
  (2026-09-24). Get the trailer right before the first push.
- **Keep decompiled game code out of any tree you might publish.** Investigation dumps land in working
  folders and are easy to forget: `_ModSource\Waypointer\vy_mm_rc.txt` (an IL dump of `Minimap.Start`)
  sat in the mod source root for five days and would have gone straight into a public repo. Dump into
  the scratchpad, and before a first commit list every file in the tree and check for strays. The same
  applies to the knowledge base's archive of raw review outputs, which quotes decompiled code: that
  belongs in a private repo, never a public one (it is not part of this published copy). A git repo for a mod must be rooted at the mod's own folder, never at the
  game folder (which holds the game's DLLs and other people's mods).
- **A shipped DLL carries the absolute path of its `.pdb`, which leaks the build machine's account
  name.** The release-candidate `TomTom.dll` and `Wayfinder.dll` each embedded
  `C:\Users\<the author's real name>\AppData\Local\...\obj\Debug\<Edition>.pdb` in the PE debug
  directory (the CodeView/RSDS entry), found 2026-09-23 by a byte scan of every packaged file. The mods
  ship under the pseudonym DoomMachine, so this was a publish-stopper, not a cosmetic issue. Two traps
  inside the trap: (a) **`-c Release` alone does not fix it** — the path is merely rewritten to
  `obj\Release`; (b) building from a different folder only changes *which* absolute path is embedded.
  The fix: `<DebugType>none</DebugType>` **plus** `<Optimize>true</Optimize>` unconditionally in
  `Plugin.props`, so every configuration produces the same path-free optimized assembly. Verified after
  the fix: a byte scan of both DLLs for `<first name>`, `<surname>`, `C:\Users`, `.pdb`, `obj\`,
  `AppData` and `scratchpad` returns zero hits, including when built from a clone under the user's own
  profile folder. **Rule: scan every packaged byte before publishing anything** — a clean source tree
  says nothing about the compiled artifact.
- **Git for Windows turns LF into CRLF on checkout.** `core.autocrlf=true` is set in its *system*
  gitconfig (`C:\Program Files\Git\etc\gitconfig`), so `git config --global --get core.autocrlf` returns
  nothing while `git config --get core.autocrlf` returns `true`. A repository holding bash scripts
  therefore needs `.gitattributes` with `*.sh text eol=lf`; without it every clone lands `build.sh` with
  CRLF and bash dies on `$'\r'`: command not found — a build path that works perfectly in the working tree
  and for nobody else. Prove line endings from a fresh clone, never from the folder you committed from.
  **`git apply` is affected too:** with `core.autocrlf=true` it writes the patched files into the working
  tree with CRLF (TomTom 1.1.0's review patch, 2026-09-24), so a script that edits those files afterwards
  and matches LF-only text silently misses or mixes line endings. Normalise the endings (or match `\r?\n`)
  before any scripted edit of a freshly patched file.
- **A code default is not the shipped value.** Serialized prefab data overrides field initialisers, and
  the decompiled initialiser is what you see. Three caught in one session: `Minimap.m_textureSize` is
  `256` in code and **2048** in the prefab, `m_pixelSize` `64f` vs **12.0**, `ZoneSystem.m_locationVersion`
  `1` vs **32**. Never ship a code default as a fact; settle it from game-written data (save files, the
  minimap cache), from the asset bundles, or from a runtime dump, and cite which.
- **Asset data is reachable without launching the game.** `valheim_Data\StreamingAssets\SoftRef\manifest`
  is plain UTF-8 text mapping every `AssetID` to `Assets/world/.../<Name>.prefab`, and the `UnityFS`
  bundles next to it still carry **type trees**, so MonoBehaviour fields can be read offline. Reach for
  that before assuming "asset data, cannot be checked" — that phrase closed several questions in this
  knowledge base prematurely.
- **A call-site tripwire proves the call exists, not what it does.** TomTom's preflight "exactly one
  `AddPin` call site, in `CreateLocalOnlyPin`" passed 6 IL mutants and 10 source mutants that each broke
  invariant 1 (markers local-only): `save:true` with the re-set deleted (the re-set check simply vanished,
  so preflight printed 24/24 instead of 25/25 and passed); `ldc.i4.s 1` logged as "= false"; owner 1; a
  6-argument `AddPin`; an `ldftn` reference; reflection; `List<PinData>.Add` (2026-09-24 review). Check the **values** passed, require the re-sets, count
  references of **any** opcode, `Resolve()` every game reference against the shipped DLLs, and prove each
  check on planted mutants. Fixed in TomTom `9cde723` (`a369892` after the repository move; preflight 25 → 32
  checks, 39 in 1.1.0), where `save:true`, a deleted `m_save` re-set, owner 1 and an `m_ownerID` re-set to 1
  all FAIL.
- **A pre-verified patch can implement a different rule than the design option the user chose.** TomTom's
  2026-09-24 review offered two delete-precedence options; the user chose (a), worded "an owned marker in
  range always wins". The verified patch took the nearest waypoint pin of any kind, so with a *followed* pin
  nearer the pointer than the mod's marker, vanilla `RemovePin` still deleted one of the player's saved
  pins. It passed a 73-agent review because the verifier tested the code's default behaviour, not the user's
  sentence; the 5-agent pre-release verification caught it (R2; fixed in 1.1.0 with
  `MinimapAccess.GetClosestOwnedWaypointPin` checked first). When the user picks a rule, verify the code
  against that exact wording on a concrete multi-pin layout (own marker, followed pin and saved pin at
  different distances from the pointer), not against the patch's description of itself.
- **Killing the process does not tear a single large write, so a crash-safety test must build the
  power-loss states itself.** In TomTom 1.1.1's re-check (2026-09-24), 16 of 16 kills during one 7.8 MB
  `FileStream.Write` left a complete file. That was observed, not guaranteed. Partial files come from power
  loss, so write those states to disk directly (a truncated `.new`; the main file missing with only `.new` or
  `.old` left) and test recovery from each. To land kills *inside* a rename sequence, use small files and a
  `Stopwatch` spin-wait spread across the sequence's measured duration; `Thread.Sleep`'s granularity steps
  right over it. The re-check's 200 spin-timed kills per runtime reached every intermediate state
  (the TomTom project's own history, v1.1.1 entry).

## 2. Shell and file tooling

- **Windows PowerShell 5.1 mangles UTF-8.** `Get-Content` reads a BOM-less UTF-8 file as cp1252 and
  `Set-Content -Encoding UTF8` writes a BOM, so a round trip turns `—` into `â€”`. Use
  `[IO.File]::ReadAllText($p, (New-Object Text.UTF8Encoding $false))` and the matching `WriteAllText`.
  Repair damage with Python: `text.encode('cp1252').decode('utf-8')`.
- **Write C# (and any file with apostrophes) with the Write tool, not bash heredocs.** An odd number of
  `'` in the body — common in prose comments — makes the Bash tool's heredoc fail to parse
  (`unexpected EOF while looking for matching '`), even with a quoted delimiter.
- **A Python script piped through a bash heredoc loses one level of backslash escaping**, even with a
  quoted delimiter (`<<'PY'`). `"...\\AppData\\valheim-dumper\\1.0.15..."` reached Python as
  `\AppData\valheim-dumper\1.0.15`, so `\v` became a vertical tab, `\n` a newline and `\1` a control
  byte - and the script then wrote that corruption straight into a knowledge-base file. Python only
  emitted a `SyntaxWarning: invalid escape sequence`, which is easy to scroll past. **Write the script
  to a file with the Write tool and run `python <file>`**, or avoid backslashes entirely (forward
  slashes work in every path the KB quotes). Always print a slice of the result back and eyeball it.
- **Git Bash mangles Windows arguments.** `/nologo`-style switches become paths, and MSYS paths
  (`/c/Users/...`) inside a csc response file are read as option flags. Pass switches as `-flag`, convert
  paths with `cygpath -w`, or call from PowerShell.
- **Backslashes vanish in bash double quotes.** `"C:\\a\\$e"` lost its separators in a loop; run
  path-heavy PowerShell scripts from the PowerShell tool instead of from bash.
- **`& script.sh | Select-Object` fails in PowerShell** ("Cannot run a document in the middle of a
  pipeline"). Run `.sh` scripts from the Bash tool.
- **`Select-Object -First N` truncating a pipe makes the tool report exit code 255** even though the
  command succeeded. Not an error.
- **Never put a literal NUL byte in a `.cs` file.** `"\0"` written as the raw byte (one U+0000 inside
  the quotes) compiles and works, but `grep` and `git` then classify the file as BINARY - it stops
  appearing in searches and diffs - and an editor may drop the byte on save, silently changing the
  string. Write the escape `"\0"` instead; it is the same string to the compiler. Found 2026-09-23 in
  `SeedLab.Dumper\src\ModeAssets.cs` (a hash-corpus key separator), where `grep -n` had been
  answering "Binary file matches" for the whole file.
- **XML comments cannot contain `--`.** A `dotnet run --project` inside `<!-- -->` in a .csproj breaks
  the project load (MSB4025). It costs a build every time: the SeedLab dumper csproj hit it on its first
  build with `-p:DeployToGame=false` written out in the header comment.
- **`netstandard2.0` cannot be restored on this machine.** It needs the `NETStandard.Library` 2.0.3 NuGet
  package; the only configured source is "Microsoft Visual Studio Offline Packages" (which has 1.6.0) and
  there is no cached copy, so the restore fails with NU1102. `netstandard2.1` ships as an SDK targeting
  pack and needs no package — use it for anything shared with a BepInEx plugin.

- **A quoted bash heredoc in this harness still eats backslashes: write the script to a file
  instead.** `python - <<'PYEOF' ... PYEOF` with `\\SeedLab\\data\\1.0.15` inside reached Python as
  `\SeedLab\data\1.0.15`, so `\1` became the control character `\x01` and a knowledge-base path was
  silently written as `data<0x01>.0.15-59f53fb5`. Python only warned (`SyntaxWarning: invalid escape
  sequence '\S'`) and carried on. A quoted delimiter is supposed to prevent exactly this, so do not
  rely on it: put the script in the scratchpad with the Write tool and run it by path, and grep the
  result for `[\x00-\x1f]` when it wrote Windows paths. (Cost time 2026-09-23 while updating the KB.)
  **It happened a second time the same day**, in a C# doc comment this time -- the corruption is not
  confined to the KB, and a `//` comment compiles fine with a 0x01 byte in it, so the build will not
  catch it. After any heredoc-driven edit, scan the whole tree:
  `python -c "...[c for c in open(p,encoding='utf-8').read() if ord(c)<9 or 13<ord(c)<32]..."`.
  **Not only heredocs** (2026-09-24): a `sed -i` replacement string and a `python -c "..."` argument lose
  the same level (`\r`, `\n`, `\\p` ...). The scan above works only because it contains no backslash. Any
  script or replacement that contains a backslash goes into a file written with the Write tool first.
  **And do not work around it with a printable placeholder** (2026-09-24): a script that wrote `~` for a
  backslash and ran `.replace("~", "\\")` also rewrote a `~` meaning "about" in the prose, publishing
  `\15 lines` in two knowledge-base files. Build backslashes with `chr(92)` in a script written to a file.
- **PowerShell variable names are case-INSENSITIVE, so `$P` and `$p` are one variable.** This broke a
  measurement harness three separate times on 2026-09-23: `$Bin = <dir>` then `foreach ($f in $bin)`
  wiped the directory path; `$P = <out dir>` then `foreach ($p in $list)` did the same; and a helper
  that did `$R = Join-Path ... 'region2'` then `$r = Invoke-Measured ...` turned the next
  `Join-Path $R` into `System.Collections.Specialized.OrderedDictionary\...` and created a **directory
  named after a .NET type in the project root**. Nothing errors - `Join-Path` happily stringifies the
  object. Never let a loop or result variable differ from a path variable only in case; when a script
  writes files, `ls` the working directory afterwards.
- **A PowerShell script launched as a background child is not detached, and killing its parent orphans
  its children.** Two traps in one: the Claude Code `run_in_background` wrapper's process died twice
  within seconds (`Start-Process powershell -WindowStyle Hidden -PassThru` from a foreground call was
  what actually stayed alive), and later `Stop-Process` on a parent left the grandchild script running
  for **50 minutes** beside its replacement, both taking turns on the same lock. Record the PID you
  launch, and before assuming a background job is gone, `Get-CimInstance Win32_Process` filtered on
  its **command line**, not on the PID you remember.
- **`Start-Process -PassThru` returns a Process whose `.ExitCode` is `$null` after the child exits**,
  unless you touch `$p.Handle` once while it is alive (that caches the OS handle). A harness that
  checked `if ($r.exit -ne 0) { throw }` therefore failed every single run with a blank exit code.
  `$null = $p.Handle` immediately after `Start-Process` fixes it; the same applies to
  `.TotalProcessorTime` and `.PeakWorkingSet64`, which are the honest way to get a child's CPU and
  peak memory.
- **`ProcessStartInfo.ArgumentList` does not exist in Windows PowerShell 5.1** - it sits on .NET
  Framework, not .NET Core - and neither does `Process.Kill($true)`. Build `.Arguments` as a quoted
  string, or use `Start-Process -ArgumentList`.
- **Do not trust `\Processor(_Total)\% Processor Time` on this machine.** While measuring "is the box
  quiet", it read a steady ~50 % while `Win32_PerfFormattedData_PerfOS_Processor` said 97 % idle and
  the per-process deltas said ~3 %. The 50 % was real work - one foreign process burning 6.5 cores -
  but the counter gave no way to see that, and the disagreement wasted time. **Sum per-process
  `TotalProcessorTime` deltas over a fixed interval, excluding the `Idle` process** (include it and
  you "discover" ~16 cores of load). That is also what identifies *which* process to go and stop.
- **Temp-then-rename onto a file another process has open fails - whatever share mode the reader
  used - with a message that names no file** (measured 2026-09-24, .NET 10, Windows 10 NTFS, SeedLab's
  "Access to the path is denied" checkpoint failure). `File.Move(tmp, dest, overwrite: true)` threw
  `UnauthorizedAccessException` 0x80070005 "Access to the path is denied." against a separate process
  holding `dest` with every share mode tried, **including `ReadWrite | Delete`** - so asking readers to
  open "politely" does not help. A read-only attribute and an ACL denial throw the identical type,
  HResult and path-less message. `File.Replace` does tell them apart (sharing -> `IOException`
  0x80070020; read-only -> 0x80070005) and succeeds against `Delete`-sharing holders, but not against
  `File.ReadAllText`-style readers. Against a separate process polling the file every 50 ms, 0.3-0.4 %
  of saves failed, and **every one recovered by retrying only the move after 10 ms** (the `.tmp` is
  intact). So: retry the rename with short back-off, and only after the retries run out, diagnose
  (read-only attribute? open for write -> sharing violation? -> permission) and say the file's name
  yourself. A check at start-up cannot predict a reader that appears later. Also:
  `File.Delete` of a held file throws `IOException` 0x80070020 unless the holder shares `Delete`, and
  a `Delete`-then-`Move` "replace" can lose the file when the move then fails.
- **`git commit -m` with a multi-line message containing double quotes, from Windows PowerShell 5.1,
  splits into pathspecs** ("error: pathspec 'writes' did not match any file(s)"): 5.1 does not escape
  embedded quotes for native programs. Write the message to a file and use `git commit -F <file>`.
- **WebFetch gets HTTP 402 from valheim.fandom.com** (2026-09-24, SeedLab run-6 name cross-check). The
  MediaWiki API answers a plain `curl`:
  `https://valheim.fandom.com/api.php?action=parse&page=<Title>&prop=wikitext&format=json` returns the
  page source, whose `{{Infobox_location}}` carries the `id=` a prefab can be matched on.

## 3. Build

- **Use the .NET SDK** (installed 2026-09-18): SDK-style csproj, `netstandard2.1`, `LangVersion latest`,
  game DLLs referenced with `<Private>false</Private>`. `assets/plugin-template/` is a working start. A
  project that must also build with the legacy C# 5 compiler should set `<LangVersion>5</LangVersion>`
  instead, so the SDK build and Visual Studio reject C# 6+ syntax with CS8026 (TomTom since 2026-09-24);
  the legacy build stays the gate for the older mscorlib API surface.
- **MSB3277 warnings are expected** for Unity mods (System.* version unification). Demote them:
  `<MSBuildWarningsAsMessages>MSB3277</MSBuildWarningsAsMessages>`.
- **`MSB3027`/`MSB3021` is a lock, not a build error: a long-running tool you (or another agent)
  started earlier is holding its own Release output.** Leftover `vseed serve` and `vseed search`
  processes held the SeedLab CLI's `bin\Release\net10.0\vseed.exe` open, and every later build of
  that project failed after ten one-second retries — a minute per attempt, and worse, a
  half-updated output directory (the DLLs copy, the apphost does not). The error names the locking
  process and its PID; confirm with
  `Get-CimInstance Win32_Process -Filter "Name='<exe>'" | Select ProcessId,CommandLine` before
  assuming a stale handle. **Killing it is wrong when the process is not yours.** Build to a private
  directory instead — `dotnet build ... -p:OutputPath=<scratchpad-dir>\` redirects that project *and*
  its dependencies' copies, so a test build never touches the shared folder, and a server started
  from the private copy is one you may stop. Run such a copy with the working directory at the
  SeedLab root, or `GameData.Load()` cannot find `data\<version>-<hash>\` and every location answer
  fails closed. (2026-09-23, SeedLab, twice)
- **An XML comment in a `.csproj` may not contain a double dash.** A comment reading
  `Run it with: dotnet run --project ...` makes MSBuild fail the whole project load with
  `MSB4025: An XML comment cannot contain '--'`. Write the command without the double dashes, or
  put it in a `<PropertyGroup>` string. (2026-09-23, SeedLab)
- **Two SDK projects may not share a folder** — their `obj\` collide. One subfolder per project.
- **To drop a file from one project, `<Compile Remove>` it with the same path expression the Include
  used** (both from one root property). A `..`-relative path from another folder may not match.
  Verify with `dotnet msbuild X.csproj -getItem:Compile`.
- **`dotnet new sln` on SDK 10 creates `.slnx`.** Visual Studio 18 opens it.
- **`ZipDirectory` is available** in MSBuild for packaging (see the TomTom `Plugin.props`).
- **The legacy compiler is C# 5 only** (`csc.exe` v4.0.30319). If a fallback build must keep working:
  no string interpolation, `nameof`, `?.`, expression-bodied members, `out var`, tuples, or getter-only
  auto-properties (`CS0840`); and `AccessTools.FieldRef`/`FieldRefAccess` are unusable (ref-return
  delegate, `CS0648`) — use `AccessTools.Field(t, "name").GetValue(obj)`.
- **An AssetBundle needs a Unity Editor no newer than the game's player**, and neither `dotnet build` nor
  a legacy-csc build can produce one (which editor to use is in environment.md, Toolchain). Without a
  bundle, generate visuals at runtime (`Texture2D.SetPixels32`, `Sprite.Create`), draw with IMGUI, or
  embed a PNG and load it with `ImageConversion.LoadImage(tex, bytes, markNonReadable)` — no Editor
  needed; published mods such as AzuEPI and AzuCraftyBoxes do it. That needs a reference to
  `UnityEngine.ImageConversionModule`, which TomTom's `build.sh` has and its `Plugin.props` does not
  (2026-09-24). Render a texture offline with Python/PIL to check it before the user ever launches the
  game. Remember `Texture2D` row 0 is the **bottom** row.
- **A project that needs `<Nullable>enable</Nullable>` must say so itself.** Inheriting it from an
  ancestor `Directory.Build.props` works inside the tree and fails the moment the project is built
  anywhere else: `SeedLab.Contracts`, whose DTOs are all `string?`/`int[]?`, built clean in place and
  emitted **310 CS8632 warnings** (155 per target framework) when copied out and built on its own
  (measured 2026-09-23). The annotations are also meaningless without it. Put the property in the
  csproj of anything that ships or is copied on its own.
- **Build output with no `$(Configuration)` in its path lets a Debug build overwrite a Release
  artifact.** TomTom's `Plugin.props` sets `OutputPath=build\$(EditionName)\` and its `PackagePlugin`
  target always writes `dist\DoomMachine-<Edition>-<Version>.zip`, so the two configurations land on the
  same paths and nothing in the file names says which is which. In the artifact the only difference is
  `AssemblyConfigurationAttribute` and `DebuggableAttribute` (**263** = optimizations disabled, vs
  **2**). Neutralized for that project on 2026-09-23 by making `DebugType`/`Optimize` unconditional (see
  the `.pdb` path leak in section 1), but the shape is worth recognising elsewhere: put the
  configuration in the output path, or verify the artifact rather than the command line.
- **Every build should end with a hash check** of the deployed DLL against the build output.
- **`LangVersion 5` makes every method-group delegate a fresh allocation.** Roslyn caches a static
  method-group conversion only from C# 11: at `latest`, `GUILayout.Window(id, rect, DrawWindow, title)` reads
  a compiler-generated `<>O::<0>__DrawWindow` field; at 5 it compiles to `ldnull; ldftn; newobj
  GUI/WindowFunction` on **every** call, i.e. every OnGUI event. Setting the language version to catch C# 6+
  syntax would have regressed TomTom's per-frame garbage. Cache the delegate in a `static readonly` field
  (`WaypointWindow.DrawWindowFn`). `Array.Empty` for an empty `params` array is emitted at both versions.
  (Verified with Cecil on SDK builds of TomTom `dfd9f64`, 2026-09-24.)
- **The legacy compiler builds arrays differently, so an IL scan tuned on Roslyn output misses it.**
  `new Type[] { typeof(A), typeof(B), typeof(C) }` is `dup`-based under Roslyn but `stloc.s V; ldloc.s V;
  ldc.i4.N; ldtoken; call GetTypeFromHandle; stelem.ref` per element under legacy csc `-langversion:5`, so
  a 3-parameter `AccessTools.Method` sits 21 instructions after its `ldstr` (19 under Roslyn). TomTom's
  preflight used a 20-instruction window and would have missed it; it uses 40. Test a Cecil scan against
  the output of **every** compiler that can build the plugin (`MinimapAccess.Init`, 2026-09-24).
- **A comment-only edit still changes a DLL's SHA-256** - the portable PDB's ID is embedded in the
  assembly. To prove "comments only", build before and after with `-p:DebugType=none` and compare those
  hashes (SeedLab dumper and contracts, 2026-09-24: byte-identical that way).

## 4. Game code and Harmony

- **Accessibility decides how you reach a member.** `scripts/api-surface.ps1` prints it. Private
  methods: Harmony can patch them by name; to *call* one use
  `AccessTools.MethodDelegate<Func<T,...>>(methodInfo)` (open-instance delegate).
- **Patch each class separately.** `harmony.PatchAll(assembly)` aborts on the first unresolved target,
  so one renamed method disables every patch. `PatchAll(typeof(OnePatchClass))` in its own try/catch.
- **A prefix returning false skips the original, never another prefix — and you cannot stop a
  co-patcher's prefix with your own.** (Corrected 2026-09-24: this entry said a false prefix also skips
  every later prefix.) HarmonyX 2.9's `HarmonyManipulator.WritePrefixes` (decompiled from
  `BepInEx\core\0Harmony.dll`) calls **every** prefix in turn, ANDs their bool results into `__runOriginal`,
  and branches once, before the original. So ServerDevcommands' `Minimap.OnMapMiddleClick` prefix, which
  pings (or teleports) itself and returns false, runs whatever your prefix returns: block such a gesture
  upstream (for the map image, its `UIInputHandler`), not with a competing prefix. A false return still
  kills the original for everyone, so return false only conditionally and check who else patches the
  target (`scripts/scan-mod-patches.ps1` — which does not see targets returned by a `TargetMethods()`
  method, e.g. ConfigurationManager's `UI_WindowInput`; search the DLL's `ldstr` operands for the member
  name too). **Postfix order** is `PatchInfoSerialization.PriorityComparer`:
  priority descending, then registration order; a postfix that forces `__result` must carry
  `[HarmonyPriority(Priority.Last)]` when a co-patcher assigns `__result` rather than ORing it (Chatter's
  `Chat.HasFocus` postfix assigns at default priority and loads after TomTom, so an equal-priority TomTom
  postfix would be silently overwritten).
- **`Minimap.instance` (and other `instance` getters that are a raw `ldsfld`) can return a destroyed
  object.** Compare with `!= null` (Unity's operator), never `ReferenceEquals`.
- **`ZNet.GetWorldUID()` throws when no world is loaded** (bare `m_world.m_uid`, no null check).
  `GetWorldName()` is null-safe. Guard with try/catch or check `ZNet.GetWorld() != null`.
- **`ZInput.GetKey*` is null-safe; `ZInput.IsMouseActive()` is not.**
- **`Harmony.Patch(original, prefix, postfix, transpiler, finalizer)` (5 args) is `[Obsolete(error:true)]`**
  in this Harmony — use the attribute form or the newer overload.
- **`Terminal.ConsoleCommand`'s constructor registers the command itself** — constructing it is enough;
  a `Terminal.InitTerminal` postfix is the conventional, conflict-free place (6 installed mods use it).
- **Nothing in `Update`/`OnGUI` may throw uncaught**, and a repeating error must be rate-limited, or it
  logs thousands of lines a minute.
- **BepInEx config supports Unity types** (`Color`, `Vector2/3/4`, `Quaternion`, `Rect`) via its lazy
  converter loader — no need to store colours as hex strings.
- **A `UnityEngine.Random` save/restore guard must never span a coroutine `yield`.** The pattern the game
  itself uses (`WorldGenerator..ctor`, `ZoneSystem.PlaceVegetation`) saves `Random.state` and writes it
  back on exit. Hold that guard across frames and the restore silently **undoes every draw the game made
  in between** — weather, effects, spawns. Wrap only synchronous blocks; for work that must be spread over
  frames, guard each frame's batch separately.
- **`SoftReference<T>.Release()` must only balance a `Load()` that actually returned.** Calling it in a
  `finally` after a throwing `Load()` decrements a reference count the caller never took and corrupts the
  asset loader's bookkeeping for the rest of the session. Set a `loadCalled` flag immediately after the
  `Load()` and test it in the `finally`.
- **`yield return` may not sit inside a `try` that has a `catch` clause** (CS1626), which matters because
  a coroutine driving a long job is exactly where you want to catch everything. Take the step inside the
  try (`more = inner.MoveNext(); current = inner.Current;`), record the exception in a local, and `yield`
  after the try. `try`/`finally` without a `catch` *is* allowed to contain a `yield return`, which is how
  a file handle can be held open across frames.
- **Restoring a swapped game static belongs in a `finally`, not after the loop.** The SeedLab dumper
  replaced `WorldGenerator.instance` per seed and restored it on the last line of the coroutine: any
  throw after the per-seed try/catch (a refused path, a full disk) skipped the restore, and the main
  menu then generated its backdrop from the dumper's throwaway world for the rest of the session.
  `try { ... } finally { restore(); }` around the whole loop is legal with `yield return` inside it,
  and the compiler implements it as a `<>m__Finally1` method called from `MoveNext`'s **fault**
  handler and from `Dispose` - so it runs when `MoveNext` throws, but NOT if a driver abandons the
  iterator without disposing it. Verify both in the IL (`method.Body.ExceptionHandlers`) and by
  forcing a throw. Same rule for a state save/restore guard: put the restore in a `finally` so a body
  that throws half way cannot leave the state displaced.
- **An IL listing that looks stack-unbalanced usually isn't — Roslyn keeps values on the evaluation
  stack across statements.** In `WorldGenerator.GetPlainsHeight` the `GetBaseHeight` result is pushed at
  IL_0008 and not consumed until IL_00f0, with `stloc`/`starg` for other variables in between, so a
  naive instruction-by-instruction stack trace shows `conv.r8` on an "empty" stack. Look for a `call`
  whose result is never stored before concluding the dump is wrong. (Cost ~10 minutes chasing a
  non-existent Mono.Cecil bug, 2026-09-22.)
- **A Mono.Cecil `ReadAssembly` on a game DLL needs a resolver**: `DefaultAssemblyResolver` +
  `AddSearchDirectory(valheim_Data\Managed)`, else it throws
  `Failed to resolve assembly: 'UnityEngine.CoreModule'`.
- **`Vector2s` and `Vector2i` are in `assembly_utils.dll`, not `assembly_valheim.dll`** — decompiling
  them from the wrong assembly gives "type not found". `Vector2s` holds two `Int16`.

## 5. Input, cursor, UI

- **`UnityEngine.Input` does not work** (new Input System). Use `ZInput.GetKeyDown(KeyCode, false)`
  (two arguments), `ZInput.pointerPosition`.
- **To take the keyboard and release the cursor, postfix `TextInput.IsVisible` to return true while
  your window is open** (only then — never clear someone else's true). That one flag is read by
  `Player.TakeInput` (hotbar, Use, attack, map, inventory keys), `PlayerController.TakeInput`
  (movement/look), `GameCamera.UpdateMouseCapture` (cursor lock), `Chat.Update`, `Menu.Update` and
  `Minimap.Update`. ConfigurationManager uses exactly this. Patching only
  `PlayerController.TakeInput` left the window unclickable (cursor re-locked every LateUpdate) and let
  typing fire hotbar slots.
- **Known gaps even then:** `InventoryGui.Update` (Tab, gamepad Y), `GameCamera.UpdateCamera` (wheel and
  gamepad zoom), `HotkeyBar.Update` and `Player.UpdatePlacementGhost` gate on `Chat.HasFocus`, not
  `TextInput.IsVisible`, so Tab still opens the inventory and **the mouse wheel still zooms the camera** —
  scrolling a list in your window zooms the camera behind it. Console key binds still fire (vanilla
  dispatches them from `Chat.Update`; with ServerDevcommands installed its own postfix does, gated only on
  chat input focus or the console window — vanilla-behaviour.md section 4). And **Escape does nothing**:
  `Menu.Update`'s open-on-Escape and `Minimap.Update`'s Escape/B close gate on `!TextInput.IsVisible`, so
  close your own window on Escape yourself. Fixes (verified in the 2026-09-24 TomTom review, shipped in
  TomTom/Wayfinder 1.1.0): postfix `ZInput.GetMouseScrollWheel` to return 0 while your window is open — a **postfix**,
  because `Internal_GetMouseScrollWheel` calls `OnInput(allowSwitchInputSource: true)` when the wheel
  moves and a skipping prefix would drop that; read the wheel for your own UI from IMGUI's `Event`
  (`GUI.EndScrollView` scrolls from `Event.current`). For Tab and zoom, a
  `Chat.HasFocus` postfix at `Priority.Last` (section 4); it also mutes every other consumer of HasFocus
  (`StoreGui`, `TextInput.Update`, `Minimap.Update`, other mods that read it, AzuEPI's loadout
  panel, which treats it as a close trigger), so state those effects.
- **An IMGUI window does not block uGUI.** The large map image's `UIInputHandler` (EventSystem-driven,
  wired in `Minimap.Start`: right click → `RemovePinUnderPointer`, middle click → `OnMapMiddleClick`,
  left down/up) fires for clicks on any IMGUI window drawn over it, so a double-click on your window can
  place a saved pin and a middle-click pings every player. ConfigurationManager 1.1.18 prefixes
  `UIInputHandler.OnPointerDown/Click/Up` for this reason. Block per handler — for the map, its image's
  `UIInputHandler.OnPointerClick` plus `Minimap.OnMapLeftDown`/`OnMapDblClick` — while the pointer is over
  your window (2026-09-24 review, decompiled).
- **IMGUI allocates on its own, and some of it you cannot remove** (UnityEngine.IMGUIModule, decompiled
  2026-09-24). `GUILayout.Width/Height` are `new GUILayoutOption(type, boxed float)` plus a `params`
  array; `GUILayoutEntry.ApplyOptions` and `GUILayoutUtility.DoGetRect` only read `option.type/value`, so
  `static readonly` option arrays are safe. A lambda capturing a loop-scoped local allocates its closure
  at the start of every iteration, even when the button is never clicked. **Unity's side:**
  `GUILayout.Window` → `DoWindow` does `new LayoutedWindow(...)` and passes the new delegate
  `layoutedWindow.DoWindow` to `GUI.Window` on every call, and `GUILayoutUtility.Begin` allocates two
  `GUILayoutGroup`s per Layout event for every enabled MonoBehaviour with `OnGUI` and `useGUILayout`. So
  "the draw allocates nothing" can only ever be true of the plugin's own IL. A cache keyed on
  `ReferenceEquals` of a `Localize` result is defeated for an untranslated `$token` (game-api.md,
  Localization).
- **If you take the keyboard with the `TextInput.IsVisible` postfix, exempt your own window from your
  own "is the player typing?" check** — otherwise the key that opens the window can never close it. The
  plugin template once had exactly this bug (found by an agent using this skill); it now has an
  `OwnWindowOpen` flag.
- **Never write `UnityEngine.Cursor` directly** — `ZCursor` tracks its own visibility state; go through
  `ZCursor.LockState` / `ZCursor.Show()`, or let the `TextInput.IsVisible` path do it.
- **IMGUI works** and is the least fragile UI without asset bundles. Mutating the number of controls in
  the middle of an event pass causes "Mismatched LayoutGroup" — defer actions to the next
  `EventType.Layout` and capture the *object*, not the row index, in deferred closures.
- **Our own IMGUI text field doesn't set any game flag**, so gate your hotkeys on it yourself
  (`GUIUtility.keyboardControl != 0`), and only for keys that type a character.
- **uGUI `GuiScaler.Awake` overwrites a game-wide static** (`m_largeGuiScale`) — adding one to your own
  canvas changes the whole game's GUI scale unless you save and restore it.
- **`MessageHud` Center messages are not queued** (a second one in the same frame overwrites the first);
  TopLeft messages are queued.
- **`Minimap.ShowPointOnMap` forces the large map open** and swallows input for 0.5 s.

## 6. Map and pins

- **`save:false` pins are invisible to every vanilla pin lookup**: `GetClosestPin`, `GetClosestPinToCursor`,
  `HavePinInRange`, `RemovePin(Vector3, float)` and the right-click delete all skip them. They still
  render. Find your own markers by walking `m_pins` (private field) yourself.
- **`save:false` is also what keeps a pin local** — `GetSharedMapData` (Cartography Table) and
  `GetMapData` (profile) export only `m_save` pins. Keep mod markers `save:false`, `m_ownerID = 0`.
- **Pins with `m_ownerID != 0` belong to someone else** and are deleted on the next map sync
  (`AddSharedMapData`) and hidden when shared-map fade is off.
- **`AddPin` before `Minimap.Start` throws** (`m_visibleIconTypes` is allocated in `Start`).
- **`Minimap.SetMapData` → `ClearPins()` wipes every pin** without going through `RemovePin`, when the
  character's map data loads — hold pin references loosely and re-find them.
- **`AddPin` re-enables a filtered-off icon type** automatically, so your marker is always shown — by
  flipping the **player's** icon filter back on (and rumbling a gamepad) through `ToggleIconFilter`
  (vanilla-behaviour.md section 1). A mod that creates markers undoes the player's filter choice for that
  icon type; say so in its README.
- **Pin names can be localization tokens** (`$hud_mapday 9` on the tombstone) — show them through
  `Localization.instance.Localize`.
- **A pin the player owns must never be modified** by a mod that merely follows it. Keep "we follow
  their pin" (persisted intent) separate from "we are showing a stand-in marker" (runtime state).
- **Returning an empty list when reflection fails is not fail-safe if callers read absence as "gone".**
  TomTom's `GetPins` returned a shared empty list when `m_pins` could not be read; then `PinIsAlive` was
  always false, `EnsurePins` would have re-added every marker every 0.5 s through the real `AddPin`, and
  `RemoveOwnMarker` could never find one to remove. Return null ("unknown") to such callers and have them
  stop (`MinimapAccess.TryGetPins`, TomTom `9cde723`, now `a369892`, 2026-09-24 review).
- **Don't hold a reference to a ping, shout or player pin.** Those pins are recreated whenever their count
  changes and otherwise overwritten by index, so a held `PinData` can die or come to stand for another
  ping or player (vanilla-behaviour.md section 1). Filter them out of anything a player can follow.

## 7. Multiplayer

- See multiplayer.md. Short version: map sharing only ever carries `m_save` pins; a client-only mod
  cannot put anything on another player's map through the Cartography Table unless it creates
  `save:true` pins.

## 8. Game lifecycle

- **Death destroys the local player but not the map.** `Player.m_localPlayer` is null between death and
  respawn while `Minimap` and its pins survive. Treating "no player" as "world changed" duplicated
  every mod marker on each respawn. Detect a world change by world UID.
- **`WorldGenerator.instance` is non-null in the main menu** (the menu world, seed 0, initialized by
  `FejdStartup.Awake`) — it is not a "world is loaded" signal. On a client joining a server it is
  **null** from `ZNet.Awake` (`WorldGenerator.Deitialize`) until `ZNet.RPC_PeerInfo` delivers the seed.
- **`WorldGenerator` runs off the main thread** too (`HeightmapBuilder`'s own thread calls GetBiome,
  GetBiomeHeight, GetBiomeSector): a Harmony patch there must be thread-safe and must not touch
  main-thread-only Unity APIs.
- **`ZoneSystem.GetGroundHeight` returns your own input y when its raycast misses** (unloaded zone), and
  `GetSolidHeight` raycasts from the y you pass plus the margin, 2000 m down. For the height of any
  point, loaded or not, `WorldGenerator.instance.GetHeight(x, z)` gives a procedural **estimate** — real
  ground differs because the terrain builder blends the corner biomes of each heightmap cell and applies
  terrain edits (valheim-worldgen skill).
- **Console cheats need you to be the server**: `Terminal.IsCheatsEnabled()` is
  `m_cheat && ZNet.instance.IsServer()` — on a remote server even an admin can't run `isCheat` commands locally.
- **A save driven from the per-frame tick needs a back-off, a flush on world change, and the right world.**
  "Clear the dirty flag only after a successful write" (2026-09-19 review) is half a fix: TomTom v1.0.0
  retries a failed save **every frame**, so a read-only route file gave 600 attempts and 600 warnings in
  600 frames (standalone mirror). A back-off alone is worse — it was built and withdrawn in the
  2026-09-24 review — because once `Player.m_localPlayer` is null the tick no longer saves, so a transient
  failure followed by a logout inside the back-off loses a change the every-frame retry would have
  written. Also save to the world the data was **loaded for**, not the current world UID: during the next
  world's load the two differ, and a quit then writes world A's data into world B's file. So: back off,
  but flush a dirty queue to its own world, ignoring the back-off, before loading or clearing for the next
  world. For crash safety copy the game's own pattern, `FileHelpers.ReplaceOldFile` (assembly_utils, used
  by `PlayerProfile.SavePlayerToDisk`): write `<file>.new` (`FileWriter.Finish` flushes it to disk), delete
  `.old` if present, move the file to `.old`, move `.new` into place. TomTom 1.1.0 did all of this except the
  crash-safe replace; **1.1.1 added it** (`SafeFile`; the TomTom project's own history, 2026-09-24). The replace
  has data-loss paths of its own, and 1.1.1's first draft had two of them. **Recover an interrupted save by
  renaming the surviving `.new`/`.old` back, never by marking the data dirty and rewriting it:** a survivor
  that could not be read (held open by a backup tool) was rewritten as an empty file and both copies were
  deleted. **Never open `.new` for writing while the main file is missing:** the survivor may be that very
  `.new`, and `FileMode.Create` truncates the only complete copy. Rename the survivor back first, and retry
  later if that fails. (Correction, 2026-09-24: this entry said TomTom's crash-safe replace "stays
  report-only".)

## 9. Reading decompiled game code and game data

- **A "count within a radius" over a group of location types is not "one of each".** Every boss altar
  has `m_quantity` 3-5, so a query for "7 boss altars within 6 km" is satisfied by three `Eikthyrnir`
  and four `GoblinKing` with five of the seven bosses absent. SeedLab shipped a `boss-rush` preset
  built on exactly that mistake. The metric that means what the English means is *the largest of the
  per-type nearest distances*; a sum over the group answers a different question. Whenever a goal is
  written over a set of prefabs, say out loud whether it is about instances or about types.
- **A location type's own `m_minDistance` / `m_maxDistance` and its biome's distance band make some
  goals impossible for every seed, and cheap to refuse.** `Hildir_camp` and `BogWitch_Camp` both carry
  `m_minDistance` 3000, so "all three traders within 3 km" is unreachable; `FaderLocation` is
  AshLands-only and `IsAshlands` is `|(x, z - 4000)| > 12000 + A`, so no Fader altar is within 7,900 m
  of the centre and "all seven bosses within 6 km" cannot happen either. Both were shipped as presets
  before anyone checked the table. Read the ring off `locations.json` before calibrating a threshold,
  not after a scan finds nothing.
- **A threshold nobody measured is not a filter.** SeedLab's `dungeon-delver` asked for "10 burial
  chambers within 3 km" and `iron-rich` for "6 sunken crypts within 3 km"; the measured medians are
  145 and about 90, so both passed every seed in the space while looking like hard searches. Sample a
  few hundred seeds and take the percentile you want before writing a number into a preset.

- **A `[Flags]` enum value and its index enum are not interchangeable, and Valheim mixes them.**
  `Heightmap.Biome` is a bitmask (Mountain = 4, BlackForest = 8, Ocean = 256) while
  `Heightmap.BiomeIndex : byte` is a dense index (Mountain = 3, BlackForest = 4, Ocean = 8), and
  `BiomeHelpers.ToBiome` / `ToBiomeIndex` convert between them. `BiomeSector.CanAddModifier` tests its
  mask with `((BiomeIndex)i).ToBiome()` but then compares a neighbour with `(Heightmap.Biome)i` — so the
  BlackForest *bit* matches a **Mountain** neighbour and the Ocean *bit* matches a **BlackForest**
  neighbour. Both the first spec draft and this knowledge base initially recorded that loop as "only
  matches None/Meadows/Swamp/Mountain/BlackForest", which is a different and wrong statement. When a
  loop index is cast to an enum, write out the whole 0..n table before describing what it matches.
- **`Enum.HasFlag(0)` is always true.** `(x & 0) == 0` holds for every `x`. In `CanAddModifier` this is
  what makes the `i == 0` iteration of the `m_requireNeighbor` loop unskippable — and, because no
  `BiomeSector` ever has `Biome == None`, what makes any non-zero `m_requireNeighbor` unsatisfiable.
  The same quirk makes every alt-biome "valid" for the `Biomes[None]` dictionary key in
  `AltBiomeWorldData.GenerateAltBiomes`.
- **Half-precision game data: round the way Unity rounds, do not apply a tolerance.** Valheim stores the
  minimap height cache through `Mathf.FloatToHalf` (native), which breaks ties **away from zero**, while
  .NET's `(Half)` cast rounds ties **to even**. Comparing a reproduced float with `(float)(Half)value`
  therefore reports a ~4×10⁻⁵ false-mismatch rate made entirely of exact midpoints — 158 of the 163
  disagreements in the first SeedLab height sweep were this and nothing else, which almost sent the
  investigation into the height formulas. Convert with the game's tie-break and compare bit-for-bit; a
  "tolerance" would have hidden the five real differences underneath.
- **When a port disagrees with game data, first measure how far the value is from the storage rounding
  boundary.** Expressing each disagreement in float ulps from the nearest half-rounding midpoint split
  the SeedLab height mismatches instantly into "knife edge, last-bit noise" (158, all at distance 0) and
  "genuinely different value" (5, at 2–94 ulps) — and the five turned out to share one property (all
  inside a river), which named the suspect subsystem in one step. Chasing an undifferentiated mismatch
  count would have taught nothing.

- **A perfect score against half-precision game data does not mean the port is right.** The minimap
  height cache stores `Mathf.FloatToHalf(height)`, so it cannot see an error smaller than a half-ulp
  (0.015 m at sea level, 0.25 m on a 400 m peak). SeedLab's port scored 99.9998 % bit-exact against it
  on both cached worlds — and 99.43 % / 99.51 % against the **float32** `y` of the 12 314 / 12 287
  location instances in the same worlds' `_main.<N>.db2`, with differences up to 47 float ulps. Those
  `y` values are free, full-precision `GetHeight` samples (`ZoneSystem.GenerateLocationsTimeSliced`
  assigns `randomPointInZone.y = GetHeight(x, z, out mask)` and `RegisterLocation` stores the vector
  unchanged), they cover every biome out to 10 300 m, and they need no reproduction of the placement
  RNG. Run them before believing a half-precision result; and when they disagree, split the failures by
  whether a river touches the point — that one split turned an undifferentiated 70 into "53 river,
  17 river-free and every one of those DeepNorth", which are two different bugs.

- **An `s_`-prefixed field is not necessarily static.** `ZoneSystem.s_tempVeg` is
  `private List<float> s_tempVeg` — an instance field, shared between `GenerateLocationsTimeSliced` and
  `PlaceVegetation`. Check the declaration; do not infer lifetime from the name.
- **A method can exist purely as dead code.** `ZoneSystem.CheckLocationDuplicates` is never called, so
  "no duplicate warning in the log" says nothing about duplicates. Before treating a log line's *absence*
  as evidence, confirm with an xref scan that something actually calls the emitter.
- **Check that a decompiler dump is source before quoting it.** `decompile.ps1` writes its failure to
  stdout, so a redirected dump of a type it cannot find (for example `Vector2s`, which lives in
  `assembly_utils`) is a `.cs` file whose contents are a PowerShell "type not found" error. A spec cited
  such a file as its evidence for `Vector2s`. Grep new dumps for `type not found` before reading them.
- **Release builds hide most of genloc's diagnostics.** `m_IterationsRun`, the per-location error totals
  and the `Placed .../...` alt-biome lines that met their minimum all sit behind
  `UnityEngine.Debug.isDebugBuild` or `ZLog.DevLog`, which is why the shipped log prints
  `iterations: 0` and `with 0 tries`, and why only *failing* alt-biomes appear. Absence of a line in
  `LogOutput.log` is not absence of the thing.
- **`BepInEx\LogOutput.log` is overwritten on every launch.** A spec cited measurements from a world
  (`test1`) whose log no longer existed. Copy the log into the scratchpad the moment you take numbers
  from it, and name the world and timestamp beside every figure.
- **"Every output is valid" is not "the output is right".** Inverting `GetStableHashCode` by greedily
  taking the first branch of the lane backtracking returns a seed text that always re-hashes to the
  target — so no correctness test fails — yet its even-indexed characters are wildly non-uniform
  (χ² up to 130,232 against df = 58; a 26× spread between the most and least common symbol), while
  `World.GenerateSeed` draws all ten positions uniformly. A tool presenting that as a "game-style"
  seed is visibly lying to anyone who looks at a handful of them. Fix: weight each descent step by the
  predecessor's multiplicity **and** reject with probability `w(E)/max w`; both halves are needed,
  because acceptance alone over-represents low-multiplicity lanes by up to 81×. Measured after the fix:
  χ² 47–76 at all ten positions over 200,000 targets. *(SeedLab spec 06 §6.1.1; reproduced
  2026-09-22.)* The general lesson: when a generator's output is checked only by a predicate that
  every candidate satisfies, the distribution is untested — test the distribution.
- **Do not assume a game's world→pixel and pixel→world functions invert each other.** `Minimap` has two
  conventions half a pixel apart: `GenerateWorldMap` samples pixel `j` at `(j−1024)·12 + 6` while
  `WorldToPixel`/`MapPointToWorld` place pixel `j` at `(j−1024)·12`. A spec asserted that
  "`WorldToPixel(centre of pixel k) == k` holds"; it is `k + 1`, for every one of the 4 194 304 pixels, and the
  error is silent — a 6 m shift looks like nothing until it is a biome lookup at a coastline. Cost: one
  acceptance check that was written to assert the wrong thing. Fix: derive the round-trip arithmetically
  before writing the assertion (`wx/12f + 1024f` is exactly `j+0.5`, and `Utils.RoundToInt` rounds that up),
  and keep the two mappings as two differently named methods so a caller must choose.
  *(SeedLab `MinimapGeometry`, 2026-09-22; game-operations.md §7.1.)*
- **A "self-test against the BCL" can fail on cases that are not the code's business.** SeedLab's binary16
  decoder was checked against `(float)BitConverter.UInt16BitsToHalf(bits)` over all 65 536 codes and reported
  1 022 disagreements — every one a *signalling* NaN, which .NET quietens and the decoder passes through.
  IEEE-754 leaves NaN payload propagation to the implementation, and no cache pixel holds a NaN. Scope a
  conformance self-test to the domain that occurs, and say in the message which cases are excluded and why,
  or the next reader spends an hour on a non-bug.
- **Mono evaluates IL float arithmetic at DOUBLE precision; a C# port on .NET does not.** Mono's x64 JIT
  holds every floating-point evaluation-stack slot as `R8` and narrows only at a `conv.r4` or a store into
  a float32 local/argument/field/array element. RyuJIT tracks `TYP_FLOAT` and rounds each `add`/`mul` to
  float32. So a transcription that is instruction-for-instruction faithful to the IL can still be wrong
  wherever the IL does **two or more float ops in a row, or widens a float result with `conv.r8`, without
  an intervening narrowing**. Two confirmed instances in `WorldGenerator` (both invisible to the
  half-precision minimap ground truth, both caught by the `.db2` float32 location heights):
  - `GetDeepNorthHeight` / IL_0008–IL_0012: `GetBaseHeight(..) + 0.1f` is left on the stack, `dup`ed at
    IL_00d1 — one copy is narrowed by `stloc.s V_5` and feeds the detail add, the other is consumed by
    `conv.r8` at IL_00f9 **unnarrowed** and is what divides by `0.4f` for the sea-level squash. So the
    game computes `k` from the *double* `(double)base + (double)0.1f`. Every other biome function stores
    `GetBaseHeight`'s return into a float32 local immediately (`stloc.2`), which is why this is
    DeepNorth-only. Measured: 17 of 895 and 15 of 925 river-free DeepNorth location heights wrong by
    1–2 float ulps; reproducing Mono's behaviour repairs 22/22 and breaks 0 of 1 328.
  - `UnityEngine.Vector2::get_magnitude` / `Distance` / `SqrMagnitude` / `get_sqrMagnitude` /
    `op_Equality`: `x*x + y*y` is summed with plain `mul`/`add` and only then `conv.r8`'d for
    `Math.Sqrt` (or narrowed by a `stloc`), so Mono sums in double. Writing
    `(float)Math.Sqrt((double)(x*x + y*y))` in C# sums in float. All five members need the double
    interior; the two subtractions inside `Distance`/`op_Equality` genuinely ARE narrowed, because
    they store into float32 locals (IL_000e, IL_001c).
  **Outcome, confirmed 2026-09-22 (this replaces the predicted figures that stood here before).**
  Applying both fixes took SeedLab's world generator from 12 244/12 314 and 12 227/12 287 bit-exact
  `.db2` location heights to **12 314/12 314 and 12 287/12 287** — zero differing samples on both
  worlds, hold-out included — and the 2048x2048 minimap height sweep from 5 and 9 differing
  binary16 codes to **0 and 0** (Tier B → Tier A). Lake (111/119), river (140/161), stream
  (2135/2059), river-grid-cell (23 380/23 262) and river-point (675 579/677 094) counts were
  **byte-identical before and after**, and the biome map did not move a pixel: these are precision
  fixes, not geometry changes. Before the fixes the whole residual was invisible to the
  half-precision minimap (7 797 / 7 568 of 4 194 304 pixels wrong in float32, only 5 / 8 in
  binary16) — which is exactly why a minimap-only acceptance gate passes a port that has them.
  Rule: when porting IL, narrow where the *IL* narrows, and treat `conv.r8` applied to the result of a
  float `add`/`mul` as a marker that Mono never rounded that value to float at all.
- **ILSpy's C# silently invents a float local for a value that only ever lives on the IL evaluation
  stack, and that hides the bug above.** `GetDeepNorthHeight` keeps `GetBaseHeight(..) + 0.1f` on the
  stack across the whole Perlin block and `dup`s it; ILSpy prints one ordinary float local `num` used
  twice (decomp lines 1359 and 1370). A port transcribed faithfully from that C# is wrong, because in
  the IL only ONE of the two copies passes through a float32 store. **Decompiled C# is a lossy
  rendering of the numerics even when it is a perfect rendering of the logic.** Whenever a ported
  expression sits near a precision boundary, read the IL for `dup`, for a missing `stloc`, and for
  `conv.r8` on a float result — the decompiler cannot show any of the three.
  *(SeedLab adversarial numerics review, 2026-09-22; worlds `asdasdasd` and hold-out `testworldclaude`.)*


- **`UnityEngine.Color.black` is `(0, 0, 0, 1)`, and forgetting the alpha inverted a documented game
  rule.** `WorldGenerator.GetBiomeHeight` opens with `mask = Color.black;` and only three biome
  branches overwrite `mask`. Reading "only three branches touch it" as "the alpha is 0 everywhere
  else" is wrong by exactly one channel: the alpha is **1** everywhere else, and Deep North is the one
  biome that really writes 0 (`new Color(0, g, 0, 0)`). That single bit flips the meaning of
  `ZoneSystem`'s vegetation filter - `m_minimumVegetation > 0` excludes *Deep North*, not
  *everything outside Mistlands/AshLands* - and it changes what the surround check scores on a
  Deep North boundary. Both the SeedLab spec and this knowledge base carried the wrong version for a
  day. **When a ported `out` parameter has a default, write the default's real value down before
  reasoning about which branches overwrite it.** (Corrected 2026-09-23; see
  `valheim-worldgen\references\zones-locations-vegetation.md` section 3.3 filter 9.)

- **`Random.Range(float, float)` with `min > max` does not widen the interval, it narrows it - so the
  "location escapes its zone when maxRadius > 32" rule is off by a factor of two.**
  `ZoneSystem.GetRandomPointInZone` draws `Random.Range(-32 + r, 32 - r)`. For `r > 32` the bounds
  invert, but the native `Range` is the lerp `(1-f)*max + f*min`, which spans the interval either way
  round: the offset covers `+/-(r - 32)`. That is still inside the 32 m half-zone until `r > 64`.
  Measured in the SeedLab port on 20 000 draws: `r = 40` never left the zone, `r = 70` did. Anything
  built on the "> 32" reading (a warning list of at-risk prefabs, a special-case code path) fires on
  entries that are perfectly well behaved. (Corrected 2026-09-23.)

- **`AltBiomeWorldData.GenerateAltBiomes` never clears `AltBiome.Sectors`, which is a trap for any
  multi-world tool and invisible in the game.** It resets `ValidPlacementSectors` and
  `ValidPlacementSectorCombos` but not `Sectors`, while its quotas test `Sectors.Count` against
  `m_minAmountSpawned`/`m_maxAmountSpawned`. In the game a `main` scene load re-instantiates the
  `AltBiomeList` prefab, so every world starts with fresh `AltBiome` objects. A seed-sweep tool that
  reuses one set across worlds starves every run after the first (the count never drops below the max)
  **and** keeps the previous world's 2048x2048 grid alive through the stale `BiomeSector` references -
  about 70 MB per leaked world. Reset the per-world state explicitly; SeedLab does it in
  `AltBiomeRuntime.ResetForWorld`, called from `AltBiomeAssignment.Generate`.

- **`groundtruth\<world>-locations.csv` leaves the name blank for every prefab hash nobody has
  recovered yet - about five rows in six.** 10 293 of 12 314 rows on `asdasdasd` and 10 309 of 12 287
  on `testworldclaude` carry 136 distinct unnamed hashes. A validator that treats "name does not hash
  to the stored hash" as a failure fails on every one of them. Compare by **hash**, and check the name
  only on rows that have one (2 021 and 1 978 rows, 40 and 41 prefabs - all of which do match).

- **`cacheMinimapHeight` is binary16, so it is the wrong source for a land/water mask.** The half
  spacing at the 30 m water line is 0.015625 m, so a cell whose true height is within about 8 mm of
  30 m can land on the other side of `h >= 30f` once it has been through `Mathf.FloatToHalf`. Measured
  2026-09-23 over the full 2048x2048 12 m grid: **597 of 4 194 304 cells on `asdasdasd` and 637 on
  `testworldclaude`** classify differently from the exact float32 `GetBiomeHeight` returns. That is
  0.014 % of cells, but it lands almost entirely on coastlines, so it changes *connectivity*: the raw
  4-connected island-component count goes 9 866 -> 9 935 and 10 302 -> 10 383. The totals barely move
  (land 117.690 -> 117.604 km2 and 120.783 -> 120.691 km2; largest island 5.766 -> 5.763 km2) and the
  count of islands of at least 1 ha is **identical either way** (227 and 216) - which is exactly why a
  raw component count should never be published as a world metric. Derive a land mask from the
  float32; use the cache only as the bit-exact oracle for the heights themselves.

- **A straight line on a biome map is not automatically a mask artifact.** Valheim really does have
  straight, kilometre-long biome edges made by a Perlin-gradient degeneracy (valheim-worldgen,
  `world-generator.md` section 5.1) — which makes it tempting to explain every straight edge that way.
  When scanning for "seams" by counting biome changes along a row or column, the Ocean test
  (`baseHeight <= 0.02`) throws up peaks of the same order: a coastline that happens to run straight
  for a while. In seed -1772362158 the "vertical seams" at x = -1500 and x = +2444 are 25/28 and 20/23
  Ocean flips, not mask crossings, and the horizontal peak at z = -592 likewise. **Attribute the
  flipped predicate** (base height / mask0 / mask1 / mask2 / mask4 / a distance band) on both sides of
  the line before explaining it: mask-driven seams sit 10-17 sd above background and attribute >90 % to
  one mask, coastline coincidences are 4-6 sd and mixed. (2026-09-23, seam investigation.)

- **A dump file name is not a field.** In the 1.0.15 SeedLab dump, `goldens/worldgen-<seedHex>-menu.json`
  and `-world.json` differ by the **capture source** - a generator the dumper built at the main menu
  from a seed text, versus the one the loaded world was running. It is *not* `World.m_menu`: all three
  captures in that dump carry `"menu": false`, because a menu-mode capture still builds a normal
  non-menu generator. A reader that keys off the file name would construct the wrong generator
  (`m_menu: true` skips `Pregenerate` entirely and switches to the menu terrain) and then blame the
  port for the mismatch. Take `menu`, `seed` and `version` from the JSON body; the file name exists
  only so the two captures of one seed cannot overwrite each other. (Noticed 2026-09-23 while writing
  `SeedLab.GoldenCheck`.)
- **The number in the game's own log is a filtered count, and it is not "enabled".** `Loading: Done.
  ... locations: 183` is `m_locations.Where(m_enable && m_quantity != 0).Count()`. The 1.0.15 table
  actually holds **232** entries, **186** of which have `m_enable` and **200** of which have a
  non-zero `m_quantity`; 3 are enabled with quantity 0 and 17 carry a quantity while disabled, so the
  three numbers name three different sets and none of them is "the enabled ones". A tool that reports
  "186 enabled, 183 with quantity" has the second half backwards. Name the predicate every time -
  `enabled`, `in placement order`, `quantity != 0` - and count it from the loaded table rather than
  copying a manifest's own field, whose meaning you then have to remember. (The SeedLab dump's
  `locations.json` calls the 183 `enabledCount`, which is exactly the trap; it is documented as
  `enable && quantity != 0`. Cost time 2026-09-23.)

- **A contiguous-record scan of a shipped asset bundle is a lower bound, not a census.** This
  knowledge base recorded "vanilla 1.0.15 ships 28 AltBiomes" from a structural scan that found 28
  contiguous `AltBiome` records in `StreamingAssets\SoftRef\Bundles\d59cfac`. Reading
  `AltBiomeList.m_altBiomes` out of the running game found **32**: the scan missed `Mushroom`,
  `Lantern`, `Bones` and `Menhir`, which do not sit in that contiguous run. The scan was not wrong
  about what it saw, it was wrong about what it had seen *all* of - and a count with no upper bound
  reads exactly like a count with one. Treat a byte-pattern census as evidence that *at least* N
  exist, mark it **Unverified:** for the total, and settle it at runtime. (Corrected 2026-09-22 when
  the dumper's `altbiomes.json` landed.)
- **A component a prefab carries is not a component the player can use: check it is active.** Run 6 of
  the SeedLab dumper found `DN_Bossroom` carrying a `Teleport` whose caption `$location_dnbossroomnew`
  ("The Prison") would have renamed the place - but the door is inactive in the prefab, and nothing in the
  dump says what enables it. A naming rule counts only a door that is active in the hierarchy, has a
  `m_targetPoint` and a non-empty caption (2026-09-24).

## 10. Offline tools, CLIs and the local web UI

Lessons from building SeedLab (the repository these skills ship in, the **seedlab** skill). They are about tools that
answer questions from game data, not about the game.

- **`double.TryParse` / `float.TryParse` accept `"NaN"`, `"Infinity"` and `"-Infinity"` on .NET Core
  whatever `NumberStyles` you pass** - `NumberStyles.Float` does not exclude them, because since
  .NET Core 3.0 the special values are recognised unconditionally. A non-finite value then makes every
  downstream comparison false (`NaN > 0` and `NaN > 10500` are both false), so range checks *pass* it
  and the tool prints nonsense with exit code 0. Found 2026-09-23 in `SeedLab.Cli`: `vseed at <seed>
  nan 0` reported "biome Meadows, inside the 10500 m water edge", and `vseed map --zoom nan,0,2500`
  wrote a PNG headed "covers x in [NaN, NaN]", both exit 0. **Add an explicit
  `!double.IsFinite(d)` / `!float.IsFinite(f)` test to every numeric argument parser**; it is the only
  thing that rejects these. **Inside the game the rules differ again:** the game's Mono mscorlib accepts
  `NaN`/`Infinity`/`-Infinity` case-sensitively (so `nan` is refused where .NET 10 accepts it), treats
  only ASCII whitespace as white and formats `(-0.4f).ToString("0")` as `"0"`, not .NET's `"-0"` —
  vanilla-behaviour.md section 13. Parser tests that pass on .NET prove nothing about the game; run them
  on the game's own corlib too (environment.md, Toolchain).

- **PowerShell 5.1 corrupts two things when you test a console exe from it, and both look like bugs in
  the exe.** (1) An empty-string argument is dropped: `& $exe hash ""` reaches the program as *no*
  argument, so a tool that handles the empty seed text correctly still prints its usage message and
  exits 2. Run it through `cmd /c "..."` to pass a real empty argument. (2) Piping a native command
  into anything that stops early - `| Select-Object -First 3`, `| Select-String ... | Select -First 1`
  - closes the pipe and leaves `$LASTEXITCODE` at **-1**, which reads exactly like a crash in a tool
  whose documented exit codes are 0-4. Redirect to a file (`> out.txt 2> err.txt`), read
  `$LASTEXITCODE` immediately, and filter the file afterwards. (Both cost time 2026-09-23 while
  verifying `vseed`.)

### A prefilter that "can only accept" is still wrong if it accepts the wrong thing (2026-09-23)

SeedLab's search engine has a cheap accept-only probe that samples every 16th grid cell and accepts a
seed when the sample already witnesses every must-goal. The reasoning is sound - a witness on a subset
is a witness on the whole grid - but the code compared the probe's **cell count** against the goal's
threshold for the `present` metric, whose exact reader returns a 0/1 **indicator**. `present >= 2`
therefore accepted 4,983 of 5,000 seeds that the exact pass rejected all 5,000 of, and `vseed search`
and `vseed explain` disagreed about the same seed. An accept-only filter has to mirror the exact
metric's *semantics*, not just beat its threshold; a one-sided-bound argument about the sample says
nothing if the two sides are measuring different quantities. The audit test that catches this is
running the same range with and without prefilters and diffing the result sets - but only if the query
actually exercises the prefilter, so pick queries whose probe/gate counters are non-zero and check
those counters.

### "Impossible" static rejections must test the reachable end of the range (2026-09-23)

The same engine proves at query time that e.g. "Meadows area >= 400 km2" is impossible, because Meadows
is confined to 0..5,100 m from the centre and so covers at most ~82 km2 on the grid. For a
`between a .. b` goal it tested **b** against that cap, so "Meadows area between 1 km2 and
1,000,000 km2" - which every seed satisfies - was refused with "can never be satisfied by any seed".
A range goal is unsatisfiable only when its *low* end is out of reach. A static rejection is the one
kind of answer a user cannot check by running longer, so it has to be the most conservative code in the
tool, and `--no-prefilter` did not bypass it either.

### One JSON document means one, so make a flush idempotent (2026-09-23)

`vseed --json` buffers a JSON document and copies the buffer to stdout in `Out.Flush()`. `Program.Main`
flushes after every command, and `search` and `explain` also flushed before returning, so those two
commands printed the whole document **twice** and no JSON parser would read them. `Flush()` now
remembers how many bytes it has already emitted. If a class has both a "flush at the end" caller and
commands that flush themselves, the flush has to be idempotent or the duplication only shows up in the
commands that do both.

### `0` as a sentinel for "everything" hides a six-day job (2026-09-23)

`vseed search --all` deliberately makes the user ask for all 4,294,967,296 seeds out loud, and refuses
`--all` together with `--seeds`. But the seed budget used `<= 0` to mean "the whole range" internally,
so `--seeds 0` (and `--seeds -5`) silently started exactly that whole-space run with no warning. When a
sentinel value inside a plan object means "no limit", the CLI that fills it in has to reject the
sentinel explicitly.

### A strict `style-src` blocks the style ATTRIBUTE, and `style.cssText` writes one (2026-09-23)

SeedLab's local web UI sends `Content-Security-Policy: ... style-src 'self'` with no `'unsafe-inline'`,
which is exactly right for a page that must make no external request. Adding location markers to the
map meant building little legend swatches, and doing it with
`el.style.cssText = 'width:14px;background-image:url(' + canvas.toDataURL() + ')'` - which the browser
refused, silently, with only a console line to show for it. `cssText` and a `style="..."` attribute in
the HTML are both governed by `style-src`; **individual CSSOM property writes are not**, so
`el.style.backgroundImage = ...`, `el.style.width = ...` and `documentElement.style.setProperty('--x', v)`
all work under the same policy. The fix is to put the fixed rules in the stylesheet and set one
property from script, never to relax the policy for a decoration.

Two things made this cost more time than it should have. The browser's console **keeps violations
across a reload**, so the same six errors reappear after the fix and look like the fix failing - check
in a fresh tab. And a blocked style produces no exception: the element is simply invisible or unstyled,
which reads as a layout bug rather than a security header doing its job.

### A detached server started from a background shell dies with the shell (2026-09-23)

`nohup vseed serve ... & disown` inside a backgrounded Bash tool call looks like it worked - the log
shows "SeedLab is serving at ..." - and the socket is dead a moment later, because the whole process
group goes when the call is reaped. PowerShell `Start-Process -WindowStyle Hidden -RedirectStandardOutput`
survives, and is the way to keep a local server up across several tool calls.

### `GCMemoryInfo.MemoryLoadBytes` is 0 until the process's first GC (2026-09-23)

Measured on win-x64, .NET 10.0.12: at startup `GC.GetGCMemoryInfo()` returns
`TotalAvailableMemoryBytes = 66,095,271,936` and `MemoryLoadBytes = 0`, so "available = total - load"
reads as *the whole machine is free*. A forced gen0 collection (`GC.Collect(0, Forced, blocking: true)`,
0.4 ms) fills it in - 21,150,487,019 B in use on the same machine a moment later. Any memory guard that
sizes worker counts from a cold `GCMemoryInfo` will size them as if nothing were running.
`TotalCommittedBytes` and `HeapSizeBytes` are 0 at that point too, for the same reason. Also note
`MemoryLoadBytes` is machine-wide, and `TotalAvailableMemoryBytes` becomes the container/job limit when
one is in force. There is no BCL route to the **physical** core count on Windows or macOS (only
`/sys/devices/system/cpu/*/topology` on Linux), so report it as unavailable rather than dividing the
logical count by a guessed SMT factor. `SeedLab.Runtime.Hardware.HardwareProbe` does all of this.

### The "allocated per block up front" leak was a single-threaded collector (2026-09-23)

`vseed search custom --all --block-size 64 --threads 16` reached 7,599 MB of working set in 120 s and
was still climbing ~60 MB/s. The audit's hypothesis was that something was allocated per block up
front (4.29e9 / 64 = 67 M blocks). Measured: nothing is - blocks are claimed and emitted lazily, and
enumerating the 67,108,864-block plan costs nothing. What grew was the queue of finished-but-unwritten
blocks: 16 workers hand their hits to ONE collector thread that did `SeedText.Invert` (a lane-table
search, not a formatting call) and all the record formatting, so whenever most seeds pass it falls
behind, and nothing bounded the queue. Moving `Invert` onto the worker (it is a pure function of the
seed) and bounding the queue took the same command to **112 MB, flat**, and 14.6x more records written
in the same 120 s. The lesson is the ordinary one and it still cost an hour: **reproduce and measure
the growth before believing a plausible cause** - a producer/consumer queue with no back-pressure looks
exactly like a leak.

### Writing a Python patch script through a quoted bash heredoc mangles backslashes (2026-09-23)

In this environment `python - <<'EOF' ... EOF` does **not** deliver the script verbatim: `'\\'` arrives
as `'\'` (a syntax error), and a heredoc whose body contains certain quote runs fails earlier still
with `unexpected EOF while looking for matching`. Two separate patch attempts were lost to it. Write
the script to a file with the Write tool and run `python thescript.py`; it is also re-runnable when a
patch has to be adjusted.

### A `str.replace` with no `assert` is a patch that silently did nothing (2026-09-23)

A patch that added two fields to `Checkpoint.Save` printed "ok" and changed nothing, because one of its
several `replace` calls had no `assert old in s` in front of it. The failure only surfaced three runs
later as "resume: no kept-results snapshot". Every replacement in a patch script gets its own
`assert old in s, <which one>` - the assert is the test.

## A percentile table is only as good as the parameter it was measured with (2026-09-23, SeedLab)

The metric-truth study (`docs\studies\coastline-verdict.md` in SeedLab) reported "median island count at min_area **1 ha**:
137/126/119/119/121/120/112/141/128 across G12..G384". That series is the **10 ha** count. The 1 ha
median at G12 is **225**, measured with SeedLab's own code over 256 uniformly drawn seeds.

The cost was not the analysis - it was the shipped preset. `archipelago` carried
`island_count >= 120` with `min_area: 10000`, a threshold taken from the 10 ha table and applied to a
1 ha goal. The measured MINIMUM over 256 seeds is 189, so that must-have passed **256 of 256** and
filtered nothing while the preset's description claimed it selected for "many small islands". The
same class of error shipped twice more in the same file: `largest_island_area at_most 3,000,000`
scored 0 for 255 of 256 seeds because the smallest largest-island measured is 3,143,808 m2, and
`land_area between 80e6 and 130e6` scored 1.0 for all 256 because land area's entire measured range
sits inside the band.

**What to do instead.** A threshold in a preset or a doc carries the parameter it was measured with
(`min_area`, `radius`, `height`) and the grid, in the same sentence, or it is not a number. Before
shipping a must-have, evaluate it on a sample and record the pass rate: a must-have that passes
100 % of a sample is a description, not a filter. SeedLab's `QueryCheck` now refuses a query whose
must-goal is PROVABLY inert; it cannot catch the merely-measured cases, which is why the sample step
is not optional.

### A prefab ASSET's state is shared and already mutated (2026-09-23)

Writing the SeedLab dumper's prefab-child walk, the obvious plan was "load each location prefab, read its RandomSpawn array, that is the authored data". It is not. `location.m_prefab.Asset` is one shared object that `ZoneSystem.SpawnLocation` mutates for every instance of that location in the world, and two of its mutations are never undone:

- `RandomSpawn.Reset()` (called on every entry at the end of every spawn) is `SetSpawned(true)`, which sets that spawn's `m_OffObject` **inactive** and leaves it there. `Utils.GetEnabledComponentsInChildren` filters on `activeSelf`, so after the first spawn the array the game itself builds can be shorter than the authored prefab's - and the array length is the RNG draw budget.
- `RandomSpawn.Randomize` / `RandomObject.Randomize` write `Location.m_biome` on the asset the first time a biome-gated entry runs, and nothing clears it - so a field that looks like authored prefab data is actually a session-wide cache of one `GetBiome` sample.

What to do instead: capture **both** the filtered count and `GetComponentsInChildren<T>(includeInactive: true)`, record each object's `activeSelf`, and warn when they disagree, so a reader can tell authored data from session drift. Prefer a dump taken in a freshly started world. And never call a `Prepare()`-style method to get at what it computes - reproduce its query read-only, or the dump changes the game it is measuring.

The same rule bit in reverse for positions. `SpawnLocation` reads `child.transform.position` after temporarily zeroing the asset root, so the naive way to reproduce it is to zero the root too. Don't - compute `Inverse(rootRot) * (childWorld - rootPos)` instead. (`InverseTransformPoint` is *also* wrong here: it divides by the root scale, which the game keeps.)

## A verdict enum that only ever goes up cannot express a downgrade (2026-09-23, SeedLab)

SeedLab's query checker has a non-negotiable property: a DATA-STAMP mismatch must disable every
refusal. It was implemented by calling the ordinary `Warn(WarnRare, ...)` helper on each refused
goal - and that helper is `if (verdict > Verdict) Verdict = verdict;`, which is exactly right for
every other caller and a no-op for this one. `WarnRare` is not greater than `Refuse`, so the gate
ran, reported that it had run, and changed nothing.

Nothing caught it, because every ordinary run has matching stamps: the property had no coverage at
all. The test that found it doctors a copy of the shipped data with ONE hex digit of the assembly
hash changed and asserts the downgrade really happened.

**What to do instead.** A monotone "raise the severity" helper needs a separate, explicitly named
method for the one place severity comes down, and that place gets its own test. More generally: a
safety property whose trigger never occurs in normal operation is untested by definition - construct
the trigger.

## A raw `GameObject.name` is not an identity, and a false "not found" is the worst answer (2026-09-23, SeedLab dumper)

The dumper's child walk indexed prefab children by raw `GameObject.name` and let a reader ask "does
this prefab contain `piece_maypole`" with an exact match. Unity appends ` (1)` to duplicated siblings
and `(Clone)` to instantiated ones, and Valheim's authored prefabs are full of both, so a child the
game itself calls `piece_maypole` can be named `piece_maypole (1)` in the hierarchy. The query would
have returned nothing - and nothing reads as *"this game has no maypole"*, so the user stops looking.

The game never does this: everything that asks "what prefab is this" goes through
`Utils.GetPrefabName`, which truncates at the first `(` or space. **Normalise with the game's own
method, keep the raw spelling beside it, and match on the normalised one.** More generally: rank wrong
answers before choosing a representation. A false negative that reads as a confident absence is worse
than a missing field, because a missing field makes the reader ask and a confident absence does not.

## Walking only the container you first thought of is a silent, complete-looking answer (2026-09-23, SeedLab dumper)

The same walk covered every LOCATION prefab, correctly and exhaustively, and was reviewed as
"captures what it claims". It could still never answer the question it was built for, because a
Valheim location with an interior contains a `DungeonGenerator` and nothing else - the contents are
ROOM prefabs out of `DungeonDB`, and nothing in a location prefab's child tree names them. A complete
walk of the wrong container produces an empty result that looks exactly like a complete walk of the
right one.

**What to do instead:** before trusting a negative, enumerate where the thing could live and check the
list against what the code actually walks. And make the tool say so itself: the fix was a `search.json`
that always writes `found: true|false`, publishes what it did and did not search, and flags a NOT FOUND
as `inconclusive` when any source was skipped or failed. A tool that can only report presences will
report an absence by staying quiet, and quiet is indistinguishable from broken.

## A warning that fires on healthy data trains the reader to skip warnings (2026-09-23, SeedLab dumper)

The child walk compared "RandomSpawns the game's query returns" against "RandomSpawns present, inactive
included" and warned when they differed, to catch session drift from `RandomSpawn.Reset()`. The
"present" side used `GetComponentsInChildren<T>(true)`; the game's side is
`Utils.GetEnabledComponentsInChildren`, which excludes a component mounted on the **root transform**
(`componentsInChildren[i].transform == root.transform`) as well as any disabled one. So every clean
prefab with a root-mounted RandomSpawn was reported as drifted and the user was told to distrust a good
dump.

**When comparing your count against the game's, reproduce every clause of the game's filter, not just
the one you are interested in** - and pin the clause you depend on with an IL check, not a comment.
A diagnostic that cries wolf is worse than no diagnostic.

## Matching a method by name and arity can still pick the wrong overload (2026-09-23, SeedLab preflight)

The preflight counts calls inside a shipped game method to pin arithmetic that member names cannot
protect. Its matcher took the first method with the right name, which is fine until the target is
overloaded: `DungeonGenerator.PlaceRoom` has a 3-argument overload that touches no RNG and a
5-argument one that owns the whole room stream, and `Utils.GetPrefabName` has **two one-argument
overloads** where the `GameObject` one is a single forwarding call to the `string` one. Asserting
"GetPrefabName contains one `IndexOfAny`" against the forwarding overload fails; asserting
"PlaceRoom reseeds three times" against the 3-argument one passes for the wrong reason.

**Disambiguate by argument count AND first parameter type, and prove it in the self test** - the
preflight now probes a non-existent overload and requires the check to fail rather than fall back to
one that exists.

## A dump's own timestamp is UTC, so its files can look a day newer than its stamp (2026-09-23, SeedLab)

`SeedLab.Dumper`'s `GameInfo.Stamp` writes `dumped={DateTime.UtcNow:yyyy-MM-dd}` into the DATA-STAMP
of every file it produces. On this machine (UTC+3) a run at **01:48 local on 2026-09-23** is
**22:48 UTC on 2026-09-22**, so the shipped `data\1.0.15-59f53fb5\` says `dumped=2026-09-22` while
every file in it carries a local mtime of 2026-09-23 01:49. Two agents then wrote two different
histories of "when the dumper ran" from the same evidence, and a third concluded there had been two
runs.

**A date in a stamp and a date in a file listing are in different clocks; say which.** The identity
of a dump is its two SHA-256s (`assembly_valheim.dll` and the file's own), never its date - and
`BepInEx\LogOutput.log` is the tiebreaker, because it records the session in local time with the
plugin's own `ARMED` / `DONE` lines.

## One worker computes a whole block, so a short run looks like a broken thread pool (2026-09-23, SeedLab)

`vseed search` splits work into blocks and hands a whole block to one worker, because the block is
also the resume granularity. A run of 400 seeds with the default 256-seed block is **two blocks**, so
two of eight workers had anything to do: measured **2.0 seeds/s**, against **7.3 seeds/s** for the
same preset and machine with `--block-size 16`. Nothing is wrong with the pool, and no amount of
`--threads` helps.

**When a work unit is also a checkpoint unit, its size is a scheduling decision, not a tuning knob** -
and a short run is exactly where the default is worst. Size the block to ~30-60 s of work, say so in
the plan the tool prints, and measure a rate over a run long enough to fill every worker at least once
before quoting it.

**It bit again the same day, in the pass whose whole purpose was to settle the numbers.** The first
attempt at SeedLab's measurement matrix left the default block size in place and reported **1.04x**
scaling from 1 to 8 workers at G12, where the truth with enough blocks is **6.36x**; it also made
`--dry-run` look 7-11x over-optimistic on the fine-grid tiers, where the honest figure is 1.07x. The
rule that fixed it: **`--block-size` such that every worker gets at least 8 blocks, never coarser than
the tool's default**, i.e. `floor(seeds / (workers * 8))` clamped to `[1, default]`. Assert the
worker count the run *reports* as well, and publish the block size beside every figure.

**The same mechanism makes a wall budget overrun** (corrected 2026-09-24: this said "a floor", which
is false). A budget checked only when a worker is about to take a block cannot interrupt a block
already taken: `vseed search all-traders --budget 20s` at the default `--block-size 256` with 8
workers ran for **355 s** and evaluated 2,048 seeds. But it is not a floor - a worker whose first
check comes after the wall never claims anything, and `--budget 0.001s` stopped at **0 seeds** (which
then reported "100 % coverage" until the coverage line was fixed). If a tool offers "stop after 20
s", either check the clock inside the block or state the overrun bound in the plan it prints.

**Fixed in SeedLab on 2026-09-24:** with no size given, the block size is chosen so every worker gets
at least 4 blocks (the 8 above was the measurement pass's own rule), the plan prints the arithmetic, an
explicit size is kept and warned about, a resumed run adopts the checkpoint's size, and the budget line
states the overrun rather than a floor.

## A "did it run" flag is not coverage; only a count is (2026-09-23, SeedLab dumper)

`SeedLab.Dumper`'s `search.json` exists to make an absence loud: it marks a NOT FOUND *inconclusive*
whenever anything the sought name could have hidden behind did not complete. Its coverage test asked
the walks' boolean flags — `RoomWalkRan`, `LocationChildWalkRan`. But `RoomWalk.Run` sets
`RoomWalkRan = true` after its loop, and an empty `DungeonDB.GetRooms()` falls straight through that
loop. A run that searched **zero** room prefabs therefore published **full** coverage, `inconclusive`
stayed false, and the user got a confident "NOT FOUND anywhere this dump can reach" out of a search
that never happened — the single worst answer the file can give, after a whole game launch.

**Decide coverage from the number of items a source actually contributed, never from whether its code
was entered.** Keep the flag for the report if "never entered" and "entered and produced nothing" are
worth telling apart — but gate nothing on it. The same trap applies to any "walked / loaded / failed"
triple: quote the *loaded* count in the verdict, not the attempted one.

## A prefab's own name is not in its child index, so searching only children is a guaranteed miss (2026-09-23, SeedLab dumper)

The same dumper's child walk skipped the asset root (`if (t == null || t == root) continue;`) because
`childNames` is documented as excluding it. That is correct for the index and wrong for the search that
shares the pass: a sought name that IS a room or location prefab, rather than something inside one,
could never match. Location names happened to be reachable through `ZoneSystem.m_locations`; **room
prefab names were reachable through nothing at all.**

**When a search rides along on an index pass, the index's exclusions become the search's blind spots.**
List them deliberately.

**And the mirror image of the same mistake:** a limitation that is true of *every* run does not belong
in the list that flips an "inconclusive" bit. The same dumper resolves `RandomObject` options one level
deep; nested ones are non-zero in almost any real dump, so putting that count in `notSearched` would
have marked every verdict in every dump inconclusive and made the word meaningless. Split the two
lists: **per-run defects** decide the verdict, **standing boundaries** are reported beside it.


## `new MyStruct()` with an optional-parameter constructor silently emits `initobj` - the constructor never runs (2026-09-23, SeedLab dumper)

`SeedLab.Dumper.RandomGuard` is `internal readonly struct RandomGuard : IDisposable` whose only
constructor is `RandomGuard(int? initState = null)`: it captures `UnityEngine.Random.state` into a
readonly field, and `Dispose()` writes that field back. Every call site was written
`using (new RandomGuard())` or `using (new RandomGuard(seed))` and reviewed by eye as correct.

It is not. For a **struct**, the implicit parameterless constructor is always a member, and C#
better-function-member resolution prefers a candidate that needs **no default-argument substitution**
over one that does. So `new RandomGuard()` binds to the implicit default constructor, and Roslyn emits
`ldloca.s V_n; initobj RandomGuard` - **no `call .ctor`, no `get_state()` at all**. The struct is
all-zeros, so `Dispose()` executes `UnityEngine.Random.set_state({0,0,0,0})`. Verified by Mono.Cecil
over the shipped DLL: `ModeAssets/<WalkPrefabs>d__11::MoveNext` IL_014d is `initobj`, IL_0290 is
`constrained. callvirt IDisposable::Dispose`, and `RandomGuard::Dispose` IL_0006 is
`call UnityEngine.Random::set_state`. The nine sites that pass an argument emit
`call RandomGuard::.ctor(Nullable<int>)` correctly - only the six argumentless ones are `initobj`.

**Why it was invisible.** All-zeros is a *fixed point* of Unity's xorshift128 (`Next()` returns 0
forever), so the game does not crash or look wrong - it just offers `"aaaaaaaaaa"` in the new-world
dialog forever, because `World.GenerateSeed()` is
`alphabet[Random.Range(0, 59)]` x10 and `Range` is `min + Next() % (max-min)`. The plugin's own
"perturbed UnityEngine.Random" tripwire (`NoDrawCheck.Around`) could not see it either: it compares
before/after **inside** its own body, and the zeroing happens in the guard's `Dispose`, outside every
`Around` - and once the state is zero, before == after trivially.

**Rules.**
- **A guard that must run its constructor cannot be a struct at all.** Factories on a struct do NOT
  fix this: `new S()` compiles for a struct *however its constructors are declared*, because the
  implicit parameterless one is always a member. Only a **reference type** turns the argumentless form
  into a compile error - `error CS1729: 'RandomGuard' does not contain a constructor that takes 0
  arguments`. That, verified by compiling it, is the fix that shipped on 2026-09-23: `sealed class`,
  private constructors, `RandomGuard.Capture()` / `RandomGuard.Seeded(int)` factories. (An earlier
  draft of this entry said a static factory on the struct would be enough. It would not have been.)
- **Never give a struct constructor an optional parameter** when the constructor does work the type
  depends on - the optional parameter is what makes the implicit constructor win overload resolution.
- **A save/restore guard must be checked in IL, not in C#.** The preflight now fails if the built DLL
  contains `initobj <the guard type>` anywhere, or if the guard type is a value type at all.
- More generally: **reading the C# of a `using` block cannot prove the constructor ran.** Reviewing
  those six sites by eye, twice, did not find this; one Mono.Cecil pass did in a minute.


## A plugin that saves and restores `UnityEngine.Random` can kill the generator for the whole session (2026-09-23, SeedLab dumper)

The class of bug above, stated as the rule any BepInEx plugin should follow.

**The symptom to recognise:** the new-world dialog offers the seed **`"aaaaaaaaaa"`**, every time, and
nothing else looks wrong. That string is a *proof* of an all-zero `UnityEngine.Random` state, not a
coincidence. `World.GenerateSeed()` is ten characters of
`"abcdefghijklmnpqrstuvwxyzABCDEFGHIJKLMNPQRSTUVWXYZ023456789"[Random.Range(0, 59)]`, and
`Random.Range(int,int)` is `min + (Next() % (uint)(max - min))` (UnityPlayer.dll rva 0x00054900,
confirmed against 276 recorded traces). Ten `a`s means `Next() % 59 == 0` ten times running.

**The mechanism:** Unity's generator is **xorshift128** with shifts 11/8/19, and `{0,0,0,0}` is a
**fixed point** of it - with `s0..s3` all zero, `Next()` returns 0 forever. So one bad write of a zero
state is permanent for the process: every ambient draw dies at once - new-world seeds, effects, piece
rotations (`RandomPieceRotation.Awake`), plant growth times (`Plant.GetGrowTime`), rune-stone text
(`RuneStone.GetRandomText`), weather and wind (`EnvMan.UpdateEnvironment`/`UpdateWind`). Nothing
crashes. Worse, the game's own per-frame save/restore in `EnvMan` faithfully preserves the zeros, and a
plugin's own "did this block perturb the stream?" tripwire reads `before == after` and stays quiet.

**The rule: never write a `Random.State` you did not verifiably capture.** In practice:
- Route every `Random.state = ...` in the plugin through ONE function, and have it refuse an all-zero
  value: log an ERROR naming the call site and re-seed from a non-deterministic source instead.
- The zero test must not be able to *skip* the write. `Random.State`'s four `s0..s3` are private
  `[SerializeField] int`s, so reading them needs reflection, and reflection can throw. Test with
  `state.Equals(default(UnityEngine.Random.State))` - four blittable ints, so `ValueType.Equals` takes
  the bitwise path - and keep the reflective read for the log message only.
- Check the **capture** too, not only the restore. If `Random.state` already reads as zeros when the
  guard is taken, something else has already killed the generator; refuse to run and say so.
- Re-seeding always escapes the fixed point: `InitState(s)` puts `s` in `s0` and derives the rest, so
  even `InitState(0)` gives `[0, 1, 1812433254, 1900727103]`
  (SeedLab `data\1.0.15-59f53fb5\goldens\natives-random.json`, item D4).


## Field coverage is a diff against the type's serialized fields, never against what a feature needed (2026-09-23, SeedLab dumper)

The dumper recorded **10 of `DungeonGenerator`'s 24 public instance fields**, because every one of the
10 had been added when some feature asked for it. Nobody ever compared the list against the type.
(Counted by Cecil over `assembly_valheim` `59f53fb5…`: `m_algorithm`, `m_minRooms`, `m_maxRooms`,
`m_minRequiredRooms`, `m_requiredRooms`, `m_themes`, `m_addBaseSeedToRandomSpawn`, `m_campRadiusMin`,
`m_campRadiusMax`, `m_useCustomInteriorTransform` were in; the record's other JSON members are derived
from the generator's transform and are not fields, which is what made an eyeball count read 14.)

The **14 missing** fields are `m_alternativeFunctionality`, `m_doorTypes`, `m_doorChance`, `m_maxTilt`,
`m_tileWidth`, `m_gridSize`, `m_spawnChance`, `m_minAltitude`, `m_perimeterSections`,
`m_perimeterBuffer`, `m_zoneCenter`, `m_zoneSize`, `m_generatedSeed` and `m_originalPosition`. Thirteen
are now captured and `m_generatedSeed` is waived (it is `GetSeed()`'s runtime output, not an input).
All of them are read by the generator, and the ones with a C# initialiser **all initialise to
non-zero** — `m_maxTilt` 10, `m_minAltitude` 1, `m_perimeterBuffer` 2, `m_zoneSize` (64,64,64),
`m_tileWidth` 8, `m_gridSize` 4, `m_spawnChance` 1, `m_doorChance` 0.5. So an offline reader that filled the gaps with
zeros (the natural thing for a missing JSON field) would have reproduced a generator the game never
runs — no exception, no empty output, just a plausible wrong answer. `Room.m_connections` was missing
outright, and that is the entire Dungeon algorithm. It cost a whole dump cycle: the user has to launch
the game for every dump.

**What to do instead, for any port or dump of a game type:** decompile the type, list its public
instance fields, **diff that list against what the dump writes**, and waive each remaining field **in
code**, with the reason it cannot matter. A waiver you have to write down is a waiver someone can check;
a field nobody listed is invisible.

`tools\SeedLab.Dumper\preflight.ps1` now enforces exactly that with a Mono.Cecil scan (its "field
coverage" section) over `DungeonGenerator`, `DoorDef`, `Room`, `RoomConnection`, `RandomSpawn`,
`RandomObject` and `ObjectEntry`: every serialized field must be read or named in the waiver table, and
a **stale waiver** — one naming a field the type no longer has — fails too. It was proved to fire by
planting two violations.

**The gate itself needed the same scepticism, found by adversarial review the day it landed.** Three
gaps between what it claimed and what it checked:

- **Any `FieldReference` operand counted, whatever the opcode** — `stfld`, `stsfld`, `ldsfld` and
  `ldtoken` all satisfied a gate whose header said "read". Filter on `ldfld`/`ldflda`.
- **`IsPublic && !IsStatic` is not "serialized", in both directions.** `[NonSerialized]` is
  `FieldAttributes.NotSerialized` — a field **flag**, not a custom attribute, so a scan that looks in
  `CustomAttributes` for it finds nothing and reports zero. The gate certified `Room.m_placeOrder` and
  `Room.m_seed` as covered while waiving `RoomConnection.m_placeOrder` *for being* `[NonSerialized]`.
  Resolution here: the claim was widened to "public instance field", which is what the scan actually
  tests and is the stronger set — the dump does record those two, as the AUTHORED values. In the other
  direction, `[SerializeField] private` fields are serialized and fall outside a public-only filter;
  there are none on these seven types in 1.0.15, so the gate now FAILS if one ever appears.
- **Reachability is not modelled.** A read in dead code satisfies it. The gate proves the IL *mentions*
  the field, not that the value reaches the dump — the script now says so in its own "what it proves"
  list rather than implying more.

**A new gate needs its own `-SelfTest` probe the day it lands**, or a typo in its lookup reads as a
clean pass. The free negative control when extending an existing plugin is **the retired build**:
`preflight.ps1 -Plugin _retired\<old>\X.dll` made this gate name all 13 unread `DungeonGenerator`
fields, 3 `DoorDef` and 4 `RoomConnection` fields — proof it fires on a genuinely unread field and not
only on a planted waiver.

Two neighbours of this rule, same file: **"A 'did it run' flag is not coverage; only a count is"** (a
walk that entered its loop zero times published full coverage) and **"Walking only the container you
first thought of"**. All three are the same failure — coverage asserted from the wrong denominator.

## A derived query is a different query, and anything keyed on its hash moves with it (2026-09-23, SeedLab funnel)

SeedLab's funnel strategy runs a first stage over a **derived** query - the original with its expensive
goals removed - to find which seeds are worth the expensive second stage. The derived query kept the
original's `search` block verbatim, including its order and range, and a comment in the code said in as
many words that stage one "must walk the same seeds in the same order".

It did not. `ScanPlan` derives its Feistel permutation key from the **query hash** when `search.key` is
null, and a query with different goals hashes differently. So stage one walked a different sample of the
2^32 space than the run it was standing in for. The funnel returned 14 matches; the same query run
ordinarily returned 16; and the two sets shared **not one seed**. Every one of those 14 was a real match
- to a question nobody had asked.

**Why it is worth an entry.** The failure does not look like a failure. There is no exception, no empty
output, no obviously wrong value - just a plausible result set of plausible seeds. It was caught only by
an end-to-end comparison against the same query run three ways (the funnel, an ordinary run, and
`--no-prefilter` audit mode), which is the contract that already existed for the tiered evaluator and
which nobody had thought to apply to a *staged* one.

**What to do instead:** when a pipeline derives a second query from a first, give the derived run the
ORIGINAL run's plan rather than trusting it to rebuild an identical one. Copying the inputs a plan is
built from is not the same as copying the plan, because a plan can also be built from the identity of
the thing asking. And mutating the derived query's key in place is not the fix either when the two
queries share their `search` block by reference - that edits the caller's object.

The regression test is the invariant, not the symptom: assert that an unconstrained derived plan
DIFFERS (so the hazard is real and the test is not vacuous) and that the constrained one agrees index
for index.

## Two QA agents sharing one scratch directory silently undo each other's fixtures (2026-09-23)

**What happened.** A refutation run injected a doctored `constraint-atlas.json` into a copied
`data\<version>-<hash>\` under the shared scratch path
`scratchpad\qa\verify-data-integrity\`, ran `vseed search`, and got a clean exit 0 - an apparent
non-reproduction of the defect under test. The file had in fact been restored to pristine between the
patch and the run: a CONCURRENT QA agent was using the same directory and had re-copied the whole
`data\` tree (mtimes 19:07:24 across every file) and rewritten `count-sample.json` at 19:08:07. The
SHA-256 taken immediately after patching and the SHA-256 taken after the run differed, which is the
only reason it was caught.

**Why it bites.** A restored fixture produces a PASSING run, not an error - exactly the shape of a
successful refutation. Nothing in the tool's output says which bytes it read.

**What to do instead:** never run a fixture-mutating experiment directly in a scratch path a workflow
may hand to more than one agent. Create a private subdirectory of it, copy the fixture there, and
re-hash the mutated file *after* the run and compare it with the hash taken *before* - treat any
difference as "this run proves nothing" rather than as a result. `ls -la --time-style=full-iso` on the
fixture directory shows a foreign restore at a glance (one common mtime across every file).

## Testing Ctrl-C: the "ignore Ctrl-C" flag is inherited, so the test silently does nothing (2026-09-23)

**What happened.** Verifying a claim that `vseed search` has no Ctrl-C handler during funnel stage one.
The harness launched vseed and sent it `GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0)`, first calling
`SetConsoleCtrlHandler(NULL, TRUE)` so the *signalling* PowerShell would not kill itself. vseed ignored
the event completely and ran to the end - which looks exactly like "the handler works, claim refuted".
It was the opposite: the ignore-Ctrl-C flag set by `SetConsoleCtrlHandler(NULL, TRUE)` is INHERITED by
every process created afterwards, so vseed was started already deaf to Ctrl-C.

**What to do instead:** create the child process FIRST, then set the ignore flag in the parent, then
send the event. Other requirements for this test on Windows: `CTRL_C_EVENT` can only be sent to process
group 0 (all processes on the console), so the target must share a console with the signaller and must
not be on the agent's own console - launch a hidden `powershell.exe` via `Start-Process -WindowStyle
Hidden` (that gets its own console) and do the launching and signalling inside it. Use
`[Diagnostics.Process]::Start` with redirected streams rather than `Start-Process -PassThru`, whose
`.ExitCode` came back empty; the real exit code is the whole evidence (`-1073741510` = `0xC000013A`
`STATUS_CONTROL_C_EXIT` means no handler ran, `0` means the handler ran).

**The control that makes the result mean something:** send the same event at a moment when the handler
IS installed. Same query, same machine, Ctrl-C at 30 s instead of 10 s: exit 0, "stopping at the next
block boundary", results and a resume point on disk. Without that control, a hard kill is just as
consistent with a broken harness as with a missing handler.

## `GameObject.activeInHierarchy` is always false on a prefab asset (2026-09-23, SeedLab dumper)

A prefab loaded from a bundle is **not in a scene**, so Unity returns `false` from `activeInHierarchy`
for every node of it. `activeSelf` still works, and the game's own test is
`Utils.IsEnabledInheirarcy(go, root)` - it walks `activeSelf` up to the root, and throws when `go` is
outside `root`'s subtree, so wrap it.

**Measured.** SeedLab's occupant walk recorded `activeInHierarchy: false` for all six occupants in the
whole dump - Haldor, Hildir, the Bog Witch, Kvastur, the Frozen King and the Seeker Queen - while the
SAME `locationchildren.json`, three fields away, reported `enabledCount` 1/1, 2/2 and 2/2 for the same
GameObjects via `Utils.IsEnabledInheirarcy`. The field's own doc said "an inactive creature is a prop,
not a spawn", so a consumer filtering on it would have discarded every creature and both traders - the
silent "not found" that dump exists to prevent.

**It then justified a second wrong claim.** Because the bosses "looked inactive", the walk's comment
asserted that an altar's boss is inactive on the prefab. It is not there at all: the five classic
altars (`Eikthyrnir`, `GDKing`, `Bonemass`, `Dragonqueen`, `GoblinKing`) carry **no `Character`
whatsoever** - `characters: []` with `occupantsCaptured: true` - and the boss is
`OfferingBowl.m_bossPrefab`, a reference to a different prefab. Only `DN_Bossroom` and one Mistlands
room hold a boss as a child, and for those the game's own test says ENABLED.

**On an uninstantiated prefab use `activeSelf`, or the game's helper. `activeInHierarchy` means
nothing there** - and a false value that looks like data will be believed and built on.

## A schema generator that silently drops a field proves nothing about that field (2026-09-24)

SeedLab's `gen_schema.py` builds the strict JSON schemas from the C# DTOs with a regex over
`public <type> <name>;`. The pattern was `([{w}[]?.]+)`, which matches no angle bracket -- so
`public Dictionary<string, string>? translations;` simply did not match, the field was not in the
generated schema, and the strict loader that exists to refuse an incomplete file **had nothing to say
about the one field that carried 6,258 strings**. It did not fail; it passed, for a field it had never
heard of.

This is the same failure mode as the dumper's field-coverage gap, one layer up: a check whose input is
derived from the thing being checked can only verify what it managed to parse. The fix is both halves
-- widen the pattern, AND diff the regenerated output line by line before trusting it, because a
generator that silently dropped one field will silently drop the next one too.

The widened pattern allows a space only INSIDE the angle brackets. The obvious "just allow spaces"
would make `public static int Count;` parse as type `static int`, field `Count` -- a new silent defect
in the fix for a silent defect.

Related, and worth knowing before designing around it: `StrictJson` **skips** JSON members it has no
schema field for, and `MaxFields` bounds the DECLARED schema rather than the object being read. So a
free-form map loads fine as a `Scalar` field; the reason the translations were empty was never a size
limit.

## A RuneStone names the place it REVEALS, not the place it stands in (2026-09-24)

Looking for player-facing location names in the dump, `RuneStone` looks like the richest source: 23 of
186 location prefabs carry one (27 stones in all), against 4 prefabs with a `Location.m_discoverLabel`.
Three of its fields are traps.

- **`m_locationName` is the location to DISCOVER**, and it is a raw prefab name, not a token --
  `RuneStone.Interact` passes it to `Game.DiscoverClosestLocation(m_locationName, pos, m_pinName, ...)`.
  **`m_pinName` is the map-pin caption the game writes for THAT location**, not for the host. A stone
  inside location X routinely names a different location Y: `StartTemple` holds five `BossStone_*`, one
  per boss. Captioning a host by its stone would call `StartTemple` "Eikthyr".
- It happens that all 7 stones with a non-empty `m_locationName` name their own host, which makes a
  self-reference test look exact. **It is a coverage artifact, not a rule** -- `Vegvisir` is the
  component that systematically points elsewhere (23 locations hold one; `SwampRuin1`/`SwampRuin2` both
  hold `Vegvisir_Bonemass`, six Plains ruins hold `Vegvisir_GoblinKing`), and dumps up to run 5 did not
  walk it, so the counterexamples are simply absent from those files. (The dumper reads Vegvisirs from
  run 6, 2026-09-24 - `vegvisirs[]` in `locationchildren.json` / `roomchildren.json` - and a consumer
  must join them on `locations[].locationName`, never on the host.)
- **`m_name` and `m_label` name the OBJECT, not the place.** Across all 27 stones `m_name` takes exactly
  three values -- "Runestone" (21), "Sacrificial Stone" (5), "A mysterious text" (1) -- and `m_label` is
  a journal heading ("Lore: Drake"). Adopting either as a place name captions
  `Mistlands_DvergrBossEntrance1` as "A mysterious text".

The rule the whole episode encodes: **a name field on a component names the component unless the code
says otherwise.** Read the `Interact` method before deciding what a string is about.

## A hand-rolled JSON writer that reflects over public fields emits `{}` for a Dictionary (2026-09-23)

SeedLab's dumper writes JSON by reflecting over an object's public **fields**. `Dictionary<K,V>` has
none, so `WriteObject` fell through and emitted `{}` - shipping a 6,258-entry localization table as
`"count": 6258, "translations": {}` while the log said `localization: 6258 strings in English`. The
count was real; the file was empty; nothing complained.

The fix is both halves, and the second matters more: an `IDictionary` case, **and** making "no public
fields" a loud throw instead of an empty object. An empty record where data should be is
indistinguishable from a record that really was empty, and a writer that guesses silently will do it
again for the next unsupported type. A third guard sits behind them - the dump reads the file back and
compares the entry count against what it claimed.

## Widening a shared "blank the arrays" helper changes what every caller does (2026-09-24, SeedLab dumper)

To make a prefab that never loaded write `[]` instead of `null` for its name arrays, `ChildWalk.Blank()`
was extended to empty them. But `Discard()` - the wipe after a throw LATER in the walk - also calls
`Blank()`, after `ReadOccupants` had already filled those arrays and set `occupantsCaptured = true`. The
entry would have been written "captured, and empty": `occupantsCaptured: true, offeringBowls: []`,
which the naming layer reads as "this altar has no boss" - the exact wrong answer the flag exists to
prevent, and silent, because the loader trusts the flag. Adversarial review caught it before the dump
ran (a Mono.Cecil scan for every store to `*Captured` and to the arrays, per method, against the
previous build).

**Do instead:** reset a capture flag and the array it vouches for in the same place, or not at all -
here a separate `BlankNames()` called only where nothing has been captured yet. **Before widening any
helper, list its callers** and check each one still keeps flag and data consistent.

## `vseed search --seeds N` with N below workers x block size leaves workers idle (2026-09-24)

A block is the unit of work and ONE worker computes a whole block, so `--seeds 512` at the default
block size of 256 is 2 blocks: 2 of the 8 workers get work and the rest sit idle. Two calibration
samples ran at 1.3 seeds/s; the same query with `--block-size 16` ran at 4.2. The run output prints "512
seeds in 2 blocks of 256" and a warning about kill granularity, but nothing says six workers did
nothing - a rate read from such a run understates the query by up to the worker count.

**Do instead:** never quote a rate from a run with fewer blocks than workers. **Fixed in the tool the
same day:** SeedLab now sizes blocks automatically when none is given (at least 4 per worker, ceiling
256), prints why, warns about an explicit size that idles workers, and labels a measured rate "(N of M
workers had work)" whenever some had none - so this trap now announces itself. The lesson stands for
any tool whose unit of work is also its unit of scheduling.
