using System.Collections.Generic;
using ModArchiveBrowser.Utils;
using Xunit;

namespace ModArchiveBrowser.Tests
{
    /// <summary>
    /// Ce que le plugin deduit d'un lien de telechargement, et ce qu'il en fait dans la grille.
    ///
    /// Environ un mod sur quatre du catalogue n'est pas installable d'ici. La classification
    /// decide de la pastille affichee sur la carte et de ce que le filtre laisse passer : une
    /// erreur ici cache des mods parfaitement installables, ou en promet qui ne le sont pas.
    /// </summary>
    public class AvailabilityTests
    {
        private static ModAvailability Of(string downloadUrl)
        {
            var config = new Configuration();
            AvailabilityIndex.Record(config, "/modid/1", downloadUrl, save: false);
            return AvailabilityIndex.Get(config, "/modid/1");
        }

        [Theory]
        [InlineData("/private/12345/modpack.pmp", ModAvailability.Installable)]
        [InlineData("/private/12345/modpack.ttmp2", ModAvailability.Installable)]
        [InlineData("/private/12345/sources.zip", ModAvailability.Archive)]
        [InlineData("/private/12345/sources.7z", ModAvailability.Archive)]
        [InlineData("/private/12345/readme.txt", ModAvailability.Unsupported)]
        [InlineData("https://mega.nz/file/abcdef", ModAvailability.External)]
        [InlineData("https://heliosphere.app/mod/abcdef", ModAvailability.Heliosphere)]
        public void A_download_link_says_where_the_mod_lives(string url, ModAvailability expected)
        {
            Assert.Equal(expected, Of(url));
        }

        [Fact]
        public void An_unread_mod_stays_unknown_rather_than_unavailable()
        {
            Assert.Equal(ModAvailability.Unknown, AvailabilityIndex.Get(new Configuration(), "/modid/999"));
        }

        /// <summary>
        /// Le filtre ne masque que les impasses averees. Une archive a des chances d'aboutir, et
        /// Heliosphere s'installe en un clic depuis l'autre plateforme : les ecarter reviendrait a
        /// cacher des mods parfaitement accessibles.
        /// </summary>
        [Theory]
        [InlineData(ModAvailability.External, true)]
        [InlineData(ModAvailability.Unsupported, true)]
        [InlineData(ModAvailability.Installable, false)]
        [InlineData(ModAvailability.Archive, false)]
        [InlineData(ModAvailability.Heliosphere, false)]
        [InlineData(ModAvailability.Unknown, false)]
        public void Only_proven_dead_ends_are_filtered_out(ModAvailability availability, bool hidden)
        {
            Assert.Equal(hidden, AvailabilityIndex.IsDeadEnd(availability));
        }

        [Theory]
        [InlineData("/modid/62528", "62528")]
        [InlineData("https://www.xivmodarchive.com/modid/62528", "62528")]
        [InlineData("/user/12345", null)]
        [InlineData("", null)]
        public void A_mod_identifier_is_read_out_of_its_address(string url, string? expected)
        {
            Assert.Equal(expected, AvailabilityIndex.ModIdFromUrl(url));
        }

        /// <summary>
        /// Le filtre agit sur ce que le prechargement a appris. Tant qu'il n'a rien appris, il ne
        /// cache rien : masquer l'inconnu viderait la grille pour la laisser se remplir carte par
        /// carte au fil des requetes.
        /// </summary>
        [Fact]
        public void The_filter_hides_known_dead_ends_and_leaves_the_rest()
        {
            var config = new Configuration { HideUnavailable = true };
            AvailabilityIndex.Record(config, "/modid/1", "/private/1/pack.pmp", save: false);
            AvailabilityIndex.Record(config, "/modid/2", "https://mega.nz/file/x", save: false);

            var page = new List<ModThumb>
            {
                Thumb("/modid/1"),
                Thumb("/modid/2"),
                Thumb("/modid/3"),
            };

            var visible = ModGrid.Visible(config, page);

            Assert.Equal(new[] { "/modid/1", "/modid/3" }, visible.ConvertAll(m => m.url));
        }

        [Fact]
        public void With_the_filter_off_nothing_is_hidden()
        {
            var config = new Configuration { HideUnavailable = false };
            AvailabilityIndex.Record(config, "/modid/2", "https://mega.nz/file/x", save: false);

            var page = new List<ModThumb> { Thumb("/modid/1"), Thumb("/modid/2") };

            Assert.Equal(2, ModGrid.Visible(config, page).Count);
        }

        private static ModThumb Thumb(string url)
            => new("A mod", url, "An author", "/thumb.png", "/user/1", "Body", "Female", "1");
    }
}
