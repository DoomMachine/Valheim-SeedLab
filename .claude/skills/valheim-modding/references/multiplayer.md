# Valheim multiplayer & networking — modder reference

> Researched 2026-09-22 against Valheim 1.0.15 by decompiling the shipped assemblies; every claim was then checked by an independent refute-by-default verifier, who corrected errors in place. Items marked **Unverified:** could not be settled from code. Re-check with `valheim-modding/scripts/decompile.ps1` after a game update.

Build examined: `Version.CurrentVersion` = **1.0.15**, network version **40** (`Version.c_networkVersion = 40u`),
Steam client build, Unity 6000.0.75. All facts decompiled from `valheim_Data/Managed/assembly_valheim.dll`
(plus `assembly_utils.dll`, `Splatform.dll`) with the ILSpy engine unless marked **Unverified**.

## Summary (read this first)

- **Server-authoritative only in name.** The server (dedicated or the hosting player) owns the world save,
  hands out ZDO ownership, relays every RPC and keeps time. It does **not** validate what clients send:
  ZDO writes are accepted by revision number only, routed-RPC sender IDs are client-asserted, and most vanilla RPC
  handlers have no permission check. The admin list is checked only by kick/ban/unban/banned/save/remote-console.
- **Three RPC layers:** `ZRpc` (one socket, peer-to-peer), `ZRoutedRpc` (global, always relayed through the server),
  `ZNetView` RPCs (routed to the owner of one object's ZDO). Unknown method hashes are **silently dropped**, so a
  vanilla server relays mod RPCs without the mod installed. Only the receiving peer needs the handler.
- **The handshake checks only network version 40.** It checks no mod list and no mod versions (`ZNet.RPC_PeerInfo`). Mods add their own
  checks. The ServerSync library (embedded in many mods) does this by hooking `ZNet.OnNewConnection` and `ZNet.RPC_PeerInfo`.
- **`ZNet.IsDedicated()` is hard-coded `return false;` in this client DLL.** The dedicated server is a separate
  Steam app with its own DLL, where it is hard-coded `return true;`. That server is installed here (app 896660,
  `...\Valheim dedicated server\valheim_server.exe`, checked 2026-09-26; environment.md), without BepInEx.
  **Corrected 2026-09-26:** this said the server was not installed. The two DLLs are one source built twice;
  what differs is in section 1.4.
- **A routed RPC's `sender` can be forged** (section 4.2). Authenticate by the connection the call came on.
- **Map pins reach other players in 4 vanilla ways.** None of them needs the mod on the receiving side:
  (1) the cartography table (saved pins, `m_save && type != Death`, merged by owner ID),
  (2) `RPC_DiscoverLocationResponse`, the Vegvisir reply, which adds one saved pin on the target's map,
  (3) pings, which last 5 s (code default), show "PING" and allow one per sender, (4) shouts, which last 5 s (code default) and also appear as chat text.
  The public-position setting shares only the player's own position.
- **Console cheats only work when you are the server.** `Terminal.IsCheatsEnabled()` = `m_cheat && ZNet.instance.IsServer()`.
  On a remote server, even an admin cannot run `isCheat` commands locally. A few `remoteCommand` commands are forwarded to the
  server, which checks the admin list.
- **`Minimap.PinType` values are not in declaration-looking order**: Icon0=0, Icon1=1, Icon2=2, Icon3=3, **Death=4, Bed=5,
  Icon4=6**, Shout=7, None=8, Boss=9, Player=10, RandomEvent=11, Ping=12, EventArea=13, Hildir1-3=14-16, Memorial=17
  (an earlier fact sheet listed Icon4 next to Icon3, which is wrong).
  Pins are sent over the network as `int`, so these values matter.

---

## 1. Roles: server, host, client, dedicated

### 1.1 Flags and what they mean

| Situation | `IsServer()` | `ZNet.IsOpenServer()` | `ZNet.IsSinglePlayer` | `IsDedicated()` |
|---|---|---|---|---|
| Single player ("Start server" unchecked) | true | false | true | false |
| Hosting player (listen server) | true | true | false | false |
| Client joined to any server | false | false | false | false |
| Dedicated server process | true | true | false | **true** (`ZNet.IsDedicated` decompiled from the server's own `assembly_valheim.dll`, 1.0.16, 2026-09-26) |

- `IsServer()` returns the static `m_isServer` (ZNet.IsServer / decompiled). It is set by
  `ZNet.SetServer(server, openServer, publicServer, name, password, world)`. The main menu calls
  `SetServer(true, openServerToggle, publicServerToggle, ...)` to host, and `SetServer(false, false, false, "", "", null)` to join
  (FejdStartup / decompiled). Re-read on 1.0.16 (2026-09-26): hosting goes through `FejdStartup.OnWorldStart` →
  `SetServer(true, ...)` and joining through `FejdStartup.JoinServer` → `SetServer(false, ...)`. So a player-hosted game
  gives the host the server path and every joiner the client path, and single player and hosting run the same code.
- `IsSinglePlayer` => `m_isServer && !m_openServer` (ZNet.IsSinglePlayer / decompiled).
- **`public bool IsDedicated() { return false; }`** in this assembly (ZNet.IsDedicated / decompiled). It is an
  instance method returning a compiled constant; there is **no `m_isDedicated` field** in either DLL [both, 1.0.16,
  2026-09-26]. Every `IsDedicated()` branch in the client DLL is therefore dead. `FejdStartup.ParseServerArguments()` handles the
  dedicated-server flags (`-world -name -port -password -savedir -public -logfile -crossplay -instanceid -backups
  -backupshort -backuplong -saveinterval -preset -modifier -resetmodifiers -setkey`, default port **2456**, default world
  "Dedicated"). In the client DLL nothing calls it: grepping the whole decompiled assembly finds only its definition.
  **In the server's DLL `FejdStartup.Awake` calls it** (section 1.4; [S], 1.0.16, 2026-09-26), and its IL is identical
  in both DLLs.
- **Detecting "I am a headless server" from a mod.** The game tests `SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null`
  itself. For example, `ZNet.UpdatePlayerList` adds the local player to the player list only when the device is **not** Null.
  Use that test, or `ZNet.instance.IsDedicated()` (true in the server DLL, 2026-09-26) once `ZNet` exists. In a
  plugin's `Awake`, before any `ZNet`, test BepInEx's `Paths.ProcessName == "valheim_server"` (case-insensitively; it is
  `Path.GetFileNameWithoutExtension` of the executable path doorstop reports, decompiled `BepInEx.Paths.SetExecutablePath`,
  BepInEx 5.4.23.3, 2026-09-26).
- `IsCurrentServerDedicated()` returns true if any ready peer has `m_characterID.IsNone()` (ZNet / decompiled).
  **Pitfall:** only the server-side handler `ZNet.RPC_CharacterID` ever writes `peer.m_characterID`. Clients send "CharacterID"
  to the server, and the server never sends it back. So **on a client this returns true whenever it is connected, even to a
  listen host**. The only vanilla caller is `RelationsManager.UpdateAuthorIfHost`. A better test on a client: is the server's peer UID
  (`ZNet.instance.GetServerPeer().m_uid`) equal to some `PlayerInfo.m_characterID.UserID` in `GetPlayerList()`? If yes, a player is
  hosting. This is derived from the code and not tested in-game.
- **Process name:** `valheim_server.exe`, seen on disk in the installed dedicated server (2026-09-26). A third-party
  mod targets the same name (Crystal-Comfortable 1.0.5's `Comfortable.dll`, which carries
  `[BepInProcess("valheim.exe")]` and `[BepInProcess("valheim_server.exe")]`). The Linux binary name is
  **Unverified**. (**Corrected 2026-09-26:** this said the server was not installed and the name was corroborated only by
  that mod.)
  What *is* verified is how the filter matches: `Chainloader.Start` compares `ProcessName.Replace(".exe", "")` with
  `Paths.ProcessName` case-insensitively, so `[BepInProcess("valheim.exe")]` keeps a plugin off any process not named `valheim`
  (the dedicated server included) **without** restricting it to Windows — see environment.md, boot chain step 3.
  **Seen live on 2026-09-26:** the Windows dedicated server with BepInEx 5.4.23.3 loaded a plugin carrying
  `[BepInProcess("valheim_server.exe")]`; its `LogOutput.log` header reads `BepInEx 5.4.23.3 - valheim_server`.

### 1.2 What runs where (verified examples)

- **Server only:** assigning ZDO ownership (`ZDOMan.Update` calls `ReleaseZDOS` only if `IsServer()`), saving the world
  (`ZNet.SaveWorld`), the ban and permitted lists (`ZNet.UpdateBanList` runs every 5 s, server only), net time
  (`ZNet.SendNetTime` every 2 s; clients overwrite theirs in `RPC_NetTime`), global keys (`ZoneSystem` registers
  `SetGlobalKey`/`RemoveGlobalKey` only when it is the server and broadcasts `GlobalKeys`), location instances
  (`ZoneSystem.GetLocationIcons` reads `m_locationInstances` only on the server; clients receive only an icon list via the
  `LocationIcons` RPC), and Vegvisir lookups (`Game.Start` registers `RPC_DiscoverClosestLocation` only if `IsServer()`).
  - **Any client can ask a vanilla server for every instance of a location type** (re-read on 1.0.16, 2026-09-26).
    `Game.DiscoverClosestLocation(name, point, pinName, pinType, showMap, discoverAll)` sends
    `RPC_DiscoverClosestLocation` to the server, and the handler checks no permission. With `discoverAll`,
    `ZoneSystem.FindLocations(name)` returns every instance of that prefab name, placed or not, and the server sends
    one `RPC_DiscoverLocationResponse(pinName, pinType, position, showMap)` back per instance. Without it,
    `FindClosestLocation(name, point)` sends only the nearest one (3-D `Vector3.Distance`). Vanilla calls it from
    `Vegvisir.Interact` and `RuneStone.Interact` (`find-usages.ps1 -Needle "Game::DiscoverClosestLocation"`).
    - **No rate limit either** (1.0.16, 2026-09-26): `ZRoutedRpc.HandleRoutedRPC` only looks the method up
      (`m_functions.TryGetValue`).
    - **The replies go only to the requester** (`InvokeRoutedRPC(sender, ...)`), so the host's own map is
      untouched when a client asks.
    - **The server logs every request.** It writes `Found N locations of type X` (discoverAll),
      `Found location of type X` (closest), or the warning `Failed to find location of type X`. On a listen host these
      lines go to the host's own log, so the host can see what its clients look up. The request's pin name is not in
      them.
    - **A request that finds nothing gets no reply at all**: with `discoverAll` and no instance of the name the
      handler logs the warning and returns without invoking anything (`Game.RPC_DiscoverClosestLocation`, [both],
      1.0.16, 2026-09-26). A protocol that waits for answers must not wait for one per request.
    - The handler is `RPC_DiscoverClosestLocation(long sender, string name, Vector3 point, string pinName, int pinType,
      bool showMap, bool discoverAll)` and checks nothing about `sender`. The dedicated server's own DLL has the same
      handler, registered the same way (`Game.Start`, under `IsServer()`; decompiled from
      `valheim_server_Data\Managed\assembly_valheim.dll`, 2026-09-26).
  - **The response handler makes shareable pins.** `Game.RPC_DiscoverLocationResponse` calls
    `Minimap.DiscoverLocation(pos, type, name, showMap)`. That skips a spot with a similar pin within 1 m, and
    otherwise calls `AddPin(..., save: true, ...)` and shows "$msg_pin_added". `save: true` pins are exactly the ones
    `GetSharedMapData` exports to a Cartography Table. A mod that uses this query must intercept its own responses
    before vanilla sees them, and fail closed if that interception is not in place.
  - **Unique locations** (`m_unique`, e.g. `BigRockClearing` with quantity 10): until one candidate zone is generated,
    `FindLocations` returns every candidate. The first candidate zone anyone generates becomes the real one, and
    `PlaceLocations` then calls `RemoveUnplacedLocations`, which drops the others (ZoneSystem, 1.0.16). The response
    carries no placed flag.
  - **Order is kept by the game's own code** (1.0.16, 2026-09-26). The request and its replies leave in the order sent.
    - `ZRoutedRpc.InvokeRoutedRPC` serializes the call and hands it straight to `peer.m_rpc.Invoke("RoutedRPC", ...)`,
      with no queue, and the server's handler replies synchronously the same way. The managed path is FIFO both
      ways (`ZSteamSocket.m_sendQueue` is a queue, sent head first with send flag 8 = reliable).
    - **Unverified on Steam:** that Steam's reliable sends arrive in order. That is Steamworks' own guarantee and was
      not checked here (corrected 2026-09-26: this said the replies "arrive" in order, without that caveat).
    - So after a batch of `discoverAll` requests, one extra request with a guaranteed single answer (a plain
      `FindClosestLocation` for `StartTemple`) marks the end of every earlier reply.
    - **Crossplay (PlayFab) keeps the order too** (1.0.16, 2026-09-26). `ZPlayFabSocket` sends with
      `DeliveryOption.Guaranteed` and delivers data messages strictly in `msgId` order. A message that arrives early
      is parked in `m_outOfOrderQueue`, and `TryDeliverOutOfOrder` drains it after `m_next` advances. So
      order-dependent protocols such as the end marker hold on crossplay as on Steam.
    - **Unverified:** a server that renames or removes `StartTemple` (a location-changing mod) would never answer
      the end marker; nothing like that is installed here.
  - **Server cost of one request.** Each request walks every location instance (about 12,000 in a fresh world),
    reading `m_location.m_prefab.Name` for each. That property is not cached:
    - `Runtime.GetAssetPath`, i.e. `Loader.GetPath`, a dictionary lookup;
    - then `Shared.GetFileName`, which allocates a `char[2]` and up to two substrings.

    A single Vegvisir use costs the same.
- **On the owner of each ZDO, which can be any peer:** creature AI (`BaseAI.UpdateAI` returns early if `!m_nview.IsOwner()`) and physics
  and status effects (`Character.CustomFixedUpdate` runs motion and status effects only if `zDO.IsOwner()`).
- **Client and local only:** the character file (`PlayerProfile`), including per-world **map data: explored fog, all
  `m_save` pins, and the public-position flag** (`Minimap.SaveMapData` → `PlayerProfile.SetMapData`, which is keyed by
  `ZNet.GetWorldUID()`). Also input, UI, and the Minimap.
- **The world seed is sent to every client.** In the handshake the server writes `m_world.m_name, m_seed, m_seedName, m_uid,
  m_worldGenVersion, m_netTime`. The client builds a `World` from these and calls `WorldGenerator.Initialize(m_world)` (ZNet.SendPeerInfo /
  ZNet.RPC_PeerInfo / decompiled). Any client can therefore run world generation locally. `ZNet.GetWorldIfIsHost()`
  returns null on clients.
- `Game.isModded` (public static, default false) is **never sent over the network**. It shows the "modded" text in the main menu
  (FejdStartup) and, in vanilla, makes `Achievements.IsCheatedAtAll()` return true. **A mod can patch it back:
  Unshamed** returns false for a clean character in a clean world (pitfalls.md section 4).

### 1.3 Connection handshake (ZNet)

1. The client connects and invokes `ServerHandshake(inviteSecretKey)`. The server replies `ClientHandshake(needPassword, salt)`
   (ZNet.OnNewConnection / RPC_ServerHandshake).
2. Each side sends `PeerInfo`: uid, version string, **network version uint (40)**, reference position, player name, PlayFab ID,
   and simulation distance. The client also sends the password hash (MD5 of password+salt), the invite key and a Steam session ticket.
   The server also sends the world data (ZNet.SendPeerInfo).
3. The server checks, in order: network version (`if (num != 40)`: **Error 3 = ErrorVersion**), the ban and permitted lists (8),
   the Steam ticket (8), PlayFab block and crossplay rules (8, 10), PlayFab authentication (5; asynchronous callback, the checks
   below continue meanwhile), **player count `GetNrOfPlayers() >= 10` (9 = ErrorFull)**,
   the password (6), and whether the UID is already connected (7) (ZNet.RPC_PeerInfo / decompiled).
   - `GetNrOfPlayers()` is `m_players.Count`, and that count includes a non-headless host. So a listen host plus 9 clients is the maximum.
     `public const int ServerPlayerLimit = 10`.
   - **The game version string is parsed but only the network version is compared. No mod list is checked.**
   - The dedicated server's `RPC_PeerInfo` drops the block-list, PlayStation and crossplay-privilege checks; the rest
     is the same ([S], 1.0.16, 2026-09-26).
4. Then, in this order (`ZNet.RPC_PeerInfo`, [both], 1.0.16, 2026-09-26): `peer.m_uid` is set (so `peer.IsReady()`
   becomes true), the per-peer RPCs are registered, and the server calls `SendPeerInfo`, `SendPlayerList`,
   `SendHistoricalPlayerList` and `SendAdminList`; **only then** `m_zdoMan.AddPeer(peer)` and
   `m_routedRpc.AddPeer(peer)`, which registers `RoutedRPC` on that connection and invokes `ZRoutedRpc.m_onNewPeer(uid)`.
   (**Corrected 2026-09-26:** this said the peer was added before the lists were sent.)
   - **`m_onNewPeer` is the earliest safe point to send a routed call to a new peer.** Vanilla `ZoneSystem.OnNewPeer`
     sends `GlobalKeys` and `LocationIcons` there. At that moment the peer's `m_characterID` is still None and
     `m_playerID` 0 (no player yet). A rejected peer never gets there. `ZRoutedRpc` is new every session, so
     subscribe once per session (section 4.4).

The whole server-side chain: `ZNet.CheckForIncommingServerConnections` → `OnNewConnection` → client's
`ServerHandshake` → `RPC_ServerHandshake` → the client's `PeerInfo` → `RPC_PeerInfo` as above.

`ZNet.ConnectionStatus` enum values: None 0, Connecting 1, Connected 2, ErrorVersion 3, ErrorDisconnected 4,
ErrorConnectFailed 5, ErrorPassword 6, ErrorAlreadyConnected 7, ErrorBanned 8, ErrorFull 9,
ErrorPlatformExcluded 10, ErrorCrossplayPrivilege 11, ErrorKicked 12 (ZNet / decompiled).

Timing constants: `ZRpc` sends a keep-alive ping every **1 s** and times out after **30 s** (**90 s** with `SetLongTimeout(true)`) (ZRpc).
`ZNet.SendPeriodicData` runs every **2 s**. On the server it sends NetTime and PlayerList. On clients it sends `ServerSyncedPlayerData`
(refPos, public flag, and the `m_serverSyncedPlayerData` dictionary). Kicked peers are disconnected **1 s** after the "Kicked" RPC (ZNet.InternalKick).

### 1.4 The dedicated server's own build (1.0.16, compared 2026-09-26)

Compared: the client's `valheim_Data\Managed\assembly_valheim.dll` (sha256 `96cfc004...6127`) with the server's
`valheim_server_Data\Managed\assembly_valheim.dll` (`7cab9b49...4d8b`), every type's surface and every method's IL
(`scripts/asmdiff.ps1`). They are **one source built twice**, the server with a server compile symbol. Tags used in
this file: [S] = server DLL, [C] = client DLL, [both].

- **Surface.** Only these differ: the render members `UpscaledFrameBuffer` / `FrameBufferScaler` (client only),
  `ZPlayFabMatchmaking` (renamed compiler lambdas; `OnServerLoginFinished` only in [S]) and the type
  `PlayFabAuthWithCustomID` (only in [S]). `Game, ZoneSystem, ZNet, ZRoutedRpc, ZDOMan, ZNetScene, ObjectDB,
  Container, Inventory, Minimap, ZInput, Chat, Terminal, TextInput, PlayerController, UIInputHandler` have the same
  surface. So do `assembly_utils`, `assembly_guiutils`, `gui_framework`, `Splatform` and `UnityEngine.UI` (a few
  bodies differ there, e.g. `FileHelpers.get_CloudStorageSupported`). `Splatform.Steam.dll` is not in the server's
  `Managed` folder.
- **105 method bodies differ.** What they do in [S]:
  - `FejdStartup.Awake` calls `ParseServerArguments()`, and quits with the warning `Server can only run in headless
    moed` (sic) unless `SystemInfo.graphicsDeviceType == Null`. `InitializeSteam` logs on with
    `SteamGameServer.LogOnAnonymous`.
  - `ZNet.LoadWorld` / `LoadOldWorld` quit the application when the load fails.
  - `Game.Awake` makes an empty `PlayerProfile` and logs server stats every 600 s. `Game.FixedUpdate` never calls
    `UpdateRespawn` and pins the reference position at (1000000, 0, 1000000). So **`Player.m_localPlayer` is never set
    on a server**: `SetLocalPlayer` is reached only through `SpawnPlayer`, from `UpdateRespawn`.
  - `ZNet.RPC_PeerInfo` drops three checks (section 1.3). `ZNet.IsDedicated()` returns true.
    `ZNet.GetDesiredSimulationDistance()` returns `m_simulationDistance` (section 3.5).
  - `Terminal.AddString(string)` also logs `Console: <text>` (vanilla-behaviour.md section 6).
  - `LoadingIndicator.*`, `GameCamera.UpdateMouseCapture` and `UIGamePad.Update` are emptied.
  - **Everything else has identical IL**, including all of `ZRoutedRpc` and `ZoneSystem`, `Terminal`'s command gating
    and `ZNet.RPC_RemoteCommand`. The client-DLL facts in this file hold on the server unless a line says otherwise.
- **The main scene is the same.** It is the SoftRef bundle `StreamingAssets\SoftRef\Bundles\17245031`
  (`Assets/Scenes/main.unity`). The server's copy has the same 1924 GameObjects and 2374 MonoBehaviours, with one each
  of `Minimap, Hud, Chat, MessageHud, Menu, InventoryGui, TextInput, GameCamera, Game, ZNet, ZNetScene, ZoneSystem,
  ObjectDB, EnvMan` (`scripts/scene-scripts.py`). That is scene data plus code, **not a live log**: `Minimap.instance`,
  `Chat.instance` and `Hud.instance` are expected to exist on a server, and `Player.m_localPlayer` to stay null.
- **Unverified:** whether Unity calls `OnGUI` under `-batchmode -nographics`.

How to run the server here, and its log: environment.md, "The dedicated server".

---

## 2. Identities — do not mix them up

| ID | Type | Scope | Where |
|---|---|---|---|
| Session / peer UID | `long` | new random value every session | `ZNet.GetUID()` == `ZDOMan.GetSessionID()` == `ZNetPeer.m_uid` on the other side. Generated by `Utils.GenerateUID()` (hostname hash + `Random.Range`). |
| Player ID | `long` | persistent, per character | `Player.GetPlayerID()` reads `ZDOVars.s_playerID` from the player ZDO, set from `PlayerProfile.m_playerID` (Player.SetPlayerID). Used for **pin `m_ownerID`**. |
| Platform user ID | `PlatformUserID` | account | String form is `"<Platform>_<id>"`, e.g. `Steam_7656...` (Splatform.PlatformUserID.TryParse splits on `_`). Used for pin `m_author`, admin, ban and permitted lists. |
| Socket host name | `string` | account | Steam: the SteamID64 as a string (`ZSteamSocket.GetHostName`). PlayFab: the platform player ID. This is what the admin and ban checks use. |
| ZDOID | `(long UserID, uint ID)` | per object | `UserID` = the session UID of the peer that **created** the object. |

**Addressing another player's client:** use `ZNet.instance.GetPlayerList()[i].m_characterID.UserID`. The character ZDO
was created by that player's session, so its `UserID` is that peer's UID. Vanilla chat addresses players exactly this way
(`Chat.CheckPermissionsAndSendChatMessageRPCsAsync` → `sendMessageHandler(playerInfo.m_characterID.UserID, ...)`).
On a client, `m_peers` contains only the server. `ZNet.GetPeer(uid)` does not work for other clients; use the player list.

---

## 3. ZDO, ZNetView and ownership

### 3.1 The model

- **ZDO** = the replicated data record for one networked object: position, rotation, prefab hash, and typed key/value stores
  (float, Vector3, Quaternion, int, long, string, byte[], plus a "connection"). Keys are
  `string.GetStableHashCode()` ints, where `GetStableHashCode` is a deterministic djb2-style hash
  (StringExtensionMethods.GetStableHashCode in assembly_utils).
  `Set(string, bool)` stores an int 0 or 1 (ZDO.Set). `Set(string, ZDOID)` stores two longs under `name+"_u"` and `name+"_i"`.
- **ZNetView** = the MonoBehaviour that binds a GameObject to its ZDO. `Awake` either adopts `m_initZDO`
  (an object loaded from the network or the save) or calls `ZDOMan.CreateNewZDO` (a new object, **owned by the creating session**).
  If `ZDOMan.instance == null` (for example in the main menu), the ZNetView destroys itself (ZNetView.Awake).
- **`m_persistent` → `ZDO.Persistent`.** Only persistent ZDOs are written to the world save
  (`ZDOMan.AddObjectsPerChunk`: `if (item3.Persistent && !PortalPrefab...)`; portals are saved separately). When a
  peer disconnects, the server destroys non-persistent ZDOs whose owner is gone (`ZDOMan.RemoveOrphanNonPersistentZDOS`).
- Only ZDOs near a peer are sent to that peer. `ZDOMan.CreateSyncList` (server side) collects sector objects around the peer's reference position,
  adds distant ZDOs when fewer than 10 are queued, and adds force-sent ones. So on a client `ZDOMan` holds only a subset of the world. The server has all of it.
  What that means for a client's loaded range: §3.5.

### 3.2 Who owns what

- The creator owns a new ZDO (`CreateNewZDO` → `SetOwnerInternal(m_sessionID)`).
- **Every 2 s the server rebalances ownership** (`ZDOMan.ReleaseZDOS` → `ReleaseNearbyZDOS`, first for the server's own reference position, then for each peer).
  This applies only to **persistent** ZDOs near that reference position:
  - If the ZDO is owned by this peer but outside the peer's active area: `SetOwner(0)` (released).
  - If the ZDO is unowned, or its owner is not in its own active area, and the ZDO is inside this peer's active area: `SetOwner(peer)`.
  - Consequence: whoever is nearby simulates the object, and ownership moves as players walk.
  - **1.0.16 details** (decompiled 2026-09-26, MobTracker review): each pass uses
    `new SimulationDistance(synced.Near, 0, synced.IsClassic)` built from the server's synced value, at the
    peer's `m_refPos` (up to 2 s old). It releases a ZDO the peer owns outside its active area **only while
    that ZDO is still inside the peer's own near scan**, and takes one inside the area when it has no owner
    or `!IsInPeerActiveArea(pos, owner)`, which is false for a disconnected owner. So a ZDO whose owner
    teleported away, respawned far off or disconnected **keeps that stale owner** until another player's
    block covers it.
- The active area is based on zones. For ownership, `ZNetScene.PointInsideActiveArea` and `ReleaseNearbyZDOS` use the **server's own**
  `ZNet.GetSyncedSimulationDistance()` for every peer, not each peer's negotiated value. The per-peer negotiated distance (the minimum of
  the client's request and the server's, stored in `ZNetPeer.m_simulationDistance` by `ZNet.RPC_RequestValidSimulationDistance`) only
  decides which ZDOs are sent to that peer (`ZDOMan.CreateSyncList`). `SimulationDistance.OriginalNear/Far = 2`.
- **The active area's shape** (`ZNetScene.PointInsideActiveArea`, re-read on 1.0.16, 2026-09-26): the point's y is
  zeroed, then its Chebyshev distance from the centre of the reference position's zone must be <= 1.5·64 = 96 m
  (<= 64 m when Near == 1); at Near == 2 in disc mode (level 1) it must also be within 1.75·64 = 112 m. Measured
  from the player: 32 / 135.8 m (guaranteed / max) at level 0, 64 / ≈157.3 m at level 1, and 64 / 181.0 m at
  levels 2-6 - always the same 3×3-zone block, however far objects load. Only owned objects are visible and
  simulated (vanilla-behaviour.md section 14), and height plays no part, so a surface player can own the
  creatures in a dungeon interior under the block.
- `ZNetView.ClaimOwnership()` → `m_zdo.SetOwner(ZDOMan.GetSessionID())` if not already owner. **There is no permission check.**
  `ZDO.SetOwner` increments `OwnerRevision`, and that change replicates.

### 3.3 How data syncs (and why "only the owner writes" matters)

- `ZDO.Set(...)` **never checks ownership** (ZDO.Set* / decompiled; the `okForNotOwner` parameter of `Set(int,int,bool)` is ignored).
  Each set that **changes** the stored value increments `DataRevision` (`ZDOExtraData.Set` returns false for an equal value, so
  re-setting the same value does not replicate; a new `byte[]` always counts as a change). On a client it also queues the ZDO in
  `m_clientChangeQueue` (ZDO.IncreaseDataRevision). **Exception:** `ZDO.SetPosition` → `InternalSetPosition` increments the revision
  only `if (IsOwner())`, so a non-owner's position change stays local.
- Clients send the ZDOs in their change queue. The server sends each peer the ZDOs whose revision is newer than what that peer has.
  Cadence: a send cycle starts once 0.05 s have passed, and then serves one peer per frame. Each send is skipped if that peer's socket queue
  holds more than 10240 bytes, or if the free budget `10240 - queued` is below 2048 bytes (so in practice above 8192 queued bytes), and
  otherwise fills up to 10240 minus the queued bytes (`ZDOMan.SendZDOToPeers2`, `SendZDOs`).
- The receiver (`ZDOMan.RPC_ZDOData`) **accepts a ZDO iff the incoming `DataRevision` > the local one** (a ZDO it does not have yet is
  always created; the server then destroys it again if its ID is in `m_deadZDOs`). It then takes the
  sender's claimed owner and full data. If data is not newer but `OwnerRevision` is, it updates only the owner. **The sender's identity
  and ownership are never checked.**
- Race: if the owner and a non-owner both write from revision N, both send N+1. The first to reach the server wins, the other
  is dropped, and the loser's local copy stays divergent until the next write. **Rule: only the owner writes. Everyone else asks the owner
  via RPC, or claims ownership first.** Vanilla uses both patterns:
  - `Sign.SetText`: `m_nview.ClaimOwnership(); m_nview.GetZDO().Set(ZDOVars.s_text, text);`
  - `TeleportWorld.SetText`: `m_nview.InvokeRPC("RPC_SetTag", text, author)`, which runs on the owner.
- **Destroying:** `ZNetScene.Destroy(go)` propagates only if `zDO.IsOwner()` (ZNetScene.Destroy → ZDOMan.DestroyZDO, which is also
  owner-gated). Destroying a non-owned object is local only, and the ZDO still exists. Claim ownership first.

### 3.4 Common mod pattern: store your own data on an existing object

Written C# 5-compatible, so it also builds with the legacy compiler (environment.md). These are unknown keys to vanilla, but vanilla still stores, replicates and saves them,
because `ZDO.Serialize` writes every key of every type (ZDO.Serialize / decompiled).

```csharp
static readonly int KeyNote = "MyMod.note".GetStableHashCode();   // prefix keys to avoid collisions

// register the handler on every instance of the component (postfix its Awake/Start)
[HarmonyPatch(typeof(Sign), "Awake")]
static class SignRpc {
    static void Postfix(Sign __instance) {
        ZNetView nv = __instance.GetComponent<ZNetView>();
        if (nv == null || !nv.IsValid()) return;
        nv.Register<string>("MyMod_SetNote", delegate(long sender, string v) {
            if (nv.IsOwner()) nv.GetZDO().Set(KeyNote, v);        // only the owner writes
        });
    }
}

// write
if (nv.IsOwner()) nv.GetZDO().Set(KeyNote, "hello");
else nv.InvokeRPC("MyMod_SetNote", "hello");   // goes to the ZDO owner; that peer needs the mod
// if the owner may be vanilla: nv.ClaimOwnership(); nv.GetZDO().Set(KeyNote, "hello");   (Sign.SetText pattern)

// read (any peer that has the ZDO)
string note = nv.GetZDO().GetString(KeyNote, "");
```

**Pitfalls:**
- `ZNetView.Register` uses `Dictionary.Add` and **throws on a duplicate name** (ZNetView.Register / decompiled).
  Two mods, or a double patch, registering the same name on one object will throw.
- If the owner has no handler, `ZNetView.HandleRoutedRPC` logs `"Failed to find rpc method <hash>"` and drops the call.
- A `ZNetView` RPC is delivered only if the receiver has the object **instantiated**. `ZRoutedRpc.HandleRoutedRPC` →
  `ZNetScene.instance.FindInstance(zdo)` returns null otherwise, and the call is dropped silently.
- `ZNetView.InvokeRPC(method, ...)` targets `m_zdo.GetOwner()`. That call returns **0 = Everybody** when the ZDO is unowned.

### 3.5 What a client has loaded (decompiled 1.0.16, 2026-09-26)

How objects are created and removed on any machine: vanilla-behaviour.md section 14. What differs on a client:
- It instantiates only ZDOs the server sent it. The server builds each peer's list in `ZDOMan.CreateSyncList`
  from `FindSectorObjects(zone of peer.m_refPos, peer.m_simulationDistance)`: the `ShouldSend` filter, then a
  sort; distant ZDOs only when fewer than 10 near ones are pending; force-send ZDOs first. `peer.m_refPos` comes
  from PeerInfo at connect, then every **2 s** from the client's `ZNet.SendPeriodicData` ->
  `RPC_ServerSyncedPlayerData`. Send rounds: section 3.3.
- **Range:** the server stores `min(request, its own ZNet.m_simulationDistance)` in `peer.m_simulationDistance`
  (first in `ZNet.RPC_PeerInfo`) and replies; the client applies `min(reply, its own desired)`, both under
  `SimulationDistance.operator<`. That operator is not a total order: if client and server have the same near and
  far values and only the client's is non-classic, the client's (smaller, circular) value wins (2026-09-26).
- **Shape of the synced area** (1.0.16, 2026-09-26). `ZDOMan.FindSectorObjects` adds a near-ring zone only when
  `ZoneSystem.instance.ZonesWithinRadius(...) || simulationDistance.IsClassic`. So only the classic levels 0 and 2
  (`SimulationDistance.GetSimulationDistance`: 0 = (1,2,classic), 2 = (2,2,classic)) sync a square; levels 1, 3 and up
  sync a roughly circular area. Level 2 is a 5x5-zone (320 m) block around the client. The server's own value is its applied setting or its `-simulationdistance`
  override. **Until `RPC_ValidatedSimulationDistance` arrives, the client's `ZNet.m_simulationDistance` (and
  ZoneSystem's copy) is the zero struct, so only its own zone loads.**
- **Stale per-peer value:** when a host changes its own setting, `SimulationDistanceServerHandshake`'s server
  branch only sends `RPC_ValidatedSimulationDistance` and never updates `peer.m_simulationDistance`, and the
  client's handshake returns early when its applied value already equals its desired one - so the server keeps
  syncing the old area until the client reconnects. Example: a client at level 3 joins a host at level 2, the
  host raises to 3; the client loads the 37-zone disc but receives new ZDOs only for the 5×5 square.
- A client keeps received ZDOs after walking away and re-instantiates from them when their zone returns,
  possibly at a stale position until a newer revision arrives. It drops one only through
  `ZDOMan.HandleDestroyedZDO`, `RemoveOrphanNonPersistentZDOS` (non-persistent ZDOs whose owner left, from
  `RemovePeer`), `ShutDown` or `ResetBeforeLoad` (2026-09-26). So a client's `ZDOMan` is the union of every area it has
  been sent this session, and nothing from earlier sessions: a mod scanning a client's ZDOs (TomTom's Find) sees the near
  area around every spot the player has visited since joining, and no further. **Unverified (code-derived only):** a held ZDO
  that changes sector outside the client's active area is sent in `m_invalidSector` (`ZDOPeer.ZDOSectorInvalidated`,
  judged with the server's value and the 2 s-old refPos) and moved to `SectorZero` by the client's
  `ZDOMan.RPC_ZDOData`, so it unloads unless the same packet carries a newer revision.
- **Destroys reach everyone:** `ZDOMan.SendDestroyed` routes `DestroyZDO` to every peer, and
  `ZNetScene.OnZDODestroyed` destroys the local instance, so no client keeps a ghost of something killed or
  despawned elsewhere.
- **The host's view is its own:** it holds every ZDO, but instantiates only around its own reference position
  with its own setting; creatures around other players are not instantiated on the host unless its own loaded
  area covers them.
  - **Why it holds every ZDO** (1.0.16, 2026-09-26): `ZNet.LoadWorld` calls `ZDOMan.LoadChunks`, which reads every
    chunk file listed in `ChunkSaveMapping` at world load, adds each ZDO to `m_objectsByID`, then calls
    `InitialAddToSector`. It is eager, not lazy per chunk. ZDOs in the portal chunk (`ZoneSystem.ChunkPortal`) go
    to `m_portalObjects` instead of a sector. `ZDOMan.Load` is reached only from `ZNet.LoadOldWorld` (the old
    single-file format).
  - **Only the server makes zone content:** `ZoneSystem.PokeLocalZone` uses `SpawnMode.Client` when `!IsServer()`
    (or the zone is generated), and the server generates ghost zones around each peer (`CreateGhostZones`).
- **Dedicated server: level 2 (a 5×5-zone square) unless told otherwise** ([S], 1.0.16, decompiled 2026-09-26). The
  client's `ZNet.GetDesiredSimulationDistance` reads `GraphicsSettingsManager.Instance.ActiveSettings.m_simulationDistance`;
  the server's returns `m_simulationDistance` itself. `FejdStartup.ParseArguments` [S] queues the value on
  `ZNet.s_onZNetStart`, which calls `ApplySimulationDistance`: `-simulationdistance N` gives
  `SimulationDistance.GetSimulationDistance(N)`, and without it `SimulationDistance.OriginalDistance` = (2,2,classic),
  level 2. (**Corrected 2026-09-26:** this said the server might fall back to the default struct, level 0, and was
  Unverified.)

---

## 4. RPCs

### 4.1 Three layers

| Layer | Register | Handler signature | Invoke | Duplicate register |
|---|---|---|---|---|
| `ZRpc` (one connection) | `peer.m_rpc.Register<T..>(name, f)` (≤4 params) | `(ZRpc rpc, T p0, ...)` | `rpc.Invoke(name, params object[])` | **replaces** (Remove then Add) |
| `ZRoutedRpc` (global) | `ZRoutedRpc.instance.Register<T..>(name, f)` (≤6 params) | `(long sender, T p0, ...)` | `ZRoutedRpc.instance.InvokeRoutedRPC(target, name, ...)` | **throws** (`Dictionary.Add`) |
| `ZNetView` (per object) | `nview.Register<T..>(name, f)` (≤6 params) | `(long sender, T p0, ...)` | `nview.InvokeRPC([target,] name, ...)` | **throws** |

Sources: ZRpc.Register*, ZRoutedRpc.Register*, ZNetView.Register* / decompiled. Methods are identified by
`name.GetStableHashCode()`, so the name string must match exactly on both ends.

### 4.2 Routing rules (ZRoutedRpc.InvokeRoutedRPC / RouteRPC / RPC_RoutedRPC / decompiled)

- Targets: `ZRoutedRpc.Everybody = 0L` (const), which is the same as `ZNetView.Everybody` (static field, 0). `InvokeRoutedRPC(name, ...)` with no target
  sends to the server's peer ID, from the **private** `GetServerPeerID()`: the server's own id on the server, and on a client
  `m_peers[0].m_uid`, **or 0 when it is not connected** ([both], 1.0.16, 2026-09-26). So a no-target call made before the
  handshake goes to Everybody and runs the handler locally. Public equivalents: `ZNet.GetUID()` on the server,
  `ZNet.instance.GetServerPeer().m_uid` on a client.
- Sender side: if `target == myId || target == 0`, the handler runs **locally, synchronously**. If `target != myId`, the message
  is sent to all ready peers. A client's only peer is the server.
- Server side: on receipt it handles the message if the target is itself or 0. If the target is not itself, it forwards: to the specific peer if
  that peer is ready, otherwise the message is dropped silently; for Everybody, to all ready peers **except the sender**. **Forwarding does not
  depend on the server having the handler.** A vanilla server relays mod RPCs.
- Receiver: `m_functions.TryGetValue(hash)`. A global call to a name the receiver never registered is **dropped silently**
  (no log). An object-targeted call whose object has no such handler logs `Failed to find rpc method <hash>` (section 3.4).
- For Everybody the server runs its own handler **before** forwarding (`HandleRoutedRPC` then `RouteRPC`). An exception in
  the server's handler would therefore stop the forward. Target specific peers when a vanilla handler might throw on a headless server.
  A dedicated server's scene does hold a `Minimap`, `Chat` and `Hud`, but never a local player (section 1.4; scene data, not
  seen live).
- **`sender` can be forged** ([both], 1.0.16, 2026-09-26). The private `ZRoutedRpc.RPC_RoutedRPC(ZRpc rpc, ZPackage pkg)`
  deserializes `m_senderPeerID` from the packet and never replaces it, and `RouteRPC` forwards it unchanged, so a modified
  client can claim the host's or an admin's uid. Never authenticate by `sender`.
  - Derived from the same code: the server's Everybody relay skips the peer whose uid equals `m_senderPeerID`, so a
    forged broadcast comes back to the forger and misses the peer it impersonates.
  - **The unforgeable identity is the connection.** A Harmony prefix on `ZRoutedRpc.RPC_RoutedRPC` that stores `rpc` in
    a static, cleared again by a Finalizer, lets a handler running inside that call find its peer: the entry of
    `ZNet.instance.GetPeers()` whose `m_rpc == rpc`. Admin test on the server:
    `ZNet.instance.IsAdmin(rpc.GetSocket().GetHostName())` (section 6.3). TomTom 1.3.0 does this (`RoutedCallContext`;
    released in v1.3.0, commit `5a83509`, 2026-09-26; a working-tree build of the same code loaded live on a dedicated
    server, and no player has joined one yet). **Derived, not tested:** a routed call the handler itself makes with target
    0 or its own id runs synchronously inside the outer call, so it would see the outer call's `rpc`.
  - **ServerSync's copy is not a substitute.** Its `SnatchCurrentlyHandlingRPC`, a prefix on `ZRpc.HandlePackage` in every mod
    that embeds it (list them with `scan-mod-patches.ps1 -Target HandlePackage`), stores the `ZRpc` and never clears it, so outside a
    call it names whichever connection spoke last.
  - Vanilla's admin checks on `ZNet`'s peer RPCs use `rpc.GetSocket().GetHostName()`, which the client cannot fake.
    `RandEventSystem.RPC_ConsoleStartRandomEvent` / `RPC_ConsoleResetRandomEvent` would trust `ZNet.instance.GetPeer(sender)`,
    but in this build they are **never registered** (no `Register` call references them), so a client's
    `"startrandomevent"` / `"resetrandomevent"` routed RPC is silently ignored by the server.
- **Delivery** ([both], 1.0.16, 2026-09-26):
  - **Order:** FIFO in the game's own code both ways; Steam's own in-order delivery is **Unverified** here, crossplay
    keeps order (section 1.2).
  - **Size:** `ZPackage` and `ZRpc` impose no limit. `ZSteamSocket.SendQueuedPackages` sends each queued package as one
    Steam message (flag 8, reliable); Steamworks.NET's `Constants.k_cbMaxSteamNetworkingSocketsMessageSizeSend` is
    **524288** bytes, and routing adds 56 bytes of framing. A failed send logs `Failed to send data <EResult>` (plain
    `ZLog.Log`) and leaves the package at the head of the queue to be tried again. **Inferred, Unverified:** an
    oversize package therefore stalls the connection until the 30 s timeout. `ZPlayFabSocket` does not split a
    message either; it zlib-compresses each one.

### 4.3 Parameter serialization (ZRpc.Serialize / ZRpc.Deserialize / decompiled)

- **Supported:** `int, uint, long, float, double, bool, string, ZPackage, List<string>, Vector3, Quaternion, ZDOID,
  HitData`, and any `ISerializableParameter`. The receiver creates `ISerializableParameter` values via `Activator.CreateInstance`,
  so the type needs a public parameterless constructor. Example: `UserInfo`.
- **Unsupported types are skipped silently when sending.** That includes `byte, short, ulong, byte[], Vector2`, and **enums**
  (a boxed enum fails the `obj is int` test). Cast enums to `int`. Wrap `byte[]` in a `ZPackage`
  (`new ZPackage(bytes)`; `ZPackage.Write(byte[])` exists).
- Serialization follows the **runtime types of the arguments you pass**. Deserialization follows the **receiver's handler
  parameter types**. A mismatch (for example passing an `int` literal to a `long` parameter) corrupts the stream.
- Exceptions: `ZRpc.Update` catches `EndOfStreamException` from `HandlePackage` and returns `ErrorCode.IncompatibleVersion`.
  `ZNet.UpdatePeers` then sets `m_connectionStatus = ErrorVersion`. Other exceptions are only logged (`"Exception in
  ZRpc::HandlePackage"`). **Derived, not tested:** routed-RPC handlers run inside `Delegate.DynamicInvoke` (except the zero-parameter
  overload, which calls the delegate directly), and the whole routed dispatch itself runs inside the peer-level `RoutedRPC` handler, which
  `ZRpc` invokes via `DynamicInvoke`, so routed exceptions arrive as `TargetInvocationException` and are merely logged. A short payload on a
  peer-level `ZRpc` method (argument deserialization happens before `DynamicInvoke`) throws a bare `EndOfStreamException` and ends the
  connection with a version error **on a client** (Game logs out). On the server `ZNet.GetConnectionStatus()` always returns Connected,
  so nothing is disconnected there.

### 4.4 When and how to register

- `ZRoutedRpc.instance` is created in every `ZNet.Awake` (`m_routedRpc = new ZRoutedRpc(m_isServer)`, [both]), so **re-register once
  per session**. Vanilla registers its global RPCs in `Game.Start`, `Chat.Awake`, `ZNetScene.Awake`, `ZoneSystem` and elsewhere, and
  a postfix on `Game.Start` is a proven point. `ZRoutedRpc.instance.m_onNewPeer` (public `Action<long>`) fires for every new peer;
  `ZoneSystem` uses it to push global keys and location icons (when it fires: section 1.3, step 4).
  - **Its static is never cleared** (1.0.16, 2026-09-26): after logout `ZRoutedRpc.instance` still returns the last
    session's object until the next `ZNet.Awake`, so "instance is not null" does not mean "in a session". `SetUID(ZDOMan.GetSessionID())`
    runs later in that `ZNet.Awake`, after the constructor.
  - `Register` uses `Dictionary.Add`: registering one name twice on one instance throws.
- Peer-level `ZRpc` methods must be registered per connection. ServerSync, embedded in many published mods (for example
  AzuCraftyBoxes), uses a **prefix on private `ZNet.OnNewConnection(ZNetPeer peer)`** that registers
  `peer.m_rpc.Register<ZPackage>("ServerSync VersionCheck", ...)` and sends its version. A **prefix on private
  `ZNet.RPC_PeerInfo(ZRpc rpc)`** refuses the connection (server: `rpc.Invoke("Error", 3)`) if the check failed.
  **This is the standard way to "require the mod on both ends".** Vanilla has no such mechanism.

### 4.5 "Does everyone need the mod?"

| What you do | Sender | Server (relay) | Receiver |
|---|---|---|---|
| Custom global RPC to a peer | mod | **not needed** | needs handler (else silently ignored) |
| Custom ZNetView RPC | mod | not needed | **the ZDO owner** needs the handler |
| Invoke a **vanilla** RPC (ChatMessage, ShowMessage, RPC_DiscoverLocationResponse, MapData, ...) | mod | not needed | **vanilla is enough** |
| Write custom ZDO keys | mod | not needed (stores and saves them) | only mod users can interpret them |
| Custom `ZRpc` (peer-level) | mod | the **server** needs it (it is the other end) | — |

### 4.6 Vanilla global routed RPCs (all `ZRoutedRpc.instance.Register` calls in the DLL)

`ChatMessage(Vector3,int,UserInfo,string)` and `RPC_TeleportPlayer(Vector3,Quaternion,bool)` (Chat);
`RPC_SetDreamCinematic(string)`; `RPC_DamageText(ZPackage)`; **`ShowMessage(int type,string)`** (MessageHud:
`MessageAll` sends to 0); `DestroyZDO(ZPackage)` and `RequestZDO(ZDOID)` (ZDOMan); `SpawnObject(Vector3,Quaternion,int)`
(ZNetScene: `Object.Instantiate` on every receiver); `SleepStart`, `SleepStop`, `RPC_Ping(float)`, `RPC_Pong(float)`,
`RPC_SetConnection(ZDOID,ZDOID)`, **`RPC_DiscoverLocationResponse(string,int,Vector3,bool)`**,
`RPC_RegisterKill(string,int,int,int,bool)` (Game); `RPC_DiscoverClosestLocation(string,Vector3,string,int,bool,bool)`
(Game, server only); PersistentEventSystem list and event start/stop RPCs; `SetEvent(string,float,Vector3)`
(RandEventSystem); `SetGlobalKey(string)` and `RemoveGlobalKey(string)` (server only), `GlobalKeys(List<string>)`
and `LocationIcons(ZPackage)` (clients) (ZoneSystem).

---

## 5. Map sharing in detail

### 5.1 Cartography table (`MapTable`)

The flow, from MapTable / Minimap / decompiled:

- The table ZDO holds one byte array under `ZDOVars.s_data` = `"data".GetStableHashCode()`, stored **compressed**
  (`Utils.Compress`).
- **Read** (`MapTable.OnRead`): decompress, then `Minimap.instance.AddSharedMapData(bytes)`. It shows `$msg_mapsynced`,
  `$msg_alreadysynced` or `$msg_mapnodata`. **OnRead does not call `PrivateArea.CheckAccess`**; only the hover text does.
  Reading inside a foreign ward therefore works (from code; not tested in-game).
- **Write** (`MapTable.OnWrite`): **first performs a silent read**, then `PrivateArea.CheckAccess` (no access means it returns
  true and writes nothing). It then builds `Utils.Compress(Minimap.GetSharedMapData(currentTableBytes))` and calls
  `m_nview.InvokeRPC("MapData", pkg)`. That goes to the table's **owner**, and `RPC_MapData` runs there:
  `if (m_nview.IsOwner()) zdo.Set(s_data, pkg.GetArray())`. **The owner can be vanilla**, and the RPC does no validation.

**`GetSharedMapData(oldTableBytes)` writes:**
- `int 3` (`Version.SharedMap.PinsAuthor`), `int m_explored.Length`, then one bool per map pixel, computed as
  `m_exploredOthers[i] || m_explored[i] || oldTable[i]`. Explored fog is OR-merged.
- Pins: **every pin with `m_save == true && m_type != PinType.Death`**. For each pin it writes `long owner` (`m_ownerID`, or the
  **local `Player.GetPlayerID()` if `m_ownerID == 0`**), `string name`, `Vector3 pos`, `int type`, `bool checked`, and `string author`
  (the pin's `m_author`, or the local PlatformUserID if the author is invalid and the owner is the local player). Needs `Player.m_localPlayer`.
- The table's own pins are **not** merged here. They reach the output only because `OnWrite` read them into the writer's map first.

**`AddSharedMapData(bytes)` reads:**
1. The explored array. If its size differs from the local `m_explored.Length`, it logs `"Map exploration array size missmatch"`
   and returns false. Newly explored pixels go to `m_exploredOthers` (the shared-fog layer, `ExploreOthers`).
2. If version ≥ 2, it marks **every foreign pin** (`m_ownerID != 0 && m_ownerID != myPlayerID`) `m_shouldDelete = true`.
3. For each table pin:
   - If any **saved** pin lies within **1 m** (XZ; `HavePinInRange(pos, 1f)`), it keeps that existing pin
     (`m_shouldDelete = false`) and adds nothing.
   - Otherwise, if the table pin's owner ≠ my player ID, it calls `AddPin(pos, type, name, save: true, checked, ownerID: owner, author)`.
   - Table pins owned by me that I no longer have are **not** restored. Deleting your own pin and then writing removes it from the table.
4. It **removes every foreign pin still marked**. Reading a table therefore **replaces your whole set of foreign pins with that
   table's set**. Pins shared through table A disappear when you read table B.

**Rules that matter for a mod's pins:**
- A mod pin is shared through tables only if it is in `Minimap.m_pins` with `m_save = true` and its type is not `Death`.
  The receiver needs no mod. It sees name, position, type, checked state and author. **It does not see a custom sprite:** the receiver calls
  `GetSprite(type)`. An out-of-range type int is coerced to `Icon3` with the warning `"Trying to add invalid pin type"`
  (`Minimap.AddPin`: `if ((int)type >= m_visibleIconTypes.Length || type < Icon0)`, where length = number of `PinType` values = 18).
- **Keep the mod's own pins at `m_ownerID = 0`.** Pins with `m_ownerID != 0` and ≠ the local player ID count as "foreign" and **will be
  deleted by the next table read** if that table lacks them.
- Received pins keep the original owner's player ID. The receiver's map tints them and hides them when "shared map data" is toggled off
  (`Minimap.UpdatePins` uses `m_ownerID != 0`; `m_showSharedMapData`). **Left-clicking a shared pin "adopts" it**: `OnMapLeftClick` sets
  `m_ownerID = 0` if it was non-zero, and otherwise toggles `m_checked`. This matters if you patch `Minimap.OnMapLeftClick`.
- `ReadExploredArray` requires matching texture sizes. The code default is `Minimap.m_textureSize = 256` and `m_pixelSize = 64`;
  the prefab value is **Unverified** (it lives in scene data, not code).

### 5.2 Public position ("visible on map")

- UI: the large-map toggle `Minimap.m_publicPosition` → `OnTogglePublicPosition()` → `ZNet.SetPublicReferencePosition(bool)`.
  The flag is saved **per world in the character's map data** and restored in `Minimap.SetMapData` (`Version.Map.VisibleOnMap`).
  The field has no initializer, so it defaults to false (ZNet).
- `Player.LateUpdate` (owner) calls `ZNet.SetReferencePosition(transform.position)` every frame. The other writers
  (1.0.16, 2026-09-26): `Player.SetLocalPlayer`; `Game.FindSpawnPoint` (logout point, custom spawn, the start point or
  `Vector3.zero`); `Valkyrie.SyncPlayer` when `doNetworkSync`; and the vanilla global `Tracker` MonoBehaviour, which
  writes its own position in `Awake` if its ZNetView is owned then, and in every `FixedUpdate` while `m_active`
  (`Tracker.SetActive` has no callers; which prefabs carry it is **Unverified**). Beware the name: pitfalls.md section 4.
- Every 2 s a client sends `ServerSyncedPlayerData(refPos, publicFlag, dict)` to the server (`ZNet.SendServerSyncPlayerData`).
  **The server always learns everyone's position.** The public flag only controls re-broadcast.
- Every 2 s the server sends `PlayerList` to all peers (`ZNet.SendPlayerList` / `WritePlayerInfo`). Each entry contains name, character ZDOID,
  platform ID, display names and public flag, and **the position only if public**.
- Receivers: `ZNet.GetOtherPublicPlayers()` (public, not self, character ID not None) → `Minimap.UpdatePlayerPins` →
  non-saved `PinType.Player` pins that move toward the target at 200 m/s.
- **This cannot carry arbitrary pins.** It carries exactly one position per player: your reference position.
  `SetReferencePosition` also drives which zones and ZDOs the server sends you and which ZDOs you own (§3.2), so do not fake it.
- `ZNet.m_serverSyncedPlayerData` (public `Dictionary<string,string>`) goes **client → server only**. Vanilla uses the keys
  `possibleEvents`, `baseValue` and `platformDisplayName`. The server never forwards it to other clients; only the display name ends up in
  PlayerList. A **server-side** mod could read custom keys from `ZNetPeer.m_serverSyncedPlayerData`.

### 5.3 Pings (middle-click on the map)

- `Minimap.OnMapMiddleClick` → `Chat.instance.SendPing(ScreenToWorldPoint(pointer))`.
- `Chat.SendPing(Vector3 pos)` (**public**): replaces `pos.y` with the local player's y, then
  `ZRoutedRpc.instance.InvokeRoutedRPC(0L, "ChatMessage", pos, 3 /*Talker.Type.Ping*/, UserInfo.GetLocalUser(), "")`.
  The sender sees its own ping too, because target 0 also runs locally.
- Receiver `Chat.OnNewChatMessage`: a platform text-permission check (`RelationsManager.CheckPermissionAsync`, skipped for self),
  `'<'`/`'>'` replaced by spaces, and ignored if there is no local player or the player is in the intro. Pings are not added to the chat log.
  An in-world text is created **only if** `Minimap.m_mode != MapMode.None` **or** the message position is within **`m_nomapPingDistance` = 50 m**
  (3D `Vector3.Distance` to the local player). This test applies to every chat type, shouts included, not only pings.
  `MapMode.None` happens only with `Game.m_noMap` (nomap modifier) or when dead (`Minimap.SetMapMode` / `Update`). So pings from far away are
  dropped in nomap worlds.
- `Chat.AddInworldText` keeps **one world text per sender ID** (`FindExistingWorldText(senderID)`), so a new ping, shout or normal/whisper
  chat line from the same sender (Talker's `RPC_Say` also ends in `OnNewChatMessage`) replaces the old one. Ping text is forced to `"PING"`.
  Lifetime is **`Chat.m_worldTextTTL` = 5 s**.
  Both 5 s and 50 m are the code initializers of public serialized MonoBehaviour fields; the values set in the game scene are **Unverified**.
- `Minimap.UpdatePingPins` mirrors ping world texts as non-saved `PinType.Ping` pins labelled `"<display name>: PING"`
  (double size, animated).
- **A mod can call `Chat.instance.SendPing(anyPos)`.** Every vanilla player then sees a 5-second ping at that XZ position. Only one is visible
  per sender at a time, it carries no label, and it is subject to the nomap/50 m rule.

### 5.4 Shouts

- `Chat.SendText(Talker.Type.Shout, text)` → `CheckPermissionsAndSendChatMessageRPCsAsync`. If the platform has no
  `RelationsProvider`, it makes one call with target `0L`. Otherwise it sends one RPC to self plus one per player
  (`playerInfo.m_characterID.UserID`) that passes the permission check. Each is `"ChatMessage"(headPoint, 2 /*Shout*/, userInfo, text)`.
- Receivers add the text to the chat log (upper-cased, yellow) and to the world text / shout pin
  (`Minimap.UpdateShoutPins` → non-saved `PinType.Shout`, `"<name>: <text>"`, same 5 s TTL, same one-per-sender rule).
- A mod could invoke `"ChatMessage"` with an arbitrary position and type 2 to place a labelled 5-second marker. It is also a
  visible chat line, so it is only suitable for deliberate announcements.
- `Talker.Type` values: Whisper 0, Normal 1, Shout 2, Ping 3 (Talker / decompiled).

### 5.5 `RPC_DiscoverLocationResponse` — a vanilla "add a saved pin" RPC

- `Game.Start` registers `RPC_DiscoverLocationResponse(long sender, string pinName, int pinType, Vector3 pos, bool showMap)` on **every
  peer**. It calls `Minimap.instance.DiscoverLocation(pos, (PinType)pinType, pinName, showMap)`. If `Minimap.m_mode == MapMode.None`
  (nomap world, or dead), it also turns the player to face the position (`SetLookDir`, 3.5).
- `Minimap.DiscoverLocation` (public): returns false if there is no local player. If `HaveSimilarPin` finds the same name, type and save flag
  within 1 m, and `showMap` is set, it shows `$msg_pin_exist` and opens the map. Otherwise it calls **`AddPin(pos, type, name, save: true, isChecked: false, ownerID: 0)`**,
  shows `"$msg_pin_added: " + name` at the top left with the icon, and, if `showMap`, **forces the large map open** at that point
  (`ShowPointOnMap` → `SetMapMode(Large)`).
- Vanilla flow: `Vegvisir` or `RuneStone` → `Game.DiscoverClosestLocation` → server `RPC_DiscoverClosestLocation` → server replies
  `RPC_DiscoverLocationResponse` to the requester (or once per location if `discoverAll`).
- **Any peer can invoke it on any other peer:** `ZRoutedRpc.instance.InvokeRoutedRPC(targetPeerId,
  "RPC_DiscoverLocationResponse", name, (int)type, pos, false)`. The receiver needs **no mod**. The pin becomes a normal **own**
  saved pin (owner 0) in the receiver's character file.
  Etiquette: pass `showMap=false` to avoid hijacking their screen. Target specific peers, not 0 (see §4.2 on server-side handlers).
- Server-side note: `RPC_DiscoverClosestLocation` has no permission check (it is registered on the server only). Any client can ask the
  server for the location of any location prefab name (Game / decompiled).

### 5.6 Which channel can carry a mod's own pins to other players?

| Channel | Persistent on receiver? | Receiver needs mod? | Label/type | Conditions / limits |
|---|---|---|---|---|
| Cartography table | yes (as shared pins, owner = author's player ID) | no | name + vanilla type (custom → Icon3) | pin `m_save=true`, not Death; the other player must *read* a table; reading another table deletes foreign pins absent from it; writer needs ward access |
| `RPC_DiscoverLocationResponse` | yes (as own pin) | no | name + vanilla type | per pin, per target peer; dedupe by same name, type and pos within 1 m; top-left message per pin; optional forced map open |
| Ping (`Chat.SendPing`) | no, 5 s | no | "PING" only | one per sender at a time; y replaced; dropped beyond 50 m in nomap |
| Shout (`ChatMessage` type 2) | no, 5 s | no | "name: text" | also appears in chat; one per sender at a time; world text and pin dropped beyond 50 m in nomap |
| Public position | no (live) | no | player name | only your own position |
| Custom routed RPC | as you implement | **yes** | anything (custom sprites etc.) | vanilla server relays; unknown hash ignored by vanilla peers |

### 5.7 Pin types (PinType enum, Minimap / decompiled) — corrects an earlier fact sheet

`Icon0=0, Icon1=1, Icon2=2, Icon3=3, Death=4, Bed=5, Icon4=6, Shout=7, None=8, Boss=9, Player=10, RandomEvent=11,
Ping=12, EventArea=13, Hildir1=14, Hildir2=15, Hildir3=16, Memorial=17`. Pins the player places create `save: true` pins
(`Minimap.ShowPinNameInput`: `m_namePin = AddPin(pos, m_selectedType, "", save: true, isChecked: false, 0L)`). Bed, Player, Ping, Shout, event and location pins are `save: false`.
Saved map data: `Version.Map` = 8 (`PinsAuthor`). It stores every `m_save` pin, **including Death pins**, and the public flag (Minimap.GetMapData).

---

## 6. Console, cheat and admin gating

### 6.1 `Terminal.ConsoleCommand`

- The constructor registers itself: `commands[command.ToLower()] = this`. **The same name overwrites** an existing command, including vanilla ones.
  `Terminal.InitTerminal()` is private static and guarded by `m_terminalInitialized`. It is called from every `Terminal.Awake` (Console and Chat).
  A postfix on it therefore runs several times per process, which is harmless because constructors just overwrite.
- Two constructors: `ConsoleEvent` (void) and `ConsoleEventFailable` (returns `object`; `false` or a `string` prints an error).
  **Only the Failable constructor ORs `onlyAdmin` into `OnlyServer`**. **`OnlyAdmin` and `AllowInDevBuild` are otherwise never read**
  (grep of the whole assembly).
- `IsValid(context)` = `(!IsCheat || context.IsCheatsEnabled()) && context.isAllowedCommand(this) && (!IsNetwork || ZNet.instance)
  && (!OnlyServer || ZNet.instance.IsServer())`.
- **`Terminal.IsCheatsEnabled()` = `m_cheat && ZNet.instance && ZNet.instance.IsServer()`.** On a client connected to someone
  else's server, **no `isCheat` command is valid**, even for admins and even after `devcommands`.
- `TryRunCommand(text)`: if the command is valid, it runs it. Otherwise, if `RemoteCommand` is set and we are a client, it calls `ZNet.RemoteCommand(text)` →
  server `RPC_RemoteCommand` → **admin-list check** (`"You are not admin"`) → `Console.instance.TryRunCommand(command)` **on the
  server**, which applies the server's own `m_cheat` and IsServer gating. Otherwise it prints "not valid in the current context".
- **`ZNet.RemoteCommand(text)` throws on a server** ([both], 1.0.16, 2026-09-26): there it calls
  `InternalCommand(null, text)`, whose log line dereferences `rpc.GetSocket()`, so a NullReferenceException. A mod on
  the server runs a command with `Console.instance.TryRunCommand(text)`, which is what `InternalCommand` would do next.
- `RunAction`: for `IsCheat` commands, if `!Achievements.IsCheatedAtAll()` and the command is not `confirmcheats`, it prints
  `$achievements_confirm_cheat` and does **not** run. The player must run `confirmcheats` once. Afterwards `PlayerProfile.m_usedCheats = true`
  (the character is permanently flagged). **`Game.isModded = true` makes `IsCheatedAtAll()` true** in vanilla, which skips the
  confirmation step (the Unshamed mod patches that result back to false, §1.2).
- `HideBehindDevCommands` only affects **listing and autocomplete** (`ShowCommand`: if `!m_cheat` it returns `!HideBehindDevCommands`). Such commands still
  **execute** without devcommands if they are valid. `IsSecret` hides from lists; `devcommands` itself is secret.
- Chat (`/cmd`) is also a `Terminal`, but `Chat.isAllowedCommand` returns false for every `IsCheat` command.

### 6.2 Enabling the console and devcommands

- The console opens only if `Console.IsConsoleEnabled()`: the `-console` launch argument (`FejdStartup.ParseArguments` →
  `SetConsoleEnabledForThisSession`) or the Gameplay settings toggle, which sets `PlatformPrefs "EnableConsole"`
  (`Valheim.SettingsGui.GameplaySettings`). It is disabled in demo mode.
- `devcommands` (secret, not a cheat) toggles the static `Terminal.m_cheat`. **On a client it also sends
  `ZNet.RemoteCommand("devcommands")`**. If the client is an admin, that toggles the **server's** `m_cheat` independently, so the two flags can
  drift apart (two admins toggling, for example).
- Commands forwarded to the server (`remoteCommand: true`): `confirmcheats, genloc, players, setkey, removekey, resetkeys,
  resetworldkeys, setworldpreset, setworldmodifier, listkeys, sleep, skiptime, restartparty`. **All other cheat commands are not
  remote.** Examples: `fly, debugmode, nocost` (isCheat + onlyServer) and `god, ghost, spawn, goto, killall, tame, heal, setpower, recall`
  (isCheat + onlyAdmin). They work only when you are the server. From this code, admins on a vanilla dedicated server cannot use them
  without a mod such as ServerDevcommands (`JereKuusela-Server_devcommands`). The server runs the same gating: `Terminal`'s command
  code (all but `AddString`) and `ZNet.RPC_RemoteCommand` have identical IL in the server's DLL (section 1.4, 2026-09-26;
  corrected, this said the server side was Unverified). How `confirmcheats` behaves on a headless server was not traced.

### 6.3 Admin, ban and permitted lists

- These exist **only on the server/host** (created in `ZNet.Awake` when `m_isServer`) as files in the save-data path:
  `adminlist.txt`, `bannedlist.txt`, `permittedlist.txt` (local or cloud storage). The server sends the admin list to each client
  on connect (`AdminList` RPC → `m_adminListForRpc`).
  - **Where** (1.0.16, 2026-09-26): with local storage, `Utils.GetSaveDataPath(Local) + "/adminlist.txt"`, i.e. the
    `-savedir` folder when one is given, else `persistentDataPath` (`ZNet.Awake`, [S]). Seen live: a dedicated server
    started with `-savedir X` wrote `X\adminlist.txt`, `bannedlist.txt`, `permittedlist.txt` and `X\worlds_local\`.
  - **File format** (`SyncedList`, assembly_utils): one ID per line; lines starting with `//` are comments, empty lines
    are skipped, and every other line is compared **exactly** (case-sensitive, not trimmed: a trailing space breaks the
    entry). On access the file is re-read at most every 10 s, and only if its write time changed.
- **Matching** (`ZNet.ListContainsId(list, id)`, [both], 1.0.16, 2026-09-26): the id is parsed as a `PlatformUserID`
  (`<Platform>_<id>`), or else taken as a Steam id. For Steam an entry matches as `Steam_<id>`, the bare `<id>`, or `V_<id>`
  (the display form from `PlatformUserID.FilterPlatformUserID`, whose prefix map has Steam → `V`). For other platforms only
  the exact `<Platform>_<id>`, or the `FilterPlatformUserID` form (for a platform whose numbers are filtered, the id times
  11400714819323198485). Server Devcommands patches `ZNet.ListContainsId` (`scan-mod-patches.ps1`, 2026-09-26).
- **The id the server checks is the socket's host name**: on Steam `ZSteamSocket.GetHostName()` = the SteamID64 in decimal;
  on crossplay `ZPlayFabSocket.GetHostName()` = `m_platformPlayerId.ToString()`, `<Platform>_<id>`, which that socket takes
  from the peer's first message. `PlayFabManager.CheckIfUserAuthenticated` is a stub that always answers true, in both DLLs.
  **Unverified:** whether PlayFab's own network layer authenticates that id, so whether a crossplay client could claim
  another player's.
- The server checks the admin list via the **socket host name** (not spoofable through RPC arguments) only in: `RPC_Kick,
  RPC_Ban, RPC_Unban, RPC_PrintBanned, RPC_Save, RPC_RemoteCommand` (ZNet). `RandEventSystem` also has console start/reset handlers
  that would look up the spoofable routed `sender`, but they are never registered in this build (§4.2). A mod's own
  check: `ZNet.instance.IsAdmin(rpc.GetSocket().GetHostName())` with the connection captured as in §4.2.
- **The host is not a peer:** its uid (`ZNet.GetUID()` == `ZDOMan.GetSessionID()`) is not in `m_peers` (`GetPeer(uid)`
  returns null) and is not put in the admin list automatically. A call with no connection is a local call, i.e. the host.
- Client-side check: `ZNet.instance.LocalPlayerIsAdminOrHost()` = `IsServer() || PlayerIsAdmin(local user)`, and
  `PlayerIsAdmin(PlatformUserID)` is an exact string match of `ToString()` (e.g. `Steam_7656...`) against the received
  list. So a bare numeric or `V_` SteamID entry passes the server's `ListContainsId` checks but not this client-side test.
  `ZNet.IsAdmin(hostName)` (= `ListContainsId(m_adminList, hostName)`) works only on the server; on clients `m_adminList` is null.
- If the permitted list is non-empty, it is a whitelist. It is enforced at connect and re-checked every 5 s (`CheckWhiteList`).

---

## 7. What a purely client-side mod can and cannot affect in someone else's game

**Can do.** The code contains no server-side validation for any of these (the handlers were read):
- Write **any key on any ZDO it has locally**, and claim ownership of any object (§3.3). The server and other clients accept newer revisions.
  This includes other players' buildings, containers, table data, and so on.
- Invoke **vanilla** handlers on other peers: add saved pins (`RPC_DiscoverLocationResponse`), show pings and shouts (`ChatMessage`),
  show HUD messages (`ShowMessage` via `MessageHud.MessageAll`), teleport a player (`Chat.RPC_TeleportPlayer` has no check), spawn
  prefabs on every peer (`SpawnObject`), overwrite a table (`MapData`), set or remove global keys (`ZoneSystem.RPC_SetGlobalKey` has no
  check), and request location positions (`RPC_DiscoverClosestLocation`).
- Anything about its own player (position, health, inventory in its own profile). The server never validates player state.
- Some of these are abuse vectors. Use only what the user's feature needs, keep it opt-in, and never teleport or spam other players.

**Cannot do:**
- Run custom code on another peer. Custom RPCs need the handler there, and unknown hashes are dropped.
- Change another player's **local** state except through vanilla handlers that do so: their character file, pins, fog, UI or
  config. Pins only arrive via tables they choose to read, or via `RPC_DiscoverLocationResponse`.
- Pass the server's admin-list checks (kick, ban, save, remote console), because those use the socket identity.
- Run `isCheat` console commands on a remote server (`IsCheatsEnabled` requires `IsServer`). This is only UI gating: the mod can still
  call the underlying game methods for its own player.
- See server-only data that no vanilla RPC exposes: location instances (except via the Vegvisir RPC or icons),
  other peers' `m_serverSyncedPlayerData`, and private positions. (The server knows every position; clients get only public ones.)
- See ZDOs outside its own sync area. The local `ZDOMan` is only a subset.

---

## 8. Pitfalls checklist

- Register global RPCs once per session (postfix `Game.Start`). A second `Register` with the same name in the same session **throws**.
- Never pass enums, `byte`, `short`, `byte[]` or `Vector2` as RPC arguments. They are silently dropped. Match `int`/`long` exactly.
- Do not trust `sender` in routed RPCs: it can be forged. Identify the caller by its connection (§4.2).
- Only the owner writes ZDO data. Otherwise RPC the owner, or `ClaimOwnership()` first. Destroy only as owner.
- Keep mod pins at `m_ownerID = 0`. Foreign-owned pins are wiped by the next table read that lacks them.
- `Minimap.PinType` ints: Death=4, Bed=5, Icon4=6 (an earlier fact sheet had them in the wrong order).
- On a client, `ZNet.GetPeers()` contains only the server. Address players via `PlayerInfo.m_characterID.UserID`.
- `ZNet.IsDedicated()` is always false in the client DLL (always true in the server's), and `IsCurrentServerDedicated()` is
  unreliable on clients.
- Guard server/headless code paths. On a dedicated server `Player.m_localPlayer` is **never** set (code, §1.4);
  `Minimap`, `Chat` and `Hud` are in its scene (scene data, not seen live), so their instances are expected to exist.
- Send to a new peer from `ZRoutedRpc.instance.m_onNewPeer`, not earlier (§1.3), and not with `InvokeRoutedRPC(name, ...)`
  before the handshake, which runs locally (§4.2).
- `ZNet.instance.GetPlayerList()` is refreshed from the server every 2 s. It includes the host when the host is not headless.

## 9. Unverified / out of reach

- Dedicated-server DLL behaviour, mostly settled on 2026-09-26 (§1.4: the whole-assembly diff, the scene contents).
  Still open: the `confirmcheats` flow on a headless server, whether Unity calls `OnGUI` under `-batchmode -nographics`,
  and anything only a live server with a connected player would show (the scene's `Minimap`/`Chat`/`Hud` instances
  are expected from scene data, not seen). Decompile the server with
  `decompile.ps1 -Assembly "...\Valheim dedicated server\valheim_server_Data\Managed\assembly_valheim.dll"`.
- The Linux executable name is unknown. (The Windows name `valheim_server.exe` was seen on disk, 2026-09-26.) If it is
  `valheim_server.x86_64`, `[BepInProcess("valheim_server.exe")]` matches it too (environment.md, boot chain step 3).
- The prefab value of `Minimap.m_textureSize` (the code default is 256), and likewise the scene values of `Chat.m_worldTextTTL`
  (code default 5 s) and `Minimap.m_nomapPingDistance` (code default 50 m).
- What an oversize Steam message does to the connection (§4.2: the limit is 524288 bytes, the send fails and stays queued;
  the stall until timeout is inferred). Steam's in-order delivery of reliable messages (§1.2).
- Runtime behaviour of exceptions inside `DynamicInvoke` (§4.3) is derived from .NET semantics, not observed.

Decompiled sources were produced with the ILSpy engine; regenerate any type with
`valheim-modding/scripts/decompile.ps1 -Type <Type>` (add `-Assembly assembly_utils` for utility types).
