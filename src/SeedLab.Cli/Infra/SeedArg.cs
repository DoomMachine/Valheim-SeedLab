using System;
using System.Globalization;
using SeedLab.Seeds;

namespace SeedLab.Cli.Infra
{
    /// <summary>A seed the user named, plus how it was read and what the alternative reading was.</summary>
    public sealed class SeedRef
    {
        public SeedRef(int seed, string? text, string how, string? ambiguityNote)
        {
            Seed = seed;
            Text = text;
            How = how;
            AmbiguityNote = ambiguityNote;
        }

        public int Seed { get; }

        /// <summary>The text the user typed, when they gave a text. Null when they gave an int.</summary>
        public string? Text { get; }

        /// <summary>"int32", "seed text" - for the output, so the reading is never silent.</summary>
        public string How { get; }

        /// <summary>Set when the token could have been read the other way; printed to stderr.</summary>
        public string? AmbiguityNote { get; }
    }

    /// <summary>
    /// Turns the seed token on the command line into an int32.
    ///
    /// <para>The ambiguity is real and is resolved explicitly rather than guessed at: "123" is both a
    /// valid int32 and a perfectly typeable seed text, and the two mean different worlds. A bare token
    /// that parses as an int32 is read as the INT, because that is the form a search or a script emits;
    /// the alternative reading is always printed to stderr, and <c>--text</c> / <c>--int</c> force
    /// either one.</para>
    ///
    /// <para><c>World..ctor</c> maps the text to the int with
    /// <c>string.GetStableHashCode</c> (<c>SeedLab.Seeds.StableHash</c>), and generation never sees the
    /// text again - so the int is the world.</para>
    /// </summary>
    public static class SeedArg
    {
        public static SeedRef Resolve(Args a, string token)
        {
            bool forceText = a.Flag("text");
            bool forceInt = a.Flag("int");
            if (forceText && forceInt) throw new CliException("--text and --int contradict each other.");

            bool isInt = int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n);

            if (forceText)
            {
                return new SeedRef(StableHash.SeedFromText(token), token, "seed text (forced by --text)", null);
            }

            if (forceInt)
            {
                if (!isInt) throw new CliException($"--int was given but '{token}' is not an int32.");
                return new SeedRef(n, null, "int32 (forced by --int)", null);
            }

            if (isInt)
            {
                int asText = StableHash.SeedFromText(token);
                string? note = asText == n
                    ? null
                    : $"'{token}' read as the int32 seed {n}. As a seed TEXT it would be {asText} - pass --text to mean that.";
                return new SeedRef(n, null, "int32", note);
            }

            // A token that is written as a number but does not fit in an int32 used to become a seed
            // text in silence, while '-2147483648' - which does fit - announced which reading it got.
            // The two readings are equally consequential, so both are announced.
            if (LooksNumeric(token))
            {
                int asText = StableHash.SeedFromText(token);
                return new SeedRef(asText, token, "seed text",
                    $"'{token}' is outside the int32 range (-2147483648 .. 2147483647), so it was read as a "
                    + $"seed TEXT, giving {asText}. There is no world with that number - the game's seed "
                    + "field is text, and World..ctor hashes it to an int32.");
            }

            return new SeedRef(StableHash.SeedFromText(token), token, "seed text", null);
        }

        /// <summary>
        /// An optional sign followed by digits and nothing else: what a user means as a number, whether
        /// or not it happens to fit in an int32.
        /// </summary>
        private static bool LooksNumeric(string token)
        {
            int i = token.Length > 0 && (token[0] == '-' || token[0] == '+') ? 1 : 0;
            if (i >= token.Length) return false;
            for (; i < token.Length; i++)
            {
                if (token[i] < '0' || token[i] > '9') return false;
            }

            return true;
        }

        /// <summary>
        /// The shortest A62 text and a 10-character game-style A59 text for an int seed.
        ///
        /// <para>The 10-character text is one of an enormous number that hash to this seed, and with
        /// no <paramref name="rng"/> <c>SeedText.Describe</c> draws it from a fresh <c>Random()</c> -
        /// so the same command printed a different value every run, in a field that reads as though
        /// it were canonical. The stream is seeded from the seed instead: still an arbitrary member
        /// of the set, but the same one every time, so two runs of <c>vseed seed</c> can be diffed.
        /// <c>vseed invert --alphabet game --length 10 --count n</c> is how to see that it is a set.</para>
        /// </summary>
        public static (string Shortest, string GameStyle) Describe(int seed, Random? rng = null)
            => SeedText.Describe(seed, rng ?? new Random(seed));
    }
}
