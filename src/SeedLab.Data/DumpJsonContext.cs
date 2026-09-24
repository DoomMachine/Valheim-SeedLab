using System.Text.Json.Serialization;
using SeedLab.Contracts.Dump;

namespace SeedLab.Data
{
    /// <summary>
    /// The source-generated <c>System.Text.Json</c> contracts for every dumped file.
    ///
    /// <para>Source generation rather than reflection for three reasons: a future
    /// <c>PublishAot</c>/trimmed build keeps working (reflection-based deserialization of these DTOs
    /// would be trimmed away and fail at run time, on the user's machine, with a message about a
    /// missing constructor), the metadata is built at compile time instead of on first use, and the
    /// generator fails the BUILD if a DTO ever gains a member it cannot handle.</para>
    ///
    /// <para><c>IncludeFields</c> is not optional here: every DTO in <c>SeedLab.Contracts.Dump</c> is
    /// public FIELDS, deliberately (they are the wire names, and the assembly also targets
    /// netstandard2.1 where <c>[JsonPropertyName]</c> does not exist). Without it every object would
    /// deserialize to all-defaults and no error - exactly the failure this assembly exists to
    /// prevent.</para>
    ///
    /// <para><c>AllowNamedFloatingPointLiterals</c> covers the <c>"NaN"</c>, <c>"Infinity"</c> and
    /// <c>"-Infinity"</c> strings in <c>goldens/natives-half.json</c>. JSON has no literal for them.
    /// Note that a NaN read this way is .NET's own NaN, whose payload need not be the one Unity
    /// produced; where the payload matters, read the bits (see <see cref="Goldens"/>).</para>
    /// </summary>
    [JsonSourceGenerationOptions(
        IncludeFields = true,
        PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
    [JsonSerializable(typeof(DumpManifest))]
    [JsonSerializable(typeof(LocationTableFile))]
    [JsonSerializable(typeof(VegetationTableFile))]
    [JsonSerializable(typeof(AltBiomeTableFile))]
    [JsonSerializable(typeof(LocationPrefabsFile))]
    [JsonSerializable(typeof(LocalizationFile))]

    // The two occupant files are TRIMMED views of locationchildren.json (29.9 MB) and
    // roomchildren.json (74.5 MB) - see LocationOccupantsFile for why the full DTOs are not used
    // here. The generator builds a contract per type, so registering the trimmed ones is also what
    // keeps the full ones out of the binary until something genuinely needs them.
    [JsonSerializable(typeof(LocationOccupantsFile))]
    [JsonSerializable(typeof(RoomOccupantsFile))]
    [JsonSerializable(typeof(PrefabConstantsFile))]
    [JsonSerializable(typeof(VersionConstantsFile))]
    [JsonSerializable(typeof(SeedInputFile))]
    [JsonSerializable(typeof(NativesRandomFile))]
    [JsonSerializable(typeof(NativesPerlinIndexFile))]
    [JsonSerializable(typeof(NativesLibmFile))]
    [JsonSerializable(typeof(NativesHalfFile))]
    [JsonSerializable(typeof(NativesHashFile))]
    [JsonSerializable(typeof(WorldGenDumpFile))]
    [JsonSerializable(typeof(LocationInstancesFile))]
    [JsonSerializable(typeof(AltBiomeAssignmentFile))]
    internal sealed partial class DumpJsonContext : JsonSerializerContext
    {
    }
}
