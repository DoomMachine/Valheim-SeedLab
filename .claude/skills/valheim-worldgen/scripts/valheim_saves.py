"""Read-only inspector for Valheim seeds and save files (Valheim 1.0.15 formats).

    python valheim_saves.py seed "MyWorldSeed"         # seed text -> the int seed the game uses
    python valheim_saves.py worlds                      # every world: name, seed, UID, world-gen version, keys
    python valheim_saves.py characters                  # every character: id, per-world logout/home, saved pins
    python valheim_saves.py locations "<world name>"    # location instances stored in the world's .db2
    python valheim_saves.py locations "<world>" --name Vendor_BlackForest   # one prefab only (SPOILERS)

Add --root DIR to inspect a specific save root (a folder containing worlds/ and characters/, or
worlds_local/ and characters_local/). By default it searches the local save folder and every Steam Cloud
folder for app 892970.

SAFETY: this script only ever opens files for reading ('rb'). Never add a write path: Steam Cloud syncs
these folders, and the game deletes files it does not expect in a world folder (see
references/seeds-and-world-files.md). Formats verified 2026-09-22 against real saves: world file version
41 (chunked format), player profile version 46, map data version 8. A game update may change them - if a
parse fails, re-derive the layout with valheim-modding/scripts/decompile.ps1 (World, PlayerProfile,
ZoneSystem) before "fixing" it here.

The `locations` command reveals where things are in a world. It is a development and testing aid; for a
player who wants an unspoiled world (see the Wayfinder mod) its output is a spoiler.
"""
import argparse
import glob
import gzip
import os
import struct
import sys


# ---------------------------------------------------------------- the seed hash

def _i32(x):
    x &= 0xFFFFFFFF
    return x - (1 << 32) if x & 0x80000000 else x


def stable_hash(s):
    """Port of StringExtensionMethods.GetStableHashCode (assembly_utils): two interleaved djb2-xor lanes
    seeded with 5381, combined as num + num2 * 1566083941, unchecked 32-bit. Also used for prefab hashes.
    Verified: reproduces the seed stored in real .fwl2 files."""
    num = 5381
    num2 = num
    i = 0
    while i < len(s) and s[i] != "\0":
        num = _i32(((num << 5) + num) ^ ord(s[i]))
        if i == len(s) - 1 or s[i + 1] == "\0":
            break
        num2 = _i32(((num2 << 5) + num2) ^ ord(s[i + 1]))
        i += 2
    return _i32(num + _i32(num2 * 1566083941))


def seed_from_text(seed_name):
    """World(string name, string seed): m_seed = seed == "" ? 0 : seed.GetStableHashCode(). No trimming,
    no case folding - "Abc" and "abc " are different worlds."""
    return 0 if seed_name == "" else stable_hash(seed_name)


# ---------------------------------------------------------------- binary reading (ZPackage layout)

class Reader:
    def __init__(self, buf):
        self.buf, self.p = buf, 0

    def take(self, n):
        v = self.buf[self.p:self.p + n]
        if len(v) != n:
            raise ValueError("unexpected end of data")
        self.p += n
        return v

    def i32(self): return struct.unpack("<i", self.take(4))[0]
    def i64(self): return struct.unpack("<q", self.take(8))[0]
    def f32(self): return struct.unpack("<f", self.take(4))[0]
    def f64(self): return struct.unpack("<d", self.take(8))[0]
    def i16(self): return struct.unpack("<h", self.take(2))[0]
    def boolean(self): return self.take(1)[0] != 0
    def vec3(self): return (self.f32(), self.f32(), self.f32())

    def string(self):
        """.NET BinaryWriter string: 7-bit encoded length, then UTF-8."""
        n = shift = 0
        while True:
            c = self.take(1)[0]
            n |= (c & 0x7F) << shift
            shift += 7
            if not c & 0x80:
                break
        return self.take(n).decode("utf-8")

    def blob(self):
        return self.take(self.i32())


# ---------------------------------------------------------------- where saves live

def default_roots():
    roots = []
    local = os.path.join(os.environ.get("USERPROFILE", ""), "AppData", "LocalLow", "IronGate", "Valheim")
    if os.path.isdir(local):
        roots.append(local)
    steam = None
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as k:
            steam = winreg.QueryValueEx(k, "SteamPath")[0]
    except Exception:
        pass
    if steam:
        roots += sorted(glob.glob(os.path.join(steam, "userdata", "*", "892970", "remote")))
    return roots


def world_dirs(root):
    """Chunked-format worlds are folders under worlds/ (cloud) or worlds_local/ (local).
    Backup folders (<name>_backup_auto-<stamp>) are skipped."""
    out = []
    for sub in ("worlds", "worlds_local"):
        for d in sorted(glob.glob(os.path.join(root, sub, "*"))):
            if not os.path.isdir(d) or "_backup_" in os.path.basename(d):
                continue
            # A cloud world still gets a worlds_local\<name>\ folder holding only the minimap texture
            # cache (cacheMinimap*). That is not a world - it has no _main.*.fwl2.
            if not glob.glob(os.path.join(d, "_main.*.fwl2")):
                continue
            out.append(d)
    return out


def newest(pattern):
    """_main.<N>.* - the highest N is the current save (a complete set also has _main.<N>.ok)."""
    files = glob.glob(pattern)
    def gen(f):
        try:
            return int(os.path.basename(f).split(".")[1])
        except (IndexError, ValueError):
            return -1
    return max(files, key=gen) if files else None


# ---------------------------------------------------------------- parsers

def read_fwl2(path):
    """.fwl2: int length, then [int version, string name, string seedName, int seed, long uid,
    int worldGenVersion, bool needsDB, int n + n strings (starting global keys / world modifiers), ...]."""
    raw = open(path, "rb").read()
    r = Reader(raw[4:4 + Reader(raw).i32()])
    w = dict(version=r.i32(), name=r.string(), seed_name=r.string(), seed=r.i32(), uid=r.i64(),
             world_gen_version=r.i32(), needs_db=r.boolean())
    w["starting_global_keys"] = [r.string() for _ in range(r.i32())]
    return w


def read_db2_locations(path):
    """.db2: int version, double netTime, then a gzip-compressed ZoneSystem block:
    [int n + n (short x, short y) generated zones, int locationVersion, int n + n global keys,
    bool locationsGenerated, int n + n (int prefabHash, vec3 pos, bool placed)]."""
    r = Reader(open(path, "rb").read())
    version, net_time = r.i32(), r.f64()
    z = Reader(gzip.decompress(r.blob()))
    zones = [(z.i16(), z.i16()) for _ in range(z.i32())]
    location_version = z.i32()
    keys = [z.string() for _ in range(z.i32())]
    generated = z.boolean()
    locations = [(z.i32(), z.vec3(), z.boolean()) for _ in range(z.i32())]
    return dict(version=version, net_time=net_time, generated_zones=len(zones),
                location_version=location_version, global_keys=keys,
                locations_generated=generated, locations=locations)


def read_fch(path):
    """.fch: int length, ZPackage data, int hashLength, SHA-512 (read and ignored by the game).
    Per-world data (keyed by world UID) holds spawn/logout/death/home points and the map blob:
    int mapVersion(8), then gzip [int textureSize, explored bits, exploredOthers bits, pins with m_save]."""
    raw = open(path, "rb").read()
    r = Reader(raw)
    r = Reader(r.take(r.i32()))
    version, nstats, ncat = r.i32(), r.i32(), r.i32()
    for _ in range(ncat):                       # stats, known keys and trophies - skipped
        for _ in range(nstats):
            r.f32()
        for _ in range(3):
            for _ in range(r.i32()):
                r.string(); r.f32()
        for _ in range(r.i32()):
            for _ in range(r.i32()):
                r.string(); r.f32()
        for _ in range(5):
            for _ in range(r.i32()):
                r.string(); r.f32()
    first_spawn = r.boolean()
    worlds = []
    for _ in range(r.i32()):
        uid = r.i64()
        has_spawn = r.boolean(); spawn = r.vec3()
        has_logout = r.boolean(); logout = r.vec3()
        has_death = r.boolean(); death = r.vec3()
        home = r.vec3()
        has_map = r.boolean()
        entry = dict(world_uid=uid, spawn=spawn if has_spawn else None, logout=logout if has_logout else None,
                     death=death if has_death else None, home=home, pins=[], explored_pixels=0)
        if has_map:
            m = Reader(r.blob())
            entry["map_version"] = m.i32()
            inner = gzip.decompress(m.blob())
            ir = Reader(inner)
            size = ir.i32()
            entry["texture_size"] = size
            entry["explored_pixels"] = sum(1 for b in inner[4:4 + size * size] if b)
            ir.p += size * size * 2
            for _ in range(ir.i32()):
                entry["pins"].append(dict(name=ir.string(), pos=ir.vec3(), type=ir.i32(),
                                          checked=ir.boolean(), owner=ir.i64(), author=ir.string()))
        worlds.append(entry)
    return dict(profile_version=version, first_spawn=first_spawn, worlds=worlds,
                name=r.string(), player_id=r.i64())


# ---------------------------------------------------------------- commands

def cmd_seed(args):
    print("seed text %r -> int seed %d" % (args.text, seed_from_text(args.text)))


def cmd_worlds(args):
    for root in args.roots:
        for d in world_dirs(root):
            f = newest(os.path.join(d, "_main.*.fwl2"))
            if not f:
                continue
            w = read_fwl2(f)
            ok = "matches" if seed_from_text(w["seed_name"]) == w["seed"] else "DOES NOT MATCH"
            print("%s\n  world %r  seed text %r -> %d (%s the stored seed)  uid %d  world-gen v%d  file v%d"
                  % (d, w["name"], w["seed_name"], w["seed"], ok, w["uid"], w["world_gen_version"], w["version"]))
            if w["starting_global_keys"]:
                print("  world modifiers / starting keys: %s" % ", ".join(w["starting_global_keys"]))


def cmd_characters(args):
    for root in args.roots:
        for sub in ("characters", "characters_local"):
            for f in sorted(glob.glob(os.path.join(root, sub, "*.fch"))):
                c = read_fch(f)
                print("%s\n  %s  player id %d  profile v%d" % (f, c["name"], c["player_id"], c["profile_version"]))
                for w in c["worlds"]:
                    print("  world uid %d: explored %d px, %d saved pins, logout %s, home %s"
                          % (w["world_uid"], w["explored_pixels"], len(w["pins"]), w["logout"], w["home"]))
                    for p in w["pins"]:
                        print("     pin %-24r type %2d at (%.0f, %.0f)" % (p["name"], p["type"], p["pos"][0], p["pos"][2]))


def cmd_locations(args):
    want = stable_hash(args.name) if args.name else None
    for root in args.roots:
        for d in world_dirs(root):
            if os.path.basename(d).lower() != args.world.lower():
                continue
            db = newest(os.path.join(d, "_main.*.db2"))
            if not db:
                print("%s: no .db2 yet" % d)
                continue
            info = read_db2_locations(db)
            print("%s\n  db v%d, location version %d, %d zones generated, %d location instances"
                  % (db, info["version"], info["location_version"], info["generated_zones"], len(info["locations"])))
            shown = [l for l in info["locations"] if want is None or l[0] == want]
            if want is not None:
                print("  %s (hash %d): %d instance(s)" % (args.name, want, len(shown)))
            for h, pos, placed in shown[: args.limit]:
                print("   hash %11d  at (%8.1f, %8.1f)  placed=%s" % (h, pos[0], pos[2], placed))


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--root", action="append", help="save root to inspect (repeatable)")
    sub = ap.add_subparsers(dest="cmd", required=True)
    s = sub.add_parser("seed"); s.add_argument("text"); s.set_defaults(func=cmd_seed)
    s = sub.add_parser("worlds"); s.set_defaults(func=cmd_worlds)
    s = sub.add_parser("characters"); s.set_defaults(func=cmd_characters)
    s = sub.add_parser("locations"); s.add_argument("world")
    s.add_argument("--name", help="location prefab name, e.g. StartTemple, Vendor_BlackForest")
    s.add_argument("--limit", type=int, default=25)
    s.set_defaults(func=cmd_locations)
    args = ap.parse_args()
    args.roots = args.root or default_roots()
    if args.cmd != "seed" and not args.roots:
        sys.exit("no save folders found - pass --root")
    args.func(args)


if __name__ == "__main__":
    main()
