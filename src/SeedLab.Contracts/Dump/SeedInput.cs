namespace SeedLab.Contracts.Dump
{
    /// <summary>
    /// What the new-world UI actually accepts as a seed text. This is serialized prefab data on
    /// <c>FejdStartup.m_newWorldSeed</c> (a <c>GUIFramework.GuiInputField</c>, which derives from
    /// <c>TMPro.TMP_InputField</c>), so it cannot be decompiled - <c>FejdStartup.OnNewWorldDone</c>
    /// passes <c>m_newWorldSeed.text</c> straight to <c>new World(name, text)</c> with no trimming,
    /// filtering or length check of its own (FejdStartup.OnNewWorldDone / decompiled).
    ///
    /// <para>It matters for exactly one reason: SeedLab inverts a 32-bit seed back into a typeable
    /// text, and it must not hand the user something the game will truncate or refuse. Seven
    /// characters reach every one of the 2^32 worlds, so any limit of 7 or more costs nothing.</para>
    /// </summary>
    public sealed class SeedInputFile
    {
        public string? stamp;
        public int schema;
        public SeedInputFieldDef? seedField;
        public SeedInputFieldDef? nameField;
        public string[]? notes;
    }

    /// <summary>One TMP_InputField's constraints, read by reflection so the plugin needs no
    /// TextMeshPro or gui_framework reference.</summary>
    public sealed class SeedInputFieldDef
    {
        /// <summary>The FejdStartup member this came from.</summary>
        public string? fieldName;

        /// <summary>The runtime component type, e.g. GUIFramework.GuiInputField.</summary>
        public string? componentType;

        /// <summary>TMP_InputField.characterLimit. <b>0 means unlimited</b> in TMP, not "rejects
        /// everything" - do not read a 0 here as a zero-length field.</summary>
        public int characterLimit;

        /// <summary>TMP_InputField.characterValidation (None, Digit, Alphanumeric, Name, ...).</summary>
        public string? characterValidation;

        /// <summary>TMP_InputField.contentType (Standard, Alphanumeric, Custom, ...).</summary>
        public string? contentType;

        public string? inputType;
        public string? lineType;
        public bool readOnly;
        public bool richText;

        /// <summary>The text in the field when the dump ran. At the create-world panel this is
        /// whatever World.GenerateSeed() produced (10 characters), which is itself evidence of what
        /// the game considers a normal seed.</summary>
        public string? currentText;
        public int currentTextLength;

        /// <summary>Property names that were asked for and could not be read, so a missing value is
        /// never mistaken for a zero.</summary>
        public string[]? unreadProperties;
    }
}
