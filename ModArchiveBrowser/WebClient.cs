using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ModArchiveBrowser.Utils;
using HtmlAgilityPack;
using System.Net;
using System.IO;

namespace ModArchiveBrowser
{
    
    internal class WebClient
    {
        public const string xivmodarchiveRoot = "https://www.xivmodarchive.com";
        public const string new_and_updated_from_patreon_subs = "search?nsfl=false&sponsored=true&dt_compat=1&sortby=time_edited&sortorder=desc";
        public const string today_most_viewed = "search?nsfl=false&dt_compat=1&sortby=views_today&sortorder=desc";
        public const string newest_mods_from_all_users = "search?nsfl=false&dt_compat=1&sortby=time_published&sortorder=desc";
        public static readonly string HtmlCachePath =
            Path.Combine(System.IO.Path.GetTempPath(), "modarchivebrowser\\htmlcache");

        private static HtmlWeb clientInstance = null;
        public static HtmlWeb ClientInstance
        {
            get
            {
                if (clientInstance == null)
                {
                    clientInstance = new HtmlWeb();
                    clientInstance.CachePath = HtmlCachePath;
                    clientInstance.UsingCache = true;
                    clientInstance.UserAgent = XmaSession.UserAgent;
                    //Sans le cookie de session, toute page NSFW répond 403.
                    clientInstance.PreRequest = request =>
                    {
                        request.CookieContainer = XmaSession.CookieJar;
                        return true;
                    };
                    return clientInstance;
                }
                else
                {
                    return clientInstance;
                }
            }
        }

        /// <summary>
        /// Vide le cache HTML sur disque.
        ///
        /// Indispensable quand l'utilisateur retire son accord pour le contenu adulte : les pages
        /// NSFW deja consultees y sont stockees en entier, lien de telechargement compris, et
        /// seraient resservies sans jamais interroger XMA. Fermer la session ne suffit donc pas,
        /// le 403 serait purement et simplement contourne.
        ///
        /// On purge tout : rien dans le cache n'indique si une page etait NSFW.
        /// </summary>
        public static void ClearHtmlCache()
        {
            try
            {
                if (!Directory.Exists(HtmlCachePath))
                    return;

                var removed = 0;
                foreach (var file in Directory.GetFiles(HtmlCachePath))
                {
                    try
                    {
                        File.Delete(file);
                        removed++;
                    }
                    catch (IOException)
                    {
                        //Fichier verrouille : on continue,le reste doit partir quand meme.
                    }
                }

                Plugin.Logger.Information($"HTML cache cleared: {removed} file(s) removed.");
            }
            catch (Exception e)
            {
                Plugin.Logger.Warning($"Could not clear HTML cache: {e.Message}");
            }
        }

        //XMA remanie sa mise en page sans prévenir. Plutôt que de planter sur un index hors
        //bornes, on retombe sur une valeur de repli et on trace le sélecteur fautif dans le log.
        private static string FirstText(HtmlNodeCollection? nodes, string fallback, string field)
        {
            if (nodes == null || nodes.Count == 0)
            {
                Plugin.Logger.Warning($"Broken selector for field '{field}': XMA likely changed its HTML.");
                return fallback;
            }

            return HtmlEntity.DeEntitize(nodes[0].InnerText).Trim();
        }

        private static string FirstAttr(HtmlNodeCollection? nodes, string attribute, string fallback, string field)
        {
            if (nodes == null || nodes.Count == 0)
            {
                Plugin.Logger.Warning($"Broken selector for field '{field}': XMA likely changed its HTML.");
                return fallback;
            }

            return nodes[0].GetAttributeValue(attribute, fallback);
        }

        /// <summary>
        /// Version publiee d'un mod, ou une chaine vide si la page ne l'annonce pas.
        ///
        /// Recuperation volontairement legere : le detecteur de mises a jour interroge une page
        /// par mod installe, il n'a pas besoin du reste de la fiche.
        /// </summary>
        public static string GetModVersion(string modId)
        {
            try
            {
                var page = ClientInstance.Load($"{xivmodarchiveRoot}/modid/{modId}");
                var node = page.DocumentNode.SelectSingleNode("//code[contains(@class, 'text-light') and contains(text(), 'Version')]");
                if (node == null)
                    return string.Empty;

                //Le noeud contient "Version: 1.4" ; on ne garde que le numero.
                var match = System.Text.RegularExpressions.Regex.Match(node.InnerText, @"([\d]+(?:\.[\d]+)*)");
                return match.Success ? match.Groups[1].Value : string.Empty;
            }
            catch (Exception e)
            {
                Plugin.Logger.Debug($"Could not read the version of mod {modId}: {e.Message}");
                return string.Empty;
            }
        }

        public static List<ModThumb> GetHomePageMods()
        {
            HtmlDocument homepage = ClientInstance.Load(xivmodarchiveRoot);
            Plugin.Logger.Debug("Request made");
            return ParseHomePage(homepage);
        }
        //param url should be in the format of xivmodarchive aka /modid/XXXX and not absolutes
        public static (Mod,HtmlNodeCollection) GetModPage(ModThumb modThumb)
        {
            string url = xivmodarchiveRoot + modThumb.url;
            Plugin.Logger.Debug($"{url}");
            HtmlDocument page = ClientInstance.Load(url);
            HtmlNodeCollection descriptionNodeStart = page.DocumentNode.SelectNodes("//div[@id='info']");
            Plugin.Logger.Debug("Request made");
            return (ParseModPage(page,modThumb),descriptionNodeStart);
        }

        public static (Mod,HtmlNodeCollection) GetModPage(string modId)
        {
            //ModThumb.url attend un chemin relatif ("/modid/62528"), pas l'identifiant nu :
            //c'est lui que "Open in browser" concatène à la racine du site. En y mettant "62528",
            //la commande /modid produisait l'URL "https://www.xivmodarchive.com62528".
            //Le defaut ne se voyait que par cette commande, jamais en passant par la recherche.
            string relativePath = "/modid/" + modId;
            string url = xivmodarchiveRoot + relativePath;
            Plugin.Logger.Debug($"{url}");
            HtmlDocument page = ClientInstance.Load(url);
            HtmlNodeCollection descriptionNodeStart = page.DocumentNode.SelectNodes("//div[@id='info']");
            Plugin.Logger.Debug("Request made");
            ModThumb mdThumb = GetModThumbFromFullPage(page,relativePath);
            return(ParseModPage(page,mdThumb),descriptionNodeStart);
        }

        /// <summary>
        /// Resultats d'une recherche : la page demandee, et de quoi situer cette page dans
        /// l'ensemble.
        ///
        /// Sans le total, l'interface annoncait "15 mods" — le contenu d'une page — pour une
        /// recherche en comptant 1854, ce qui donnait l'impression que le filtre avait tout
        /// balaye ou que la recherche etait cassee.
        /// </summary>
        public readonly record struct SearchResults(List<ModThumb> Mods, int TotalCount, int PageCount);

        public static SearchResults DoSearch(string searchUrl)
        {
            string url = xivmodarchiveRoot + '/' + searchUrl;
            HtmlDocument page = ClientInstance.Load(url);
            Plugin.Logger.Debug("Request made");

            return new SearchResults(
                ParseSearchResults(page),
                ParseCount(page, @"([\d,]+)\s*Results"),
                ParseCount(page, @"over\s+([\d,]+)\s*\n?\s*Pages"));
        }

        /// <summary>Extrait un nombre de l'entete de resultats, ou zero s'il est introuvable.</summary>
        private static int ParseCount(HtmlDocument page, string pattern)
        {
            try
            {
                var match = System.Text.RegularExpressions.Regex.Match(page.DocumentNode.InnerHtml, pattern);
                if (match.Success && int.TryParse(match.Groups[1].Value.Replace(",", string.Empty), out var value))
                    return value;
            }
            catch (Exception e)
            {
                Plugin.Logger.Debug($"Could not read the result count: {e.Message}");
            }

            return 0;
        }

        public static ModThumb GetModThumbFromFullPage(HtmlDocument page,string url)
        {
            string title;
            string thumbUrl;
            string authorName;
            string type;
            string gender;
            string views;
            HtmlNodeCollection titleNode = page.DocumentNode.SelectNodes("//h1[contains(@class, 'display-5')]");
            HtmlNodeCollection imageNode = page.DocumentNode.SelectNodes("//img[contains(@class, 'mod-carousel-image')]/@src");
            HtmlNodeCollection authorNode = page.DocumentNode.SelectNodes("//a[contains(@class, 'user-card-link')]");
            HtmlNodeCollection typeNodes = page.DocumentNode.SelectNodes("//div[contains(@class, 'col-8')]//p[contains(@class, 'lead')]");
            //Ces deux-là étaient des XPath absolus positionnels, cassés par une refonte du site.
            //On reprend le motif par classe utilisé pour les races et les tags, qui lui a tenu.
            HtmlNodeCollection genderNodes = page.DocumentNode.SelectNodes("//div[contains(@class, 'mod-meta-block')]//code[contains(@class, 'text-light')]//a[contains(@href, '/search?genders=')]");
            HtmlNodeCollection viewsNodes = page.DocumentNode.SelectNodes("//span[contains(@class, 'emoji-block') and contains(@title, 'Views')]//span[contains(@class, 'count')]");
            title = FirstText(titleNode, "Untitled", "title");
            thumbUrl = FirstAttr(imageNode, "src", "none", "thumbnail");
            authorName = FirstText(authorNode, "Unknown", "author");
            type = FirstText(typeNodes, "", "type");
            gender = FirstText(genderNodes, "", "gender");
            views = FirstText(viewsNodes, "0", "views");
            return new ModThumb(title, url, authorName,thumbUrl,"none",type,gender,views);


        }

        public static Mod ParseModPage(HtmlDocument page,ModThumb thumb)//I know,I know,this is ugly
        {
            string profile_pic;
            string download_url;
            string affectReplace;
            string[] races;
            string[] tags;
            string views;
            string downloads;
            string pins;
            string lastVersionUpdate;
            string originalReleaseDate;
            HtmlNodeCollection authorProfilePictureNodes = page.DocumentNode.SelectNodes("//div[contains(@class, 'user-card')]//img[contains(@class, 'rounded-circle')]/@src");
            HtmlNodeCollection downloadModButtonNodes = page.DocumentNode.SelectNodes("//a[@id='mod-download-link']/@href");
            HtmlNodeCollection affectsReplacesNodes = page.DocumentNode.SelectNodes("//div[contains(@class, 'mod-meta-block')][contains(text(),'Affects')]//code[contains(text(), '')]");
            HtmlNodeCollection racesNodes = page.DocumentNode.SelectNodes("//div[contains(@class, 'mod-meta-block')]//code[contains(@class, 'text-light')]//a[contains(@href, '/search?races=')]");
            HtmlNodeCollection tagsNodes = page.DocumentNode.SelectNodes("//div[contains(@class, 'mod-meta-block')]//code[contains(@class, 'text-light')]//a[contains(@href, '/search?tags=')]");
            HtmlNodeCollection viewsNodes = page.DocumentNode.SelectNodes("//span[contains(@class, 'emoji-block') and contains(@title, 'Views')]//span[contains(@class, 'count')]");
            HtmlNodeCollection downloadsNodes = page.DocumentNode.SelectNodes("//span[contains(@class, 'emoji-block') and contains(@title, 'Downloads')]//span[contains(@class, 'count')]");
            HtmlNodeCollection pinsNodes = page.DocumentNode.SelectNodes("//span[contains(@class, 'emoji-block') and contains(@title, 'Followers')]//span[contains(@class, 'count')]");
            HtmlNodeCollection lastVersionUpdateNodes = page.DocumentNode.SelectNodes("//div[contains(@class, 'mod-meta-block')]//code[contains(@class, 'server-date')][1]");
            HtmlNode dtCompatible = page.DocumentNode.SelectSingleNode(".//div[contains(@class, 'alert-success')]");
            DTCompatibility dTCompatibility = DTCompatibility.FullyCompatible;
            if(dtCompatible is null)
            {
                HtmlNode dtTexTools = page.DocumentNode.SelectSingleNode(".//div[contains(@class, 'alert-info')]");
                dTCompatibility = DTCompatibility.TexToolsCompatible;
                if(dtTexTools is null)
                {
                    HtmlNode dtPartial = page.DocumentNode.SelectSingleNode(".//div[contains(@class, 'alert-warning')]");
                    dTCompatibility = DTCompatibility.PartiallyCompatible;
                    if(dtPartial is null)
                    {
                        HtmlNode dtFucked = page.DocumentNode.SelectSingleNode(".//div[contains(@class, 'alert-danger')]");
                        dTCompatibility = DTCompatibility.NotCompatible;
                    }
                    else
                    {
                        dTCompatibility = DTCompatibility.PartiallyCompatible;//should never happen but you never know
                    }
                }
            }
            profile_pic = authorProfilePictureNodes[0].GetAttributeValue("src", "none");
            download_url = downloadModButtonNodes[0].GetAttributeValue("href", "none");
            if (affectsReplacesNodes != null)
            {
                affectReplace = affectsReplacesNodes[0].InnerText;
            }
            else
            {
                affectReplace = "N/A";
            }
            races = new string[racesNodes.Count];
            for (int i = 0; i < racesNodes.Count; i++)
            {
                races[i] =  racesNodes[i].InnerText;
            }
            tags = new string[tagsNodes.Count];
            for (int i = 0;i < tagsNodes.Count; i++)
            {
                tags[i] = tagsNodes[i].InnerText;
            }
            views = viewsNodes[0].InnerText;
            downloads = downloadsNodes[0].InnerText;
            pins = pinsNodes[0].InnerText;
            lastVersionUpdate = lastVersionUpdateNodes[0].InnerText;
            originalReleaseDate = lastVersionUpdateNodes[1].InnerText;
            //lastVersionUpdate = "N/A";
            //originalReleaseDate = "N/A";
            string description = "I will implement description parsing/rendering,later";
            ModMetadata modMetadata = new ModMetadata(views,downloads,pins,lastVersionUpdate,originalReleaseDate, 
                races,tags,description,affectReplace,dTCompatibility);
            return (new Mod(thumb, download_url,profile_pic, modMetadata));


        }

        public static List<ModThumb> ParseHomePage(HtmlDocument homepage)
        {
            List<ModThumb> modthumbnails = new List<ModThumb>();
            HtmlNodeCollection titleNodes = homepage.DocumentNode.SelectNodes("//div[contains(@class, 'card-body')]//h5[contains(@class, 'card-title')]");
            HtmlNodeCollection urlNodes = homepage.DocumentNode.SelectNodes("//a[contains(@href, '/modid/')]//@href");
            HtmlNodeCollection thumbUrlNodes = homepage.DocumentNode.SelectNodes("//div[contains(@class, 'mod-card-img-container')]//img[contains(@class, 'card-img-top mod-card-img')]/@src");
            HtmlNodeCollection authorNameNodes = homepage.DocumentNode.SelectNodes("//div[contains(@class, 'card-body')]//p[contains(@class, 'card-text')]//a");
            HtmlNodeCollection authorUrlNodes = homepage.DocumentNode.SelectNodes("//div[contains(@class, 'card-body')]//p[contains(@class, 'card-text')]//a/@href");
            HtmlNodeCollection typeNodes = homepage.DocumentNode.SelectNodes("//div[contains(@class, 'card-body')]//p[contains(@class, 'card-text')]//code[contains(text(), 'Type')]");
            HtmlNodeCollection gendersNodes = homepage.DocumentNode.SelectNodes("//div[contains(@class, 'card-body')]//p[contains(@class, 'card-text')]//code[contains(text(), 'Genders')]");
            HtmlNodeCollection viewsNodes = homepage.DocumentNode.SelectNodes("//div[contains(@class, 'card-body')]//p[contains(@class, 'card-text')]//em[contains(text(), 'Views')]");

            int size = titleNodes.Count;

            for (int i = 0; i < size; i++)
            {
                string title = WebUtility.HtmlDecode(titleNodes[i].InnerText);
                string modUrl = urlNodes[i].GetAttributeValue("href", "none");
                string thumbUrl = thumbUrlNodes[i].GetAttributeValue("src", "none");
                string authorName = WebUtility.HtmlDecode(authorNameNodes[i].InnerText);
                string authorUrl =  authorUrlNodes[i].GetAttributeValue("href", "none");
                string type = typeNodes[i].InnerText;
                string gender = gendersNodes[i].InnerText;
                string views = viewsNodes[i].InnerText;
                modthumbnails.Add(new ModThumb(title, modUrl, authorName, thumbUrl, authorUrl,type,gender,views));
            }

            return modthumbnails;
        }

        public static List<ModThumb> ParseSearchResults(HtmlDocument searchpage)
        {
            List<ModThumb> modthumbnails = new List<ModThumb>();
            HtmlNodeCollection titleNodes = searchpage.DocumentNode.SelectNodes("//div[contains(@class, 'mod-card')]//h5[contains(@class, 'card-title')]");
            HtmlNodeCollection authorNameNodes = searchpage.DocumentNode.SelectNodes("//div[contains(@class, 'mod-card')]//p[contains(@class, 'card-text')]//a[contains(@href, '/user/')]");
            HtmlNodeCollection urlNodes = searchpage.DocumentNode.SelectNodes("//a[contains(@href, '/modid/')]/@href");
            HtmlNodeCollection thumbUrlNodes = searchpage.DocumentNode.SelectNodes("//div[contains(@class, 'mod-card-img-container')]//img[contains(@class, 'mod-card-img')]/@src");
            HtmlNodeCollection typeNodes = searchpage.DocumentNode.SelectNodes("//div[contains(@class, 'mod-card')]//code[contains(text(), 'Type')]");
            HtmlNodeCollection gendersNodes = searchpage.DocumentNode.SelectNodes("//div[contains(@class, 'mod-card')]//code[contains(text(), 'Genders')]");
            HtmlNodeCollection viewsNodes = searchpage.DocumentNode.SelectNodes("//div[contains(@class, 'mod-card')]//span[contains(@title, 'Lifetime Views')]");
            /*HtmlNodeCollection downloadsNodes = searchpage.DocumentNode.SelectNodes("//span[contains(@class, 'emoji-block') and contains(@title, 'Downloads')]//span[contains(@class, 'count')]");
            HtmlNodeCollection pinsNodes = searchpage.DocumentNode.SelectNodes("//span[contains(@class, 'emoji-block') and contains(@title, 'Followers')]//span[contains(@class, 'count')]");*/
            int size = titleNodes.Count;
            for (int i = 0; i < size; i++)
            {
                string title = WebUtility.HtmlDecode(titleNodes[i].InnerText);
                string modUrl = urlNodes[i].GetAttributeValue("href", "none");
                string thumbUrl = thumbUrlNodes[i].GetAttributeValue("src", "none");
                string authorName = WebUtility.HtmlDecode(authorNameNodes[i].InnerText);
                string type = typeNodes[i].InnerText;
                string gender = gendersNodes[i].InnerText;
                string views = viewsNodes[i].InnerText;
                modthumbnails.Add(new ModThumb(title, modUrl, authorName, thumbUrl, "", type, gender, views));
            }

            return modthumbnails;

        }

        public static string BuildSearchURL(
            SortBy sortBy,
        SortOrder sortOrder,
        string basicText = null,
        NSFW nsfw = NSFW.False,
        string name = null,
        string author = null,
        Gender? gender = null,
        string race = null,
        string tags = null,
        string affects = null,
        string comments = null,
        DTCompatibility dtCompatibility = DTCompatibility.TexToolsCompatible,
        HashSet<Types> types = null,
        int page = 1)
        {
            var queryParams = new Dictionary<string, string>();

            // Required Parameters
            queryParams["sortby"] = sortBy.ToString().ToLower();
            queryParams["sortorder"] = sortOrder.ToString().ToLower();
            queryParams["nsfw"] = nsfw == NSFW.True ? "true" : "false";
            queryParams["dt_compat"] = ((int)dtCompatibility).ToString();

            // Optional Parameters
            if (!string.IsNullOrEmpty(basicText)) queryParams["basic_text"] = basicText;
            if (!string.IsNullOrEmpty(name)) queryParams["name"] = name;
            if (!string.IsNullOrEmpty(author)) queryParams["author"] = author;
            if (gender.HasValue) queryParams["genders"] = gender.ToString().ToLower();
            if (!string.IsNullOrEmpty(race)) queryParams["races"] = race;
            if (!string.IsNullOrEmpty(tags)) queryParams["tags"] = tags;
            if (!string.IsNullOrEmpty(affects)) queryParams["affects"] = affects;
            if (!string.IsNullOrEmpty(comments)) queryParams["comments"] = comments;
            
            if (types != null && types.Count > 0)
            {
                // comma-separated string for url
                var typesString = string.Join("%2C", types.Select(t => ((int)t).ToString()));
                queryParams["types"] = typesString;
            }
            queryParams["page"] = page.ToString();
            // Construct the URL
            var sb = new StringBuilder("search?");
            foreach (var param in queryParams)
            {
                sb.Append($"{param.Key}={param.Value}&");
            }

            // Remove the last '&'
            sb.Length--;

            return sb.ToString();
        }
        public WebClient() {
        
        }
    }
}
