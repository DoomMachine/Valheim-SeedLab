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
  Steam app with its own DLL (inferred: nothing in this DLL starts a dedicated server, and the server is not installed here; see §9).
  `valheim_server.exe` is the process name that mods target.
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
| Dedicated server process | true | true | false | **Unverified**, presumably true in the server DLL |

- `IsServer()` returns the static `m_isServer` (ZNet.IsServer / decompiled). It is set by
  `ZNet.SetServer(server, openServer, publicServer, name, password, world)`. The main menu calls
  `SetServer(true, openServerToggle, publicServerToggle, ...)` to host, and `SetServer(false, false, false, "", "", null)` to join
  (FejdStartup / decompiled).
- `IsSinglePlayer` => `m_isServer && !m_openServer` (ZNet.IsSinglePlayer / decompiled).
- **`public bool IsDedicated() { return false; }`** in this assembly (ZNet.IsDedicated / decompiled). Every
  `IsDedicated()` branch in the client DLL is therefore dead. `FejdStartup.ParseServerArguments()` handles the
  dedicated-server flags (`-world -name -port -password -savedir -public -logfile -crossplay -instanceid -backups
  -backupshort -backuplong -saveinterval -preset -modifier -resetmodifiers -setkey`, default port **2456**, default world
  "Dedicated"). It exists in the client DLL, but nothing calls it: grepping the whole decompiled assembly finds only its definition.
- **Detecting "I am a headless server" from a mod.** The game tests `SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null`
  itself. For example, `ZNet.UpdatePlayerList` adds the local player to the player list only when the device is **not** Null.
  Use that test, or `IsDedicated()` (**Unverified** in the server DLL).
- `IsCurrentServerDedicated()` returns true if any ready peer has `m_characterID.IsNone()` (ZNet / decompiled).
  **Pitfall:** only the server-side handler `ZNet.RPC_CharacterID` ever writes `peer.m_characterID`. Clients send "CharacterID"
  to the server, and the server never sends it back. So **on a client this returns true whenever it is connected, even to a
  listen host**. The only vanilla caller is `RelationsManager.UpdateAuthorIfHost`. A better test on a client: is the server's peer UID
  (`ZNet.instance.GetServerPeer().m_uid`) equal to some `PlayerInfo.m_characterID.UserID` in `GetPlayerList()`? If yes, a player is
  hosting. This is derived from the code and not tested in-game.
- **Process name:** `valheim_server.exe`. The dedicated server is not installed here. The name is corroborated by a third-party
  mod (Crystal-Comfortable 1.0.5's `Comfortable.dll`, which carries
  `[BepInProcess("valheim.exe")]` and `[BepInProcess("valheim_server.exe")]`). **Unverified** from game code. The Linux binary name is
  **Unverified**.
  What *is* verified is how the filter matches: `Chainloader.Start` compares `ProcessName.Replace(".exe", "")` with
  `Paths.ProcessName` case-insensitively, so `[BepInProcess("valheim.exe")]` keeps a plugin off any process not named `valheim`
  (the dedicated server included) **without** restricting it to Windows — see environment.md, boot chain step 3.

### 1.2 What runs where (verified examples)

- **Server only:** assigning ZDO ownership (`ZDOMan.Update` calls `ReleaseZDOS` only if `IsServer()`), saving the world
  (`ZNet.SaveWorld`), the ban and permitted lists (`ZNet.UpdateBanList` runs every 5 s, server only), net time
  (`ZNet.SendNetTime` every 2 s; clients overwrite theirs in `RPC_NetTime`), global keys (`ZoneSystem` registers
  `SetGlobalKey`/`RemoveGlobalKey` only when it is the server and broadcasts `GlobalKeys`), location instances
  (`ZoneSystem.GetLocationIcons` reads `m_locationInstances` only on the server; clients receive only an icon list via the
  `LocationIcons` RPC), and Vegvisir lookups (`Game.Start` registers `RPC_DiscoverClosestLocation` only if `IsServer()`).
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
  (FejdStartup) and makes `Achievements.IsCheatedAtAll()` return true.

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
4. The peer is added to `ZDOMan` and `ZRoutedRpc` (`AddPeer`). The server immediately sends `PlayerList`, `HistoricalPlayerList` and `AdminList`.

`ZNet.ConnectionStatus` enum values: None 0, Connecting 1, Connected 2, ErrorVersion 3, ErrorDisconnected 4,
ErrorConnectFailed 5, ErrorPassword 6, ErrorAlreadyConnected 7, ErrorBanned 8, ErrorFull 9,
ErrorPlatformExcluded 10, ErrorCrossplayPrivilege 11, ErrorKicked 12 (ZNet / decompiled).

Timing constants: `ZRpc` sends a keep-alive ping every **1 s** and times out after **30 s** (**90 s** with `SetLongTimeout(true)`) (ZRpc).
`ZNet.SendPeriodicData` runs every **2 s**. On the server it sends NetTime and PlayerList. On clients it sends `ServerSyncedPlayerData`
(refPos, public flag, and the `m_serverSyncedPlayerData` dictionary). Kicked peers are disconnected **1 s** after the "Kicked" RPC (ZNet.InternalKick).

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

### 3.2 Who owns what

- The creator owns a new ZDO (`CreateNewZDO` → `SetOwnerInternal(m_sessionID)`).
- **Every 2 s the server rebalances ownership** (`ZDOMan.ReleaseZDOS` → `ReleaseNearbyZDOS`, first for the server's own reference position, then for each peer).
  This applies only to **persistent** ZDOs near that reference position:
  - If the ZDO is owned by this peer but outside the peer's active area: `SetOwner(0)` (released).
  - If the ZDO is unowned, or its owner is not in its own active area, and the ZDO is inside this peer's active area: `SetOwner(peer)`.
  - Consequence: whoever is nearby simulates the object, and ownership moves as players walk.
- The active area is based on zones. For ownership, `ZNetScene.PointInsideActiveArea` and `ReleaseNearbyZDOS` use the **server's own**
  `ZNet.GetSyncedSimulationDistance()` for every peer, not each peer's negotiated value. The per-peer negotiated distance (the minimum of
  the client's request and the server's, stored in `ZNetPeer.m_simulationDistance` by `ZNet.RPC_RequestValidSimulationDistance`) only
  decides which ZDOs are sent to that peer (`ZDOMan.CreateSyncList`). `SimulationDistance.OriginalNear/Far = 2`.
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
  sends to the server's peer ID.
- Sender side: if `target == myId || target == 0`, the handler runs **locally, synchronously**. If `target != myId`, the message
  is sent to all ready peers. A client's only peer is the server.
- Server side: on receipt it handles the message if the target is itself or 0. If the target is not itself, it forwards: to the specific peer if
  that peer is ready, otherwise the message is dropped silently; for Everybody, to all ready peers **except the sender**. **Forwarding does not
  depend on the server having the handler.** A vanilla server relays mod RPCs.
- Receiver: `m_functions.TryGetValue(hash)`. An unknown hash is **ignored silently** (no log for global RPCs).
- For Everybody the server runs its own handler **before** forwarding (`HandleRoutedRPC` then `RouteRPC`). An exception in
  the server's handler would therefore stop the forward. Target specific peers when a vanilla handler might throw on a headless server.
  **Unverified** whether e.g. `Minimap.instance` is null on a dedicated server.
- **`sender` is client-asserted.** `RoutedRPCData.m_senderPeerID` is read from the packet and forwarded unchanged. Never
  authenticate by `sender`. Vanilla's admin checks on `ZNet`'s peer RPCs use `rpc.GetSocket().GetHostName()` instead, which the client cannot fake.
  `RandEventSystem.RPC_ConsoleStartRandomEvent` / `RPC_ConsoleResetRandomEvent` would trust `ZNet.instance.GetPeer(sender)`, but in this build
  they are **never registered** (no `Register` call references them), so a client's `"startrandomevent"` / `"resetrandomevent"` routed RPC is
  silently ignored by the server.

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

- `ZRoutedRpc.instance` is created in every `ZNet.Awake` (`m_routedRpc = new ZRoutedRpc(m_isServer)`), so **re-register once
  per session**. Vanilla registers its global RPCs in `Game.Start`, `Chat.Awake`, `ZNetScene.Awake`, `ZoneSystem` and elsewhere, and
  a postfix on `Game.Start` is a proven point. `ZRoutedRpc.instance.m_onNewPeer` (public `Action<long>`) fires for every new peer;
  `ZoneSystem` uses it to push global keys and location icons.
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
- `Player.LateUpdate` (owner) calls `ZNet.SetReferencePosition(transform.position)` every frame.
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
- `RunAction`: for `IsCheat` commands, if `!Achievements.IsCheatedAtAll()` and the command is not `confirmcheats`, it prints
  `$achievements_confirm_cheat` and does **not** run. The player must run `confirmcheats` once. Afterwards `PlayerProfile.m_usedCheats = true`
  (the character is permanently flagged). **`Game.isModded = true` makes `IsCheatedAtAll()` true**, which skips the confirmation step.
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
  without a mod such as ServerDevcommands (`JereKuusela-Server_devcommands`). The server-side half of that claim is **Unverified** (no server DLL here).

### 6.3 Admin, ban and permitted lists

- These exist **only on the server/host** (created in `ZNet.Awake` when `m_isServer`) as files in the save-data path:
  `adminlist.txt`, `bannedlist.txt`, `permittedlist.txt` (local or cloud storage). The server sends the admin list to each client
  on connect (`AdminList` RPC → `m_adminListForRpc`).
- Entry format: `ZNet.ListContainsId` accepts `"Steam_<id64>"` or the bare numeric SteamID for Steam, and `PlatformUserID.ToString()`
  for other platforms.
- The server checks the admin list via the **socket host name** (not spoofable through RPC arguments) only in: `RPC_Kick,
  RPC_Ban, RPC_Unban, RPC_PrintBanned, RPC_Save, RPC_RemoteCommand` (ZNet). `RandEventSystem` also has console start/reset handlers
  that would look up the spoofable routed `sender`, but they are never registered in this build (§4.2).
- Client-side check: `ZNet.instance.LocalPlayerIsAdminOrHost()` (true if server, else local PlatformUserID in the received
  admin list). It is an exact string match on `PlatformUserID.ToString()` (e.g. `Steam_7656...`), so a bare numeric SteamID entry passes the
  server's `ListContainsId` checks but not this client-side test. `ZNet.IsAdmin(hostName)` works only on the server; on clients `m_adminList` is null.
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
- Do not trust `sender` in routed RPCs.
- Only the owner writes ZDO data. Otherwise RPC the owner, or `ClaimOwnership()` first. Destroy only as owner.
- Keep mod pins at `m_ownerID = 0`. Foreign-owned pins are wiped by the next table read that lacks them.
- `Minimap.PinType` ints: Death=4, Bed=5, Icon4=6 (an earlier fact sheet had them in the wrong order).
- On a client, `ZNet.GetPeers()` contains only the server. Address players via `PlayerInfo.m_characterID.UserID`.
- `ZNet.IsDedicated()` is always false in the client DLL, and `IsCurrentServerDedicated()` is unreliable on clients.
- Guard server/headless code paths: `Player.m_localPlayer`, `Minimap.instance` and `Chat.instance` may be null. **Unverified** which
  of these exist on a dedicated server.
- `ZNet.instance.GetPlayerList()` is refreshed from the server every 2 s. It includes the host when the host is not headless.

## 9. Unverified / out of reach

- Dedicated-server DLL behaviour (`IsDedicated()` value, whether `Minimap`, `Chat` or `Game.instance` exist there, and the `confirmcheats`
  flow on a headless server). The dedicated server (Steam app "Valheim Dedicated Server") is not installed on this machine.
- The `valheim_server.exe` process name: evidence is a third-party `BepInProcess` attribute only. The Linux executable name is unknown.
- The prefab value of `Minimap.m_textureSize` (the code default is 256), and likewise the scene values of `Chat.m_worldTextTTL`
  (code default 5 s) and `Minimap.m_nomapPingDistance` (code default 50 m).
- The Steam per-message size limit for large table packages. `ZSteamSocket` sends with flag 8 (reliable). Steamworks' own limit is
  external knowledge, not checked here.
- Runtime behaviour of exceptions inside `DynamicInvoke` (§4.3) is derived from .NET semantics, not observed.

Decompiled sources were produced with the ILSpy engine; regenerate any type with
`valheim-modding/scripts/decompile.ps1 -Type <Type>` (add `-Assembly assembly_utils` for utility types).
