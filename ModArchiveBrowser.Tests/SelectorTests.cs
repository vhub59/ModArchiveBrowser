using System.Linq;
using ModArchiveBrowser.Utils;
using Xunit;

namespace ModArchiveBrowser.Tests
{
    /// <summary>
    /// Verifie que chaque selecteur XPath trouve encore ce qu'il vise.
    ///
    /// C'est la partie la plus fragile du plugin, et la seule qui casse sans bruit. Le code
    /// n'echoue pas quand un selecteur ne trouve rien : il retombe sur une valeur de repli —
    /// "Untitled", "Unknown", "none", la chaine vide — journalise un avertissement que personne ne
    /// lit, et continue. Une refonte de xivmodarchive donne donc une grille de cartes intactes,
    /// toutes intitulees "Untitled".
    ///
    /// Ces tests s'ecrivent donc contre les valeurs de repli : la reussite, c'est de ne pas les
    /// obtenir. Une assertion sur un titre precis serait plus stricte, mais casserait des qu'un
    /// auteur renomme son mod — elle signalerait alors du bruit et non une regression.
    /// </summary>
    public class SelectorTests
    {
        [Fact]
        public void Homepage_yields_a_full_grid_of_mods()
        {
            var mods = WebClient.ParseHomePage(Fixtures.Homepage());

            //XMA sert une vitrine bien remplie ; en tirer une poignee signalerait un selecteur
            //devenu trop etroit plutot qu'une page vide.
            Assert.True(mods.Count >= 10, $"Only {mods.Count} mods parsed from the homepage.");

            //Sans exiger l'unicite : l'accueil range les memes mods dans plusieurs vitrines —
            //recents, plus vus, sponsorises — et un mod y figure legitimement deux fois. C'est la
            //raison du Distinct applique par la fenetre d'accueil apres l'analyse.
            AssertWellFormed(mods, expectDistinct: false);
        }

        [Fact]
        public void Search_results_yield_a_full_page_of_mods()
        {
            var mods = WebClient.ParseSearchResults(Fixtures.SearchResults());

            //Quinze par page, sans moyen d'en obtenir davantage : take, skip, per_page et limit
            //ont tous ete essayes contre le site, aucun n'a d'effet.
            Assert.Equal(15, mods.Count);
            AssertWellFormed(mods);
        }

        [Fact]
        public void Search_header_still_carries_the_totals()
        {
            var (total, pages) = WebClient.ParseCounts(Fixtures.SearchResults());

            //Zero est la valeur de repli quand le motif ne trouve rien dans l'entete.
            Assert.True(total > 1000, $"Result count came back as {total}.");
            Assert.True(pages > 100, $"Page count came back as {pages}.");
        }

        [Fact]
        public void Mod_page_yields_its_summary()
        {
            var thumb = WebClient.GetModThumbFromFullPage(Fixtures.ModPage(), $"/modid/{Fixtures.ModPageId}");

            Assert.NotEqual("Untitled", thumb.name);
            Assert.NotEqual("none", thumb.url_thumb);
            Assert.NotEqual("Unknown", thumb.author);
            Assert.False(string.IsNullOrWhiteSpace(thumb.type), "The type selector found nothing.");
            Assert.False(string.IsNullOrWhiteSpace(thumb.views), "The views selector found nothing.");
        }

        [Fact]
        public void Mod_page_yields_its_details()
        {
            var page = Fixtures.ModPage();
            var thumb = WebClient.GetModThumbFromFullPage(page, $"/modid/{Fixtures.ModPageId}");
            var mod = WebClient.ParseModPage(page, thumb);

            Assert.False(string.IsNullOrWhiteSpace(mod.url_download_button), "The download link selector found nothing.");
            Assert.False(string.IsNullOrWhiteSpace(mod.url_author_profilepic), "The author avatar selector found nothing.");
            Assert.False(string.IsNullOrWhiteSpace(mod.modMeta.downloads), "The downloads selector found nothing.");
            Assert.False(string.IsNullOrWhiteSpace(mod.modMeta.last_update), "The update date selector found nothing.");
            Assert.NotEmpty(mod.modMeta.races);
        }

        /// <summary>
        /// Le couple de selecteurs le plus sollicite : le prechargement l'applique a chaque carte.
        ///
        /// S'il lache, tout le catalogue passe en "installabilite inconnue" et plus aucun mod
        /// n'est signale comme adulte — deux fonctions qui disparaissent sans message d'erreur.
        /// </summary>
        [Fact]
        public void Mod_page_yields_the_facts_the_prefetcher_needs()
        {
            var facts = WebClient.ReadFacts(Fixtures.ModPage());

            Assert.False(string.IsNullOrWhiteSpace(facts.DownloadUrl), "The download link selector found nothing.");
            Assert.NotEqual(ModAvailability.Unknown, ClassifyOf(facts.DownloadUrl));
        }

        /// <summary>Traverse l'index pour atteindre la classification, qui est privee.</summary>
        private static ModAvailability ClassifyOf(string downloadUrl)
        {
            var config = new Configuration();
            AvailabilityIndex.Record(config, "/modid/1", downloadUrl, save: false);
            return AvailabilityIndex.Get(config, "/modid/1");
        }

        private static void AssertWellFormed(System.Collections.Generic.List<ModThumb> mods, bool expectDistinct = true)
        {
            Assert.All(mods, mod =>
            {
                Assert.NotEqual("Untitled", mod.name);
                Assert.False(string.IsNullOrWhiteSpace(mod.name));

                //L'URL porte l'identifiant du mod : sans elle, rien n'est cliquable ni indexable.
                Assert.NotNull(AvailabilityIndex.ModIdFromUrl(mod.url));

                Assert.NotEqual("none", mod.url_thumb);
                Assert.NotEqual("Unknown", mod.author);
            });

            //Les champs sont lus par collections paralleles, puis reassembles par position : si
            //l'une d'elles rate un element, les mods suivants heritent des donnees de leur voisin.
            //Des URL en double sont le symptome le plus visible de ce desalignement — la ou les
            //doublons ne sont pas legitimes.
            if (expectDistinct)
                Assert.Equal(mods.Count, mods.Select(m => m.url).Distinct().Count());
        }
    }
}
