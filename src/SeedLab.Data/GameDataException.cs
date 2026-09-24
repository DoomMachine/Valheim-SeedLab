using System;

namespace SeedLab.Data
{
    /// <summary>
    /// Something is wrong with the shipped game data: a file is missing, its SHA-256 does not match
    /// the manifest, or an object in it lacks a field the port reads.
    ///
    /// <para>This is deliberately not recoverable by defaulting. A zero substituted for an absent
    /// <c>m_quantity</c> or <c>m_minAltitude</c> does not produce a slightly worse answer, it produces
    /// a confidently wrong one - a location that the tool places and the game does not - and nothing
    /// downstream can tell that it happened. So every absent field stops the load and names itself.</para>
    /// </summary>
    public sealed class GameDataException : Exception
    {
        public GameDataException(string message) : base(message) { }

        public GameDataException(string message, Exception inner) : base(message, inner) { }

        /// <summary>The dump file the problem is in, when it is one file's problem.</summary>
        public string? File { get; init; }

        /// <summary>JSON path of the offending value, e.g. <c>$.locations[87].quantity</c>.</summary>
        public string? Field { get; init; }
    }

    /// <summary>
    /// The data was loaded fine, but it describes a different Valheim build than the one installed.
    /// Thrown only by the answers that depend on dumped asset data (locations, vegetation, alt
    /// biomes); terrain answers depend on the seed and the generator alone and continue with a
    /// warning. See <see cref="DataPolicy"/>.
    /// </summary>
    public sealed class StaleGameDataException : Exception
    {
        public StaleGameDataException(string message, StampCheck check) : base(message)
        {
            Check = check;
        }

        public StampCheck Check { get; }
    }
}
