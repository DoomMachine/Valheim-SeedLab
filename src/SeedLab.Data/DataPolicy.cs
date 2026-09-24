using System;

namespace SeedLab.Data
{
    /// <summary>
    /// What the tool is allowed to answer when the shipped data does not describe the installed game.
    ///
    /// <para>The two halves of SeedLab do not have the same exposure, so they do not get the same
    /// rule:</para>
    ///
    /// <list type="bullet">
    /// <item><b>Terrain - warn and continue.</b> Biome and height come from <c>WorldGenerator</c>,
    /// which is ported code driven by the seed and <c>worldGenVersion</c> alone. No dumped asset
    /// takes part. A game update can of course change <c>WorldGenerator</c> itself, but the dumped
    /// data is not what would be wrong, and the acceptance suite - 0 biome mismatches over 5.1 M
    /// minimap pixels, 4,194,304/4,194,304 height codes, 24,601 float-exact location heights - is
    /// what says whether the port still matches. So the answer is given, with the mismatch stated
    /// once.</item>
    ///
    /// <item><b>Locations, vegetation, alt biomes - refuse.</b> These are read straight out of
    /// <c>locations.json</c> and friends: quantities, radii, altitude bounds, biome masks, the order
    /// of the table, the prefab names that seed each RNG stream. If the patch that changed the
    /// assembly also added a location, raised a quantity or reordered a LocationList, every placement
    /// downstream is wrong - and wrong in a way that still looks like a coordinate, so nobody
    /// notices. There is no partial answer worth giving, so there is none.</item>
    /// </list>
    ///
    /// <para><see cref="StampMatch.GameNotFound"/> is not a mismatch: nothing contradicts the data.
    /// It warns, and it refuses too if <c>SEEDLAB_REQUIRE_GAME_MATCH</c> is set - see
    /// <see cref="GameInstall.RequireMatchEnvironmentVariable"/>.</para>
    /// </summary>
    public static class DataPolicy
    {
        /// <summary>
        /// Gate for any answer that reads the dumped asset tables. Throws
        /// <see cref="StaleGameDataException"/> when the stamp does not match the installed game.
        /// </summary>
        public static void RequireMatchForAssetData(StampCheck check, string whatWasAsked)
        {
            if (check == null) throw new ArgumentNullException(nameof(check));

            if (check.Result == StampMatch.Match) return;

            if (check.Result != StampMatch.Mismatch && !RequireMatchRequested()) return;

            throw new StaleGameDataException(BuildRefusal(check, whatWasAsked), check);
        }

        /// <summary>
        /// The one line to print before a terrain answer, or null when the stamp matches. Terrain
        /// does not depend on the dumped data, so this never blocks.
        /// </summary>
        public static string? WarningForTerrain(StampCheck check)
        {
            if (check == null) throw new ArgumentNullException(nameof(check));
            if (check.Result == StampMatch.Match) return null;

            string head = check.Result == StampMatch.Mismatch
                ? "the installed Valheim is not the build this tool's game data was dumped from"
                : "this tool's game data could not be checked against an installed Valheim";

            return "warning: " + head + ". Terrain and biomes are generated from the seed alone and are "
                   + "unaffected; anything that names a location, a dungeon or a resource is refused "
                   + "until the data is re-dumped. " + Remedy(check);
        }

        /// <summary>
        /// The same line for the data this process would actually use, or null when there is nothing
        /// to say.
        ///
        /// <para>It warns on exactly the states <see cref="RequireMatchForAssetData"/> refuses on,
        /// because that is what the sentence it prints claims: with no install found and
        /// <c>SEEDLAB_REQUIRE_GAME_MATCH</c> unset, locations are still ANSWERED, so telling the user
        /// they are refused - and to re-dump - would be false.</para>
        ///
        /// <para>It never throws. Data that cannot be opened at all is not a terrain problem: terrain
        /// reads no dumped asset, so the caller prints nothing and answers as before.</para>
        /// </summary>
        public static string? WarningForTerrain()
        {
            try
            {
                StampCheck check = GameData.Load().InstalledGameCheck;
                if (check.Result != StampMatch.Mismatch && !RequireMatchRequested()) return null;

                return WarningForTerrain(check);
            }
            catch (GameDataException)
            {
                return null;
            }
            catch (System.IO.IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }

        /// <summary>True when the user asked for an unverifiable install to count as a failure.</summary>
        public static bool RequireMatchRequested()
        {
            string? v = Environment.GetEnvironmentVariable(GameInstall.RequireMatchEnvironmentVariable);
            if (string.IsNullOrEmpty(v)) return false;

            return v == "1"
                   || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildRefusal(StampCheck check, string whatWasAsked)
        {
            string why = check.Result switch
            {
                StampMatch.Mismatch =>
                    "the game data shipped with this tool was dumped from Valheim " + check.Stamp.GameVersion
                    + " (assembly_valheim " + check.Stamp.AssemblyValheimSha256.Substring(0, 16) + "..., dumped "
                    + check.Stamp.Dumped + "), and the installed game is a different build ("
                    + (check.InstalledSha256 ?? "unknown") + ").",
                StampMatch.GameNotFound =>
                    "no Valheim install was found to check the game data against, and "
                    + GameInstall.RequireMatchEnvironmentVariable + " demands a positive match.",
                _ =>
                    "the installed assembly_valheim.dll could not be read, and "
                    + GameInstall.RequireMatchEnvironmentVariable + " demands a positive match.",
            };

            return whatWasAsked + " needs the dumped game data (the location table, vegetation and alt "
                   + "biomes), and " + why + Environment.NewLine
                   + "  A location table from another build gives coordinates that look right and are "
                   + "not, so this is refused rather than guessed." + Environment.NewLine
                   + "  " + Remedy(check) + Environment.NewLine
                   + "  Terrain answers (biome, height, map) do not use this data and still work: try "
                   + "'vseed at', 'vseed map' or 'vseed seed'.";
        }

        private static string Remedy(StampCheck check)
        {
            string dir = check.GameDirectory ?? "your Valheim install";
            return "To fix: run the SeedLab dumper plugin in the game - build "
                   + "tools\\SeedLab.Dumper, drop it in " + dir + "\\BepInEx\\plugins, start Valheim, "
                   + "load a world and run all three dump modes - then copy the new "
                   + "<version>-<hash> folder from %APPDATA%\\..\\valheim-dumper into SeedLab's data\\ "
                   + "folder. 'vseed data' will then report a match.";
        }
    }
}
