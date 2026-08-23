using System.Collections.Generic;
using ModArchiveBrowser.Utils;
using Xunit;

namespace ModArchiveBrowser.Tests
{
    /// <summary>
    /// Le rapprochement entre un mod de XMA et ce que Penumbra possede deja.
    ///
    /// Deux erreurs symetriques guettent, et les deux ont ete constatees en jeu : proposer
    /// d'installer un mod deja present — Penumbra ne deduplique pas, il cree un dossier suffixe,
    /// et trois copies de 95 Mo se sont retrouvees sur le disque — ou l'inverse, masquer une mise
    /// a jour en la prenant pour un doublon.
    /// </summary>
    public class MatchingTests
    {
        private static InstalledMod Mod(string name, string version, string? xmaId = null)
            => new(name.ToLowerInvariant(), name, version, xmaId);

        [Fact]
        public void An_untouched_penumbra_matches_nothing()
        {
            var (state, match) = InstalledMods.Compare(new List<InstalledMod>(), "1234", "Bibo+ v3.1.5", "3.1.5");

            Assert.Equal(InstallState.Absent, state);
            Assert.Null(match);
        }

        /// <summary>
        /// L'identifiant XMA prime sur le nom : c'est la seule correspondance qui ne se trompe
        /// pas, et elle tient meme quand l'auteur renomme son mod entre deux versions.
        /// </summary>
        [Fact]
        public void The_xma_identifier_wins_over_the_name()
        {
            var installed = new List<InstalledMod>
            {
                Mod("Something else entirely", "3.1.5", "1234"),
            };

            var (state, match) = InstalledMods.Compare(installed, "1234", "Bibo+ v3.1.5", "3.1.5");

            Assert.Equal(InstallState.SameVersion, state);
            Assert.Equal("Something else entirely", match!.Value.Name);
        }

        [Fact]
        public void A_newer_version_is_an_update_and_not_a_duplicate()
        {
            var installed = new List<InstalledMod> { Mod("Bibo+", "3.1.4", "1234") };

            var (state, _) = InstalledMods.Compare(installed, "1234", "Bibo+ v3.1.5", "3.1.5");

            Assert.Equal(InstallState.DifferentVersion, state);
        }

        /// <summary>
        /// Un mod installe par un autre canal ne porte pas l'adresse de XMA dans son meta.json :
        /// Heliosphere y inscrit la sienne et prefixe le nom de "[HS]". Sans rapprochement par
        /// nom, ces mods paraitraient absents et seraient reinstalles par-dessus.
        /// </summary>
        [Theory]
        [InlineData("[HS] Bibo+ (Bibo+ Base Install)", "Bibo+ (Bibo+ Base Install) v3.1.5")]
        [InlineData("Bibo+", "Bibo+ v3.1.5")]
        [InlineData("Bibo+ (DT Update)", "Bibo+ 3.1.5")]
        public void Labels_and_version_numbers_do_not_prevent_a_match(string installedName, string candidate)
        {
            var installed = new List<InstalledMod> { Mod(installedName, "3.1.5") };

            var (state, _) = InstalledMods.Compare(installed, null, candidate, null);

            Assert.NotEqual(InstallState.Absent, state);
        }

        /// <summary>
        /// Le nettoyage des noms ne doit pas aller jusqu'a confondre deux mods distincts : un
        /// entier isole fait souvent partie du nom, il reste donc en place.
        /// </summary>
        [Fact]
        public void A_trailing_number_is_part_of_the_name()
        {
            Assert.NotEqual(InstalledMods.BaseName("Adidas Superstar 2"), InstalledMods.BaseName("Adidas Superstar"));
        }

        [Theory]
        [InlineData("Bibo+ v3.1.5", "3.1.5")]
        [InlineData("Modpack 1.0.4.pmp", "1.0.4")]
        [InlineData("No version here", null)]
        public void A_version_is_read_out_of_whatever_carries_it(string text, string? expected)
        {
            Assert.Equal(expected, InstalledMods.Normalize(text));
        }

        /// <summary>
        /// XMA ecrit "1.4" la ou Penumbra a enregistre "1.4.0" : les comparer caractere a
        /// caractere annoncerait une mise a jour a chaque verification, sur des mods a jour.
        /// </summary>
        [Theory]
        [InlineData("1.4", "1.4.0", true)]
        [InlineData("1.4.0.0", "1.4", true)]
        [InlineData("1.4", "1.4.1", false)]
        [InlineData("2.0", "1.4", false)]
        public void Trailing_zeroes_do_not_make_two_versions_different(string a, string b, bool same)
        {
            Assert.Equal(same, UpdateChecker.SameVersion(a, b));
        }
    }
}
