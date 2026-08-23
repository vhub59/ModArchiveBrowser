using System;
using System.Collections.Generic;
using System.Linq;
using ModArchiveBrowser.Utils;
using Xunit;

namespace ModArchiveBrowser.Tests
{
    /// <summary>
    /// L'URL de recherche, ou se logent les erreurs les plus couteuses.
    ///
    /// Toutes celles corrigees jusqu'ici etaient invisibles : la recherche repondait, la grille
    /// se remplissait, et rien n'indiquait qu'un tiers du catalogue manquait ou qu'un tri etait
    /// ignore. Chaque test ci-dessous fige une decouverte payee par une mesure contre le site.
    /// </summary>
    public class SearchUrlTests
    {
        private static Dictionary<string, string> Query(string url)
        {
            var start = url.IndexOf('?') + 1;
            return url[start..]
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(parts => parts[0], parts => parts.Length > 1 ? parts[1] : string.Empty);
        }

        /// <summary>
        /// Sans "types", XMA n'applique pas "tous les types" mais sa propre liste, qui laisse de
        /// cote les poses (30 381 mods), la categorie Other, les presets ReShade et les plugins
        /// Dalamud. Le plugin en annoncait 63 900 la ou le site en comptait 96 206.
        /// </summary>
        [Fact]
        public void No_type_checked_asks_for_every_type()
        {
            var query = Query(WebClient.BuildSearchURL(SortBy.Rank, SortOrder.Desc, nsfw: NSFW.Both));

            var requested = query["types"].Split("%2C").Select(int.Parse).ToHashSet();
            var everything = Enum.GetValues<Types>().Select(t => (int)t).ToHashSet();

            Assert.Equal(everything, requested);
        }

        [Fact]
        public void A_chosen_type_is_the_only_one_asked_for()
        {
            var query = Query(WebClient.BuildSearchURL(SortBy.Rank, SortOrder.Desc, nsfw: NSFW.Both,
                types: new HashSet<Types> { Types.Body }));

            Assert.Equal(((int)Types.Body).ToString(), query["types"]);
        }

        /// <summary>
        /// Mesure : 52 114 mods sans adultes, 8 413 adultes seuls, 60 527 sans le parametre. Le
        /// filtre n'est donc pas exclusif comme je l'avais d'abord ecrit — l'omettre reunit les
        /// deux ensembles, ce qui est la seule facon d'obtenir un catalogue melange.
        /// </summary>
        [Fact]
        public void Both_omits_the_nsfw_parameter_entirely()
        {
            Assert.False(Query(WebClient.BuildSearchURL(SortBy.Rank, SortOrder.Desc, nsfw: NSFW.Both)).ContainsKey("nsfw"));
            Assert.Equal("true", Query(WebClient.BuildSearchURL(SortBy.Rank, SortOrder.Desc, nsfw: NSFW.True))["nsfw"]);
            Assert.Equal("false", Query(WebClient.BuildSearchURL(SortBy.Rank, SortOrder.Desc, nsfw: NSFW.False))["nsfw"]);
        }

        /// <summary>
        /// dt_compat est un seuil cumulatif et non un choix : 1 ne renvoie que les mods
        /// pleinement compatibles Dawntrail, 3 comprend tout ce qui l'est au moins partiellement.
        /// Le preset livre demandait 1 et amputait donc les listes sans le dire.
        /// </summary>
        [Fact]
        public void Compatibility_travels_as_its_numeric_threshold()
        {
            var query = Query(WebClient.BuildSearchURL(SortBy.Rank, SortOrder.Desc, nsfw: NSFW.Both,
                dtCompatibility: DTCompatibility.PartiallyCompatible));

            Assert.Equal(((int)DTCompatibility.PartiallyCompatible).ToString(), query["dt_compat"]);
        }

        /// <summary>
        /// Le site refuse "name" comme critere de tri et retombe silencieusement sur son defaut ;
        /// il attend "name_slug". L'enumeration porte donc ce nom, et le tri doit le transmettre
        /// tel quel.
        /// </summary>
        [Fact]
        public void Sorting_by_name_uses_the_slug_the_site_expects()
        {
            Assert.Equal("name_slug", Query(WebClient.BuildSearchURL(SortBy.Name_slug, SortOrder.Asc, nsfw: NSFW.Both))["sortby"]);
        }

        [Fact]
        public void Empty_criteria_are_left_out_rather_than_sent_blank()
        {
            var query = Query(WebClient.BuildSearchURL(SortBy.Rank, SortOrder.Desc, nsfw: NSFW.Both,
                author: string.Empty, tags: "   x   "));

            Assert.False(query.ContainsKey("author"));

            //Non renseigne, donc absent : envoyer name= vide reduirait la recherche a rien.
            Assert.False(query.ContainsKey("name"));
            Assert.True(query.ContainsKey("tags"));
        }

        [Fact]
        public void Sponsored_is_only_asked_for_when_wanted()
        {
            Assert.False(Query(WebClient.BuildSearchURL(SortBy.Rank, SortOrder.Desc, nsfw: NSFW.Both)).ContainsKey("sponsored"));
            Assert.Equal("true", Query(WebClient.BuildSearchURL(SortBy.Rank, SortOrder.Desc, nsfw: NSFW.Both, sponsoredOnly: true))["sponsored"]);
        }

        [Fact]
        public void Page_number_is_carried_through()
        {
            Assert.Equal("7", Query(WebClient.BuildSearchURL(SortBy.Rank, SortOrder.Desc, nsfw: NSFW.Both, page: 7))["page"]);
        }
    }
}
