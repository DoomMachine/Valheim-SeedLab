using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace SeedLab.Data
{
    /// <summary>
    /// Loads one dumped JSON file: SHA-256 against the manifest, then a strict structural pass, then
    /// the source-generated deserializer.
    ///
    /// <para>The structural pass is the point of this class. <c>System.Text.Json</c> leaves an absent
    /// member at its CLR default and says nothing, which for this data is the worst possible
    /// behaviour - a missing <c>minAltitude</c> becomes 0, which flips
    /// <c>GetRandomPointByBiomes</c> to <c>GetRandomPointByBiomesAboveSeaLevel</c> and changes every
    /// subsequent draw of that location's RNG stream (ZoneSystem.cs:1919). So before anything is
    /// deserialized the file is walked in lockstep with <see cref="DumpSchemas"/>, and the first
    /// field that is not there stops the load with the file name and the JSON path.</para>
    ///
    /// <para>Unknown members are skipped rather than rejected: <c>DumpFormat.Schema</c> is documented
    /// to stay at 1 for additive changes, so a newer dumper must remain readable. A RENAMED member
    /// still fails, because the old name is then missing.</para>
    ///
    /// <para>Floats carry a sibling <c>"bits"</c> entry (DumpFormat, "JSON conventions"). Both are
    /// required, and the decimal is checked against the bit pattern, so the value the deserializer
    /// produces is known to be the exact float the game held - not a decimal that merely looks
    /// right.</para>
    /// </summary>
    internal static class StrictJson
    {
        /// <summary>No dumped object declares more than this many fields (LocationDef, 46 + bits).</summary>
        private const int MaxFields = 64;

        internal static T Load<T>(string path, DumpSchema schema, JsonTypeInfo<T> typeInfo, string? expectedSha256)
            where T : class
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(path);
            }
            catch (Exception ex)
            {
                throw new GameDataException(
                    path + ": could not be read (" + ex.GetType().Name + ": " + ex.Message + "). " + ReDumpHint,
                    ex)
                { File = path };
            }

            if (expectedSha256 != null)
            {
                string actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
                if (!string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new GameDataException(
                        path + ": SHA-256 " + actual + " does not match the manifest's " + expectedSha256
                        + ". The file has been edited or truncated since the dump was written; restore it, "
                        + "or re-run tools\\SeedLab.Dumper in the game to produce a fresh dump.")
                    { File = path };
                }
            }

            Validate(bytes, schema, path);

            T? value;
            try
            {
                value = JsonSerializer.Deserialize(bytes, typeInfo);
            }
            catch (JsonException ex)
            {
                throw new GameDataException(path + ": " + ex.Message, ex) { File = path };
            }

            if (value == null)
            {
                throw new GameDataException(path + ": the file is JSON null.") { File = path };
            }

            return value;
        }

        internal const string ReDumpHint =
            "Re-run tools\\SeedLab.Dumper in the game to produce a fresh dump.";

        // ---- the structural pass -----------------------------------------------------------------

        /// <summary>Reusable per-depth scratch, so a 52,000-object file allocates nothing per object.</summary>
        private sealed class Frame
        {
            internal readonly bool[] Seen = new bool[MaxFields];
            internal readonly bool[] HasDecimal = new bool[MaxFields];
            internal readonly double[] Decimal = new double[MaxFields];
            internal readonly bool[] HasBits = new bool[MaxFields];
            internal readonly ulong[] Bits = new ulong[MaxFields];
        }

        private sealed class Walker
        {
            internal readonly List<Frame> Frames = new List<Frame>();
            internal readonly StringBuilder Path = new StringBuilder("$");
            internal string FileName = "";
            internal int UnknownMembers;

            internal Frame FrameAt(int depth)
            {
                while (Frames.Count <= depth) Frames.Add(new Frame());
                return Frames[depth];
            }
        }

        internal static void Validate(ReadOnlySpan<byte> utf8, DumpSchema schema, string path)
        {
            Walker w = new Walker { FileName = path };
            Utf8JsonReader reader = new Utf8JsonReader(
                utf8,
                new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip });

            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                throw Fail(w, "the root of the file is not a JSON object.");
            }

            ValidateObject(ref reader, schema, w, 0);
        }

        private static void ValidateObject(ref Utf8JsonReader reader, DumpSchema schema, Walker w, int depth)
        {
            if (schema.Fields.Length > MaxFields)
            {
                throw Fail(w, schema.TypeName + " declares " + schema.Fields.Length
                              + " fields, more than the loader's limit of " + MaxFields + ".");
            }

            Frame f = w.FrameAt(depth);
            Array.Clear(f.Seen, 0, schema.Fields.Length);
            Array.Clear(f.HasDecimal, 0, schema.Fields.Length);
            Array.Clear(f.HasBits, 0, schema.Fields.Length);

            bool sawBits = false;

            while (true)
            {
                if (!reader.Read()) throw Fail(w, "the file ends in the middle of an object.");
                if (reader.TokenType == JsonTokenType.EndObject) break;
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw Fail(w, "expected a member name, found " + reader.TokenType + ".");
                }

                int index = IndexOf(ref reader, schema);
                if (index < 0)
                {
                    if (reader.ValueTextEquals("bits"u8))
                    {
                        sawBits = true;
                        ReadBits(ref reader, schema, w, f);
                        continue;
                    }

                    w.UnknownMembers++;
                    reader.Read();
                    reader.Skip();
                    continue;
                }

                SchemaField field = schema.Fields[index];
                f.Seen[index] = true;

                if (!reader.Read()) throw Fail(w, "the file ends after '" + field.Name + "'.");

                switch (field.Kind)
                {
                    case FieldKind.F32:
                    case FieldKind.F64:
                        ReadNumber(ref reader, field, w, f, index);
                        break;

                    case FieldKind.Object:
                        if (reader.TokenType == JsonTokenType.Null) break;
                        if (reader.TokenType != JsonTokenType.StartObject)
                        {
                            throw Fail(w, "'" + field.Name + "' should be an object, found " + reader.TokenType + ".");
                        }

                        Push(w, field.Name);
                        ValidateObject(ref reader, field.Child!, w, depth + 1);
                        Pop(w);
                        break;

                    case FieldKind.ObjectArray:
                        if (reader.TokenType == JsonTokenType.Null) break;
                        if (reader.TokenType != JsonTokenType.StartArray)
                        {
                            throw Fail(w, "'" + field.Name + "' should be an array, found " + reader.TokenType + ".");
                        }

                        int i = 0;
                        while (true)
                        {
                            if (!reader.Read()) throw Fail(w, "the file ends inside '" + field.Name + "'.");
                            if (reader.TokenType == JsonTokenType.EndArray) break;
                            if (reader.TokenType != JsonTokenType.StartObject)
                            {
                                throw Fail(w, "'" + field.Name + "[" + i + "]' should be an object, found "
                                              + reader.TokenType + ".");
                            }

                            Push(w, field.Name + "[" + i.ToString(CultureInfo.InvariantCulture) + "]");
                            ValidateObject(ref reader, field.Child!, w, depth + 1);
                            Pop(w);
                            i++;
                        }

                        break;

                    default:
                        reader.Skip();
                        break;
                }
            }

            for (int i = 0; i < schema.Fields.Length; i++)
            {
                if (f.Seen[i]) continue;
                throw Fail(w, "required field '" + schema.Fields[i].Name + "' is missing from this "
                              + schema.TypeName + ". " + ReDumpHint,
                           w.Path.ToString() + "." + schema.Fields[i].Name);
            }

            if (schema.BitsFields.Length != 0 && !sawBits)
            {
                throw Fail(w, "this " + schema.TypeName + " has " + schema.BitsFields.Length
                              + " float fields but no 'bits' object. " + ReDumpHint);
            }

            for (int i = 0; i < schema.Fields.Length; i++)
            {
                FieldKind k = schema.Fields[i].Kind;
                if (k != FieldKind.F32 && k != FieldKind.F64) continue;

                if (!f.HasBits[i])
                {
                    throw Fail(w, "'bits." + schema.Fields[i].Name + "' is missing, so the exact value of '"
                                  + schema.Fields[i].Name + "' cannot be confirmed. " + ReDumpHint,
                               w.Path.ToString() + ".bits." + schema.Fields[i].Name);
                }

                if (!f.HasDecimal[i]) continue;   // written as "NaN" / "Infinity": the bits are the value

                double fromBits = k == FieldKind.F32
                    ? BitConverter.Int32BitsToSingle(unchecked((int)(uint)f.Bits[i]))
                    : BitConverter.Int64BitsToDouble(unchecked((long)f.Bits[i]));

                double asWritten = k == FieldKind.F32 ? (float)f.Decimal[i] : f.Decimal[i];

                if (fromBits.Equals(asWritten)) continue;   // Equals, so NaN == NaN

                throw Fail(w, "'" + schema.Fields[i].Name + "' is written as "
                              + asWritten.ToString("R", CultureInfo.InvariantCulture)
                              + " but its bit pattern 0x" + f.Bits[i].ToString("X8", CultureInfo.InvariantCulture)
                              + " is " + fromBits.ToString("R", CultureInfo.InvariantCulture)
                              + ". The file is inconsistent with itself. " + ReDumpHint,
                           w.Path.ToString() + "." + schema.Fields[i].Name);
            }
        }

        private static void ReadNumber(ref Utf8JsonReader reader, SchemaField field, Walker w, Frame f, int index)
        {
            if (reader.TokenType == JsonTokenType.Number)
            {
                f.HasDecimal[index] = true;
                f.Decimal[index] = reader.GetDouble();
                return;
            }

            if (reader.TokenType == JsonTokenType.String)
            {
                // JSON has no literal for NaN or the infinities; the dumper writes them as strings and
                // the bits carry the exact pattern, including the NaN payload.
                f.HasDecimal[index] = false;
                return;
            }

            throw Fail(w, "'" + field.Name + "' should be a number, found " + reader.TokenType
                          + (reader.TokenType == JsonTokenType.Null
                              ? " - a float field is never null in this format. " + ReDumpHint
                              : "."),
                       w.Path.ToString() + "." + field.Name);
        }

        private static void ReadBits(ref Utf8JsonReader reader, DumpSchema schema, Walker w, Frame f)
        {
            if (!reader.Read()) throw Fail(w, "the file ends after 'bits'.");
            if (reader.TokenType != JsonTokenType.StartObject)
            {
                throw Fail(w, "'bits' should be an object, found " + reader.TokenType + ".");
            }

            while (true)
            {
                if (!reader.Read()) throw Fail(w, "the file ends inside 'bits'.");
                if (reader.TokenType == JsonTokenType.EndObject) return;
                if (reader.TokenType != JsonTokenType.PropertyName)
                {
                    throw Fail(w, "expected a member name inside 'bits', found " + reader.TokenType + ".");
                }

                int index = IndexOf(ref reader, schema);
                bool wanted = index >= 0
                              && (schema.Fields[index].Kind == FieldKind.F32
                                  || schema.Fields[index].Kind == FieldKind.F64);

                if (!reader.Read()) throw Fail(w, "the file ends inside 'bits'.");

                if (!wanted)
                {
                    w.UnknownMembers++;
                    reader.Skip();
                    continue;
                }

                if (reader.TokenType != JsonTokenType.String)
                {
                    throw Fail(w, "'bits." + schema.Fields[index].Name + "' should be a hex string, found "
                                  + reader.TokenType + ".");
                }

                string hex = reader.GetString()!;
                ReadOnlySpan<char> digits = hex.AsSpan();
                if (digits.StartsWith("0x".AsSpan(), StringComparison.OrdinalIgnoreCase)) digits = digits.Slice(2);

                if (!ulong.TryParse(digits, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out ulong bits))
                {
                    throw Fail(w, "'bits." + schema.Fields[index].Name + "' is '" + hex
                                  + "', which is not a hexadecimal bit pattern.");
                }

                f.HasBits[index] = true;
                f.Bits[index] = bits;
            }
        }

        private static int IndexOf(ref Utf8JsonReader reader, DumpSchema schema)
        {
            for (int i = 0; i < schema.Fields.Length; i++)
            {
                if (reader.ValueTextEquals(schema.Fields[i].Name)) return i;
            }

            return -1;
        }

        private static void Push(Walker w, string segment)
        {
            w.Path.Append('.').Append(segment);
        }

        private static void Pop(Walker w)
        {
            int dot = -1;
            for (int i = w.Path.Length - 1; i >= 0; i--)
            {
                if (w.Path[i] == '.') { dot = i; break; }
            }

            if (dot > 0) w.Path.Length = dot;
        }

        private static GameDataException Fail(Walker w, string message, string? field = null)
        {
            string where = field ?? w.Path.ToString();
            return new GameDataException(w.FileName + ": at " + where + ", " + message)
            {
                File = w.FileName,
                Field = where,
            };
        }
    }
}
