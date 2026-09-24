using System;
using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace SeedLab.Dumper
{
    /// <summary>
    /// A dependency-free JSON writer for the DTOs in SeedLab.Contracts.
    ///
    /// Why not <c>JsonUtility</c>: it will not serialize a Dictionary, nested generic lists, or
    /// produce a stable key order. Why not <c>Newtonsoft.Json</c> (which does ship with the game):
    /// nothing here needs it, and a dumper with no third-party serializer cannot be broken by one.
    ///
    /// Three rules from spec 04 section 3.5, all enforced here rather than per call site:
    /// <list type="number">
    /// <item><b>Every float and double carries its raw IEEE-754 pattern.</b> Any object that has
    /// floating-point members gets a sibling <c>"bits"</c> object keyed by the same member names -
    /// <c>"0xXXXXXXXX"</c> for float, 16 hex digits for double. A decimal round trip through JSON is
    /// not bit-exact in every reader, and one ulp in <c>m_exteriorRadius</c> moves
    /// <c>GetRandomPointInZone</c>'s draw range and therefore every subsequent draw.</item>
    /// <item><b>Enums are written as integers</b>, never names.</item>
    /// <item><b>Nothing is omitted because it equals a default</b>: null fields are written as
    /// <c>null</c> and default numbers are written out, so a future default change cannot be mistaken
    /// for an unchanged value.</item>
    /// </list>
    ///
    /// Object key order is the reflected field order, which for a C#-compiled type is declaration
    /// order on both Mono and CoreCLR. It is advisory: only array order is semantic.
    /// </summary>
    internal static class Json
    {
        private const string Indent = "  ";

        public static string Serialize(object root)
        {
            var sb = new StringBuilder(1 << 16);
            WriteValue(sb, root, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        private static void WriteValue(StringBuilder sb, object v, int depth)
        {
            if (v == null) { sb.Append("null"); return; }

            switch (v)
            {
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case float f: WriteFloat(sb, f); return;
                case double d: WriteDouble(sb, d); return;
                case char c: WriteString(sb, c.ToString()); return;
            }

            Type t = v.GetType();

            // Enums are integers on the wire (rule 2). Heightmap.Biome is a bitmask whose names would
            // be ambiguous anyway (All = 0x37F prints as a name, not as the bits a reader needs).
            if (t.IsEnum)
            {
                sb.Append(Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            }

            if (v is sbyte || v is byte || v is short || v is ushort || v is int || v is long)
            {
                sb.Append(Convert.ToInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (v is uint || v is ulong)
            {
                sb.Append(Convert.ToUInt64(v, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture));
                return;
            }
            if (v is decimal dec)
            {
                sb.Append(dec.ToString(CultureInfo.InvariantCulture));
                return;
            }

            // Dictionaries BEFORE the object path. WriteObject reflects over public FIELDS, and a
            // Dictionary has none - so it used to fall through and emit "{}" for a table of 6,258
            // strings while the caller's own count field cheerfully reported 6,258. That shipped: a
            // localization dump said "6258 strings in English" in the log and carried an empty object.
            if (v is IDictionary dict) { WriteDictionary(sb, dict, depth); return; }

            if (v is IList list) { WriteArray(sb, list, depth); return; }

            WriteObject(sb, v, t, depth);
        }

        /// <summary>
        /// A dictionary with string keys, as a JSON object. Keys are written in the order the
        /// dictionary enumerates them, which for a table read out of the game is the game's own order.
        /// </summary>
        private static void WriteDictionary(StringBuilder sb, IDictionary dict, int depth)
        {
            if (dict.Count == 0) { sb.Append("{}"); return; }
            sb.Append("{\n");
            int i = 0;
            foreach (DictionaryEntry e in dict)
            {
                AppendIndent(sb, depth + 1);
                WriteString(sb, Convert.ToString(e.Key, CultureInfo.InvariantCulture) ?? "");
                sb.Append(": ");
                WriteValue(sb, e.Value, depth + 1);
                if (i < dict.Count - 1) sb.Append(',');
                sb.Append('\n');
                i++;
            }

            AppendIndent(sb, depth);
            sb.Append('}');
        }

        private static void WriteArray(StringBuilder sb, IList list, int depth)
        {
            if (list.Count == 0) { sb.Append("[]"); return; }
            sb.Append("[\n");
            for (int i = 0; i < list.Count; i++)
            {
                AppendIndent(sb, depth + 1);
                WriteValue(sb, list[i], depth + 1);
                if (i < list.Count - 1) sb.Append(',');
                sb.Append('\n');
            }
            AppendIndent(sb, depth);
            sb.Append(']');
        }

        private static void WriteObject(StringBuilder sb, object v, Type t, int depth)
        {
            FieldInfo[] fields = t.GetFields(BindingFlags.Public | BindingFlags.Instance);
            if (fields.Length == 0)
            {
                // A type this writer does not understand must NOT come out as an empty object. That is
                // indistinguishable from "this really was empty", and it is how a 6,258-entry
                // localization table shipped as "{}" beside a count of 6258. Fail loudly instead: the
                // dump records the error and the file says what happened.
                throw new InvalidOperationException(
                    "Json cannot serialise " + t.FullName + ": it has no public instance fields and is "
                    + "neither a dictionary, a list, nor a primitive. Writing it as {} would be a "
                    + "silently empty record. Add a case to WriteValue, or give the DTO fields.");
            }

            // Collected while writing, then emitted as the trailing "bits" object (rule 1).
            StringBuilder bits = null;
            int bitsCount = 0;

            sb.Append("{\n");
            for (int i = 0; i < fields.Length; i++)
            {
                FieldInfo f = fields[i];
                object value = f.GetValue(v);

                AppendIndent(sb, depth + 1);
                WriteString(sb, f.Name);
                sb.Append(": ");
                WriteValue(sb, value, depth + 1);

                if (value is float fv)
                {
                    if (bits == null) bits = new StringBuilder();
                    if (bitsCount++ > 0) bits.Append(",\n");
                    AppendIndent(bits, depth + 2);
                    WriteString(bits, f.Name);
                    bits.Append(": \"0x").Append(FloatBits(fv).ToString("X8", CultureInfo.InvariantCulture)).Append('"');
                }
                else if (value is double dv)
                {
                    if (bits == null) bits = new StringBuilder();
                    if (bitsCount++ > 0) bits.Append(",\n");
                    AppendIndent(bits, depth + 2);
                    WriteString(bits, f.Name);
                    bits.Append(": \"0x").Append(DoubleBits(dv).ToString("X16", CultureInfo.InvariantCulture)).Append('"');
                }

                if (i < fields.Length - 1 || bits != null) sb.Append(',');
                sb.Append('\n');
            }

            if (bits != null)
            {
                AppendIndent(sb, depth + 1);
                sb.Append("\"bits\": {\n");
                sb.Append(bits);
                sb.Append('\n');
                AppendIndent(sb, depth + 1);
                sb.Append("}\n");
            }

            AppendIndent(sb, depth);
            sb.Append('}');
        }

        private static void AppendIndent(StringBuilder sb, int depth)
        {
            for (int i = 0; i < depth; i++) sb.Append(Indent);
        }

        /// <summary>JSON has no literal for NaN or the infinities, so they are written as strings.
        /// They occur only in the <c>Mathf.FloatToHalf</c> adversarial set, where the input is
        /// deliberately pathological; the "bits" object carries the exact pattern either way.</summary>
        private static void WriteFloat(StringBuilder sb, float f)
        {
            if (float.IsNaN(f)) { sb.Append("\"NaN\""); return; }
            if (float.IsPositiveInfinity(f)) { sb.Append("\"Infinity\""); return; }
            if (float.IsNegativeInfinity(f)) { sb.Append("\"-Infinity\""); return; }
            sb.Append(f.ToString("R", CultureInfo.InvariantCulture));
        }

        private static void WriteDouble(StringBuilder sb, double d)
        {
            if (double.IsNaN(d)) { sb.Append("\"NaN\""); return; }
            if (double.IsPositiveInfinity(d)) { sb.Append("\"Infinity\""); return; }
            if (double.IsNegativeInfinity(d)) { sb.Append("\"-Infinity\""); return; }
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        public static uint FloatBits(float f)
        {
            // netstandard2.1 has no BitConverter.SingleToUInt32Bits.
            return BitConverter.ToUInt32(BitConverter.GetBytes(f), 0);
        }

        public static ulong DoubleBits(double d)
        {
            return unchecked((ulong)BitConverter.DoubleToInt64Bits(d));
        }

        public static string Hex(float f)
        {
            return "0x" + FloatBits(f).ToString("X8", CultureInfo.InvariantCulture);
        }

        private static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\b': sb.Append("\\b"); break;
                    case '\f': sb.Append("\\f"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < ' ') sb.Append("\\u").Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
