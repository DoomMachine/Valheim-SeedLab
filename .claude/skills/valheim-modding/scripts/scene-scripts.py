"""Compare the main scene of two Valheim builds: its GameObjects and the classes of its MonoBehaviours.

Written 2026-09-26 to check that the dedicated server's main scene holds the same objects as the client's
(valheim-modding references/multiplayer.md section 1.4 is its result). It reads the SoftRef bundle that holds
Assets/Scenes/main.unity (file 17245031 in 1.0.16) from both installs, counts GameObjects by name and
MonoBehaviours by class (each script reference is resolved through the bundle that holds the MonoScript), and
prints what differs plus a list of watched classes.

Read-only; writes nothing. Pure Python 3: LZMA from the standard library, LZ4 block decoding implemented here.
Handles UnityFS bundles and SerializedFile version 22+ (Unity 6000.0.75f1 writes both).

Usage:
  python scene-scripts.py [--a DATA] [--b DATA] [--label-a client] [--label-b server]
                          [--bundle 17245031] [--watch Minimap,Hud,...] [--list]
  DATA is a <game>_Data folder. Defaults: --a <game>\\valheim_Data, --b the dedicated server beside the game
  (..\\Valheim dedicated server\\valheim_server_Data); <game> is found as the other valheim-modding
  scripts find it: SEEDLAB_VALHEIM_DIR, the folders above this script, then the Steam libraries.
  --list prints every MonoBehaviour class count of A as well.

Merged 2026-09-26 from four scripts written for that investigation and the UnityFS / SerializedFile readers
of the 2026-09-26 MobTracker review.
"""
import argparse
import collections
import lzma
import os
import re
import struct
import sys

# ---------------------------------------------------------------- UnityFS bundles

def lz4_block(src, usize):
    dst = bytearray()
    i, n = 0, len(src)
    while i < n:
        tok = src[i]; i += 1
        lit = tok >> 4
        if lit == 15:
            while True:
                b = src[i]; i += 1; lit += b
                if b != 255:
                    break
        dst += src[i:i + lit]; i += lit
        if i >= n:
            break
        off = src[i] | (src[i + 1] << 8); i += 2
        ml = tok & 15
        if ml == 15:
            while True:
                b = src[i]; i += 1; ml += b
                if b != 255:
                    break
        ml += 4
        start = len(dst) - off
        for k in range(ml):
            dst.append(dst[start + k])
    if len(dst) != usize:
        raise ValueError("LZ4 block decoded to %d bytes, expected %d" % (len(dst), usize))
    return bytes(dst)


def decomp(data, usize, ctype):
    if ctype == 0:
        return data
    if ctype in (2, 3):
        return lz4_block(data, usize)
    if ctype == 1:
        props = data[:5]
        f = lzma.LZMADecompressor(format=lzma.FORMAT_RAW, filters=[{
            "id": lzma.FILTER_LZMA1, "dict_size": struct.unpack('<I', props[1:5])[0],
            "lc": props[0] % 9, "lp": (props[0] // 9) % 5, "pb": props[0] // 45}])
        return f.decompress(data[5:])[:usize]
    raise ValueError("unknown compression type %d" % ctype)


def read_cstr(b, i):
    j = b.index(b'\0', i)
    return b[i:j].decode(), j + 1


def _bundle_header(b, path):
    i = 8
    ver = struct.unpack('>I', b[i:i + 4])[0]; i += 4
    _, i = read_cstr(b, i)
    uv, i = read_cstr(b, i)
    size, csz, usz, flags = struct.unpack('>qIII', b[i:i + 20]); i += 20
    if ver >= 7:
        i = (i + 15) & ~15
    return uv, i, csz, usz, flags


def _block_info(info):
    p = 16
    nblocks = struct.unpack('>i', info[p:p + 4])[0]; p += 4
    blocks = []
    for _ in range(nblocks):
        u, c, f = struct.unpack('>IIH', info[p:p + 10]); p += 10
        blocks.append((u, c, f))
    nnodes = struct.unpack('>i', info[p:p + 4])[0]; p += 4
    nodes = []
    for _ in range(nnodes):
        off, sz, fl = struct.unpack('>qqI', info[p:p + 20]); p += 20
        name, p = read_cstr(info, p)
        nodes.append((off, sz, fl, name))
    return blocks, nodes


def bundle_node_names(path):
    """Node names of a UnityFS bundle, reading as little of the file as possible. [] if not a bundle."""
    with open(path, 'rb') as f:
        b = f.read(65536)
        if not b.startswith(b'UnityFS'):
            return []
        uv, i, csz, usz, flags = _bundle_header(b, path)
        if flags & 0x80:
            f.seek(-csz, 2); bi = f.read(csz)
        elif i + csz > len(b):
            f.seek(i); bi = f.read(csz)
        else:
            bi = b[i:i + csz]
    _, nodes = _block_info(decomp(bi, usz, flags & 0x3f))
    return [n[3] for n in nodes]


def unpack_bundle(path):
    """(unity version, [(offset, size, flags, name)], decompressed data) of a UnityFS bundle."""
    with open(path, 'rb') as f:
        b = f.read()
    uv, i, csz, usz, flags = _bundle_header(b, path)
    if flags & 0x80:
        bi = b[len(b) - csz:]
    else:
        bi = b[i:i + csz]; i += csz
    blocks, nodes = _block_info(decomp(bi, usz, flags & 0x3f))
    if flags & 0x200:
        i = (i + 15) & ~15
    out = bytearray()
    for u, c, f in blocks:
        out += decomp(b[i:i + c], u, f & 0x3f); i += c
    return uv, nodes, bytes(out)

# ---------------------------------------------------------------- SerializedFile (v22+)

def parse_serialized(b):
    """(unity version, class ids by type index, [(pathID, data offset, size, type index)], externals)."""
    ver = struct.unpack('>I', b[8:12])[0]
    if ver < 22:
        raise ValueError("SerializedFile version %d not handled (needs 22+)" % ver)
    msize, fsize, doff = struct.unpack('>Iqq', b[20:40])
    E = '<' if b[16] == 0 else '>'
    p = 48
    uver, p = read_cstr(b, p)
    p += 4                                   # target platform
    ett = b[p]; p += 1                       # type trees present
    ntypes = struct.unpack(E + 'i', b[p:p + 4])[0]; p += 4
    types = []
    for _ in range(ntypes):
        cid = struct.unpack(E + 'i', b[p:p + 4])[0]; p += 4
        p += 1                               # stripped
        sti = struct.unpack(E + 'h', b[p:p + 2])[0]; p += 2
        if cid == 114 or sti >= 0 or cid < 0:
            p += 16                          # script id hash
        p += 16                              # old type hash
        if ett:
            nn, sbs = struct.unpack(E + 'ii', b[p:p + 8]); p += 8
            p += 32 * nn + sbs
            nd = struct.unpack(E + 'i', b[p:p + 4])[0]; p += 4 + 4 * nd
        types.append(cid)
    nobj = struct.unpack(E + 'i', b[p:p + 4])[0]; p += 4
    objs = []
    for _ in range(nobj):
        p = (p + 3) & ~3
        pid, bstart, bsize, tid = struct.unpack(E + 'qqIi', b[p:p + 24]); p += 24
        objs.append((pid, doff + bstart, bsize, tid))
    nscr = struct.unpack(E + 'i', b[p:p + 4])[0]; p += 4
    for _ in range(nscr):
        p = (p + 3) & ~3; p += 12
    next_ = struct.unpack(E + 'i', b[p:p + 4])[0]; p += 4
    ext = []
    for _ in range(next_):
        _, p = read_cstr(b, p)
        p += 16 + 4                          # guid, type
        path, p = read_cstr(b, p)
        ext.append(path)
    return uver, types, objs, ext


def read_str(b, p):
    """An aligned Unity string field (int32 length, UTF-8 bytes, pad to 4)."""
    n = struct.unpack('<i', b[p:p + 4])[0]
    s = b[p + 4:p + 4 + n].decode('utf-8', 'replace')
    return s, (p + 4 + n + 3) & ~3

# ---------------------------------------------------------------- the comparison

def scene_summary(data_dir, bundle_file):
    bundles = os.path.join(data_dir, "StreamingAssets", "SoftRef", "Bundles")
    uv, nodes, data = unpack_bundle(os.path.join(bundles, bundle_file))
    scene_nodes = [n for n in nodes if '.' not in n[3]]
    if not scene_nodes:
        raise ValueError("no scene file in bundle %s (nodes: %s)" % (bundle_file, [n[3] for n in nodes]))
    off, sz, fl, name = max(scene_nodes, key=lambda n: n[1])
    b = data[off:off + sz]
    uver, types, objs, ext = parse_serialized(b)

    go_names = collections.Counter()
    mb_refs = collections.Counter()
    for (pid, st, size, tid) in objs:
        cid = types[tid]
        if cid == 1:                         # GameObject: m_Component[], m_Layer, m_Name
            n = struct.unpack('<i', b[st:st + 4])[0]
            nm, _ = read_str(b, st + 4 + 12 * n + 4)
            go_names[nm] += 1
        elif cid == 114:                     # MonoBehaviour: m_GameObject, m_Enabled, m_Script
            fid, spid = struct.unpack('<iq', b[st + 16:st + 28])
            mb_refs[(fid, spid)] += 1

    # resolve script references through the external files that hold the MonoScripts
    need = {}
    for fid in sorted(set(f for f, _ in mb_refs)):
        if fid > 0:
            need[ext[fid - 1].split('/')[-1]] = fid
    where = {}
    for fn in os.listdir(bundles):
        try:
            names = bundle_node_names(os.path.join(bundles, fn))
        except Exception:
            continue
        for nm in names:
            if nm in need:
                where[nm] = fn
    classes = {}
    for cab, fn in where.items():
        _, bnodes, bdata = unpack_bundle(os.path.join(bundles, fn))
        for boff, bsz, bfl, bname in bnodes:
            if bname != cab:
                continue
            sb = bdata[boff:boff + bsz]
            _, stypes, sobjs, _ = parse_serialized(sb)
            for (pid, st, size, tid) in sobjs:
                if stypes[tid] == 115:       # MonoScript: m_Name, m_ExecutionOrder, m_PropertiesHash, m_ClassName, ...
                    _, p = read_str(sb, st)
                    p += 4 + 16
                    cname, p = read_str(sb, p)
                    classes[(need[cab], pid)] = cname
    mb_classes = collections.Counter()
    unresolved = 0
    for k, v in mb_refs.items():
        if k in classes:
            mb_classes[classes[k]] += v
        else:
            mb_classes["?%s" % (k,)] += v
            unresolved += 1
    return {
        "unity": uv, "scene": name, "gameobjects": go_names, "mb": mb_classes,
        "refs": len(mb_refs), "unresolved": unresolved, "missing_files": sorted(set(need) - set(where)),
    }


def _is_game_dir(d):
    return bool(d) and os.path.isfile(os.path.join(d, "valheim_Data", "Managed", "assembly_valheim.dll"))


def find_game_dir():
    """The Valheim folder, found as the valheim-modding PowerShell scripts find it (_common.ps1): the
    SEEDLAB_VALHEIM_DIR environment variable (authoritative when set), then the folders above this script, then
    the Steam libraries (the registry's SteamPath and the default Steam folders, and every library their
    steamapps\\libraryfolders.vdf lists)."""
    env = os.environ.get("SEEDLAB_VALHEIM_DIR")
    if env:
        if _is_game_dir(env):
            return os.path.abspath(env)
        sys.exit("SEEDLAB_VALHEIM_DIR is set but is not a Valheim folder (no valheim_Data\\Managed\\assembly_valheim.dll): "
                 + env)
    d = os.path.dirname(os.path.abspath(__file__))
    while True:
        if _is_game_dir(d):
            return d
        parent = os.path.dirname(d)
        if parent == d:
            break
        d = parent
    roots = []
    try:
        import winreg
        with winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Valve\Steam") as key:
            roots.append(winreg.QueryValueEx(key, "SteamPath")[0])
    except (ImportError, OSError):
        pass
    for var in ("ProgramFiles(x86)", "ProgramFiles"):
        if os.environ.get(var):
            roots.append(os.path.join(os.environ[var], "Steam"))
    libraries = []
    for root in roots:
        if not os.path.isdir(root):
            continue
        libraries.append(root)
        vdf = os.path.join(root, "steamapps", "libraryfolders.vdf")
        if os.path.isfile(vdf):
            with open(vdf, encoding="utf-8", errors="replace") as f:
                for m in re.finditer(r'"path"\s+"([^"]+)"', f.read()):
                    libraries.append(m.group(1).replace("\\\\", "\\"))
    seen = set()
    for lib in libraries:
        k = os.path.normcase(os.path.normpath(lib))
        if k in seen:
            continue
        seen.add(k)
        cand = os.path.join(lib, "steamapps", "common", "Valheim")
        if _is_game_dir(cand):
            return os.path.abspath(cand)
    sys.exit("Cannot find the Valheim folder; set SEEDLAB_VALHEIM_DIR or pass --a and --b.")


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--a"); ap.add_argument("--b")
    ap.add_argument("--label-a", default="client"); ap.add_argument("--label-b", default="server")
    ap.add_argument("--bundle", default="17245031")
    ap.add_argument("--watch", default="Minimap,Hud,Chat,Console,MessageHud,Menu,InventoryGui,TextInput,GameCamera,"
                                       "Game,ZNet,ZNetScene,ZoneSystem,ObjectDB,EnvMan,Player,LoadingIndicator,UIGamePad")
    ap.add_argument("--list", action="store_true")
    args = ap.parse_args()
    if not args.a or not args.b:
        game = find_game_dir()
        args.a = args.a or os.path.join(game, "valheim_Data")
        args.b = args.b or os.path.join(os.path.dirname(game), "Valheim dedicated server", "valheim_server_Data")

    res = {}
    for label, d in ((args.label_a, args.a), (args.label_b, args.b)):
        s = scene_summary(d, args.bundle)
        res[label] = s
        print("== %s: %s (Unity %s, scene file %s)" % (label, d, s["unity"], s["scene"]))
        print("   GameObjects %d, MonoBehaviours %d, script references %d (%d unresolved)"
              % (sum(s["gameobjects"].values()), sum(s["mb"].values()), s["refs"], s["unresolved"]))
        if s["missing_files"]:
            print("   script files not found in any bundle:", s["missing_files"])
    a, b = res[args.label_a], res[args.label_b]
    gd = sorted((k, a["gameobjects"][k], b["gameobjects"][k]) for k in set(a["gameobjects"]) | set(b["gameobjects"])
                if a["gameobjects"][k] != b["gameobjects"][k])
    md = sorted((k, a["mb"][k], b["mb"][k]) for k in set(a["mb"]) | set(b["mb"]) if a["mb"][k] != b["mb"][k])
    print("GameObject names whose count differs (name, %s, %s): %s" % (args.label_a, args.label_b, gd or "none"))
    print("MonoBehaviour classes whose count differs (class, %s, %s): %s" % (args.label_a, args.label_b, md or "none"))
    for w in [x.strip() for x in args.watch.split(",") if x.strip()]:
        print("   %-18s %s %d   %s %d" % (w, args.label_a, a["mb"][w], args.label_b, b["mb"][w]))
    if args.list:
        for k, v in sorted(a["mb"].items()):
            print("   %s %-40s %d" % (args.label_a, k, v))


if __name__ == "__main__":
    main()
