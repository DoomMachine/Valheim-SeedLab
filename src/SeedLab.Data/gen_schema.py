import re, os, glob

# The repository root, two folders above this script (src\SeedLab.Data\gen_schema.py).
ROOT = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), '..', '..'))
os.chdir(ROOT)

dtos = {}
order = []
for p in sorted(glob.glob('src/SeedLab.Contracts/Dump/*.cs')):
    src = re.sub(r'///.*', '', open(p, encoding='utf-8').read())
    cur = None
    for line in src.splitlines():
        m = re.search(r'public sealed class (\w+)', line)
        if m:
            cur = m.group(1); dtos[cur] = []; order.append(cur)
        # The type may be generic: 'Dictionary<string, string>?' carries angle brackets, a comma and a
        # space, and the earlier '[\w\[\]?\.]+' matched none of them, so LocalizationFile.translations
        # was SILENTLY dropped from the schema - the loader then validated six of its seven fields and
        # said nothing about the seventh. A space is allowed only INSIDE the angle brackets, on purpose:
        # allowing it in the type at large would make 'public static int Count;' parse as type
        # 'static int', field 'Count'.
        m = re.match(r'\s*public ([A-Za-z_][\w\.]*(?:<[^;]*>)?[\[\]?]*) (\w+);', line)
        if m and cur:
            dtos[cur].append((m.group(2), m.group(1)))

def base(t): return t.replace('[]', '').replace('?', '')

# topological order so a schema is built after its children
built = []
def emit(t, seen):
    if t in built: return
    if t in seen: raise SystemExit('cycle at ' + t)
    seen = seen | {t}
    for n, ft in dtos[t]:
        b = base(ft)
        if b in dtos: emit(b, seen)
    built.append(t)
for t in order: emit(t, set())

lines = []
for t in built:
    fs = []
    for n, ft in dtos[t]:
        b = base(ft)
        arr = ft.endswith('[]') or ft.endswith('[]?')
        if b in dtos:
            kind = 'Arr(' if arr else 'Obj('
            fs.append('                %s"%s", %s),' % (kind, n, b))
        elif b == 'float':
            fs.append('                F32("%s"),' % n)
        elif b == 'double':
            fs.append('                F64("%s"),' % n)
        else:
            fs.append('                Scalar("%s"),' % n)
    lines.append('        %s = new DumpSchema("%s", new[]\n            {\n%s\n            });' % (t, t, '\n'.join(fs)))

decls = '\n'.join('        internal static readonly DumpSchema %s;' % t for t in built)
body = '\n\n'.join(lines)

out = '''using System;

namespace SeedLab.Data
{
    /// <summary>One field's shape in a <see cref="DumpSchema"/>.</summary>
    internal enum FieldKind
    {
        Scalar = 0,
        F32 = 1,
        F64 = 2,
        Object = 3,
        ObjectArray = 4,
    }

    internal readonly struct SchemaField
    {
        internal readonly string Name;
        internal readonly FieldKind Kind;
        internal readonly DumpSchema? Child;

        internal SchemaField(string name, FieldKind kind, DumpSchema? child)
        {
            Name = name; Kind = kind; Child = child;
        }
    }

    internal sealed class DumpSchema
    {
        internal readonly string TypeName;
        internal readonly SchemaField[] Fields;

        /// <summary>Names of the fields that must appear in this object's <c>"bits"</c> sibling.</summary>
        internal readonly string[] BitsFields;

        internal DumpSchema(string typeName, SchemaField[] fields)
        {
            TypeName = typeName;
            Fields = fields;

            int n = 0;
            foreach (SchemaField f in fields)
            {
                if (f.Kind == FieldKind.F32 || f.Kind == FieldKind.F64) n++;
            }

            BitsFields = new string[n];
            n = 0;
            foreach (SchemaField f in fields)
            {
                if (f.Kind == FieldKind.F32 || f.Kind == FieldKind.F64) BitsFields[n++] = f.Name;
            }
        }

        internal bool TryField(ReadOnlySpan<char> name, out SchemaField field)
        {
            for (int i = 0; i < Fields.Length; i++)
            {
                if (name.SequenceEqual(Fields[i].Name.AsSpan()))
                {
                    field = Fields[i];
                    return true;
                }
            }

            field = default;
            return false;
        }
    }

    /// <summary>
    /// The shape every dumped JSON object must have, one schema per DTO in
    /// <c>SeedLab.Contracts.Dump</c>. It exists so that a MISSING field is an error instead of a
    /// silently defaulted zero: <see cref="StrictJson"/> walks the file in lockstep with the schema
    /// and names the file and the field the moment one is absent.
    ///
    /// <para><b>ORDER OF OPERATIONS, and it is a trap.</b> Regenerate this file only AFTER the dump
    /// that carries the new fields exists, never before. On 2026-09-23 <c>DungeonGeneratorDef</c>
    /// gained fourteen fields and <c>RoomChildrenDef</c> two; regenerating first would have made the
    /// loader demand <c>fullFieldsCaptured</c> of a shipped <c>locationprefabs.json</c> that predates
    /// it, and every location answer in the tool would have failed closed on a file that is perfectly
    /// good. <see cref="StrictJson"/> ignores UNKNOWN members by design, so a schema lagging behind
    /// the DTO is harmless in the meantime - the new fields simply are not validated until the dump
    /// that carries them lands. Dump first, regenerate second.</para>
    ///
    /// <para>The field lists are a mechanical transcription of the DTO declarations - same names,
    /// same order, every public field - produced by <c>src\\SeedLab.Data\\gen_schema.py</c> from
    /// <c>src\\SeedLab.Contracts\\Dump\\*.cs</c> and pasted here. They are hard-coded rather than
    /// reflected so that the loader stays trim- and AOT-safe. If a DTO gains a field, re-run the
    /// generator; the loader will then demand the new field, which is the intended behaviour - a dump
    /// written before the field existed is NOT usable data for code that reads it.</para>
    ///
    /// <para><see cref="FieldKind.F32"/>/<see cref="FieldKind.F64"/> additionally require the sibling
    /// <c>"bits"</c> entry the dump format promises (DumpFormat, "JSON conventions"), and the decimal
    /// is checked against it. Every float in the shipped dump agrees with its bits, so the
    /// source-generated deserializer's values are exact; the check is what keeps that true.</para>
    /// </summary>
    internal static class DumpSchemas
    {
        private static SchemaField Scalar(string n) => new SchemaField(n, FieldKind.Scalar, null);
        private static SchemaField F32(string n) => new SchemaField(n, FieldKind.F32, null);
        private static SchemaField F64(string n) => new SchemaField(n, FieldKind.F64, null);
        private static SchemaField Obj(string n, DumpSchema c) => new SchemaField(n, FieldKind.Object, c);
        private static SchemaField Arr(string n, DumpSchema c) => new SchemaField(n, FieldKind.ObjectArray, c);

__DECLS__

        static DumpSchemas()
        {
__BODY__
        }
    }
}
'''
out = out.replace('__DECLS__', decls).replace('__BODY__', body)
dest = os.path.join(ROOT, 'src', 'SeedLab.Data', 'DumpSchemas.cs')
open(dest, 'w', encoding='utf-8', newline='\n').write(out)  # LF, like every file in the project
print('wrote', dest, len(built), 'schemas')
