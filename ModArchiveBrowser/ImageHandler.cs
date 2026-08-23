using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using ModArchiveBrowser.Utils;

namespace ModArchiveBrowser
{
    /// <summary>
    /// Récupère et met en cache les images distantes (vignettes de mods, avatars d'auteurs).
    ///
    /// Ces images sont demandées depuis la boucle de rendu, donc plusieurs dizaines de fois par
    /// seconde pour une même URL. Tout le travail consiste à répondre instantanément et à ne
    /// déclencher qu'un seul téléchargement par ressource.
    /// </summary>
    public class ImageHandler : IDisposable
    {
        public readonly string _downloadDirectory;
        private readonly HttpClient _httpClient;

        //ConcurrentDictionary et non HashSet : la boucle de rendu lit pendant que les tâches de
        //téléchargement écrivent. Un HashSet partagé entre les deux se corrompt silencieusement.
        private readonly ConcurrentDictionary<string, byte> _downloaded = new();

        //Téléchargements en cours, pour qu'une image manquante ne soit demandée qu'une fois
        //même si la boucle de rendu la réclame à chaque image affichée.
        private readonly ConcurrentDictionary<string, byte> _inFlight = new();

        public ImageHandler(string downloadDirectory)
        {
            _downloadDirectory = downloadDirectory;
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("User-Agent", XmaSession.UserAgent);
            _httpClient.Timeout = TimeSpan.FromSeconds(20);

            if (!Directory.Exists(_downloadDirectory))
            {
                Directory.CreateDirectory(_downloadDirectory);
            }

            //Les vignettes sont petites mais nombreuses : 250 Mo laissent de quoi parcourir
            //longuement le catalogue sans que le dossier ne grossisse indefiniment.
            StaticHelpers.PruneCache(_downloadDirectory, 250);
        }

        /// <summary>
        /// Chemin local de l'image, ou une chaîne vide si elle n'est pas encore disponible.
        ///
        /// Ne bloque jamais : si l'image manque, le téléchargement est lancé en arrière-plan et
        /// l'appel suivant, une fois l'image arrivée, renverra son chemin. Auparavant cette
        /// méthode se contentait de consulter le cache et renvoyait un chemin bidon
        /// ("thumbnail.jpg") sans jamais rien télécharger : les avatars d'auteurs, que personne
        /// ne téléchargeait ailleurs, ne pouvaient donc jamais s'afficher.
        /// </summary>
        public string GetImage(string imageUrl)
        {
            if (string.IsNullOrWhiteSpace(imageUrl))
                return string.Empty;

            try
            {
                var fileName = Path.GetFileName(new Uri(imageUrl).AbsolutePath);
                if (string.IsNullOrEmpty(fileName))
                    return string.Empty;

                var filePath = Path.Combine(_downloadDirectory, fileName);
                if (_downloaded.ContainsKey(fileName) && File.Exists(filePath))
                    return filePath;

                //TryAdd fait office de verrou : une seule tâche par URL.
                if (_inFlight.TryAdd(imageUrl, 0))
                {
                    _ = Task.Run(async () =>
                    {
                        try { await DownloadImage(imageUrl).ConfigureAwait(false); }
                        finally { _inFlight.TryRemove(imageUrl, out _); }
                    });
                }

                return string.Empty;
            }
            catch (Exception e)
            {
                Plugin.Logger.Debug($"Malformed image URL '{imageUrl}': {e.Message}");
                return string.Empty;
            }
        }

        /// <summary>Télécharge l'image et renvoie son chemin local, ou une chaîne vide en cas d'échec.</summary>
        public async Task<string> DownloadImage(string imageUrl)
        {
            try
            {
                var fileName = Path.GetFileName(new Uri(imageUrl).AbsolutePath);
                var filePath = Path.Combine(_downloadDirectory, fileName);

                if (_downloaded.ContainsKey(fileName) && File.Exists(filePath))
                    return filePath;

                var imageBytes = await _httpClient.GetByteArrayAsync(imageUrl).ConfigureAwait(false);
                await File.WriteAllBytesAsync(filePath, imageBytes).ConfigureAwait(false);

                _downloaded[fileName] = 0;
                return filePath;
            }
            catch (Exception e)
            {
                //Une image manquante n'est pas un incident : l'interface affiche un cadre neutre.
                //On trace sans alerter l'utilisateur, sinon le moindre avatar absent inonderait
                //le chat d'erreurs a chaque affichage.
                Plugin.Logger.Debug($"Failed to download image {imageUrl}: {e.Message}");
                return string.Empty;
            }
        }

        /// <summary>Oublie les images connues, pour forcer un rechargement.</summary>
        public void ClearCache()
        {
            _downloaded.Clear();
        }

        /// <summary>
        /// Ne supprime rien.
        ///
        /// Le dechargement du plugin vidait auparavant tout le dossier : chaque vignette etait
        /// donc retelechargee a la session suivante, alors qu'aucune n'avait change. Du gaspillage
        /// pur, pour l'utilisateur comme pour XMA. Le cache se vide a la demande depuis la
        /// configuration, ou automatiquement quand il depasse la taille autorisee.
        /// </summary>
        public void Dispose()
        {
        }
    }
}
