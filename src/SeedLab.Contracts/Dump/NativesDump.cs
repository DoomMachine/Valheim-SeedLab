namespace SeedLab.Contracts.Dump
{
    /// <summary>
    /// <c>UnityEngine.Random</c> evidence: items D4-D11 of spec 03 section 6.2. These close the open
    /// variants of spec 03 section 5.2 - <c>Range(float,float)</c>'s interpolation form, whether
    /// <c>Range(a,a)</c> consumes a draw, and whether <c>insideUnitCircle</c> is two draws with
    /// <c>x = cos, y = sin</c> or something else.
    ///
    /// <c>Random.State</c>'s four words <c>s0..s3</c> are PRIVATE fields of the struct
    /// (UnityEngine.Random.State, UnityEngine.CoreModule.dll), so they are read by reflection over a
    /// boxed copy. Reading <c>Random.state</c> does not advance the generator.
    /// </summary>
    public sealed class NativesRandomFile
    {
        public string? stamp;
        public int schema;
        public string? unityVersion;

        /// <summary>The very first ground-truth item (spec 04 section 3.4): <c>Random.state</c> is a
        /// public readable/writable struct but its setter is native, so the round trip is PROVEN here,
        /// not assumed. Everything else in the dumper depends on it.</summary>
        public RandomStateRoundTripDef? stateRoundTrip;

        /// <summary>D4: <c>InitState(s)</c> then the four state words, for a list of seeds. Proves the
        /// seeding recurrence directly instead of by inference.</summary>
        public RandomInitStateDef[]? initStates;

        /// <summary>D5-D10: ordered call traces with the state after every draw.</summary>
        public RandomTraceDef[]? traces;
    }

    public sealed class RandomInitStateDef
    {
        public int seed;

        /// <summary>s0, s1, s2, s3 in declaration order.</summary>
        public int[]? state;
    }

    public sealed class RandomStateRoundTripDef
    {
        public int[]? saved;
        public int[]? afterInitState;
        public int[]? afterRestore;

        /// <summary>Draw bits from the untouched control run.</summary>
        public string[]? controlDraws;

        /// <summary>Draw bits after save -&gt; InitState -&gt; restore. Must equal <see cref="controlDraws"/>.</summary>
        public string[]? restoredDraws;

        public bool statesEqual;
        public bool drawsEqual;
    }

    /// <summary>An ordered sequence of <c>UnityEngine.Random</c> calls with the state after each.</summary>
    public sealed class RandomTraceDef
    {
        public string? id;
        public string? note;

        /// <summary>The <c>InitState</c> argument that opened the trace.</summary>
        public int initState;

        public int[]? stateAfterInit;
        public RandomDrawDef[]? draws;
        public int[]? stateAtEnd;
    }

    public sealed class RandomDrawDef
    {
        /// <summary>The call as written, e.g. "Range(-10000,10000)", "value", "insideUnitCircle".</summary>
        public string? call;

        /// <summary>"int", "float" or "vector2".</summary>
        public string? kind;

        public int resultInt;
        public float resultFloat;

        /// <summary>y component for kind "vector2"; 0 otherwise.</summary>
        public float resultFloat2;

        public int[]? stateAfter;

        /// <summary>True when the four state words are unchanged by this call - which is how the
        /// <c>Range(a,a)</c> question (spec 03 R2) is settled, for both the int and the float overload.</summary>
        public bool stateUnchanged;
    }

    /// <summary>
    /// Index for <c>natives-perlin.bin</c>. The samples themselves are raw float32 triples, never
    /// decimal text: a decimal round trip through JSON is not bit-exact in every reader, and one ulp
    /// in a Perlin argument changes a biome boundary.
    /// </summary>
    public sealed class NativesPerlinIndexFile
    {
        public string? stamp;
        public int schema;
        public string? binFile;
        public long binBytes;
        public string? binSha256;
        public PerlinBlockDef[]? blocks;
    }

    public sealed class PerlinBlockDef
    {
        /// <summary>D1, D2, D3-off0, D3-base, ... - the spec 03 item this block satisfies.</summary>
        public string? id;

        public string? note;

        /// <summary>0 = <c>Mathf.PerlinNoise(x, y)</c>, 1 = <c>Mathf.PerlinNoise1D(x)</c>.</summary>
        public int kind;

        public int sampleCount;

        /// <summary>Byte offset of this block's first sample triple in the .bin file.</summary>
        public long byteOffset;
    }

    /// <summary>
    /// D12: the libm delta. Mono's <c>Math.Sin</c> / <c>Atan2</c> / <c>Pow</c> versus .NET 10's, as
    /// input bits to output bits, at exactly the arguments world generation uses -
    /// <c>WorldGenerator.WorldAngle = Sin((float)((float)Atan2(wx,wy) * 20.0))</c>, the Mistlands
    /// <c>^1.5</c> and the Ashlands <c>^1.4</c>. <c>WorldAngle</c> reaches <c>GetBiome</c>, so this is a
    /// biome-correctness item, not a river-only one.
    /// </summary>
    public sealed class NativesLibmFile
    {
        public string? stamp;
        public int schema;
        public LibmSampleDef[]? samples;
        public WorldAngleSampleDef[]? worldAngle;
    }

    public sealed class LibmSampleDef
    {
        /// <summary>"Sin", "Cos", "Atan2", "Pow".</summary>
        public string? fn;

        public double a;

        /// <summary>Second argument for Atan2/Pow; 0 for the one-argument functions.</summary>
        public double b;

        public double result;
    }

    /// <summary><c>WorldGenerator.WorldAngle(wx, wy)</c> itself - a float function over a float grid.</summary>
    public sealed class WorldAngleSampleDef
    {
        public float wx;
        public float wy;
        public float result;
    }

    /// <summary>
    /// <c>Mathf.FloatToHalf</c> over an adversarial float set. It is
    /// <c>[FreeFunction(IsThreadSafe = true)] public static extern ushort FloatToHalf(float)</c> -
    /// native, unreadable from managed code - and it is what writes <c>cacheMinimapHeight</c>
    /// (<c>Utils.FloatsToCompressedHalfBuffer</c>). The whole height acceptance bar is defined in terms
    /// of it, so its rounding mode must be measured, not assumed.
    ///
    /// The plugin targets netstandard2.1 and has no <c>System.Half</c>, so it records Unity's answer
    /// only; the offline tool compares it against <c>BitConverter.HalfToUInt16Bits((Half)f)</c>.
    /// </summary>
    public sealed class NativesHalfFile
    {
        public string? stamp;
        public int schema;
        public HalfSampleDef[]? samples;
    }

    public sealed class HalfSampleDef
    {
        public string? note;

        /// <summary>The input. Its exact bits are in the sibling "bits" object; NaN and the infinities
        /// are written as JSON strings because JSON has no literal for them.</summary>
        public float value;

        /// <summary><c>Mathf.FloatToHalf(value)</c> as an unsigned 16-bit value widened to int.</summary>
        public int half;

        /// <summary>The same, as 4 hex digits.</summary>
        public string? halfHex;

        /// <summary><c>Mathf.HalfToFloat(half)</c> - the round trip back, for a sanity check.</summary>
        public float back;
    }

    /// <summary>
    /// <c>StringExtensionMethods.GetStableHashCode</c> over the exact strings that matter: every
    /// location and vegetation prefab name, every AltBiome name, and the seed texts. The port's hash
    /// is then proven on the inputs it will actually see rather than on a synthetic corpus.
    /// </summary>
    public sealed class NativesHashFile
    {
        public string? stamp;
        public int schema;
        public HashSampleDef[]? samples;
    }

    public sealed class HashSampleDef
    {
        public string? s;
        public int hash;

        /// <summary>"location", "vegetation", "altbiome" or "seedtext".</summary>
        public string? kind;
    }
}
