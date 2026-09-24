# Seeds, World Files and Character Profiles (Valheim 1.0.15, network version 40)

> Researched 2026-09-22 against Valheim 1.0.15 by decompiling the shipped assemblies; every claim was then checked by an independent refute-by-default verifier, who corrected errors in place. Items marked **Unverified:** could not be settled from code. Re-check with `valheim-modding/scripts/decompile.ps1` after a game update.

**Summary**
- The seed text the player types (`World.m_seedName`) becomes the integer seed via `m_seed = seedName == "" ? 0 : seedName.GetStableHashCode()` (the `World(string name, string seed)` constructor). The hash is a public extension method in `assembly_utils.dll` (`StringExtensionMethods.GetStableHashCode`), and it gave the stored seed exactly for two real saves on this machine.
- World generation reads only the **int** `m_seed`, `m_worldGenVersion` (currently 2) and the `m_menu` flag, plus `m_world.m_biomeData` (the alt-biome grid, which `AltBiomeWorldData.VerifyBiomeData` itself derives from this generator, see §2.3). The seed text is never read by `WorldGenerator`.
- The world UID (`m_uid`, a long) is **random**: `name.GetStableHashCode() + Utils.GenerateUID()`. Everything a character stores per world is keyed by this UID, including map exploration, pins, logout, bed, death and home points.
- **This build has a new save format ("chunked", world version 40 and later).** Each world is a folder `worlds_local/<World>/` holding `_main.<N>.fwl2`, `_main.<N>.db2`, `_main.<N>.chunks`, `_main.<N>.ok` and `*.chunk` files. The old `<World>.fwl` / `<World>.db` pair is still loaded, but only for migration.
- Map exploration and personal pins are stored **per character, per world UID**, in the client's `.fch` file: `PlayerProfile.m_worldData[worldUID].m_mapData`. They are never stored in the world save. Cartography-table shared map data is stored in the world, in the MapTable piece's ZDO.
- World modifiers and presets are stored as strings in `World.m_startingGlobalKeys` in the `.fwl2` file, for example `"resourcerate 300"` and `"preset combat_default:...:resources_most:..."`.
- Game version is **1.0.15**. Current format versions: world 41 (DeepNorth), player profile 46 (DeepNorth), map data 8, shared map 3, player data 33, item 109, world generator 2, network 40.
- Clients receive `m_name`, `m_seed`, `m_seedName`, `m_uid` and `m_worldGenVersion` from the server in `ZNet.RPC_PeerInfo`. Every client, and every client-side mod, can therefore rebuild terrain and biomes from the seed.

Evidence tags: *(Type.Member / decompiled)* means ILSpy output of the shipped DLL. *(verified on disk)* means I parsed a real save on this machine with a script written from the decompiled layout. *(runtime log)* means `Player.log`.

---

## 1. Version constants (`Version` class, assembly_valheim)

| Constant | Value | Source |
|---|---|---|
| `Version.CurrentVersion` | `GameVersion(1, 0, 15)`, so `ToString()` returns `"1.0.15"` | *(Version.CurrentVersion / decompiled)*; runtime log line `Valheim version: 1.0.15 (network version 40)` |
| `Version.c_networkVersion` | `40u` | *(Version / decompiled)* |
| `Version.c_WorldVersion` | `World.DeepNorth` = **41** | *(Version / decompiled)* |
| `Version.c_PlayerVersion` | `Player.DeepNorth` = **46** | *(Version / decompiled)*; real `.fch` starts with 46 *(verified on disk)* |
| `Version.c_MapVersion` | `Map.PinsAuthor` = **8** | *(Version / decompiled)*; `Minimap.GetMapData` writes literal `8` |
| `Version.c_SharedMapVersion` | `SharedMap.PinsAuthor` = **3** | *(Version / decompiled)*; `Minimap.GetSharedMapData` writes `3` |
| `Version.c_WorldGenVersion` | **2** | *(Version / decompiled)*; `World(name, seed)` sets `m_worldGenVersion = 2` |
| `Version.c_PlayerDataVersion` | `PlayerData.ChunkedNorth` = 33 | *(Version / decompiled)* |
| `Version.c_ItemDataVersion` | `Item.ChunksNCheats` = 109 | *(Version / decompiled)* |
| `Version.c_CachedMinimapVersion` | `CachedMinimap.Original` = 1 | *(Version / decompiled)* |
| `Version.c_biomeDataVersion` | `BiomeData.Data` = 0 | *(Version / decompiled)* |

- `Version.GetVersionString()` adds a platform prefix: `"l"` for Steam Linux, `"dw"` for Steam Deck under Proton, `"dl"`, `"ms"` or `"sw2"` for other platforms. Steam on Windows has no prefix. *(Version.GetVersionString, GetPlatformPrefix / decompiled)*
- Compatible world file versions are 9 to 41. Compatible profile versions are 27 to 46. Anything outside these ranges is rejected: a world gets `SaveDataError.BadVersion`, a profile is ignored. *(Version.IsWorldVersionCompatible / IsPlayerVersionCompatible / decompiled)*
- **Multiplayer compatibility checks only the network version (40)**, not the version string. *(ZNet.RPC_PeerInfo / decompiled: `if (num != 40) ... ErrorVersion`)*
- The `Version.World` enum names each format step. The chunked-save steps are `DeepNorthNoChunk=38`, `DeepNorthNoChunk2=39`, `ChunkedSave=40` and `DeepNorth=41`. Other steps: `GlobalKeys=32` (starting keys stored in the meta file), `NeedsDB=30`, `WorldGenVersion=26`. The `Version.Player` enum ends with `AbandonedDN=44`, `Chunked=45`, `DeepNorth=46`.
- `GameVersion.ToString()` drops a zero patch, so `1.0.0` prints as `"1.0"`. A negative patch prints as `.rcN`. *(GameVersion.ToString / decompiled)*

---

## 2. From seed text to integer seed

### 2.1 The hash
```csharp
// StringExtensionMethods.GetStableHashCode  (assembly_utils.dll, public static extension)
int num = 5381; int num2 = num;
for (int i = 0; i < str.Length && str[i] != 0; i += 2) {
    num = ((num << 5) + num) ^ str[i];
    if (i == str.Length - 1 || str[i + 1] == '\0') break;
    num2 = ((num2 << 5) + num2) ^ str[i + 1];
}
return num + num2 * 1566083941;
```
- The arithmetic is **unchecked 32-bit and wraps around**. The IL uses plain `add`/`mul`/`shl`, not the `.ovf` forms. *(IL dump of StringExtensionMethods.GetStableHashCode)*
- It is two interleaved djb2-xor streams, one over even-indexed chars and one over odd-indexed chars, combined as `a + b*1566083941`. A `'\0'` character ends the string. Chars are UTF-16 code units.
- **No normalization.** The seed is case-sensitive, and whitespace is significant. `FejdStartup.OnNewWorldDone` passes `m_newWorldSeed.text` straight into `new World(name, seed)`. *(FejdStartup.OnNewWorldDone / decompiled)*
- **An empty seed text gives seed 0.** The same is true for the menu, editor and dev worlds: `m_seed = ((!(m_seedName == "")) ? m_seedName.GetStableHashCode() : 0)` *(World..ctor(string,string) / decompiled)*.
- Different seed texts can hash to the same int. The world will then be identical, because generation uses only the int (§2.3).
- The same hash names prefabs everywhere, for example location prefab hashes in the `.db2`. A real save has "StartTemple" at -1544986047 and "Eikthyrnir" at -316818231, and both matched *(verified on disk)*.

**Verified:** I parsed real `.fwl2` files in Steam Cloud and recomputed the hash with a Python port of the code above. Both matched:
- seed text `"MWd8eV6svz"` gives seed `-1772362158`
- seed text `"j"` gives seed `372029384`

*(verified on disk: <Steam>/userdata/<accountId>/892970/remote/worlds/.../_main.N.fwl2)*

**Verified again, 2026-09-22, by a C# port** (`src/SeedLab.Seeds/StableHash.cs`) against three
independent pieces of ground truth the game itself wrote:
- the `int seed` in the first 4 bytes of `cacheMinimapMeta` for both local worlds -
  `"MWd8eV6svz"` -> `-1772362158` (asdasdasd) and `"hnBd9gJf2G"` -> `319486907` (testworldclaude);
- **47 location prefab names with the hashes the game logged for them**
  (`groundtruth/location-names.csv`) - all 47 reproduced exactly, which is the
  broadest confirmation on this machine that the port is the game's function and not merely a
  plausible djb2 variant;
- the spec 06 vector set, including `"a"` -> 372029373, `"abc"` -> 1099313834, `"HHcLC5acQt"` ->
  298112588, `"Abc"` -> 1099314826 (case matters) and `"abc "` -> -1139976598 (a trailing space matters).

Note `""` and `"\0"` both have **raw hash 371857150**, but `World..ctor` maps `""` to seed 0 and
`"\0"` to 371857150 - different worlds. The create-world field can never produce a NUL anyway.

A mod can simply call `"text".GetStableHashCode()`. It is public in assembly_utils.

### 2.2 Random seed suggestion (`World.GenerateSeed`, public static)
It builds 10 characters, each `UnityEngine.Random.Range(0, 59)` from the alphabet `abcdefghijklmnpqrstuvwxyzABCDEFGHIJKLMNPQRSTUVWXYZ023456789`. The alphabet has no `o`, `O` or `1`. *(World.GenerateSeed / decompiled)*
- Called by `FejdStartup.OnWorldNew` to fill the seed box. Also called by `World.GetCreateWorld` when a named world does not exist yet, or exists but loads with any `SaveDataError` (it then creates a new world under that name with a random seed). This is the dedicated-server `-world` path.
- There is **no `-seed` command-line argument**. `FejdStartup.ParseServerArguments` accepts `-world -name -port -password -savedir -public -logfile -crossplay -instanceid -backups -backupshort -backuplong -saveinterval -resetmodifiers -preset -modifier -setkey`. A dedicated server that creates a world always uses a random seed text. *(FejdStartup.ParseServerArguments / decompiled)*
- **Unverified:** in this client DLL, `ParseServerArguments` has no IL callers (xref scan). It is presumably called only in the separate dedicated-server build, which is not installed here.
- The UI requires a world name of at least 5 characters (`m_newWorldDone.interactable = m_newWorldName.text.Length >= 5`) and places no rule on the seed **in code**. The limits are prefab data on the input components, and they were read out of the running game 2026-09-22 (`seed-input.json` in the SeedLab data snapshot; **settled, was Unverified**):
  - `FejdStartup.m_newWorldSeed` (`GUIFramework.GuiInputField`): `characterLimit` **10**, `characterValidation` **Alphanumeric**.
  - `m_newWorldName`: `characterLimit` **20**, also Alphanumeric.

  So a typed seed really is **1-10 characters of A-Z a-z 0-9**, which is the 853,058,371,866,181,866-text space of 2.4 — and since 7 characters reach every int32, every world a tool can name is typeable. Nothing *enforces* this on a seed that arrives another way: a dedicated server's world, a `.fwl2` edited by a tool, or any string passed to `new World(name, seed)` may be any UTF-16 text, and `GetStableHashCode` will hash it.

### 2.3 How the seed is consumed
```csharp
// WorldGenerator private ctor (WorldGenerator.Initialize(World) is the public entry)
m_version = m_world.m_worldGenVersion; VersionSetup(m_version);
UnityEngine.Random.State state = UnityEngine.Random.state;
UnityEngine.Random.InitState(m_world.m_seed);
... m_noiseGen.SetSeed(0);                       // static FastNoise, always ends up with seed 0
m_offset0..m_offset3 = Random.Range(-10000, 10000);   // int overload, stored in floats
m_riverSeed  = Random.Range(int.MinValue, int.MaxValue);
m_streamSeed = Random.Range(int.MinValue, int.MaxValue);
m_offset4    = Random.Range(-10000, 10000);
if (!m_world.m_menu) Pregenerate();
UnityEngine.Random.state = state;                // restores the global RNG
```
*(WorldGenerator..ctor / decompiled)*
- The static `FastNoise` (`m_noiseGen`) is created with the first world's seed, but `SetSeed(0)` runs on every initialize. `SetSeed` only assigns `m_seed` *(FastNoise.SetSeed / decompiled, assembly_utils)*, so the cellular noise does not depend on the seed.
- Generation depends on `m_seed` **and** `m_worldGenVersion`. `VersionSetup(v)`: if `v <= 0`, `m_minMountainDistance = 1500`. If `v <= 1`, `minDarklandNoise = 0.5` and `maxMarshDistance = 8000`. *(WorldGenerator.VersionSetup / decompiled)*
- `WorldGenerator.GetBiomeSector` (used by `GetBiomeArea`, `EnvMan.GetBiome`, `Heightmap`, `SpawnSystem`, `Player.UpdateBiome`, `Minimap.UpdateBiome`) reads `m_world.m_biomeData`. It returns `BiomeSector.EmptyBlackForest` while that is null and `EmptyMeadows` while it is not ready. A `World` that a mod builds itself and passes to `WorldGenerator.Initialize` therefore has no alt-biome sectors until `AltBiomeWorldData.VerifyBiomeData(world)` runs. *(WorldGenerator.GetBiomeSector / decompiled; xref scan)*
- Nothing in `WorldGenerator` reads `m_seedName`. Callers of `WorldGenerator.GetSeed()` include `ZoneSystem.PlaceLocations`, `ZoneSystem.PlaceVegetation`, `ZoneSystem.GenerateLocationsTimeSliced`, `DungeonGenerator.GetSeed` and `AltBiomeWorldData.GenerateAltBiomes`. *(xref scan)*
- Reproducing world generation outside Unity requires reimplementing `UnityEngine.Random.InitState`/`Range` exactly. **Done and verified against the running game** (2026-09-23): xorshift128, seeded `s0 = s; s1..s3 = prev*1812433253 + 1`, shifts 11/8/19 — the full statement, with the range mappings and the draw-consumption rules, is in `world-generator.md` 9, and the implementation is in SeedLab (this repository, skill **seedlab**).

### 2.4 Inverting the hash, and the real size of the seed space

*Verified 2026-09-22 by a C# port (`src/SeedLab.Seeds`) plus an independent Python
recomputation; the full derivation is in the SeedLab spec `06-seed-space.md`, which now lives at
`docs\specs\` (the specs were copied out of a session scratchpad on 2026-09-23 so
that citations like "spec 06 section 6.2" still resolve). `vseed space` recomputes the lane tables and
the inverse round trips before printing them.*

`GetStableHashCode` is two **independent lanes**: `num` eats the even-indexed characters, `num2` the
odd-indexed ones, and `hash = E + 1566083941 * O (mod 2^32)`. One lane step is `h = (33*h) ^ c` from
`h0 = 5381`, and it is invertible given the character because `33 * 1041204193 = 1 (mod 2^32)`:
`h = (v ^ c) * 1041204193`. So a preimage is a meet-in-the-middle over the two lanes, not a search.

Cost, measured 2026-09-23 on this machine (`vseed bench`, one thread): **1,241 shortest-text
inversions per second** (2,000 in 1.612 s) — i.e. inverting a seed is under a millisecond, not a
search. Forward hashing runs at 13.57 M 10-character strings per second.

A free self-test for any port: `lane(c1..cn) & 31 == (5381 ^ c1 ^ ... ^ cn) & 31`, because
`33*h = h (mod 32)` and the XOR only touches bits 0-6. *(Checked on 200,000 random 1-10 character
strings over U+0001..U+CFFF; 0 failures, together with the `E + K*O` recomposition.)*

`E_n` = lane values reachable with exactly n characters. It collides hard - these were recomputed
exactly here, not taken from the spec:

| n | A62 (`0-9A-Za-z`) | A59 (`World.GenerateSeed`) | max strings per value |
|---|---|---|---|
| 1 | 62 | 59 | 1 |
| 2 | 2,097 | 2,048 | 3 |
| 3 | 66,014 | 64,726 | 9 |
| 4 | 2,058,466 | 2,016,367 | 27 |
| 5 | 64,105,880 | 62,658,885 | 81 |

The maximum multiplicity is exactly `3^(n-1)` at every level, and `sum of w_n over E_n = |A|^n`
(checked for n = 1..4, both alphabets).

**The seed space is 2^32, not 8.5e17.** There really are 853,058,371,866,181,866 seed texts of 1-10
alphanumeric characters (and 519,929,111,116,169,700 over A59), but `World..ctor` crushes each into
one int32 and generation never sees the text again, so there are at most **4,294,967,296** distinct
worlds and on average **198,618,129.796** texts open the identical world. Anything that searches
worlds must enumerate the ints.

Coverage, from the spec's exact sumset computation (**not** re-derived here except as noted):
**every int32 has a 7-character text in both alphabets**, while 984,542,424 ints (22.92 %, A62) and
1,081,577,348 (25.18 %, A59) have no text of 6 characters or fewer - including seed 0, the menu
world's seed. Measured over 200,000 random targets with the C# port: 22.800 % / 25.111 % needed 7
characters, both within 5 sigma of exact.

**Corrected here:** spec 06 section 6.2 gives `union(<= 4) / 2^32 = 0.102 %`. The exact value is
**4,531,329 ints = 0.10550 %**, computed twice independently (exhaustive hashing of all 15,018,570
A62 texts of length 1-4, and a direct sumset enumeration in Python). The L = 1..4 sumsets turn out to
be collision-free and pairwise disjoint - 62 + 62^2 + 2097*62 + 2097^2 = 4,531,329 exactly - which is
why the number is so clean. The spec's verdict is unaffected (both round to "about 0.1 %").

### 2.5 World UID
```csharp
m_uid = name.GetStableHashCode() + Utils.GenerateUID();   // int + long
// Utils.GenerateUID (assembly_utils):
return (long)(hostName + ":" + domainName).GetHashCode() + (long)UnityEngine.Random.Range(1, int.MaxValue);
```
*(World..ctor(string,string), Utils.GenerateUID / decompiled)*
- The UID is **not deterministic.** Creating a new world with the same name and seed gives a new UID, so characters lose that world's map, pins and spawn points.
- Copying the world files keeps the UID, because it is stored in the `.fwl2`.
- `Utils.GenerateUID` also produces `PlayerProfile.m_playerID` when a new profile is created. *(PlayerProfile..ctor / decompiled)*
- In the real save, the uid of world "asdasdasd" appears both in the `.fwl2` and as the `m_worldData` key in the test character's `<character>.fch` *(verified on disk)*.

### 2.6 Name fields on `World` (all public unless noted)
| Field | Meaning |
|---|---|
| `m_name` | Name stored **inside** the meta file. Used by `ZNet.GetWorldName()`, the knownWorlds stats, the biome cache file name and `GenerateWorldMap`'s cache delete. |
| `m_worldName` | **Save / folder name**. `LoadWorld` sets it to `saveFile.Name`, which for chunked saves is the parent directory name (`SaveSystem.GetChunkedSaveName`). All paths use it (`GetSaveDirectory`, `GetDBPath`, ...). |
| `m_seedName` | The seed text. It is shown in the world list, used by `printseeds`, and sent to clients. A public server's password may not contain it or the world name (`FejdStartup.IsPublicPasswordValid`). It is also passed as the `worldName` field of the PlayFab server registration (`ZNet.OpenServer` → `ZPlayFabMatchmaking.RegisterServer(..., m_world.m_seedName)`). |
| `m_seed` | int seed (§2.1). |
| `m_uid` | long UID (§2.5). |
| `m_worldGenVersion` | 2 for new worlds. |
| `m_menu` | true only for the main-menu world. |
| `m_needsDB` | false for a freshly created world. Set to true on the first real save. When true, a missing db file gives `SaveDataError.MissingDB`. |
| `m_startingGlobalKeys` | World modifiers (§4). |
| `m_worldVersion` | File version read by `LoadWorld` (the only writer of this field, per xref scan). **It stays 0 for a newly constructed `World`**: the name/seed constructor and `new World()` in `RPC_PeerInfo` both leave it at 0. However, a world created in the main menu does not run with that object: `FejdStartup.OnNewWorldDone` writes `_main.0.fwl2` and calls `UpdateWorldList`, which swaps `m_world` for the `World` re-read from that file, so it starts with 41. Only worlds from `World.GetCreateWorld` / `GetDevWorld` (dedicated server, dev world) and client-side worlds run with 0. *(FejdStartup.OnNewWorldDone / UpdateWorldList / decompiled)* |
| `m_fileSource` | `FileHelpers.FileSource` (§3.1). |
| `m_playerHistory` | Cross-platform users who have played the world. Stored in the `.fwl2`. |
| `m_chunkedSave`, `m_saveNumber` | **private**. Read them with `IsChunkedSave()` and `SaveNumber()`. |

`m_name` and `m_worldName` are equal unless someone renames the folder. When they differ, cache and stat code (which uses `m_name`) and path code (which uses `m_worldName`) disagree.

### 2.7 Menu, editor and dev worlds
```csharp
public static World GetMenuWorld()  { return new World("menu", "") { m_menu = true }; }
public static World GetEditorWorld(){ return new World("editor", ""); }
```
*(World.GetMenuWorld / GetEditorWorld / decompiled)*
- The menu world has **seed 0**, name "menu" and a random UID. `FejdStartup.Awake` calls `WorldGenerator.Initialize(World.GetMenuWorld())`, and so does `TestSceneSetup.Awake`.
- With `m_menu` set, `WorldGenerator` skips `Pregenerate()` (rivers and streams). `GetBiome` returns only `Mountain` (when base height ≥ 0.4) or `BlackForest`, and heights come from `GetMenuHeight` / `GetSnowMountainHeight(menu:true)`. *(WorldGenerator.GetBiome / GetBiomeHeight / decompiled)*
- `World.GetDevWorld` uses `Game.instance.m_devWorldName` (code default `"DevWorld"`) and `m_devWorldSeed` (code default `""`). `ZNet.Awake` uses it only when `m_world` is null on a server.
- **Pitfall:** in the main menu, `WorldGenerator.instance` is **not null**: it is the menu world. Check `WorldGenerator.instance.m_world.m_menu` (public) before trusting biome or height queries.
- `ZNet.Awake` calls `WorldGenerator.Deitialize()`. A server then re-initializes with the real world. On a client, `WorldGenerator.instance` is **null** until `RPC_PeerInfo` arrives. *(ZNet.Awake, ZNet.RPC_PeerInfo / decompiled)*

### 2.8 How the world reaches clients
The server's `ZNet.SendPeerInfo` writes `m_world.m_name, m_seed, m_seedName, m_uid, m_worldGenVersion, m_netTime`. The client's `ZNet.RPC_PeerInfo` builds `m_world = new World()`, fills these five fields, then calls `WorldGenerator.Initialize(m_world)` and `AltBiomeWorldData.VerifyBiomeData(m_world)`. *(ZNet.SendPeerInfo / RPC_PeerInfo / decompiled)*
- Terrain and biomes are **generated on each client** from the seed.
- On a client, `ZNet.World.m_worldName` is `""`, `m_startingGlobalKeys` is filled only by the `GlobalKeys` RPC, and `m_worldVersion` is 0.
- Useful public accessors:
  - `ZNet.World` (static property)
  - `ZNet.instance.GetWorld()`, `GetWorldUID()`, `GetWorldName()` (returns `m_name`)
  - `ZNet.GetWorldIfIsHost()` (static, null on clients)
  - `WorldGenerator.instance.GetSeed()`

*(ZNet / WorldGenerator / decompiled)*
- Console: `printseeds` (not a cheat) prints `"<Server|Client> version <v>, world seed: <m_seed>/<m_seedName>"` plus the seeds of loaded dungeons. *(Terminal.InitTerminal / decompiled)*

---

## 3. World save files

### 3.1 Where files live
```csharp
// Utils.GetSaveDataPath(FileSource)  (assembly_utils, public static)
if (FileHelpers.CloudStorageSupportedAndEnabled && fileSource.IsAutoOrCloud()) return "";
if (m_saveDataOverride != null) return m_saveDataOverride;   // Utils.SetSaveDataPath (-savedir)
return persistantDataPath;                                    // = Application.persistentDataPath
// SaveSystem.GetWorldsSaveRootPath(src)  = GetSaveDataPath(src) + (src.IsLocal() ? "/worlds_local" : "/worlds")
// SaveSystem.GetCharacterFolderPath(src) = GetSaveDataPath(src) + (src.IsLocal() ? "/characters_local/" : "/characters/")
```
*(Utils.GetSaveDataPath, SaveSystem.GetWorldsSaveRootPath / GetCharacterFolderPath / decompiled)*

`FileHelpers.FileSource` is a `[Flags]` enum: `Auto=1, Local=2, Cloud=4, Legacy=8`. The helpers `IsLocal()`, `IsCloud()` and the rest are in `FileSourceHelper` (assembly_utils).

| Source | World root | Character folder | Notes |
|---|---|---|---|
| Local | `%USERPROFILE%/AppData/LocalLow/IronGate/Valheim/worlds_local` | `.../characters_local/` | Local path confirmed by `Player.log` ("All files in local storage save data: C:/Users/.../LocalLow/IronGate/Valheim\...") |
| Cloud (Steam) | `/worlds` inside platform storage | `/characters/` | On Steam this is Steam Remote Storage. Files are physically at `<Steam>/userdata/<accountId>/892970/remote/worlds/<World>/...` and `.../remote/characters/<name>.fch` *(verified on disk; the log shows `Cloud Save: ... /worlds/test1/_main.1.fwl2`; app id 892970 from `Player.log`)* |
| Legacy | `.../LocalLow/IronGate/Valheim/worlds` | `.../characters/` | Pre-local/cloud-split location. It is read but **never written**: `FileWriter` sets `OpenFailed` for Legacy. On save, `SaveSystem.CheckMove` migrates the save to Cloud (if enabled and within quota) or Local, and moves the old files to backup. On this machine those folders contain only `steam_autocloud.vdf`. |

- Reload scan order is Cloud, then Local, then Legacy. *(SaveCollection.Reload / decompiled)*
- New worlds and characters default to Cloud when `CloudStorageSupportedAndEnabled`, unless the player forces local. *(FejdStartup.OnNewWorldDone, PlayerProfile..ctor / decompiled)*
- `-savedir` (via `Utils.SetSaveDataPath`) is parsed only in `ParseServerArguments` (see the caveat in §2.2).
- `Utils.GetSaveDataPath`, `SetSaveDataPath`, `SaveSystem.GetWorldsSaveRootPath` and `GetCharacterFolderPath` are all **public static**.

### 3.2 The chunked world format (world version 40 and later; everything written by 1.0.15)
Folder: `<worldsRoot>/<m_worldName>/`, given by `World.GetSaveDirectory(src)`. `<N>` is the save number `SaveSystem.GetSaveNumber()`. It starts at 0 for a new world and increases by 1 on each successful world save. *(SaveSystem.BeginSave / EndSave / decompiled)*

| File | Written by | Content |
|---|---|---|
| `_main.<N>.fwl2` | `World.SaveWorldFWLData` | Metadata: `int byteLen` followed by a ZPackage (layout below). About 50 to 300 bytes. |
| `_main.<N>.db2` | `ZNet.SaveWorldThread` | `int 41`, `double m_netTime`, then `ZoneSystem.Save`, `RandEventSystem.Save`, `PersistentEventSystem.Save` |
| `_main.<N>.chunks` | `ChunkSaveMapping.Save` | Index of chunk files: `short 41`, `int totalZDOs`, `int count`, then per chunk `ushort chunk, byte size, uint version, int numZDOs` |
| `<hi>_<lo>__<size>_<ver>.chunk` | `ZDOMan.SaveChunk` | `short 41`, `int zdoCount`, then `zdo.Save(pkg)` for each ZDO. Contains persistent ZDOs only. Portal ZDOs go to the special `ZoneSystem.ChunkPortal` (chunk index 1, size 0). |
| `_main.<N>.ok` | `ZNet.SaveWorldThread` | `int 41`. This is the commit marker, written last. |

All five file types were **verified on disk** (headers parsed): `.ok` = `29 00 00 00`, `.chunks` begins `29 00`, `.chunk` begins with short 41.

**`.fwl2` ZPackage layout** *(World.SaveWorldFWLData / LoadWorld / decompiled; verified on disk)*

`int version(41)`, `string m_name`, `string m_seedName`, `int m_seed`, `long m_uid`, `int m_worldGenVersion` (present if version ≥ 26), `bool m_needsDB` (≥ 30), `int n` followed by n × `string startingGlobalKey` (≥ 32), `int n` followed by n × `CrossNetworkUserInfo` (≥ 41). A `CrossNetworkUserInfo` is 4 strings: `PlatformUserID.ToString()` (for example `"Steam_7656..."`), display name, server-assigned name, PlayFab id.

Strings use `BinaryWriter.Write(string)` format: a 7-bit-encoded length followed by UTF-8 bytes.

**`.db2` details** *(ZoneSystem.Save / Load / decompiled; verified on disk)*

After the version and netTime, the ZoneSystem block is `int len` followed by **GZip** bytes (`Utils.Compress` = `GZipStream`, `CompressionLevel.Fastest`). The decompressed block contains:
1. `int n` + n × `Vector2s` generated zones (two `short`s each)
2. `int m_locationVersion`: 32 in a real save, even though the code default is 1, so the prefab overrides it
3. `int n` + n global keys (server-option keys are **excluded**, see §4)
4. `bool locationsGenerated`
5. `int n` + n × (`int prefabHash`, `float x, y, z`, `bool placed`) **location instances**

The real save holds **12,314 locations**, both placed and not yet placed. The first was StartTemple at (70.5, 33.9, -2.8).

So location positions (boss altars, traders, and so on) are generated from the seed **once** and then **stored** in the save. `ZoneSystem.m_locationInstances` is a public field populated on the server. Clients get only icon positions via the routed RPC `"LocationIcons"`. *(ZoneSystem.SendLocationIcons / RPC_LocationIcons / decompiled)* If the stored `m_locationVersion` differs from the current one, `locationsGenerated` is reset to false. `ZNet.ServerLoadWorld` then calls `GenerateLocationsIfNeeded`, and `GenerateLocationsTimeSliced` starts with `ClearNonPlacedLocations()`: instances already placed (zone generated) are kept, and all **unplaced** ones are discarded and regenerated. So "generated once" holds only until the game bumps `m_locationVersion`. *(ZoneSystem.Load / GenerateLocationsTimeSliced / ClearNonPlacedLocations, ZNet.ServerLoadWorld / decompiled)*

**Chunk grid** *(ZoneSystem.GetZonesChunk, GetZoneFromChunk, ChunkIndexFromXY, ChunkSaveMapping.GetChunkFilename / decompiled)*
- Zones are 64 m (`ZoneSystem.GetZone`: `floor((x+32)/64)`). The sector grid is 512 × 512 zones, with `SectorToIndex = (y+256)*512 + (x+256)`.
- A base chunk is 8 × 8 zones (`c_ZonesPerChunk = 8`), giving a 64 × 64 chunk grid. `Chunk = cx + (cy << 8)`.
- The file name is `(Chunk>>8):x2 + "_" + (Chunk&0xFF):x2 + "__" + size + "_" + version + ".chunk"`, which means **`<cy>_<cx>` in hex**. Zone of the chunk corner: `x = cx*8-256`, `y = cy*8-256`.
- Chunks with few ZDOs are merged into size 1, 2 or 3 blocks (2×2, 4×4, 8×8 base chunks) by `ZDOMan.DecideChunkSize` (`c_MaxNumberOfZDOsPerBiggerChunks = 100000`). A 2×2 group of the smaller size merges only when its combined count is > 0 and < 100000 (the count is every ZDO in those sectors, not only persistent ones) **and** the current chunk mapping (from the last load or save) has no chunk of the smaller size with ZDOs inside that block, so a region once saved at a smaller size is not merged later. `ZoneSystem.s_sizeFilters` is only the bit mask that maps a chunk index to its enclosing block (`ChunkIndexFromIndexAndSize`). *(ZDOMan.DecideChunkSize / GetSaveClonePerChunk / decompiled)*
- Each save rewrites **only dirty chunks**. `ChunkSaveMapping.CreateOrUpdate` bumps the chunk's `m_version`, which changes its file name. `ZDOMan.DeleteOldChunks` then deletes the previous file.
- Example: `1e_20__1_1.chunk` is cy=0x1e (30), cx=0x20 (32), so zones x 0..15, y -16..-1, size 1, version 1. This name was seen in the `Player.log` save of world test1 and matches the player position (387,-1037) there, which is zone (6,-16). The on-disk world is a different one, asdasdasd: its first save wrote `1e_20__1_1.chunk` (`Player.log`), and the file on disk is now `1e_20__1_2.chunk`, the same chunk index after its second save bumped the version.

**Save sequence** *(ZNet.SaveWorld / SaveWorldThread / decompiled; the runtime log shows steps 1/5 to 5/5)*
1. On the main thread: `ZDOMan.PrepareSave`, `ZoneSystem.PrepareSave`, `RandEventSystem.PrepareSave`, `PersistentEventSystem.PrepareSave`.
2. The rest runs on a background thread. It considers an auto-backup, runs cloud checks and possible migration, then `SaveSystem.BeginSave` (N+1).
3. Chunks, then `.chunks`, then `.db2`, then `.fwl2` (with `m_needsDB = true`).
4. If everything closed OK, it writes `.ok` and deletes the previous N's four `_main` files and old chunks. Otherwise it deletes the new files and reverts chunk versions.
5. The "World saved" message is shown by `ZNet.PrintWorldSaveMessage`.

- Autosave interval: `Game.m_saveInterval = 1800f` seconds (public static; the server's `-saveinterval` sets it, minimum 5). A 30-second warning is sent to all players. *(Game.UpdateSaving / decompiled)*
- `ZNet.WorldSaveStarted` / `WorldSaveFinished` are `public static Action` fields (plain delegates, not C# events; subscribe with `+=`). `WorldSaveStarted` is invoked at the start of `ZNet.SaveWorld` on the main thread. `WorldSaveFinished` is invoked in `PrintWorldSaveMessage` about 0.5 s after the save thread ends, whether or not the save succeeded. That timer runs in `UpdateSave`, called from `ZNet.Update`, and `ZNet.Shutdown` sets `enabled = false` right after its synchronous save. *(ZNet fields, SaveWorld, Update, UpdateSave, PrintWorldSaveMessage, Shutdown / decompiled)*

**Load and orphan cleanup: pitfall for tools and mods that touch save folders**
- `SaveCollection.KeepOnlyNewest` (run for the Cloud and Local sources, not Legacy) groups `_main.*` files by folder and walks save numbers from highest down. The highest number that has a **complete set of 4** wins. The code only counts **4 files sharing that N**; it does not check that they are `fwl2`, `db2`, `chunks` and `ok`. The exception is a lone `_main.0.fwl2`, which is a never-played new world.
- Every other `_main.*` file in that folder, newer or older, is **deleted** with the log line "Removing orphan save file". An interrupted save (no `.ok`, so only 3 files) therefore falls back to the previous complete save. **If no N qualifies, every `_main.*` file in the folder is deleted.** *(SaveCollection.KeepOnlyNewest / decompiled)*
- N is parsed with `int.Parse` on the second extension. If any `_main.*` file's name does not have an integer there (for example `_main.bak.txt` or `_main.2.fwl2.old`), the parse throws, "Failed to parse files for save" is logged, nothing is deleted, and the **whole folder is dropped from the save list** (the world disappears from the menu). *(SaveCollection.KeepOnlyNewest / decompiled)*
- `ZDOMan.LoadChunks` deletes any `.chunk` in the folder that the `.chunks` index does not reference ("Removing orphan CHUNK file").
- `SaveSystem.IsChunkedSave(path)`: a file counts as chunked if its grandparent directory is named `worlds` or `worlds_local` and its name starts with `_main.` or ends with `.chunk`.
- On load, `World.LoadWorld` parses N from the file name with the regex `_main.(\d+).fwl2`. `ZNet.LoadWorld` reads `.db2`, then `ZDOMan.LoadChunks`. A missing `.db2` means a fresh world.

### 3.3 Legacy format (`<World>.fwl` + `<World>.db`, world version up to 39)
- Paths: `<worldsRoot>/<World>.fwl` and `<worldsRoot>/<World>.db`, directly in the root folder. *(World.GetMetaPath: both overloads private. World.GetDBPath(): public; its FileSource overload is private.)*
- The `.fwl` uses the same ZPackage layout as `.fwl2`, gated by version.
- The `.db` contains: `int version`, `double netTime` (≥ 4), `ZDOMan.Load` (long, `uint nextUid`, `int count`, ZDOs), `ZoneSystem.LoadOld`, `RandEventSystem.Load` (≥ 15), `PersistentEventSystem.Load` (≥ 35). *(ZNet.LoadOldWorld / decompiled)*
- `ZNet.ServerLoadWorld` chooses `LoadWorld()` if `m_world.IsChunkedSave()`, otherwise `LoadOldWorld()`.
- The **next save is always written in the chunked format**.
- `World.LoadWorld` sets `m_createBackupBeforeSaving` for any version other than 41. Before the first save, `SaveSystem.PreSaveCloudChecksAndOperations` either renames the old non-chunked files to `<World>_backup_<yyyyMMdd-HHmmss>.fwl/.db` or copies an older chunked folder as a backup.

### 3.4 Backups *(SaveSystem.ConsiderBackup, ZNet.ConsiderAutoBackup / decompiled)*
Names use `SaveSystem.s_defaultDateFormat = "yyyyMMdd-HHmmss"`:

| Name | Created when |
|---|---|
| `<name>_backup_auto-<ts>` | Auto-backup. For a chunked world this is a **folder**, for example `worlds/sadasdasdasd_backup_auto-20260922-180825/_main.0.fwl2` *(verified on disk)*. For a character it is `<name>_backup_auto-<ts>.fch`. |
| `<name>_backup_<ts>` | Manual or migration backup |
| `<name>_backup_restore-<ts>` | The current save, renamed aside during a restore |
| `<name>_backup_cloud-<ts>` | A cloud write failed and the file was dumped locally |

Auto-backup settings:
- `ZNet.m_backupCount = 2`, `m_backupShort = 7200` s and `m_backupLong = 43200` s (public static). They are overridden by PlatformPrefs `AutoBackups`, `AutoBackups_short` and `AutoBackups_long`.
- **`m_backupCount == 1` disables auto-backups.**
- No backup is made while the session world time is under 1200 s (the `autowait` default).
- The dedicated-server argument parser sets `m_backupCount = 4` before reading `-backups`.
- Terminal test values `autoshort`, `autolong` and `autowait` override these settings.

Character files also rotate through `<name>.fch.new` (written first), then `ReplaceOldFile` swaps it in and keeps the previous file as `<name>.fch.old`. *(PlayerProfile.SavePlayerToDisk / decompiled; verified on disk: `<character>.fch`, `<character>.fch.old`, `<character>_backup_auto-20260922-190509.fch`)*

---

## 4. World modifiers, presets and starting global keys
- Modifiers are strings in `World.m_startingGlobalKeys` (public `List<string>`), persisted in the `.fwl2` (world version ≥ 32).
- A key is `"<globalkey> <value>"`, lower-cased. The key names are the `GlobalKeys` enum: indices 0 to 40 are server options (`PlayerDamage`, `EnemyDamage`, `WorldLevel`, `EventRate`, `ResourceRate`, ..., `NoMap`, `NoPortals`, `NoBossPortals`, `DungeonBuild`, `TeleportAll`, `NoPseudoDrops`, `NoBuildingFall`, `NoHeavySnow`, `AllHeavySnow`, `Preset`). After those come `NonServerOption = 41`, the progress keys (`defeated_eikthyr`, `defeated_gdking`, ...) and `Count`. *(GlobalKeys / decompiled)*
- Slider keys: the UI slider `KeySlider` (a `WorldModifiers` value of `Combat`, `DeathPenalty`, `Resources`, `Raids` or `Portals`, set to a `WorldModifierOption` value from `Default` through `Most`) writes the keys listed in that setting's `m_keys`.
- Preset key: `ServerOptionsGUI.SetKeys` then adds one `"preset <x>"` key. `<x>` is either a preset name (`WorldPresets`: `Normal`, `Casual`, `Easy`, `Hard`, `Hardcore`, `Immersive`, `Hammer`) or, if any slider was set by hand, `combat_<opt>:deathpenalty_<opt>:resources_<opt>:raids_<opt>:portals_<opt>`. The preset name is used only when `m_preset > WorldPresets.Custom`. With preset `Default` or `Custom` and no slider set by hand, no `preset` key is written. *(ServerOptionsGUI.SetKeys, KeySlider.SetKeys / decompiled)*
- Real example *(verified on disk)*: `["resourcerate 300", "preset combat_default:deathpenalty_default:resources_most:raids_default:portals_default"]`.
- **Unverified:** the full mapping from each slider or preset to key strings lives in UI prefab data (`KeySlider.SliderSetting.m_keys`, `KeyButton.m_keys`), not in code. Only the `resources_most` → `resourcerate 300` pair was observed.
- Menu flow: `FejdStartup.OnServerOptionsDone` clears the keys, calls `SetKeys`, then rewrites the current `_main.<N>.fwl2` in place.
- Load flow: `ZNet.WorldSetup` → `ZoneSystem.SetStartingGlobalKeys()` removes enum keys 0 to 40, adds every starting key, and applies `ServerOptionsGUI.SetPreset` for a `preset` key.
- Setting a server-option key in game (`GlobalKeyAdd` with `gk < NonServerOption`) also updates `ZNet.World.m_startingGlobalKeys`.
- Server-option keys are **dropped** from the `.db2` global keys in `ZoneSystem.Save`. They persist only through the `.fwl2`. Progress keys persist in the `.db2`.
- The server is authoritative for global keys. Clients receive the whole list through the routed RPC `"GlobalKeys"`. *(ZoneSystem.SendGlobalKeys / RPC_GlobalKeys / decompiled)*
- `ServerOptionsGUI.WorldContainsCheatedModifiers` marks a world as cheated for achievements (`Achievements.IsWorldCheated`) if it has any starting key, other than `preset...`, that is not one of the UI's possible values. Examples are keys added with `-setkey` or by a mod.
- Server command-line options: `-preset <WorldPresets>`, `-modifier <WorldModifiers> <WorldModifierOption>`, `-setkey <key>`, `-resetmodifiers`. `-setkey` adds the key lower-cased, but checks for duplicates against the original casing. *(FejdStartup.ParseServerArguments / decompiled)*

---

## 5. Derived caches (deterministic from the seed and safe to delete)
- **Minimap textures**, set up in `Minimap.Start`: `worlds_local/<m_worldName>/cacheMinimapMask`, `cacheMinimapBiome` and `cacheMinimapHeight` (GZip buffers), plus `cacheMinimapMeta` (`int seed`, `int 1`).
  - These are always in the **Local** folder, even for cloud worlds. `Minimap.Start` also creates that folder, which is why this machine has empty `worlds_local/<World>/` folders for cloud worlds.
  - The cache loads only if `ZNet.World.m_worldVersion == 41`, the meta seed equals `m_seed`, and the meta version is 1. Otherwise `GenerateWorldMap` runs (about 4.2 s in `Player.log`).
  - A brand-new world regenerates on its first session because no cache files exist yet. For a world created in the main menu this is **not** a version effect: that world runs with `m_worldVersion` 41, because the menu re-reads it from its new `_main.0.fwl2` (§2.6). Worlds made by `GetCreateWorld` / `GetDevWorld` run with 0 and skip the cache for that session. The log shows the pattern: "Generating new world minimap", then "Loading minimap textures done" on the next session. On a client, `m_worldVersion` is always 0 (§2.8), so the cache is never used there. *(Minimap.TryLoadMinimapTextureData / GenerateWorldMap / SaveMapTextureDataToDisk, FejdStartup.UpdateWorldList / decompiled)*
  - `GenerateWorldMap` deletes the old cache under `World.GetSaveDirectory(Local, ZNet.World.m_name)` but writes under `m_worldName`, so the two differ for a renamed folder.
  - **Unverified:** where a pure client writes the cache. Code facts: `Minimap.Start` sets the cache paths only if `ZNet.World` is non-null at that moment, and a joining client has `ZNet.World == null` until `RPC_PeerInfo` (`FejdStartup` calls `ZNet.SetServer(false, ..., null)`). With the paths unset, `TryLoadMinimapTextureData` returns false and `SaveMapTextureDataToDisk` returns early, so nothing is written. If `ZNet.World` were already set, its `m_worldName` of `""` would give the `worlds_local` root. Which case happens depends on timing and was not observed.
- **Alt-biome grid** (`AltBiomeWorldData`): 2048 × 2048 points, `c_pixelSize = 12` m, `c_halfWidth = 1024`. Points beyond radius 10500 (`sqrMagnitude > 110250000`) are Ocean.
  - `VerifyBiomeData` runs on server load and on client connect. It **always deletes** `cache/<m_name>_biomedatacache.bin` and regenerates.
  - `SaveCache` and `TryLoadCache` have no callers (xref scan), so they are dead code in this build.

---

## 6. Character profiles (`.fch`) and per-world map data

### 6.1 File
- Path: `SaveSystem.GetCharacterPath(src, name)` = `<characterFolder><m_filename>.fch`.
- `m_filename` is the character name **lower-cased**. The display name keeps its case. *(FejdStartup.OnNewCharacterDone: `new PlayerProfile(text.ToLower())` + `SetName(text)`; verified on disk: `<character>.fch` for a character whose display name starts with a capital)*
- Layout: `int dataLen`, `byte[dataLen]` (ZPackage), `int hashLen` (64), `byte[64]` SHA-512 of the data (`ZPackage.GenerateHash`).
- **The hash is read and discarded on load** (`PlayerProfile.LoadPlayerDataFromDisk`), so edited files load fine. *(verified on disk: hashLen 64)*
- ZPackage order *(PlayerProfile.SavePlayerToDisk / decompiled)*:
  1. `int 46`, `int 205` (stat count), `int 10` (stat categories)
  2. The per-category stats and dictionaries: knownWorlds, knownWorldKeys, knownCommands, 5 enemy tables, pickups, crafts, pickables, food, pieces
  3. `bool m_firstSpawn`
  4. **`int worldCount`**, then per world: `long worldUID`, `bool haveCustomSpawn`, `Vector3 spawn`, `bool haveLogout`, `Vector3 logout`, `bool haveDeath`, `Vector3 death`, `Vector3 home`, `bool hasMap` + `byte[] mapData`
  5. `string name`, `long playerID`, `string m_startSeed`, `bool usedCheats`, `long dateCreated` (unix seconds)
  6. `bool hasPlayerData` + `byte[] playerData` (the `Player.Save` blob, PlayerData version 33)
- `m_startSeed` is private, initialized to `""`, and only written and read. It is not used elsewhere (xref scan).

### 6.2 Per-world data is keyed by world UID
- `private Dictionary<long, WorldPlayerData> m_worldData`. `WorldPlayerData` is a **private** nested class.
- All public accessors use `ZNet.instance.GetWorldUID()` as the key: `SetMapData`/`GetMapData`, `Set/GetLogoutPoint`, `Set/GetDeathPoint`, `Set/GetCustomSpawnPoint` (bed), `Set/GetHomePoint`, `HaveLogoutPoint`, and so on. `SetMapData` ignores UID 0. *(PlayerProfile / decompiled)*
- The map is stored **per character and per world UID, on the client machine**, even for dedicated servers.
  - Another character does not see it.
  - A server-side mod cannot read it.
  - It follows the world UID, not the world name or seed (§2.5).

### 6.3 Map data blob (`Minimap.GetMapData` private, `SetMapData` private, `SaveMapData` public)
- Outer ZPackage: `int 8` (map version), then `WriteCompressed(inner)` = `int len` + GZip bytes.
- Inner:
  1. `int m_textureSize`
  2. `m_textureSize²` × `bool` explored
  3. `m_textureSize²` × `bool` exploredOthers (area revealed by map tables or others)
  4. `int n` pins, each `string name`, `Vector3 pos`, `int PinType`, `bool checked`, `long ownerID`, `string author (PlatformUserID)`
  5. `bool publicReferencePosition`
- **Only pins with `m_save == true` are stored.**
- Runtime `m_textureSize` is **2048**. The code field default is 256, so the prefab overrides it. Evidence: the log shows "compressed mapData 8388617", which equals 2·2048² + 9, and the real file decodes with textureSize 2048 *(runtime log + verified on disk)*.
- A mismatched size throws `Exception("Error: minimap mismatch")` on load.
- Runtime `m_pixelSize` is **12** (code default 64). This is **inferred**, not read from the prefab. In the real save, one explore circle has a radius of about 9 px for `m_exploreRadius` (code default 100 m); 100/12 ≈ 8.3, while 64 m pixels would give about 2 px. It is consistent with `AltBiomeWorldData.c_pixelSize = 12`.
- Map ↔ world: pixel `px = RoundToInt(x/pixelSize + textureSize/2)`, `py = RoundToInt(z/pixelSize + textureSize/2)`. The bit index is `py*textureSize + px`. *(Minimap.WorldToPixel / Explore(int,int), both private / decompiled)* `WorldToMapPoint` gives the same value divided by textureSize, that is 0..1.
- When the map is saved: `Game.SavePlayerProfile` calls `Minimap.instance.SaveMapData()` before `PlayerProfile.Save()`. Loading happens once in `Minimap.Update`, after `WorldGenerator.instance` exists and the map textures are ready.
- Real sample *(verified on disk)*: a Boss pin `$enemy_eikthyr` (type 9 = `PinType.Boss`) was stored with `m_save`.

### 6.4 Shared map (cartography table), stored in the world
- `Minimap.GetSharedMapData` writes `int 3`, `int m_explored.Length`, then one merged bool per pixel (explored OR exploredOthers OR the old table data). Then come the pins **excluding Death pins**, each `long owner`, `string name`, `Vector3`, `int type`, `bool checked`, `string author`.
- `MapTable` stores it GZip-compressed in the table's **ZDO** under `ZDOVars.s_data` (`byte[]`). It is therefore part of the world save (chunk files). *(MapTable / Minimap.GetSharedMapData / AddSharedMapData / decompiled)*
- `Minimap.ResetSharedMapData()` and the `resetsharedmap` console command clear the exploredOthers layer and also remove every pin whose `m_ownerID != 0` (pins received from others). *(Minimap.ResetSharedMapData / decompiled)*

---

## 7. What a modder needs to know

**Deterministic from the seed and `m_worldGenVersion`**
- Terrain height, biomes, rivers and streams (`WorldGenerator`).
- The minimap textures and the alt-biome grid.
- The **candidate** location placement. After world creation, however, placement is **stored** in the `.db2`. A game update that bumps `ZoneSystem.m_locationVersion` does not move locations that are already placed (their zone was generated), but it discards and regenerates every **unplaced** location instance on the next server load (§3.2).

**Stored and not deterministic**
- The world UID.
- Every ZDO (built pieces, dropped items, spawned and modified objects, terrain modifications) in `.chunk` files.
- Generated-zone set, location instances, progress global keys, events and net time in `.db2`.
- Modifiers and player history in `.fwl2`.
- Per-character map, pins and spawn points in `.fch` on each client.

**Client-side vs server-side**
- World files exist only on the host or server.
- A joining client knows name, seed, seed text, UID and worldGenVersion (from PeerInfo), plus global keys and location icons (from RPCs). It does **not** have the `.db2` location list.
- Map exploration is purely client-side, in the `.fch`.

**Private members that need reflection**
- `World.m_chunkedSave` and `m_saveNumber` (use `IsChunkedSave()` / `SaveNumber()` instead), and `World.GetMetaPath`, `GetDBPath(FileSource)` and `GetSaveFWLPath(FileSource)`.
- `PlayerProfile.m_worldData` / `WorldPlayerData` / `GetWorldData`. Reading another world's map requires reflection.
- `Minimap.GetMapData` / `SetMapData` / `m_explored` / `m_exploredOthers` / `m_pins`.
- `AltBiomeWorldData` members are mostly public.

**Useful public entry points**
- `World.GetMenuWorld()`, `World.GenerateSeed()`, `new World(name, seedText)`
- `"x".GetStableHashCode()`
- `ZNet.World`
- `WorldGenerator.Initialize(World)` and `WorldGenerator.instance`
- `SaveSystem.GetWorldList()`, which loads the meta of every world
- `SaveSystem.GetWorldsSaveRootPath(src)`, `Utils.GetSaveDataPath(src)`
- `Game.instance.GetPlayerProfile()`

**Pitfalls**
1. `WorldGenerator.instance` in the main menu is the seed-0 menu world. On a client it is null until PeerInfo arrives.
2. Anything a mod drops into a world folder as `_main.<int>.*` that is not part of the newest complete set, or a stray `.chunk`, **will be deleted** by the orphan cleanup. A `_main.` file without an integer second extension is not deleted but makes the whole world vanish from the save list (§3.2). The minimap cache files survive only because they lack those names.
3. Recreating a world with the same name and seed gives a **new UID**, so characters "lose" their map, pins and bed spawn for it.
4. A world renamed on disk changes `m_worldName` but not `m_name`.
5. A world file from a newer game (version > 41) is rejected as `BadVersion`.
6. Code defaults of serialized Unity fields are not runtime values: `Minimap.m_textureSize` is 256 in code but 2048 at runtime, and `ZoneSystem.m_locationVersion` is 1 in code but 32 at runtime.

---

## 8. Unverified or not fully checked
- **Unverified:** the dedicated-server command-line handling (`ParseServerArguments`) has no callers in this client DLL, and the server build is not installed, so it could not be confirmed that the server runs this exact code.
- ~~the seed and name input character limits~~ — **settled 2026-09-22** from the live components: seed 10 / Alphanumeric, name 20 / Alphanumeric (§2.2).
- **Unverified:** the preset and slider key strings other than `resources_most` → `resourcerate 300`, which are in prefab data.
- ~~`Minimap.m_pixelSize = 12` at runtime~~ — **settled 2026-09-22** by reading the loaded `Minimap`: `m_textureSize` **2048**, `m_pixelSize` **12** (previously only derived from the cache geometry). Also read there: `m_removeRadius` **300** (code 128f), `m_exploreRadius` **50** (code 100f), `m_exploreInterval` **0.25** (code 2f).
- **Unverified:** minimap cache behaviour on a pure client (empty `m_worldName`), which is inferred from code and not observed.
- **Unverified:** the ZDO binary layout inside `.chunk` files (`ZDO.Save` / `ZDO.Load`). It was not decompiled for this document.
- **Unverified:** the `RandEventSystem.Save` and `PersistentEventSystem.Save` layouts inside `.db2`. They were not decompiled (40 bytes follow the ZoneSystem block in the real save).
---

## 9. Tools

`scripts/valheim_saves.py` (in this skill) is the consolidated, read-only version of the parsers used to verify this document against real saves:

- `python valheim_saves.py seed "<text>"` - the Python port of `GetStableHashCode`; reproduces stored seeds.
- `python valheim_saves.py worlds` - `.fwl2` metadata for every world (name, seed text, seed, uid, world-gen version, world modifiers), with a check that the seed text hashes to the stored seed.
- `python valheim_saves.py characters` - `.fch` per-world data: logout/home points, explored pixel count, saved pins.
- `python valheim_saves.py locations "<world>" [--name Prefab]` - location instances from the `.db2` (spoilers).

For the decompiled sources behind any section, run `valheim-modding/scripts/decompile.ps1 -Type <Type>`.
