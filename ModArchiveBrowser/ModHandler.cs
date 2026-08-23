using ModArchiveBrowser.Interop.Penumbra;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using SharpCompress.Archives;
using SharpCompress.Archives.Rar;
using SharpCompress.Archives.SevenZip;

using SharpCompress.Common;
using Dalamud.Interface.Textures.TextureWraps;
using Dalamud.Interface.Textures;
using ModArchiveBrowser.Utils;
namespace ModArchiveBrowser
{
    public class ModHandler : IDisposable
    {
        private readonly string _downloadDirectory;
        private readonly string _thumbnailDirectory;
        private readonly HttpClient _httpClient;
        public HashSet<string> _downloadedFilenames;
        public  Dictionary<string, string> _modNameToThumbnail;
        public Dictionary<string,ISharedImmediateTexture> _thumbnailToTextures = new Dictionary<string, ISharedImmediateTexture>();
        private Plugin plugin;
        public ModHandler(string downloadDirectory,string thumbnailsDirectory, Plugin plugin)
        {
            _downloadDirectory = downloadDirectory;
            _thumbnailDirectory = thumbnailsDirectory;
            //Le même conteneur de cookies que le parsing : sans lui, les fichiers NSFW répondent 403.
            _httpClient = new HttpClient(XmaSession.CreateHandler());
            _httpClient.DefaultRequestHeaders.Add("User-Agent", XmaSession.UserAgent);
            _downloadedFilenames = plugin.Configuration.CacheFiles;
            _modNameToThumbnail = plugin.Configuration.modNameToThumbnail;
            this.plugin = plugin;
            // Check if it exist first
            if (!Directory.Exists(_downloadDirectory))
            {
                Directory.CreateDirectory(_downloadDirectory);
            }
            if (!Directory.Exists(_thumbnailDirectory))
            {
                Directory.CreateDirectory(_thumbnailDirectory);
            }
            NormalizeCacheKeys();
            //Le cache de mods n'etait borne par rien : 532 Mo apres une seule journee, sans que
            //rien ne l'arrete jamais.
            StaticHelpers.PruneCache(_downloadDirectory, plugin.Configuration.CacheSize);
            UpdateTextures();
        }

        /// <summary>
        /// Les versions antérieures mémorisaient le nom encodé ("kitty%20city.zip") alors que le
        /// fichier était écrit décodé : ces entrées ne correspondent plus à rien et la
        /// configuration les accumulerait indéfiniment. On les décode une fois pour toutes et on
        /// jette au passage celles dont le fichier a disparu.
        /// </summary>
        private void NormalizeCacheKeys()
        {
            var normalized = new HashSet<string>();
            foreach (var entry in _downloadedFilenames)
            {
                var name = Uri.UnescapeDataString(entry);
                if (File.Exists(Path.Combine(_downloadDirectory, name)))
                    normalized.Add(name);
            }

            if (normalized.Count == _downloadedFilenames.Count)
                return;

            Plugin.Logger.Debug($"Cache keys normalized: {_downloadedFilenames.Count} -> {normalized.Count}.");
            _downloadedFilenames.Clear();
            foreach (var name in normalized)
                _downloadedFilenames.Add(name);
        }

        private void UpdateTextures()//Cant call TextureProvider in PenumbraAPI so need the textures to be ready in advance
        {
            foreach(string mod in _modNameToThumbnail.Keys)
            {
                if (!_thumbnailToTextures.ContainsKey(mod))
                {
                    //file could be deleted from external source
                    if(!File.Exists(_modNameToThumbnail[mod]))
                    {
                        Plugin.ReportError("one of your downloaded mod had it's thumbnail deleted externally",null);
                        Plugin.ReportError($"mod: {mod}, file not found: {_modNameToThumbnail[mod]}", null);
                        _modNameToThumbnail.Remove(mod);
                    }
                    var tex = Plugin.TextureProvider.GetFromFile(_modNameToThumbnail[mod]);
                    Plugin.Logger.Debug($"Tex updated for:{mod}");
                    _thumbnailToTextures.Add(mod, tex);
                }
            }
        }

        public void Dispose()
        {
            plugin.Configuration.modNameToThumbnail = this._modNameToThumbnail;
            plugin.Configuration.CacheFiles = this._downloadedFilenames;
            plugin.Configuration.Save();
            
        }

        public async Task<string> DownloadModAsync(string modUrl)
        {
            try
            {
                modUrl = modUrl.Replace("&#39;", "'");
                //Même correctif que dans DownloadMod : un seul nom, décodé, partout.
                string fileName = Uri.UnescapeDataString(Path.GetFileName(new Uri(modUrl).AbsolutePath));
                string filePath = Path.Combine(_downloadDirectory, fileName);

                if (_downloadedFilenames.Contains(fileName))
                {
                    if (File.Exists(filePath))
                        return filePath;

                    Plugin.Logger.Debug($"Stale cache entry for {fileName}, re-downloading.");
                    _downloadedFilenames.Remove(fileName);
                }

                using (HttpResponseMessage response = await _httpClient.GetAsync(modUrl, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();

                    var modBytes = await response.Content.ReadAsByteArrayAsync();
                    await File.WriteAllBytesAsync(filePath, modBytes);
                    _downloadedFilenames.Add(fileName);
                    return filePath;
                }
            }
            catch (Exception ex)
            {
                Plugin.ReportError($"Failed to download mod: {modUrl}. Check /xllog for details",ex);
                return null;
            }
        }
        public string DownloadMod(string modUrl)
        {
            try
            {
                //L'URL est percent-encodée : "kitty%20city.zip". On décode UNE fois et ce nom
                //sert partout, clé de cache comprise. Auparavant le fichier était écrit décodé
                //mais mémorisé encodé : au second téléchargement le cache renvoyait un chemin
                //inexistant et l'installation échouait sur "Invalid file path".
                string fileName = Uri.UnescapeDataString(Path.GetFileName(new Uri(modUrl).AbsolutePath));
                string filePath = Path.Combine(_downloadDirectory, fileName);

                if (_downloadedFilenames.Contains(fileName))
                {
                    if (File.Exists(filePath))
                        return filePath;

                    //Entrée périmée (fichier supprimé à la main, ou cache écrit par une version
                    //antérieure au correctif) : on l'oublie et on retélécharge.
                    Plugin.Logger.Debug($"Stale cache entry for {fileName}, re-downloading.");
                    _downloadedFilenames.Remove(fileName);
                }

                byte[] modBytes = _httpClient.GetByteArrayAsync(modUrl).Result;
                File.WriteAllBytes(filePath, modBytes);

                _downloadedFilenames.Add(fileName);
                return filePath;
            }
            catch (Exception ex)
            {
                Plugin.ReportError($"Failed to download mod: {modUrl}. Check /xllog for details", ex);
                return null;
            }
        }

        /// <param name="replacing">
        /// Vrai quand ce fichier vient remplacer un mod deja installe.
        ///
        /// Deux comportements changent alors. La garde anti-doublon est levee : elle compare les
        /// noms, et une mise a jour porte souvent celui de la version qu'elle remplace — elle
        /// bloquerait donc precisement ce qu'on cherche a faire, Penumbra recevant par ailleurs
        /// l'ordre de substituer l'ancien. Et sa fenetre ne s'ouvre plus : sur un lot de vingt
        /// mises a jour, elle se rappellerait au premier plan vingt fois.
        /// </param>
        public void InstallMod(string filePath, string imagepath, bool replacing = false)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                Plugin.ReportError("Invalid file path or file does not exist.",null);
                return;
            }

            string extension = Path.GetExtension(filePath).ToLowerInvariant();

            //.ttmp2 or .pmp - Direct install
            if (extension == ".ttmp2" || extension == ".pmp")
            {
                InstallSingle(filePath, imagepath, replacing);
            }
            //Extract .ttmp2 and .pmp files, queue everything
            else if (extension == ".zip" || extension == ".rar" || extension == ".7z")
            {
                Plugin.Logger.Debug($"Extracting mod from archive: {filePath}");
                List<string> modFiles = ExtractModFiles(filePath);

                //Une archive XMA ne contient pas forcément un modpack : beaucoup d'auteurs y
                //déposent leurs sources (.psd, .blend, textures en vrac). Sans ce garde-fou,
                //la boucle ne tournait pas et l'utilisateur ne voyait strictement rien.
                if (modFiles.Count == 0)
                {
                    Plugin.ReportError(
                        $"{Path.GetFileName(filePath)} contains no .pmp or .ttmp2 " +
                        "(these are likely the author's source files). Nothing to install.",
                        null);
                    return;
                }

                // Install each extracted mod file
                foreach (var modFile in modFiles)
                    InstallSingle(modFile, imagepath, replacing);
            }
            else
            {
                Plugin.ReportError($"Unsupported file format: {extension}",null);
            }
        }

        /// <summary>
        /// Transmet un modpack à Penumbra, sauf s'il s'y trouve déjà.
        ///
        /// Penumbra ne déduplique pas : à chaque appel d'InstallMod il crée un dossier suffixé
        /// "(2)", "(3)"... Sans cette garde, réinstaller le même mod empilait les copies —
        /// trois dossiers de 95 Mo pour un seul mod, constaté en test.
        /// </summary>
        private void InstallSingle(string modFile, string imagepath, bool replacing = false)
        {
            var modName = Path.GetFileNameWithoutExtension(modFile);

            if (!replacing && plugin.penumbra.IsModInstalled(modName))
            {
                Plugin.ReportError($"\"{modName}\" is already installed in Penumbra, skipping.", null);
                plugin.penumbra.OpenModWindow();
                return;
            }

            Plugin.Logger.Debug($"Installing mod: {modFile}");
            plugin.penumbra.InstallMod(modFile);

            //La vignette peut manquer : une mise a jour part d'un identifiant de mod, et rien ne
            //garantit que son image ait pu etre recuperee. File.Copy levait alors en pleine
            //installation, sur ce qui n'est qu'un agrement d'affichage.
            if (!string.IsNullOrEmpty(imagepath) && File.Exists(imagepath))
            {
                Plugin.Logger.Debug($"Saving thumbnail: {imagepath}");
                File.Copy(imagepath, Path.Combine(_thumbnailDirectory, Path.GetFileName(imagepath)), true);

                //Indexeur plutôt que Add : réinstaller un mod, ou traiter une archive portant
                //plusieurs modpacks, aurait levé une ArgumentException.
                _modNameToThumbnail[modName] = Path.Combine(_thumbnailDirectory, Path.GetFileName(imagepath));

                UpdateTextures();
            }

            if (!replacing)
                plugin.penumbra.OpenModWindow();
        }

        private List<string> ExtractModFiles(string archivePath)
        {
            string extension = Path.GetExtension(archivePath).ToLowerInvariant();
            List<string> modFiles = new List<string>();

            if (extension == ".zip")
            {
                using (ZipArchive archive = ZipFile.OpenRead(archivePath))
                {
                    foreach (ZipArchiveEntry entry in archive.Entries)
                    {
                        if (entry.FullName.EndsWith(".ttmp2", StringComparison.OrdinalIgnoreCase) ||
                            entry.FullName.EndsWith(".pmp", StringComparison.OrdinalIgnoreCase))
                        {
                            //Chemin aplati : l'entrée vit souvent dans un sous-dossier qui n'existe
                            //pas ici, et un nom du type "../.." sortirait du dossier (zip slip).
                            string destinationPath = Path.Combine(_downloadDirectory, Path.GetFileName(entry.FullName));
                            entry.ExtractToFile(destinationPath, true);
                            modFiles.Add(destinationPath);
                        }
                    }
                }
            }
            else if (extension == ".rar")
            {
                using (var archive = RarArchive.Open(archivePath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (!entry.IsDirectory && (entry.Key.EndsWith(".ttmp2", StringComparison.OrdinalIgnoreCase) ||
                                                   entry.Key.EndsWith(".pmp", StringComparison.OrdinalIgnoreCase)))
                        {
                            //Chemin aplati, même raison que pour le .zip.
                            string destinationPath = Path.Combine(_downloadDirectory, Path.GetFileName(entry.Key));
                            entry.WriteToFile(destinationPath);
                            modFiles.Add(destinationPath);
                        }
                    }
                }
            }
            else if (extension == ".7z")
            {
                using (var archive = SevenZipArchive.Open(archivePath))
                {
                    foreach (var entry in archive.Entries)
                    {
                        if (!entry.IsDirectory && (entry.Key.EndsWith(".ttmp2", StringComparison.OrdinalIgnoreCase) ||
                                                   entry.Key.EndsWith(".pmp", StringComparison.OrdinalIgnoreCase)))
                        {
                            //Chemin aplati, même raison que pour le .zip.
                            string destinationPath = Path.Combine(_downloadDirectory, Path.GetFileName(entry.Key));
                            entry.WriteToFile(destinationPath);
                            modFiles.Add(destinationPath);
                        }
                    }
                }
            }

            return modFiles;
        }

    }
}
