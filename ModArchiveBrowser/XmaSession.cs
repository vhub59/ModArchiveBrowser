using System;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace ModArchiveBrowser
{
    /// <summary>
    /// Session partagee vers xivmodarchive.com.
    ///
    /// Sans session,les pages marquees NSFW repondent 403 et le plugin ne peut ni les lire
    /// ni les installer.XMA expose pour cela un endpoint public,/anon_login,qui pose un cookie
    /// de session sans demander de compte : ce n'est pas une authentification que l'on contourne,
    /// c'est la porte d'acceptation que le site propose lui-meme dans sa navigation.
    ///
    /// Le meme conteneur de cookies sert aux deux couches HTTP du plugin : HtmlWeb pour le
    /// parsing des pages,HttpClient pour le telechargement des fichiers.
    /// </summary>
    internal static class XmaSession
    {
        public const string Root = "https://www.xivmodarchive.com";
        public const string UserAgent = "ModArchiveBrowser (Dalamud plugin; +https://github.com/Noevain/ModArchiveBrowser)";

        private static readonly CookieContainer Cookies = new();
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static bool _established;

        /// <summary>Conteneur partage,a brancher sur tout client HTTP du plugin.</summary>
        public static CookieContainer CookieJar => Cookies;

        /// <summary>Handler pret a l'emploi pour un HttpClient qui doit voir le NSFW.</summary>
        public static HttpClientHandler CreateHandler() => new()
        {
            CookieContainer = Cookies,
            UseCookies = true,
            AllowAutoRedirect = true,
        };

        /// <summary>
        /// Ouvre la session anonyme si elle ne l'est pas deja.Idempotent et sur en concurrence :
        /// plusieurs fenetres peuvent declencher un chargement en meme temps.
        /// </summary>
        public static async System.Threading.Tasks.Task EnsureAsync()
        {
            if (_established)
                return;

            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (_established)
                    return;

                using var handler = CreateHandler();
                using var client = new HttpClient(handler);
                client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
                client.Timeout = TimeSpan.FromSeconds(20);

                using var response = await client.GetAsync($"{Root}/anon_login").ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                var sid = Cookies.GetCookies(new Uri(Root))["connect.sid"];
                if (sid == null)
                {
                    Plugin.Logger.Warning("anon_login n'a pose aucun cookie : le contenu NSFW restera inaccessible.");
                    return;
                }

                _established = true;
                Plugin.Logger.Information("Session XMA anonyme etablie,contenu NSFW accessible.");
            }
            catch (Exception e)
            {
                Plugin.Logger.Warning($"Echec de l'ouverture de session XMA : {e.Message}");
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// A appeler sur un 403 : la session a expire (le cookie vit un an) ou n'a jamais ete posee.
        /// Le prochain appel a EnsureAsync la retablira.
        /// </summary>
        public static void Invalidate()
        {
            _established = false;
            Plugin.Logger.Debug("Session XMA invalidee,elle sera reouverte a la prochaine requete.");
        }

        /// <summary>En-tete Cookie pour les couches qui ne savent pas manipuler un CookieContainer.</summary>
        public static string CookieHeader()
        {
            var jar = Cookies.GetCookies(new Uri(Root));
            var parts = new System.Collections.Generic.List<string>();
            foreach (Cookie c in jar)
                parts.Add($"{c.Name}={c.Value}");

            return string.Join("; ", parts);
        }
    }
}
