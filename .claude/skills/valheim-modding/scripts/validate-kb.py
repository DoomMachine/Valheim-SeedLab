"""Checks the Valheim knowledge base for the ways it can silently break.

    python validate-kb.py            (from anywhere; finds .claude relative to itself)

Checks every skill and agent in the .claude folder this script sits in, plus a CLAUDE.md beside that
folder if there is one:
  - frontmatter parses as strict YAML (PyYAML) with a name matching its folder/file and a description
    (a malformed header stops a skill or agent from loading, with no error shown)
  - SKILL.md bodies stay under 500 lines (the size at which a skill stops being cheap to load)
  - every relative path a SKILL.md or reference mentions in backticks or a markdown link exists
  - no file starts with a UTF-8 BOM, contains mojibake ("â€" - what a PowerShell 5.1
    Get-Content/Set-Content round trip does to an em dash), or contains a control character
    (what a bash heredoc does to "\1" or "\0" when it eats a backslash)
  - environment.md still carries its KB-STAMP line
Exit code 0 = clean, 1 = problems found (each printed).
"""
import io
import os
import re
import sys

try:
    import yaml
except ImportError:
    yaml = None

HERE = os.path.dirname(os.path.abspath(__file__))
CLAUDE_DIR = os.path.normpath(os.path.join(HERE, "..", "..", ".."))      # scripts -> skill -> skills -> .claude
BASE = os.path.dirname(CLAUDE_DIR)                                        # the folder holding .claude
problems = []


def problem(path, msg):
    problems.append("%s: %s" % (os.path.relpath(path, BASE), msg))


def read(path):
    raw = open(path, "rb").read()
    if raw.startswith(b"\xef\xbb\xbf"):
        problem(path, "starts with a UTF-8 BOM")
    text = raw.decode("utf-8", errors="replace")
    # Inline code is exempt: the pitfalls file quotes the corrupted sequence on purpose.
    prose = re.sub(r"`[^`\n]*`", "", text)
    if "â€" in prose:
        problem(path, "contains mojibake (a-circumflex + euro sign, a mangled em dash or quote) - "
                      "repair with text.encode('cp1252').decode('utf-8')")
    # A control character means a botched escape got written into the file (a bash heredoc eating a
    # backslash turns \1 into 0x01, \0 into NUL). grep and git then treat the file as binary.
    bad = sorted(set(c for c in text if ord(c) < 32 and c not in "\t\n\r"))
    if bad:
        where = text.index(bad[0])
        problem(path, "contains control character(s) %s - a mangled escape sequence; context: %r"
                      % (", ".join("0x%02X" % ord(c) for c in bad),
                         text[max(0, where - 40):where + 20]))
    # \r is excluded above because it is half of a CRLF line ending, but a CR that is NOT followed by LF
    # is the same class of bug: a Windows path separator written as an escape, so "skill\references\x.md"
    # became a carriage return that swallowed the r. Seen twice in the knowledge base's changelog (2026-09-23).
    m = re.search(r"\r(?!\n)", text)
    if m:
        problem(path, "contains a stray carriage return (a backslash-r path separator read as a control "
                      "character); context: %r - use forward slashes in documentation paths"
                      % text[max(0, m.start() - 40):m.start() + 20])
    return text


def skill_root(path):
    """The folder holding the SKILL.md this file belongs to (paths like scripts/x are relative to it)."""
    d = os.path.dirname(path)
    while d and d != os.path.dirname(d):
        if os.path.isfile(os.path.join(d, "SKILL.md")):
            return d
        d = os.path.dirname(d)
    return os.path.dirname(path)


def check_frontmatter(path, expected_name):
    text = read(path)
    m = re.match(r"^---\n(.*?)\n---\n", text, re.S)
    if not m:
        problem(path, "no YAML frontmatter")
        return text, ""
    if yaml is None:
        print("note: PyYAML not installed - frontmatter only checked loosely (pip install pyyaml)")
        meta = dict(l.split(":", 1) for l in m.group(1).splitlines() if ":" in l)
        meta = {k.strip(): v.strip() for k, v in meta.items()}
    else:
        try:
            meta = yaml.safe_load(m.group(1)) or {}
        except yaml.YAMLError as e:
            problem(path, "frontmatter is not valid YAML: %s" % e)
            return text, ""
    if meta.get("name") != expected_name:
        problem(path, "name is %r, expected %r" % (meta.get("name"), expected_name))
    desc = meta.get("description")
    if not isinstance(desc, str) or len(desc) < 50:
        problem(path, "description missing or too short to trigger reliably")
    return text, text[m.end():]


def check_paths(path, text):
    """Relative paths that look like files in this skill must exist.

    Markdown links resolve against the file's own folder (standard markdown). Backticked paths that start
    with references/, scripts/ or assets/ are written relative to the skill root, wherever they appear.
    """
    for link in set(re.findall(r"\]\(([^)#\s]+)\)", text)):
        if link.startswith("http") or "<" in link:
            continue
        if not os.path.exists(os.path.normpath(os.path.join(os.path.dirname(path), link))):
            problem(path, "links to %s, which does not exist" % link)

    root = skill_root(path)
    for c in set(re.findall(r"`((?:references|scripts|assets)[/\\][^`\s]+)`", text)):
        if "<" in c or "*" in c:
            continue
        if not os.path.exists(os.path.normpath(os.path.join(root, c))):
            problem(path, "mentions %s, which does not exist in the skill" % c)


skills_dir = os.path.join(CLAUDE_DIR, "skills")
for name in sorted(os.listdir(skills_dir)):
    skill_md = os.path.join(skills_dir, name, "SKILL.md")
    if not os.path.isfile(skill_md):
        continue
    text, body = check_frontmatter(skill_md, name)
    if body.count("\n") > 500:
        problem(skill_md, "body is %d lines - move detail into references/" % body.count("\n"))
    check_paths(skill_md, text)
    refs = os.path.join(skills_dir, name, "references")
    if os.path.isdir(refs):
        for root, _, files in os.walk(refs):
            for f in files:
                if f.endswith(".md"):
                    check_paths(os.path.join(root, f), read(os.path.join(root, f)))

agents_dir = os.path.join(CLAUDE_DIR, "agents")
if os.path.isdir(agents_dir):
    for f in sorted(os.listdir(agents_dir)):
        if f.endswith(".md"):
            check_frontmatter(os.path.join(agents_dir, f), f[:-3])

claude_md = os.path.join(BASE, "CLAUDE.md")
if os.path.isfile(claude_md):
    read(claude_md)

env = os.path.join(skills_dir, "valheim-modding", "references", "environment.md")
if os.path.isfile(env) and not re.search(r"(?m)^KB-STAMP ", read(env)):
    problem(env, "KB-STAMP line missing - check-game-version.ps1 depends on it")

if problems:
    print("KNOWLEDGE BASE PROBLEMS (%d):" % len(problems))
    for p in problems:
        print("  - " + p)
    sys.exit(1)
print("knowledge base OK - frontmatter, sizes, paths, encoding and stamp all check out")
