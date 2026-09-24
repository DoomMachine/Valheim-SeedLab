# 06 — Seed space: the mathematics of Valheim seeds, and the seed-text tooling

Target: Valheim **1.0.15**, world-gen version 2. Every constant below is taken from the decompiled
shipped assemblies; every count below was computed, not estimated, and each load-bearing count was
produced by at least two independent implementations. Reproduction commands are in §10.

---

## 0. Executive answers

| Question | Answer |
|---|---|
| Number of seed texts, 1–10 chars, `A–Z a–z 0–9` | **853,058,371,866,181,866** — the user's figure is **exactly correct** (§2) |
| Number of distinct **worlds** those can produce | **at most 2³² = 4,294,967,296** (§4) |
| Are all 2³² actually reachable from a ≤10-char alphanumeric text? | **Yes.** Proven exactly (§5) |
| Shortest length L at which *every* int32 is reachable | **L = 7**, for both the 62-char and the game's own 59-char alphabet (§5) |
| Seed texts per world, on average | **198,618,129.796** (§8) |
| What a search tool must enumerate | **the 2³² int seeds**, never seed strings (§8) |
| int32 → typeable seed text | solved in **0.08 ms** with **8 MB** of tables, no large bitmap (§6); **0.34 ms / ~10 MB** for output that is statistically indistinguishable from a game-suggested seed (§6.1.1) |

The practical consequence: the user's 853 quadrillion is a real number but it is not the search space.
The search space is 4,294,967,296 — a factor of 198.6 million smaller, and fully enumerable.

---

## 1. The hash — exact code and constants

`StringExtensionMethods.GetStableHashCode` (`assembly_utils.dll`, `public static` extension; verbatim):

```csharp
public static int GetStableHashCode(this string str)
{
    int num = 5381;
    int num2 = num;
    for (int i = 0; i < str.Length && str[i] != 0; i += 2)
    {
        num = ((num << 5) + num) ^ str[i];
        if (i == str.Length - 1 || str[i + 1] == '\0')
        {
            break;
        }
        num2 = ((num2 << 5) + num2) ^ str[i + 1];
    }
    return num + num2 * 1566083941;
}
```

Consumption (`World..ctor(string name, string seed)`, verbatim):

```csharp
m_worldName = (m_name = name);
m_seedName  = seed;
m_seed      = ((!(m_seedName == "")) ? m_seedName.GetStableHashCode() : 0);
m_uid       = name.GetStableHashCode() + Utils.GenerateUID();
m_worldGenVersion = 2;
```

Constants to hard-code:

| Name | Decimal | Hex | Source |
|---|---|---|---|
| lane seed `h0` | 5381 | `0x1505` | `GetStableHashCode` |
| lane multiplier | 33 (`(h<<5)+h`) | `0x21` | `GetStableHashCode` |
| lane combiner `K` | 1566083941 | `0x5D588B65` | `GetStableHashCode` |
| 33⁻¹ mod 2³² | 1041204193 | `0x3E0F83E1` | computed; `33 × 1041204193 ≡ 1 (mod 2³²)` |
| empty-text seed | 0 | — | `World..ctor` forces 0, bypassing the hash |

Rules that matter for a port:

- **All arithmetic is unchecked 32-bit and wraps.** The IL uses plain `add`/`mul`/`shl`, not the
  `.ovf` forms *(IL dump of `StringExtensionMethods.GetStableHashCode`; recorded in
  `valheim-worldgen/references/seeds-and-world-files.md` §2.1)*. Implement in `uint`/`unchecked`.
  *Re-verified by review, directly from the assembly* (Mono.Cecil over
  `valheim_Data/Managed/assembly_utils.dll`): the method body is
  `ldc.i4 5381 / stloc.0 / stloc.1 / … / shl / add / callvirt String::get_Chars / xor / … /
  ldc.i4 1566083941 / mul / add / ret` — every `add`, `mul` and `shl` is the plain opcode, no
  `add.ovf` / `mul.ovf` anywhere, and the loop guard is `bge.s` on `String::get_Length` followed by
  `brtrue.s` on `get_Chars(i)` (the `str[i] != 0` test).
- **`str[i]` is a UTF-16 code unit**, not a Unicode scalar. A non-BMP character is two code units and
  therefore occupies one even-lane slot and one odd-lane slot.
- **No trimming, no case folding, no normalisation.** `"abc"`, `"Abc"` and `"abc "` are three
  different worlds *(`FejdStartup.OnNewWorldDone` passes `m_newWorldSeed.text` straight into
  `new World(text, text2)`)*.
- **A NUL character terminates the string** (both the `str[i] != 0` guard and the `str[i+1] == '\0'`
  break). `"\0"` and `""` both give the raw hash 371857150, but `World..ctor` maps `""` to seed 0 and
  `"\0"` to 371857150 — they are *different worlds*. The UI cannot produce a NUL (§7), so a tool must
  simply never emit one.
- **The same function names prefabs** (location prefab hashes in `.db2`), so the port is reusable.

### 1.1 Verification vectors (use these as unit tests)

| Input | `GetStableHashCode` | Note |
|---|---|---|
| `"MWd8eV6svz"` | **-1772362158** | the user's real world `asdasdasd`; matches the `m_seed` stored in its `_main.N.fwl2` |
| `"hnBd9gJf2G"` | **319486907** | the real second save `testworldclaude`; matches the `m_seed` stored in its `_main.N.fwl2` |
| `"j"` | **372029384** | single character — exercises the `L = 1` path, where `num2` is never touched |
| `"StartTemple"` | **-1544986047** | prefab hash seen in a real `.db2` |
| `"Eikthyrnir"` | **-316818231** | prefab hash seen in a real `.db2` |
| `""` | 371857150 (raw) → **seed 0** | `World..ctor` special case |
| `"a"` | 372029373 | |
| `"ab"` | 1093630535 | |
| `"abc"` | 1099313834 | |
| `"Abc"` | 1099314826 | case sensitivity |
| `"abc "` | -1139976598 | trailing space is significant |

> *Corrected by review.* `"j"` was previously labelled "second real save on this machine". It is not a
> save: `valheim_saves.py worlds` lists exactly two worlds on this machine — `asdasdasd` (seed text
> `MWd8eV6svz`) and `testworldclaude` (seed text `hnBd9gJf2G`) — and
> `%USERPROFILE%\AppData\LocalLow\IronGate\Valheim\worlds_local\` contains **no `.fwl2` at all**
> (only minimap/biome caches), so there is no third world anywhere. The hash value 372029384 is correct;
> only the provenance note was wrong. The genuine second ground-truth vector is `"hnBd9gJf2G"` →
> 319486907, added above. `"StartTemple"` (-1544986047) and `"Eikthyrnir"` (-316818231) were both
> re-confirmed present in `asdasdasd\_main.3.db2`
> (`valheim_saves.py locations asdasdasd`, 12314 location instances).

Four independent implementations agree on all of these: the C# port in
`scratchpad/probe/seedspace`, a throwaway Python port written for this task, the pre-existing
`valheim-worldgen/scripts/valheim_saves.py seed "<text>"` (`stable_hash`; that script's `seed`, `worlds`
and `locations` commands have counterparts in `vseed hash`, `vseed worlds` and
`vseed world <name> --locations` today - the last only counts; the per-instance comparison is the
location gate),
and the reviewer's
independent C# port in `scratchpad/probe/review/rv`. The two real-save values are ground truth read
off disk.

---

## 2. The seed-text space (the user's number)

Exact integer computation, alphabet `A–Z a–z 0–9` (62 symbols), lengths 1..10:

```
62^1  =                     62
62^2  =                  3,844
62^3  =                238,328
62^4  =             14,776,336
62^5  =            916,132,832
62^6  =         56,800,235,584
62^7  =      3,521,614,606,208
62^8  =    218,340,105,584,896
62^9  = 13,537,086,546,263,552
62^10 = 839,299,365,868,340,224
------------------------------------
sum   = 853,058,371,866,181,866
```

**853,058,371,866,181,866 — the user's figure is exact.** (≈ 8.53 × 10¹⁷, just under 2⁶⁰.)

Two footnotes:
- The game also accepts the **empty** seed text (it maps to seed 0), so "texts the create-world box
  accepts" is 853,058,371,866,181,**867** if you count it.
- The game's *own* generator uses a 59-symbol alphabet (§3.2); the same sum over that alphabet is
  519,929,111,116,169,700. That is the size of the set of seeds the game will ever *suggest*, not the
  set it will *accept*.

---

## 3. Lane decomposition

### 3.1 The structure to exploit

Write the per-lane step as `h ← (33·h) ^ c` (mod 2³²), starting from `h0 = 5381`. Then for a seed
text `s` of length `L`:

```
E = lane over s[0], s[2], s[4], ...   ceil(L/2) characters   (num)
O = lane over s[1], s[3], s[5], ...   floor(L/2) characters   (num2)
hash(s) = E + K·O        (mod 2^32)
```

Loop-shape proof of the character counts, from the code in §1: for **even L** the loop runs `L/2`
iterations and both lanes consume `L/2` characters; for **odd L** the final iteration updates `num`
with `s[L-1]` and breaks on `i == str.Length - 1` before touching `num2`, so the even lane gets
`(L+1)/2` and the odd lane `(L-1)/2`. `L = 1` leaves `num2 = 5381` untouched.

*Verified computationally*: 200,000 random 1–10 character strings, lane recomposition
`E + K·O` equals the real `GetStableHashCode` on every one (`seedspace selftest`).

Define `E_n` = the set of lane values reachable with exactly `n` characters. Then the set of hashes
of length-`L` texts is the **sumset** `E_ceil(L/2) + K·E_floor(L/2)` over `Z/2³²`. Everything in §5
is a statement about that sumset.

### 3.2 The two alphabets

- **A62** = `0-9 A-Z a-z`, 62 symbols — what a user can type.
- **A59** = `"abcdefghijklmnpqrstuvwxyzABCDEFGHIJKLMNPQRSTUVWXYZ023456789"`, 59 symbols — the
  game's own random suggestion (`World.GenerateSeed`, verbatim):

```csharp
public static string GenerateSeed()
{
    string text = "";
    for (int i = 0; i < 10; i++)
    {
        text += "abcdefghijklmnpqrstuvwxyzABCDEFGHIJKLMNPQRSTUVWXYZ023456789"
                [UnityEngine.Random.Range(0, "abcdefghijklmnpqrstuvwxyzABCDEFGHIJKLMNPQRSTUVWXYZ023456789".Length)];
    }
    return text;
}
```

  No `o`, no `O`, no `1` (lookalike removal). Always exactly **10** characters. Called from
  `FejdStartup.OnWorldNew` to prefill the box, and from `World.GetCreateWorld` (the dedicated-server
  `-world` path). There is **no `-seed` command-line argument** *(`FejdStartup.ParseServerArguments`)*.

### 3.3 Lane set sizes — the key surprise

The lane step collides heavily. `|E_n|` is far below `|A|^n`:

| n | A62: `62^n` | A62: `\|E_n\|` | distinct | A59: `59^n` | A59: `\|E_n\|` |
|---|---|---|---|---|---|
| 1 | 62 | **62** | 100 % | 59 | **59** |
| 2 | 3,844 | **2,097** | 54.55 % | 3,481 | **2,048** |
| 3 | 238,328 | **66,014** | 27.70 % | 205,379 | **64,726** |
| 4 | 14,776,336 | **2,058,466** | 13.93 % | 12,117,361 | **2,016,367** |
| 5 | 916,132,832 | **64,105,880** | 7.00 % | 714,924,299 | **62,658,885** |

`|E_5|` is only **1.4926 %** of 2³² (A62) / **1.4589 %** (A59). This number drives every cost estimate
below — do not assume the lanes are near-injective.

Multiplicity (how many n-character strings reach one lane value), A62:

| n | mean | **max** |
|---|---|---|
| 1 | 1.00 | 1 |
| 2 | 1.83 | 3 |
| 3 | 3.61 | 9 |
| 4 | 7.18 | 27 |
| 5 | 14.29 | 81 |

The maximum is exactly `3^(n-1)` at every level tested (both alphabets). A62 `n=5` histogram:
`[1]=519,141  [2-3]=3,919,672  [4-7]=12,395,850  [8-15]=22,571,929  [16-31]=20,062,306
[32-63]=4,549,255  [64-127]=87,727`.

### 3.4 A free invariant for the implementer

Because `33·h ≡ h (mod 32)` and the XOR only touches bits 0–6:

```
hash_lane(c1..cn) & 31  ==  (5381 ^ c1 ^ c2 ^ ... ^ cn) & 31
```

*Verified on 200,000 random strings* (by the author, and again independently by the reviewer over
200,000 random 1–10 character strings drawn from U+0001..U+CFFF — 0 failures of either the lane
recomposition or this invariant). Useful as a cheap self-test of a port, and as a 5-bit
pre-filter when brute-forcing lane values. (Note the low 5 bits of the A62 alphabet are exactly
`{1..26}` — `0` and `27..31` are never produced by a single character. **A59 is `{1..14, 16..26}`**:
`15` is missing because A59 drops `o` (0x6F & 31 = 15) and `O` (0x4F & 31 = 15), while `1` (0x31 &
31 = 17) is still covered by `Q`/`q`. *Computed by review from the two alphabet literals.*)

---

## 4. Why 853 quadrillion collapses to 4.29 billion

`World.m_seed` is a single `int`. `WorldGenerator` and everything downstream read **only** the int
seed, `m_worldGenVersion` and the `m_menu` flag — never the seed text
*(`valheim-worldgen/references/world-generator.md` §1; `WorldGenerator..ctor`)*. The seed text is
carried in the `.fwl2` and sent to clients for display only.

**Re-verified by review, exhaustively rather than by citation** (Mono.Cecil scan over every `*.dll` in
`valheim_Data/Managed`, listing every method whose body references the field). This was the author's
own stated risk #4; it is now closed:

- **`World::m_seedName` is referenced by exactly twelve methods**, and *none* of them is in a
  generation path: `World..ctor` (`ldfld` for the `== ""` test, `stfld`), `World.LoadWorld`,
  `World.SaveWorldFWLData`, `FejdStartup.IsPublicPasswordValid`, `FejdStartup.GetPublicPasswordError`,
  `FejdStartup.UpdateWorldList` (list display), `Terminal/<>c.<InitTerminal>b__7_146` (the `printseeds`
  console command), `ZNet.OpenServer`, `ZNet.SendPeerInfo`, `ZNet.RPC_PeerInfo` (`stfld` — the client
  receives the text), and `ZNet/<<OnSteamServerRegistered>g__DelayThenRegisterCoroutine|16_1>d.MoveNext`
  (server registration). **`WorldGenerator`, `ZoneSystem`, `Heightmap`, `HeightmapBuilder`,
  `AltBiomeWorldData` and `Minimap` never touch it.**
- **`World::m_uid` is referenced by exactly six methods**: `World..ctor`, `World.LoadWorld`,
  `World.SaveWorldFWLData`, `ZNet.GetWorldUID`, `ZNet.SendPeerInfo`, `ZNet.RPC_PeerInfo`. No generation
  code reads it, confirming the parenthetical below.
- **`World::m_seed` reaches generation through exactly two members**: `WorldGenerator..ctor`
  (`UnityEngine.Random.InitState(m_world.m_seed)` and `new FastNoise(m_world.m_seed)`) and
  `WorldGenerator.GetSeed()` (`return m_world.m_seed;`). The remaining readers are UI
  (`FejdStartup.UpdateWorldList`), the password checks, `printseeds`, `ZNet`, and
  `Minimap.Update` / `Minimap.SaveMapTextureDataToDisk` (minimap-cache keying, not generation).
- Downstream placement is seeded the same way, with no other world-level input:
  `ZoneSystem.GenerateLocationsTimeSliced(ZoneLocation, …)` uses
  `int seed = WorldGenerator.instance.GetSeed() + location.m_prefab.Name.GetStableHashCode();` and
  `ZoneSystem.PlaceVegetation` uses
  `UnityEngine.Random.InitState(seed + zoneID.x * 4271 + zoneID.y * 9187 + veg.m_prefab.name.GetStableHashCode())`
  with `int seed = WorldGenerator.instance.GetSeed();`. Global keys / world modifiers are read and
  written by `ZoneSystem` as gameplay flags (`GlobalKeyAdd`, `m_startingGlobalKeys`) and never enter
  either RNG seed.

Therefore:

- The set of distinct worlds a seed text can select has **at most 2³² = 4,294,967,296 members**.
- Two texts with the same `GetStableHashCode` produce **bit-identical terrain**. (World *UID* differs
  — `m_uid = name.GetStableHashCode() + Utils.GenerateUID()` is random — but the UID affects only
  per-character bookkeeping, not generation.)
- 853,058,371,866,181,866 / 4,294,967,296 = **198,618,129.796** texts per int seed on average.

Non-uniformity is real and large at short lengths: §5 shows that 23.88 % of all int seeds have **no**
6-character A62 preimage at all, while the reachable ones average 17.37 preimages each
(56,800,235,584 / 3,269,480,795). At length 10 every int has on the order of 10⁶ `(E,O)` lane pairs,
so the preimage count concentrates around the mean of 195,414,612 — but it is still not uniform, and
that alone is a reason a search tool must enumerate ints rather than sample texts.

---

## 5. Is every int32 reachable? — exact results

### 5.1 The counting bound (what is impossible)

The sumset `E_ce + K·E_co` has at most `|E_ce| · |E_co|` elements, so `L` can only work if that
product is ≥ 2³² = 4,294,967,296:

| L | (ce, co) | A62 `\|E_ce\|·\|E_co\|` | × 2³² |
|---|---|---|---|
| 5 | (3, 2) | 138,431,358 | 0.032× — **impossible** |
| 6 | (3, 3) | 4,357,848,196 | 1.01× — possible by a hair |
| 7 | (4, 3) | 135,887,574,524 | 31.6× |
| 8 | (4, 4) | 4,237,282,273,156 | 987× |
| 9 | (5, 4) | 131,959,774,380,080 | 30,724× |
| 10 | (5, 5) | 4,109,563,850,574,400 | 956,832× |

So **L ≤ 5 cannot cover 2³²** for either alphabet (the same bound in the crude form `|A|^L ≥ 2³²`
gives `62^5 = 916,132,832 < 2³²`, `59^5 = 714,924,299 < 2³²`). L = 6 is the first length that is not
excluded by counting — and it fails anyway.

### 5.2 Exact computed coverage

`|E_ce + K·E_co|` over `Z/2³²`, exact:

**A62 (`0-9 A-Z a-z`)**

| L | (ce, co) | reachable int seeds | missing | verdict |
|---|---|---|---|---|
| ≤5 (union) | — | **142,962,629** (3.3286 %) | 4,152,004,667 | impossible (counting) |
| 6 | (3,3) | **3,269,480,795** (76.1235 %) | **1,025,486,501** | **incomplete** |
| 7 | (4,3) | **4,294,967,296** | **0** | **COMPLETE** |
| 8 | (4,4) | 4,294,967,296 | 0 | complete |
| 9 | (5,4) | 4,294,967,296 | 0 | complete |
| 10 | (5,5) | 4,294,967,296 | 0 | complete |

**A59 (the game's own alphabet)**

| L | (ce, co) | reachable int seeds | missing | verdict |
|---|---|---|---|---|
| 6 | (3,3) | **3,170,820,113** (73.8264 %) | **1,124,147,183** | **incomplete** |
| 7 | (4,3) | **4,294,967,296** | **0** | **COMPLETE** |
| 8 | (4,4) | 4,294,967,296 | 0 | complete |
| 9 | (5,4) | 4,294,967,296 | 0 | complete |
| 10 | (5,5) | 4,294,967,296 | 0 | complete |

The union over **every** length 1..6 (not just L = 6) is still incomplete:
**A62: 3,310,424,872 (77.0768 %), missing 984,542,424. A59: 3,213,389,948 (74.8176 %), missing
1,081,577,348.**

> **Corrected by review — the `≤5` row.** It previously read "≤ 138,431,358", which is the *single-length*
> `|E_3|·|E_2|` product for L = 5 and is **not** an upper bound on the union over L = 1..5; the true value
> exceeds it. Exact unions, computed independently: **A62 = 142,962,629 (3.3286 %)**, **A59 = 136,877,472
> (3.1869 %)**; the corresponding sum-of-per-length-products upper bounds are 142,962,687 and 136,877,524.
> The verdict is unaffected — both are ≈ 3.3 % of 2³² — but a bound a reader could check and find violated
> does not belong in a spec.

**Completeness at L = 7 does not imply completeness at L = 8, 9, 10.** The level sets are not nested:
the reviewer checked directly that `E_3 ⊄ E_4` for both alphabets and `E_4 ⊄ E_5` for A62, so
`E_4 + K·E_3` and `E_4 + K·E_4` are genuinely different sumsets. Each of L = 8, 9, 10 was therefore
computed separately, and the reviewer reproduced all six of those "complete, missing = 0" results
independently (see §11).

> **Answer to "what is the shortest length L such that every int32 is reachable": L = 7**, for both
> alphabets. Every one of the 4,294,967,296 int seeds has a 7-character seed text, and 23 % of them
> have no seed text of 6 characters or fewer.

Examples of int seeds with **no** A62 text of length ≤ 6 (each independently confirmed unreachable at
L = 6 and reachable at L = 7 by a separate Python search): `0, 1, 2, 3, 32, 33, 34, 35, 44, 64, 65,
66`. Note in particular that **int seed 0 — the menu world's seed — is not reachable from any 6-char
alphanumeric text**, only from the empty text or from a 7+ character text.

### 5.3 Methods, and why the answer is trustworthy

Four independent computations, all on the real hash:

1. **Chunked exact sumset** (used for L = 6, 7, 8). The output space `Z/2³²` is split into 128 chunks
   of 2²⁵ bits (4 MB each, cache-resident). `K·E_co` is sorted once; for each output chunk and each
   `e ∈ E_ce`, binary-search the contiguous (wrap-aware) range of `t` with `e + t` in the chunk and
   set those bits. Exact; visits every pair that can land in the chunk. A chunk that reaches full
   popcount short-circuits, which is what makes the 4.24 × 10¹² pairs of L = 8 finish in ~10 s.
2. **Naive full-bitmap scatter** (cross-check, L ≤ 6). 16 per-thread 512 MB bitmaps, every pair
   written directly, OR-reduced at the end. No chunking, no binary search — a completely different
   code path. It reproduces the L = 6 numbers **exactly** for both alphabets
   (3,269,480,795 / 3,170,820,113).
3. **Cyclic bitmap rotation** (used for L = 9, 10). A set `S` and its shift `S + t` are the same
   2³²-bit bitmap rotated by `t` bits; OR successive rotations of the `E_5` bitmap by `K·o` and count.
   L = 9 filled after **859** shift values (A62) / 858 (A59); L = 10 after **877** (A62) / **889**
   (A59) — i.e. fewer than a thousand odd-lane values are enough to cover the whole int space at
   those lengths. *Unverified by review: these shift counts depend on the order in which `E_co` is
   enumerated and were not reproduced. They are a performance observation, not a result — the
   **completeness** of L = 9 and L = 10 was reproduced by an unrelated method (§11) and does hold.*
4. **Per-target witness search** (independent check of the L = 7 claim). For 16,777,216 *contiguous*
   targets starting at 0 **plus** 16,777,216 uniformly random targets, per alphabet, find an actual
   7-character text and re-hash it with the real `GetStableHashCode`. **33,554,432 solved and verified
   per alphabet, 0 failures** (mean 2,630 probes/target). This never touches the sumset code at all.

Plus a Python spot-check: 400 random targets → 304 reachable at L = 6 (76.0 %, against the exact
76.1235 %), 200/200 reachable at L = 7, and all 12 listed gap values confirmed individually.

---

## 6. The inverse: int32 → typeable seed text

### 6.1 Algorithm

Two facts make this cheap:

1. **Split on the lanes.** `hash = E + K·O`, so pick `O`, and the even lane is forced:
   `E = target − K·O (mod 2³²)`. One subtraction, one multiply.
2. **The lane step is invertible given the character.** `v = (33·h) ^ c` ⇒
   `h = (v ^ c) · 1041204193 (mod 2³²)`. So from a lane value you can walk backwards, trying all
   `|A|` characters at each step and keeping the branch whose predecessor is a member of `E_{n-1}`.

**Producing a 10-character text (game alphabet):**

```
precompute: E_1..E_4 as sorted uint32 arrays          (A59: 7.9 MB, A62: 8.1 MB)
loop:
    draw 5 random characters -> odd lane value O      (any 5-char string is a valid preimage of O)
    E = target - K*O   (mod 2^32)
    ev = reconstruct_lane(E, 5)    # backtracking below; null if E is not in E_5
    if ev != null: return interleave(ev, oc)          # ev[0] oc[0] ev[1] oc[1] ... ev[4] oc[4]

reconstruct_lane(v, n):
    if n == 0: return (v == 5381) ? "" : null
    for c in alphabet:
        h = (v ^ c) * 1041204193          # mod 2^32
        if h in E_{n-1}:                  # binary search in the sorted array
            r = reconstruct_lane(h, n-1)
            if r != null: return r + c
    return null
```

Expected iterations = `2³² / |E_5|` = **68.5** (A59) / 67.0 (A62), because a random `E` lies in `E_5`
with probability `|E_5|/2³²`. No meet-in-the-middle table over 2³² is needed: **membership in `E_5`
is decided by the same backtracking that produces the characters**, using only `E_1..E_4`.

> *Corrected by review:* the A59 figure was **68.6**; `4,294,967,296 / 62,658,885 = 68.545`, so it is
> **68.5**. (A62: `4,294,967,296 / 64,105,880 = 66.998` → 67.0, as stated.) Measured over 200,000
> targets by the reviewer: 68.61 probes/target (A59), consistent with 68.545.

### 6.1.1 **Corrected by review: this sampler does not produce game-looking seeds**

The loop above returns **the first** branch `reconstruct_lane` finds, and it scans the alphabet in
order at every backtracking step. The odd lane is drawn uniformly, so odd (1-indexed: 2nd, 4th, …)
positions are uniform — but the five **even** positions are not, because they are whatever the greedy
descent happened to settle on. Measured by the reviewer, 200,000 targets, A59, all re-hash-verified:

| position | χ² vs uniform (df = 58) | most common | least common |
|---|---|---|---|
| 0 (even, solved) | **16,141** | `Y` 3.06 % | `2` 1.21 % |
| 1 (odd, random) | 56.0 | 1.76 % | 1.63 % |
| 2 (even, solved) | **77,405** | `z` 3.18 % | `6` 0.40 % |
| 4 (even, solved) | **116,592** | `z` 3.47 % | `5` 0.18 % |
| 6 (even, solved) | **126,055** | `z` 3.50 % | `7` 0.13 % |
| 8 (even, solved) | **130,232** | `x` 3.33 % | `4` 0.13 % |
| 3, 5, 7, 9 (odd) | 37.6 / 50.3 / 63.3 / 60.3 | — | — |

Uniform would be 1/59 = 1.695 % per symbol and χ² ≈ 58 ± 11. A 26× spread between the most and least
common symbol at position 8 is visible to the naked eye over a handful of seeds and trivially
detectable statistically. **`World.GenerateSeed` draws all ten positions uniformly**
(`UnityEngine.Random.Range(0, 59)` per character, §3.2), so the §8.5 claim that the output is
"indistinguishable from one the game would have suggested" was **false as specified**.

**Fix (established by measurement, use this).** Sample uniformly from the target's preimage set:

```
precompute additionally: w_1..w_4, one byte per level entry        (A59: +2,083,200 B; max w_4 = 27)
    w_1[*] = 1 ;  w_n[index_of((33*e)^c)] += w_{n-1}[index_of(e)]   for e in E_{n-1}, c in alphabet

loop:
    draw 5 random characters -> odd lane value O
    E = target - K*O   (mod 2^32)
    m = sum over c in alphabet of w_4( (E ^ c) * 1041204193 )       # == w_5(E); 59 lookups, no E_5 table
    if m == 0: continue
    if random() * 81 >= m: continue          # accept with probability w_5(E)/81, 81 = max w_5 (S3.3)
    ev = reconstruct_lane_weighted(E, 5)     # at each step pick c with prob proportional to w_{n-1}(pred)
    return interleave(ev, oc)
```

Both halves are needed. The weighted descent alone still leaves χ² ≈ 8,000–12,000 (measured), because
the *acceptance* is biased: a draw is accepted whenever `E ∈ E_5` regardless of how many preimages `E`
has, so low-multiplicity even lanes are over-represented by a factor of up to 81. The `w_5(E)/81`
rejection removes exactly that factor. With both, measured χ² is **39–77 at every one of the ten
positions** (df = 58) — indistinguishable from uniform, as it must be, since the result is then a
uniform draw from the set of 10-character A59 texts hashing to the target, which is precisely the
conditional distribution of a `World.GenerateSeed` output given its hash.

Cost, measured on the same machine, single-threaded .NET 10: **486.1 probes/target, 0.339 ms/target**
versus 68.55 probes / 0.049 ms for the biased version — a 7.1× slowdown for a sub-millisecond
operation. Tables grow from ~7.9 MiB to ~9.9 MiB (A59). Use the biased version only where the output
is never shown as a "game-style" seed.

**Producing the shortest possible text:** for `L = 1, 2, 3, ...` with `ce = ceil(L/2)`,
`co = floor(L/2)`, enumerate **all** of `E_co` (62 / 2,097 / 66,014 / 2,058,466 values) and test
`target − K·o ∈ E_ce`. The first `L` that yields a hit is provably the shortest, because the
enumeration of `E_co` is exhaustive. L = 6 needs at most 66,014 probes, L = 7 at most 66,014 probes
against `E_4`; by §5 the loop always terminates at `L ≤ 7`.

### 6.2 Measured cost (Ryzen 7 9800X3D, .NET 10, single-threaded)

| Task | Tables | Result | Time |
|---|---|---|---|
| 10-char text in **A59**, 512 MB `E_5` bitmap variant | 512 MB + 8 MB | 1000/1000 re-hash-verified, mean **70.7** probes | **0.025 ms**/target |
| 10-char text in **A59**, low-memory variant (no bitmap) | **7.9 MB** | 5000/5000 verified, mean **68.0** probes | **0.081 ms**/target |
| 10-char text in **A62**, low-memory variant | **8.1 MB** | 5000/5000 verified, mean **67.1** probes | **0.072 ms**/target |
| **Shortest** text in A62 (exhaustive per length) | 8.1 MB (+ bitmap, unused in practice) | 1000/1000 verified | **0.57 ms**/target |

Both variants produce identical output for identical RNG streams, confirming the low-memory
membership test. **Use the low-memory variant** — 8 MB and 0.08 ms is nothing, and it removes a
512 MB allocation from the tool.

Shortest-length distribution over 1000 uniformly random int32 targets (A62): **length 4: 1,
length 5: 32, length 6: 744, length 7: 223.** Checked against §5 by the reviewer: the exact
expectations are `union(≤4)/2³²` = 0.102 % → 1.0, `[union(≤5) − union(≤4)]/2³²` = 3.23 % → 32.3,
`[union(≤6) − union(≤5)]/2³²` = **73.75 %** → 737.5, and `1 − union(≤6)/2³²` = **22.92 %** → 229.2.
The sample matches all four within noise (σ ≈ 14 at L = 6). **Corrected by review:** the parenthetical
previously called these "predicted `|E_ce|·|E_co| / 2³²` Poisson rates". At L = 4 and L = 5 the raw
ratio and a Poisson estimate do coincide with the exact value to two figures, but at L = 6 neither
gives 76.1 %: the raw ratio is `4,357,848,196 / 2³²` = **101.46 %** (meaningless as a probability) and
the Poisson estimate `1 − e^(−1.01464)` is **63.75 %** — twelve points below the true 76.12 %.
The sumset is markedly *more* covering than a random-scatter model predicts; do not size anything from
the Poisson form at L ≥ 6, use the exact §5.2 numbers.

Worked examples (verify these against your port):

| target int seed | 10-char A59 text | shortest A62 text |
|---|---|---|
| -575213773 | `UKgPfUfKhD` | `WxBRBb` (6) |
| -1980035179 | `xL7dNmwQXS` | `whKZBE` (6) |
| -1210178378 | `VTs6PRcQbe` | `L1AJNQM` (7) |
| -1519224518 | — | `Z3EaIs5` (7) |
| -847690108 | — | `zaQoK8` (6) |

### 6.3 Enumerating preimages, not just one

If the tool wants *several* texts for one world (e.g. "give me a 10-char seed that looks
game-generated"), keep drawing odd lanes; each success is an independent preimage. If it wants *all*
short preimages, enumerate `E_co` exhaustively and, for each hit, enumerate all backtracking branches
in `reconstruct_lane` instead of returning the first. The number of such texts is
`w_ce(E) · w_co(O)` summed over solutions, with `w_n` bounded by `3^(n-1)` (§3.3).

---

## 7. What the game's create-world UI actually accepts

### 7.1 What the code does

```csharp
// FejdStartup.OnWorldNew
m_createWorldPanel.SetActive(value: true);
m_newWorldName.text = "";
m_newWorldSeed.text = World.GenerateSeed();          // always 10 chars

// FejdStartup.OnNewWorldDone(bool forceLocal)
string text  = m_newWorldName.text;
string text2 = m_newWorldSeed.text;
... m_world = new World(text, text2);                 // no trim, no validation, no length check

// FejdStartup.Update (the only gate on the Done button)
m_newWorldDone.interactable = m_newWorldName.text.Length >= 5;    // the *name*, not the seed
```

- `public GuiInputField m_newWorldSeed;` — `GUIFramework.GuiInputField : TMPro.TMP_InputField`
  (`gui_framework.dll`). `GuiInputField` adds only Ctrl+Backspace/Ctrl+Delete word editing, virtual
  keyboard plumbing and submit/deselect wiring; **it never sets `characterLimit`, `contentType`,
  `characterValidation`, `inputType`, `lineType` or `onValidateInput`.**
- A Mono.Cecil scan of **every** non-Unity managed assembly in `valheim_Data/Managed` found exactly
  one call to `TMP_InputField::set_characterLimit` in the whole game:
  `assembly_valheim.dll :: TextInput.Show` (the in-game text-entry popup — sign text, and so on).
  **Nothing in code sets the limit on `m_newWorldSeed`.**
  *Re-run by review over every `*.dll` in `valheim_Data/Managed`, widened to the other five relevant
  setters. The complete result set is three call sites:* `assembly_valheim.dll :: TextInput.Show →
  set_characterLimit`, and `Unity.TextMeshPro.dll :: TMP_InputField.SetToCustom` and
  `TMP_InputField.SetToCustomIfContentTypeIsNot → set_contentType` (TMP's own internals). **No game
  code anywhere sets `characterLimit`, `characterValidation`, `contentType`, `inputType`, `lineType`
  or `onValidateInput` on the create-world fields** — so whatever those are, they come from the prefab
  (§7.3) and are never changed at runtime.
- There is **no length constraint on the seed anywhere downstream**: `World.SaveWorldFWLData` writes
  `zPackage.Write(m_seedName)` as a `BinaryWriter` string (7-bit length prefix + UTF-8), so any
  length and any Unicode round-trips through the `.fwl2`. *(Verified by review: `ZPackage.Write(string
  data)` is `m_writer.Write(data)` on a `System.IO.BinaryWriter`, and `ZPackage.ReadString()` is
  `m_reader.ReadString()` — the standard 7-bit-encoded length prefix plus UTF-8, with no length cap.)*
  The only other uses of `m_seedName` are display (`FejdStartup.UpdateWorldList`), `printseeds`
  (`Terminal/<>c.<InitTerminal>b__7_146`), the public-password sanity checks
  (`FejdStartup.IsPublicPasswordValid` **and `FejdStartup.GetPublicPasswordError`**: the password may
  not be a substring of the world name or the seed text) and the server-registration / peer-info path
  (`ZNet.OpenServer`, `ZNet.SendPeerInfo`, `ZNet.RPC_PeerInfo`,
  `ZNet/<<OnSteamServerRegistered>g__DelayThenRegisterCoroutine|16_1>d.MoveNext`). *Corrected by
  review: the original list omitted `GetPublicPasswordError` and named only "the PlayFab server
  registration" for what is four `ZNet` members; the exhaustive twelve-member list is in §4.*

### 7.2 What TMP_InputField does with the limit (this is the behaviour that matters)

Inside `TMPro.TMP_InputField`, `characterLimit` is read by exactly three members
(`get_characterLimit` callers, Cecil scan): `Insert`, `LateUpdate` and `ActivateInputFieldInternal`.

```csharp
private void Insert(char c) {
    if (m_ReadOnly) return;
    string value = c.ToString();
    Delete();
    if (characterLimit <= 0 || text.Length < characterLimit) { ... m_Text = text.Insert(...); ... }
}

protected virtual void Append(string input) {
    if (m_ReadOnly || !InPlaceEditing()) return;
    for (int i = 0, length = input.Length; i < length; i++) {
        char c = input[i];
        if (c >= ' ' || c == '\t' || c == '\r' || c == '\n') Append(c);
    }
}
```

Consequences, all evidence-backed:

- **Typing and pasting both end at `Append(char)` → `Insert`**, so a paste longer than the limit is
  **silently truncated**, character by character — it is not rejected, and no error is shown
  (`Insert` simply does nothing once `text.Length >= characterLimit`).
- **Corrected by review: the two routes do *not* share a filter.** Pasting is
  `KeyPressed` case `KeyCode.V` with Ctrl → `Append(clipboard)`, i.e. the `Append(string)` overload
  above, which admits `c >= ' '` plus `'\t'`, `'\r'`, `'\n'`. Typing is the fall-through at the end of
  `KeyPressed`: `char c = evt.character;` → `if (!multiLine && (c == '\t' || c == '\r' || c == '\n'))
  return EditState.Continue;` → `if (IsValidChar(c)) Append(c);`, where
  `TMP_InputField.IsValidChar` rejects `'\x7f'` and everything `< ' '` **except `'\t'` and `'\n'`**.
  The practical difference: in a single-line field a **pasted** tab/CR/LF reaches `Insert` while a
  **typed** one is dropped before `IsValidChar` is even reached. So — subject to the unverified
  `characterValidation`/`lineType` (§7.3) — a seed text containing a tab or newline is reachable by
  paste but not by typing. The tool must not emit one either way.
- **A NUL can never reach the field, by any of three independent routes.** `Append(string)` drops
  `c < ' '` other than tab/CR/LF (paste); `IsValidChar` returns false for `c < ' '` other than
  `'\t'`/`'\n'` (typing); and `TMP_InputField.SetText`, the only thing the `text` setter calls, runs
  `value = value.Replace("\0", string.Empty)` before assigning `m_Text` (programmatic set). A
  NUL-terminated seed text is unreachable from the UI.
- **Setting `.text` programmatically bypasses the limit** — but **corrected by review: not for the
  reason given.** The original text claimed `set_text` "is not among the three members that read
  `characterLimit`". The first half is right — the `text` property setter is just `{ SetText(value); }`
  and `SetText` never mentions `characterLimit` — but `LateUpdate` *does* truncate:
  `if (characterLimit > 0 && m_Text.Length > characterLimit) { m_Text = m_Text.Substring(0,
  characterLimit); }`. The reason that line never fires here is that it sits inside the
  **touch-screen-keyboard** branch, after `if (m_SoftKeyboard == null || m_SoftKeyboard.status !=
  TouchScreenKeyboard.Status.Visible) { …; return; }`, and `GUIFramework.GuiInputField.Awake()` sets
  `base.shouldHideSoftKeyboard = true`, so `ActivateInputFieldInternal`'s
  `if (TouchScreenKeyboardShouldBeUsed() && !shouldHideSoftKeyboard)` is never taken and
  `m_SoftKeyboard` stays null for this field. The third reader,
  `ActivateInputFieldInternal`, passes `characterLimit` to `TouchScreenKeyboard.Open` — same dead
  branch. **Net effect is as originally stated** (`m_newWorldSeed.text = World.GenerateSeed()` lands
  all 10 characters whatever the prefab limit is), but a port or a mod that re-enables the soft
  keyboard would see truncation, so record the real reason.
- *Also noted by review:* `GuiInputField` has one further `characterLimit` reader of its own,
  `OpenSteamKeyboard` (Big Picture mode), which forwards `(characterLimit > 0) ? characterLimit :
  int.MaxValue` to `SteamUtils.ShowGamepadTextInput`; the text that comes back is written into
  `m_Text` by `ValidateVirtualKeyboardText` **without any `characterLimit` check**. Irrelevant to
  desktop play, relevant if anyone reasons about the limit as an invariant.
- **Leading/trailing spaces are preserved and significant.** Space is `>= ' '` so `Append` accepts it
  (unless `characterValidation` filters it — prefab data, see below), and `World..ctor` does not
  trim. `"abc "` ≠ `"abc"` ≠ `" abc"`, three different worlds.
- **Non-ASCII is accepted by the hash** (UTF-16 code units) and survives the save file, but see the
  Unverified note below on whether the field filters it.

### 7.3 **Unverified: the actual character limit and content type**

`m_newWorldSeed.characterLimit`, `.characterValidation`, `.contentType`, `.inputType` and
`.inputValidator` are **serialized prefab data**, not code. Unity type trees are stripped from this
build (`m_CharacterLimit` does not appear as a string in `resources.assets`, `globalgamemanagers*`,
`sharedassets0.assets` or `level0`), the UI lives in the `StreamingAssets/SoftRef` asset bundles, and
no Unity asset parser (UnityPy/AssetsTools) is available here — so the value **could not be read
offline**. Do not assume 10.
*Spot-checked by review:* `grep -a -c m_CharacterLimit` returns **0** for all five files
(`resources.assets` 79,228,948 B, `globalgamemanagers` 205,668 B, `globalgamemanagers.assets`
342,960 B, `sharedassets0.assets` 13,444 B, `level0` 1,408 B). The negative evidence holds; the value
remains **Unverified**.

**How to settle it in one line, in game.** `FejdStartup` exposes a public singleton
(`public static FejdStartup instance => m_instance;`) and `m_newWorldSeed` is a public field, so a
BepInEx dumper plugin (the project already plans one) can log, from the main menu with the create-world
panel open:

```csharp
var f = FejdStartup.instance.m_newWorldSeed;
ZLog.Log($"seedField limit={f.characterLimit} validation={f.characterValidation} " +
         $"contentType={f.contentType} inputType={f.inputType} lineType={f.lineType} " +
         $"validator={(f.inputValidator == null ? "null" : f.inputValidator.GetType().Name)} " +
         $"nameLimit={FejdStartup.instance.m_newWorldName.characterLimit}");
```

Manual check without a plugin: open **New World**, select the prefilled 10-character seed, type a
long string of `a`s and count what stays in the box; then try pasting a 30-character string and count
again. Both answers must agree, and both give the limit directly.

**Until it is measured, the tool must emit at most 10 characters.** That is the length the game
itself generates and the length everyone shares, so it is the only length known to be safe. The 7-
character output from §6 is strictly safer and is always available (§5).

---

## 8. What this means for the tool — the summary the user will read

1. **Your arithmetic is right.** 1–10 characters over `A–Z a–z 0–9` really is
   **853,058,371,866,181,866** seed texts.
2. **But that is not how many worlds there are.** Before anything else happens, the text is crushed
   into one 32-bit integer by `GetStableHashCode`, and world generation never sees the text again —
   only the int, the world-gen version and the menu flag. So there are **at most 4,294,967,296
   distinct worlds**, and we proved by exact computation that **all 4,294,967,296 actually occur**
   (any 7-character text can reach any of them; 10-character texts certainly can).
3. **On average 198,618,130 different seed texts produce the exact same world** — identical terrain,
   identical biomes, identical everything the generator decides. Searching seed *strings* would
   therefore do roughly 198 million times more work than necessary and would revisit the same world
   over and over.
4. **So the tool searches integers, from −2,147,483,648 to 2,147,483,647, each exactly once.** That is
   a finite, fully enumerable space: at 1,000 worlds/second it is 1,193 hours; at 10,000/s, 119 hours;
   at 100,000/s, 12 hours. A full sweep with cheap criteria is a weekend, not a fantasy — and unlike
   valheim.gaming.tools it has no 20,000-seed cap and no duplicate worlds.
5. **When the tool finds a world you want, it converts the integer back into something you can type**
   — instantly (0.34 ms), either as a 10-character seed in the game's own alphabet (**statistically
   indistinguishable from one the game would have suggested, provided the §6.1.1 uniform sampler is
   used** — the greedy sampler originally specified here is *not*: its even-position characters are
   wildly non-uniform, χ² up to 130,000 against df = 58) or as the shortest text that works (usually
   6 characters, sometimes 7).
6. Two practical warnings: seeds are **case-sensitive and space-sensitive** — `Abc`, `abc` and `abc `
   are three different worlds; and **the empty seed box is not "random"**, it is the specific world
   with int seed 0 (the same seed the main-menu background world uses).

### 8.1 Concrete requirements this places on the tool

- Canonical world identity is the **int32 seed**, not the text. Store, index, cache, de-duplicate and
  report by int. Keep the text as a display/entry artifact only.
- Iterate ints in `[int.MinValue, int.MaxValue]` (or `uint` 0..2³²−1, whichever the worker code
  prefers) — never by generating random strings.
- Progress and ETA are meaningful because the denominator is exactly 4,294,967,296.
- When the user pastes a seed text, hash it (`World..ctor` rule: `"" → 0`) and work with the int.
  Show both, and warn when the text differs from a previously-seen text for the same int.
- The seed-text generator must be restricted to a typeable alphabet (default A59, so the output is
  visually identical to a game-suggested seed) and to ≤10 characters until §7.3 is measured. It must
  use the **uniform** sampler of §6.1.1; the greedy one produces a visibly skewed character
  distribution at even positions.
- `SmallLevels` must be accompanied by the multiplicity tables `w_1..w_4` (one byte per entry,
  max `w_4 = 27`) if §6.1.1 is used. *Added by review.*

---

## 9. Reference implementation notes

- Do all of it in `uint` with `unchecked`. Convert to `int` only at the boundary where a value is
  shown to the user or written to a save.
- `SmallLevels(alphabet, 4)` — the four sorted `uint[]` level sets — is the only table the seed-text
  tooling needs (8 MB; exactly `62 + 2,097 + 66,014 + 2,058,466 = 2,126,639` entries = 8,506,556 B =
  8.11 MiB for A62, and `59 + 2,048 + 64,726 + 2,016,367 = 2,083,200` entries = 8,332,800 B =
  7.95 MiB for A59). Build it once at startup: ~0.5 s single-threaded, trivially cacheable to disk.
  *Added by review:* if §6.1.1's uniform sampler is used, build the parallel multiplicity tables
  `w_1..w_4` in the same pass — one `byte` per level entry (`max w_4 = 27`, so a byte is enough),
  +2.0 MiB per alphabet. `w_5` is never stored; it is `sum over c of w_4((E ^ c) * 1041204193)`.
- A 2³²-bit bitmap is 2²⁶ `ulong`s = 512 MB; it fits in a single .NET array (no
  `gcAllowVeryLargeObjects` needed) and is only required if you want set-level analytics, not for
  the tool's normal operation.
- The bitmap-rotation identity is worth knowing if analytics are ever added: for `t ∈ Z/2³²`,
  `bitmap(S + t)` is `bitmap(S)` rotated by `t` bits, i.e. with `s = −t mod 2³²`, `q = s >> 6`,
  `r = s & 63`, word `j` of the result is `(b[(j+q) mod 2²⁶] >> r) | (b[(j+q+1) mod 2²⁶] << (64−r))`
  (special-case `r == 0`).

### 9.1 Reproduction

All code is in `<work>\probe\seedspace`
(`Program.cs`, `seedspace.csproj`, .NET 10, ~700 lines, no dependencies).

```
dotnet build seedspace.csproj -c Release
seedspace selftest    # hash vectors + 200k-string lane-decomposition check
seedspace counts      # |E_n| for n = 0..5, both alphabets                       (~3 s)
seedspace weights     # multiplicity distributions                                (~40 s, 8 GB RAM)
seedspace cover A62   # exact sumset coverage for L = 6..10                       (~80 s, 1 GB RAM)
seedspace cover A59
seedspace verify6     # independent naive full-bitmap union over L = 1..6         (~15 s, 8 GB RAM)
seedspace verify7     # 33.5M per-target witness searches per alphabet, re-hashed (~60 s)
seedspace invert 1000 # inverse with the 512 MB bitmap
seedspace invert2 5000# low-memory inverse (this is the one to ship)
```

Nothing in the game install or in any save folder was written to; the user's world `asdasdasd` was
used read-only, as ground truth for the hash.

---

## 10. Unverified / open

- **The create-world seed field's `characterLimit`, `characterValidation` and `contentType`**
  (§7.3). Prefab data; type trees stripped; no asset parser available. One BepInEx log line or one
  manual typing test settles it. Everything else in §7 is verified from code.
- **Whether a seed text longer than the field limit can reach the game by another route** — editing a
  `.fwl2` by hand would work as far as `World.LoadWorld` is concerned (the string is length-prefixed,
  §7.1), but the user's saves are Steam-Cloud-synced and must not be written to, so this was not
  tested and the tool should not rely on it.
- **Non-ASCII seed texts in practice.** The hash handles them (UTF-16 code units) and the save format
  stores them (UTF-8), but whether the input field accepts them depends on the same unverified
  `characterValidation`, and the font may not render them. The tool should not emit them.
- The coverage results are for `GetStableHashCode` as shipped in **1.0.15**. The function has been
  stable for years and also names every prefab (changing it would invalidate every save), but
  `tools\check-game-version.ps1` should gate this document like any other decompiled fact.
  *Checked by review, 2026-09-22:* the knowledge base's `check-game-version.ps1` (the same check
  `tools\check-game-version.ps1` makes) exits 0 — Valheim 1.0.15, network 40,
  Steam build 25390630, `assembly_valheim.dll` sha256 `59f53fb5…33adb1`, matching the KB stamp.

### Open questions (added by review)

- **Whether a pasted tab / CR / LF can actually survive into a seed text.** `Append(string)` lets
  `'\t'`, `'\r'`, `'\n'` through to `Append(char)` (§7.2), and `Append(char)` only filters them if
  `characterValidation != CharacterValidation.None` or `onValidateInput != null` — both **Unverified**
  prefab data (§7.3). The same one-line dump settles this. It does not affect the tool (which must
  never emit such a character) but it does affect the statement "the set of texts a user can enter".
- **Whether `w_5`'s maximum really is 81 for every alphabet the tool might offer.** §6.1.1's rejection
  constant `81` is `3^4`, confirmed by exhaustive computation for A62 and A59 at n = 1..5 (§3.3), but
  it is an empirical maximum, not a proved bound. A port that adds a third alphabet (e.g. one
  including `-` or `_`) **must recompute `max w_5` before reusing the constant** — too small a
  constant silently breaks the rejection sampler's uniformity; too large only costs speed. A safe
  implementation computes `max w_5` at table-build time instead of hard-coding it.
- **Whether per-position uniformity is the right acceptance test for §6.1.1.** The reviewer measured
  the ten marginal character distributions (χ² 39–77, df = 58). The argument that the full joint
  distribution is uniform over the preimage set is analytic (uniform odd lane × acceptance ∝ `w_5(E)`
  × uniform-by-weight descent), not measured; a joint test over 10 positions is not feasible at this
  sample size. **Unverified: the joint distribution was not tested directly.**

---

## 11. Verification

Independent adversarial check of this document, 2026-09-22, against Valheim **1.0.15** (network 40,
Steam build 25390630, `assembly_valheim.dll` sha256 `59f53fb5…33adb1`; `check-game-version.ps1`
exit 0). Everything below was re-derived from the decompiled sources and from a **separate** C# port
written for this review — `scratchpad/probe/review/rv` (`Program.cs`, `rv.csproj`, .NET 10) — that
shares no code with `scratchpad/probe/seedspace`. Its level sets are built by 2³²-bit-bitmap dedupe
rather than by sorting, and its sumset engine iterates the *even* side against a sorted `K·E_co` with
a per-chunk saturation exit. Decompiler output came from
`.claude\skills\valheim-modding\scripts\decompile.ps1`; assembly scans from Mono.Cecil over
`valheim_Data/Managed`.

### Checked and confirmed correct

- **The hash.** `StringExtensionMethods.GetStableHashCode` quoted in §1 is verbatim. Its IL was dumped
  directly: plain `shl` / `add` / `xor` / `mul`, no `.ovf` forms — §1's unchecked-wrapping rule holds.
  Constants 5381 = 0x1505, 33, K = 1566083941 = 0x5D588B65 all confirmed;
  `33 × 1041204193 mod 2³² = 1` confirmed.
- **`World..ctor`, `World.GenerateSeed`, `FejdStartup.OnWorldNew` / `OnNewWorldDone`, and the `Update`
  gate on `m_newWorldName.text.Length >= 5`** — all quoted verbatim, re-read at
  `decomp/World.cs:69-76,163-171` and `decomp/FejdStartup.cs:1482-1493,2190`. A59 is 59 symbols and is
  character-for-character the literal in `World.GenerateSeed`. No `-seed` command-line argument exists
  (`ParseServerArguments` handles `-world -name -port -password -savedir -public -logfile -crossplay
  -instanceid -backups -backupshort -backuplong -saveinterval -resetmodifiers -preset -modifier
  -setkey` and nothing else).
- **All hash vectors in §1.1**, plus the two real saves read off disk read-only, plus `StartTemple`
  and `Eikthyrnir` re-confirmed present in `asdasdasd\_main.3.db2`.
- **§2's 853,058,371,866,181,866** and the A59 total 519,929,111,116,169,700.
- **Every `|E_n|`, both alphabets, n = 0..5** — reproduced exactly (62 / 2,097 / 66,014 / 2,058,466 /
  64,105,880 and 59 / 2,048 / 64,726 / 2,016,367 / 62,658,885), as were the §3.3 percentages, the
  multiplicity means, the `max = 3^(n-1)` result, and the A62 n = 5 histogram to the unit
  (519,141 / 3,919,672 / 12,395,850 / 22,571,929 / 20,062,306 / 4,549,255 / 87,727, summing to
  64,105,880).
- **§3.1's lane decomposition and §3.4's low-5 invariant** — 200,000 random 1–10 character strings over
  U+0001..U+CFFF, zero failures of either.
- **Every product in §5.1** and every coverage number in §5.2: A62 L=6 = **3,269,480,795**,
  A59 L=6 = **3,170,820,113**, union L≤6 = **3,310,424,872** / **3,213,389,948**, and L = 7, 8, 9, 10
  **complete with missing = 0 for both alphabets** — all six completeness results recomputed here, not
  inferred from L = 7. A two-chunk naive scatter with no binary search agreed with the chunked engine.
- **All twelve claimed L≤6 gap values** (0, 1, 2, 3, 32, 33, 34, 35, 44, 64, 65, 66): each confirmed to
  have no A62 preimage of length ≤ 6 by exhaustive enumeration of `E_co`, and each given a 7-character
  witness that re-hashes to it (e.g. `J0NDEFI` → 0).
- **All five worked examples in §6.2** re-hash correctly, lie in the stated alphabets, and have the
  stated shortest length.
- **§4's central premise**, which the author listed as an open risk: closed by exhaustive Cecil scan
  (details in §4). No generation code reads `m_seedName` or `m_uid`; `ZoneSystem` location and
  vegetation RNGs seed from `WorldGenerator.instance.GetSeed()` plus prefab-name hashes and zone
  coordinates only.
- **§7.2's TMP_InputField quotes** (`Insert`, `Append(string)`) are verbatim; the Cecil
  `set_characterLimit` result was reproduced and widened to five more setters; the §7.3 "type trees
  stripped" negative evidence was reproduced.

### Corrected in place

1. **§6.1.1 (new) / §8.5 / §0 — the 10-character sampler produces non-game-like seeds.** The greedy
   `reconstruct_lane` biases the five even positions severely (χ² up to 130,232 against df = 58;
   26× spread between the most and least common symbol). Measured over 200,000 targets, all
   re-hash-verified. Replaced with a uniform sampler (weighted descent **plus** `w_5(E)/81` rejection)
   measured at χ² 39–77 at all ten positions, 486 probes / 0.339 ms per target. **This is the
   correction most likely to have shipped as a silent defect** — every output of the original was a
   valid seed, so no correctness test would have failed.
2. **§5.2 — the `≤5` row stated a bound that the true value violates.** "≤ 138,431,358" is the L = 5
   product alone; the exact union over L = 1..5 is 142,962,629 (A62) / 136,877,472 (A59). Verdict
   unchanged.
3. **§6.1 — `2³²/|E_5|` for A59 was 68.6; it is 68.545 → 68.5.**
4. **§6.2 — "Poisson rates" mislabelled.** 76.1 % is the exact §5.2 coverage, not `|E_3|²/2³²`
   (101.46 %) nor a Poisson estimate (63.75 %). Replaced with the four exact expectations.
5. **§7.2 — the reason `.text` bypasses `characterLimit` was wrong.** `LateUpdate` *does* truncate to
   `characterLimit`; that line is simply unreachable here because it sits behind
   `m_SoftKeyboard != null && status == Visible`, and `GuiInputField.Awake` sets
   `shouldHideSoftKeyboard = true`. Conclusion unchanged, reasoning replaced.
6. **§7.2 — "typing and pasting both go through `Append`" conflated two different filters.** Typing is
   `IsValidChar` → `Append(char)`; pasting is `Append(string)` → `Append(char)`. Consequence added: a
   pasted tab/CR/LF can reach `Insert` in a single-line field, a typed one cannot. NUL is still blocked
   on all routes — a third one was added (`SetText` strips `"\0"`).
7. **§1.1 — `"j"` was labelled "second real save on this machine".** There is no such save; the real
   second save is `testworldclaude` / `hnBd9gJf2G` → 319486907, now added as a vector. The hash value
   for `"j"` was correct.
8. **§7.1 — the `m_seedName` usage list was incomplete** (omitted `GetPublicPasswordError`; "the
   PlayFab server registration" stands for four `ZNet` members). Replaced with the exhaustive list.
9. **§1, §3.4, §5.2, §7.1, §7.3, §9 — evidence added** where a claim rested on a citation rather than
   on the code: the IL dump, the A59 low-5 set `{1..14, 16..26}`, the non-nesting of the level sets,
   the `ZPackage` string encoding, the asset-file grep counts, and the exact table sizes.

### Still unverified

- **The create-world field's `characterLimit` / `characterValidation` / `contentType` / `inputType` /
  `lineType` / `inputValidator`** (§7.3). Re-confirmed unreadable offline; the one-line in-game dump is
  still the way to settle it. The document's mitigation — emit ≤ 10 characters from A59 — is sound, and
  §5's result that 7 characters always suffice is the stronger guarantee.
- **Whether a pasted tab/CR/LF survives** and **whether non-ASCII is accepted** — both depend on the
  same prefab data (see §10 Open questions).
- **§5.3 method 3's shift counts** (859 / 858 / 877 / 889) — enumeration-order dependent, not
  reproduced. The completeness results they support were reproduced by other means.
- **§6.2's timings** for the author's own binaries, and §5.3's "~10 s" / "~60 s" figures — machine- and
  implementation-specific, taken as reported.
- **The joint (as opposed to per-position) distribution of the §6.1.1 sampler** — argued analytically,
  not measured.
- **Everything here is pinned to 1.0.15.** If `GetStableHashCode` ever changes, every number in this
  document changes with it.

Nothing in the game install, in `worlds_local\`, or in the Steam-Cloud save folder was written to.
All save access was read-only via `valheim_saves.py`; all scratch work lives in
`scratchpad/probe/review\`.

*— checked by an independent reviewer*
