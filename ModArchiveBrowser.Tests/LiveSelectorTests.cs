using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using HtmlAgilityPack;
using ModArchiveBrowser.Utils;
using Xunit;

namespace ModArchiveBrowser.Tests
{
    /// <summary>
    /// Les memes verifications, mais contre xivmodarchive tel qu'il est aujourd'hui.
    ///
    /// Les pages enregistrees prouvent que le code sait analyser le HTML du jour de leur capture ;
    /// elles ne diront jamais que le site a change depuis. Or c'est precisement le seul evenement
    /// capable de casser ce plugin, et il arrive sans preavis : les XPath du depot d'origine ont
    /// vieilli ainsi, en silence, jusqu'a ce que tous les mods s'appellent "Untitled".
    ///
    /// Ces tests sortent donc sur le reseau, ce qui les rend lents et dependants d'un tiers. Ils
    /// portent le trait "Category=Live" et sont exclus des executions ordinaires : une
    /// verification qui echoue parce que le site est en maintenance apprend a l'ignorer.
    ///
    ///     dotnet test --filter Category=Live
    ///
    /// A lancer depuis une connexion ordinaire : xivmodarchive repond 403 aux adresses de
    /// datacenter, ce qui exclut de les automatiser sur un runner GitHub — mesure faite, les cinq
    /// echouent en 403 la ou elles passent en local. Le seul contournement serait de se faire
    /// passer pour un navigateur, ce que ce plugin s'interdit ailleurs.
    /// </summary>
    [Trait("Category", "Live")]
    public class LiveSelectorTests
    {
        private const string Root = "https://www.xivmodarchive.com";

        //Le plugin s'annonce plutot que d'emprunter l'identite d'un navigateur : le site a le
        //droit de savoir qui l'interroge, et de nous bloquer si cela lui deplait.
        private static readonly HttpClient Client = new()
        {
            DefaultRequestHeaders = { { "User-Agent", "ModArchiveBrowser/selector-check (+https://github.com/vhub59/ModArchiveBrowser)" } },
        };

        private static async Task<HtmlDocument> Fetch(string path)
        {
            var html = await Client.GetStringAsync(Root + path);
            var document = new HtmlDocument();
            document.LoadHtml(html);
            return document;
        }

        [Fact]
        public async Task The_live_homepage_still_parses()
        {
            var mods = WebClient.ParseHomePage(await Fetch("/"));

            Assert.True(mods.Count >= 10, $"Only {mods.Count} mods parsed from the live homepage.");
            Assert.All(mods, mod =>
            {
                Assert.NotEqual("Untitled", mod.name);
                Assert.NotNull(AvailabilityIndex.ModIdFromUrl(mod.url));
            });
        }

        [Fact]
        public async Task The_live_search_still_parses()
        {
            var page = await Fetch("/" + WebClient.BuildSearchURL(SortBy.Rank, SortOrder.Desc, nsfw: NSFW.False));
            var mods = WebClient.ParseSearchResults(page);

            Assert.Equal(15, mods.Count);
            Assert.All(mods, mod => Assert.NotEqual("Untitled", mod.name));
        }

        /// <summary>
        /// Le catalogue entier, et non la selection de types que XMA applique par defaut.
        ///
        /// Le seuil est volontairement bas : il ne surveille pas la croissance du site mais
        /// l'exhaustivite de la requete. Sans le parametre "types", le total retombe sous les
        /// 64 000 — c'est ce qui a revele qu'un tiers du catalogue restait invisible.
        /// </summary>
        [Fact]
        public async Task The_whole_catalogue_is_still_reachable()
        {
            var page = await Fetch("/" + WebClient.BuildSearchURL(SortBy.Rank, SortOrder.Desc, nsfw: NSFW.False));
            var (total, pages) = WebClient.ParseCounts(page);

            Assert.True(total >= 70_000, $"The catalogue came back with only {total} mods.");
            Assert.True(pages > 100, $"Page count came back as {pages}.");
        }

        [Fact]
        public async Task A_live_mod_page_still_parses()
        {
            //Un mod pris dans les resultats du jour plutot qu'un identifiant fige : un mod donne
            //peut etre retire, et le test echouerait alors pour une raison sans rapport.
            var results = WebClient.ParseSearchResults(
                await Fetch("/" + WebClient.BuildSearchURL(SortBy.Rank, SortOrder.Desc, nsfw: NSFW.False)));

            var modUrl = results.First().url;
            var page = await Fetch(modUrl);

            var thumb = WebClient.GetModThumbFromFullPage(page, modUrl);
            Assert.NotEqual("Untitled", thumb.name);
            Assert.NotEqual("Unknown", thumb.author);
            Assert.NotEqual("none", thumb.url_thumb);

            var mod = WebClient.ParseModPage(page, thumb);
            Assert.False(string.IsNullOrWhiteSpace(mod.url_download_button), "The download link selector found nothing.");
            Assert.False(string.IsNullOrWhiteSpace(mod.modMeta.downloads), "The downloads selector found nothing.");

            var facts = WebClient.ReadFacts(page);
            Assert.False(string.IsNullOrWhiteSpace(facts.DownloadUrl), "The prefetcher's download link selector found nothing.");
        }

        /// <summary>
        /// L'historique des versions ne passe pas par le HTML mais par un endpoint JSON, celui-la
        /// meme qu'interroge l'onglet History du site. Il peut disparaitre ou changer de forme
        /// independamment de la mise en page.
        /// </summary>
        [Fact]
        public async Task The_version_history_endpoint_still_answers()
        {
            var results = WebClient.ParseSearchResults(
                await Fetch("/" + WebClient.BuildSearchURL(SortBy.Rank, SortOrder.Desc, nsfw: NSFW.False)));

            //Un mod recemment mis a jour a forcement un historique ; le premier venu peut n'en
            //avoir aucun sans que rien ne soit casse. On accepte donc qu'un seul reponde.
            var answered = results.Take(5)
                .Select(mod => AvailabilityIndex.ModIdFromUrl(mod.url))
                .Where(id => id != null)
                .Any(id => WebClient.GetVersionHistory(id!).Count > 0);

            Assert.True(answered, "None of the first five mods returned any version history.");
        }
    }
}
