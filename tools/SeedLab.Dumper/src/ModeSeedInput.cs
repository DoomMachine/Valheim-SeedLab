using System;
using System.Collections.Generic;
using System.Reflection;
using SeedLab.Contracts.Dump;

namespace SeedLab.Dumper
{
    /// <summary>
    /// Reads the new-world UI's seed and name input constraints at the main menu.
    ///
    /// <para>Read-only in the strictest sense: it fetches FejdStartup.instance (a public static
    /// getter, FejdStartup.instance / decompiled line 299), reads two public fields and then a fixed
    /// list of properties by name, and writes them to JSON. It never assigns anything, never
    /// instantiates anything, and cannot draw from UnityEngine.Random.</para>
    ///
    /// <para>Reflection rather than a direct cast so the plugin needs no reference to
    /// gui_framework.dll or Unity.TextMeshPro.dll: the field's declared type is
    /// GUIFramework.GuiInputField, which derives from TMPro.TMP_InputField (gui_framework.dll of this
    /// build; <c>tools\decompile.ps1 -Type GUIFramework.GuiInputField -Assembly gui_framework</c> shows
    /// the declaration).</para>
    /// </summary>
    internal static class ModeSeedInput
    {
        // Every property we want, read off the live component. Names are TMP_InputField's.
        private static readonly string[] Wanted =
        {
            "characterLimit", "characterValidation", "contentType", "inputType", "lineType",
            "readOnly", "richText", "text",
        };

        /// <summary>
        /// Writes seed-input.json when the main menu is up. Returns false (and writes nothing) when
        /// FejdStartup is not present, so an in-world run cannot overwrite a good capture with an
        /// empty one.
        /// </summary>
        public static bool Capture(DumpWriter w, string stamp, List<string> notes)
        {
            try
            {
                FejdStartup fejd = FejdStartup.instance;
                if (fejd == null)
                {
                    notes.Add("seed-input.json not written: FejdStartup.instance is null, so this was " +
                              "not the main menu. Run seedlab_natives at the main menu to capture the " +
                              "seed field's character limit.");
                    return false;
                }

                var fileNotes = new List<string>();
                var file = new SeedInputFile
                {
                    stamp = stamp,
                    schema = DumpFormat.Schema,
                    seedField = ReadField(fejd, "m_newWorldSeed", fileNotes),
                    nameField = ReadField(fejd, "m_newWorldName", fileNotes),
                };

                // The create-world panel may never have been opened this session, in which case the
                // fields hold whatever the prefab shipped with rather than a generated seed. Say so
                // rather than letting a reader infer something from an empty string.
                if (file.seedField != null && string.IsNullOrEmpty(file.seedField.currentText))
                {
                    fileNotes.Add("The seed field was empty when this was read, so the create-world " +
                                  "panel had not populated it with World.GenerateSeed(). The limit " +
                                  "values are prefab data and are valid regardless.");
                }

                file.notes = fileNotes.ToArray();
                w.WriteJson(DumpFormat.SeedInputFile, file);

                string limit = file.seedField == null
                    ? "unknown"
                    : (file.seedField.characterLimit == 0
                        ? "0 (TMP: unlimited)"
                        : file.seedField.characterLimit.ToString());
                Plugin.Log.LogInfo("seed field characterLimit = " + limit +
                                   ", validation = " + (file.seedField?.characterValidation ?? "?") +
                                   ", contentType = " + (file.seedField?.contentType ?? "?"));
                return true;
            }
            catch (Exception e)
            {
                // Never let this stop a dump: it is a footnote, not the payload.
                notes.Add("seed-input capture failed: " + e.GetType().Name + " " + e.Message);
                Plugin.Log.LogWarning("seed-input capture failed: " + e);
                return false;
            }
        }

        private static SeedInputFieldDef ReadField(FejdStartup fejd, string memberName, List<string> notes)
        {
            FieldInfo fi = typeof(FejdStartup).GetField(memberName,
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (fi == null)
            {
                notes.Add("FejdStartup." + memberName + " does not exist in this build - the game " +
                          "changed and this capture is not valid.");
                return null;
            }

            object component = fi.GetValue(fejd);
            if (component == null)
            {
                notes.Add("FejdStartup." + memberName + " is null (the prefab reference is unassigned " +
                          "or the panel was destroyed).");
                return null;
            }

            Type t = component.GetType();
            var def = new SeedInputFieldDef { fieldName = memberName, componentType = t.FullName };
            var unread = new List<string>();

            foreach (string name in Wanted)
            {
                object value;
                if (!TryRead(component, t, name, out value)) { unread.Add(name); continue; }

                switch (name)
                {
                    case "characterLimit": def.characterLimit = Convert.ToInt32(value); break;
                    case "characterValidation": def.characterValidation = value?.ToString(); break;
                    case "contentType": def.contentType = value?.ToString(); break;
                    case "inputType": def.inputType = value?.ToString(); break;
                    case "lineType": def.lineType = value?.ToString(); break;
                    case "readOnly": def.readOnly = Convert.ToBoolean(value); break;
                    case "richText": def.richText = Convert.ToBoolean(value); break;
                    case "text":
                        string s = value as string;
                        def.currentText = s;
                        def.currentTextLength = s == null ? 0 : s.Length;
                        break;
                }
            }

            def.unreadProperties = unread.ToArray();
            return def;
        }

        private static bool TryRead(object instance, Type t, string name, out object value)
        {
            value = null;
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                                           BindingFlags.Instance | BindingFlags.FlattenHierarchy;
                PropertyInfo p = t.GetProperty(name, flags);
                if (p != null && p.CanRead) { value = p.GetValue(instance, null); return true; }

                FieldInfo f = t.GetField(name, flags);
                if (f != null) { value = f.GetValue(instance); return true; }

                // TMP declares some of these on a base type with a different casing convention.
                PropertyInfo alt = t.GetProperty("m_" + name, flags);
                if (alt != null && alt.CanRead) { value = alt.GetValue(instance, null); return true; }

                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}
