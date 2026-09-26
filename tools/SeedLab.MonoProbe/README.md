# SeedLab.MonoProbe - the game's arithmetic, run on the game's own runtime

Valheim's C# runs on the Mono runtime Unity ships (`MonoBleedingEdge\EmbedRuntime\mono-2.0-bdwgc.dll`),
and Unity starts it with the option `-O=-float32` (the string sits in `UnityPlayer.dll`, next to
`mono_jit_parse_options`). With that option Mono keeps every float value on the evaluation stack as a
double and rounds to float only where the IL stores or converts one. .NET, which SeedLab runs on, rounds
every float operation to float. So a line of the game's C# can give a different answer under .NET when it
does two or more float operations in a row.

This folder runs pieces of the game's IL on the game's own runtime, outside the game, to settle such
questions without launching Valheim:

| file | what it is |
|---|---|
| `MonoHost\` | a 64-bit .NET program that loads the game's `mono-2.0-bdwgc.dll` in place and runs a .NET Framework program on it, optionally with a Mono option |
| `RoundingProbe.cs` | reads the IL of `Utils.FloorToInt`, `Utils.RoundToInt` (assembly_utils) and `AltBiomeWorldData.WorldSpaceToMapSpace` (assembly_valheim) out of the game's DLLs, re-emits it byte for byte and runs it on inputs a hair from a boundary; also the call-site shapes of `ZoneSystem.GetZone` and `Minimap.WorldToPixel` |
| `run-rounding-probe.ps1` | builds both into `%TEMP%\seedlab-monoprobe` and runs the probe with and without `-O=-float32` |

```powershell
.\tools\SeedLab.MonoProbe\run-rounding-probe.ps1
.\tools\SeedLab.MonoProbe\run-rounding-probe.ps1 -ValheimDir "D:\Steam\steamapps\common\Valheim"
```

It looks for Valheim only in `SEEDLAB_VALHEIM_DIR` and in the folders above SeedLab (SeedLab inside the
game folder). Anywhere else, name the game folder with `-ValheimDir`, as in the second line.

Needs the .NET SDK and the .NET Framework's C# compiler (`%WINDIR%\Microsoft.NET\Framework64\
v4.0.30319\csc.exe`, present on every Windows 10/11). It reads the game folder and writes nothing there.

**What it established (2026-09-26, Valheim 1.0.16, runtime SHA-256 `35FD9D80...A028`).** With
`-O=-float32` the three helpers compute in double: `FloorToInt(-0.0001f) == -1`,
`RoundToInt(100.4999f) == 100`, `WorldSpaceToMapSpace(-8190.00048828125f) == 340`, and
`Minimap.WorldToPixel`'s argument is computed in double and narrowed to float once, at the call. Without
the option the same IL gives .NET's answers (0, 101, 341). Those values are the expected values of
`tests\SeedLab.Tests -- rounding`.

**Why the real methods are not simply called:** `Utils` has a static constructor that reads
`UnityEngine.Application.persistentDataPath`, an engine call that does not exist outside the player, so
the first call throws. Reading a method's IL by reflection runs no type initialiser, and the IL of these
three methods contains only constants, so it can be re-emitted exactly (the probe checks the bytes).

**What it cannot show:** that the running game really passes `-O=-float32`. That is inferred from the
string in `UnityPlayer.dll` and from two in-game measurements that only the double reading fits (the
alt-biome sector centres, `SeedLab.Locations.BiomeGrid.MapSpaceToWorldSpace`, and the Deep North
heights). A dumper run in the game could settle it directly.
