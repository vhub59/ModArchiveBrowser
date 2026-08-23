using System;
using System.Net;
using System.Net.Http;
using System.Threading;

namespace ModArchiveBrowser
{
    /// <summary>
    /// Session partagée vers xivmodarchive.com.
    ///
    /// Sans session, les pages marquées NSFW répondent 403 et le plugin ne peut ni les lire
    /// ni les installer — soit environ un quart du catalogue. XMA expose pour cela un endpoint
    /// public, /anon_login, qui pose un cookie de session sans demander de compte : ce n'est pas
    /// une authentification que l'on contourne, c'est la porte d'acceptation que le site propose
    /// lui-même dans sa navigation.
    ///
    /// Le même conteneur de cookies sert aux deux couches HTTP du plugin : HtmlWeb pour le
    /// parsing des pages, HttpClient pour le téléchargement des fichiers.
    /// </summary>
    internal static class XmaSession
    {
        public const string Root = "https://www.xivmodarchive.com";
        public const string UserAgent = "ModArchiveBrowser (Dalamud plugin; +https://github.com/Noevain/ModArchiveBrowser)";

        private static readonly CookieContainer Cookies = new();
        private static readonly SemaphoreSlim Gate = new(1, 1);
        private static bool _established;

        /// <summary>Conteneur partagé, à brancher sur tout client HTTP du plugin.</summary>
        public static CookieContainer CookieJar => Cookies;

        /// <summary>Handler prêt à l'emploi pour un HttpClient qui doit voir le NSFW.</summary>
        public static HttpClientHandler CreateHandler() => new()
        {
            CookieContainer = Cookies,
            UseCookies = true,
            AllowAutoRedirect = true,
        };

        /// <summary>
        /// Ouvre la session anonyme si elle ne l'est pas déjà. Idempotent et sûr en concurrence :
        /// plusieurs fenêtres peuvent déclencher un chargement en même temps.
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
                    Plugin.Logger.Warning("anon_login set no cookie: NSFW content will stay unreachable.");
                    return;
                }

                _established = true;
                Plugin.Logger.Information("Anonymous XMA session established, NSFW content reachable.");
            }
            catch (Exception e)
            {
                Plugin.Logger.Warning($"Could not open XMA session: {e.Message}");
            }
            finally
            {
                Gate.Release();
            }
        }

        /// <summary>
        /// À appeler sur un 403 : la session a expiré (le cookie vit un an) ou n'a jamais été posée.
        /// Le prochain appel à EnsureAsync la rétablira.
        /// </summary>
        public static void Invalidate()
        {
            _established = false;
            Plugin.Logger.Debug("XMA session invalidated, will reopen on next request.");
        }

        /// <summary>
        /// Ferme la session et jette le cookie. Appelé quand l'utilisateur retire son accord
        /// pour le contenu NSFW : XMA redevient alors incapable de servir ces pages, ce qui vaut
        /// mieux qu'un simple filtre d'affichage côté plugin.
        ///
        /// CookieContainer n'expose pas de Clear ; marquer les cookies expirés les retire du
        /// conteneur et les empêche d'être renvoyés.
        /// </summary>
        public static void Close()
        {
            foreach (Cookie c in Cookies.GetCookies(new Uri(Root)))
                c.Expired = true;

            _established = false;
            Plugin.Logger.Information("XMA session closed, NSFW content unreachable again.");
        }

        /// <summary>Vrai si la session anonyme est ouverte.</summary>
        public static bool IsEstablished => _established;

        /// <summary>En-tête Cookie pour les couches qui ne savent pas manipuler un CookieContainer.</summary>
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
