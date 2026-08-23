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
            //Le meme conteneur de cookies que le parsing : sans lui,les fichiers NSFW repondent 403.
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
            UpdateTextures();
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

        public double CalculateFolderSizeInMB()
        {
            if (!Directory.Exists(_downloadDirectory))
            {
                //Plugin.Logger.Error("Directory does not exist.");
                return 0;
            }

            // Get all files in the directory and sum up their sizes
            var files = Directory.GetFiles(_downloadDirectory, "*", SearchOption.AllDirectories);
            long totalSizeBytes = files.Select(file => new FileInfo(file)).Sum(fileInfo => fileInfo.Length);

            // Convert the size from bytes to megabytes (1 MB = 1024 * 1024 bytes)
            double totalSizeMB = totalSizeBytes / (1024.0 * 1024.0);
            return totalSizeMB;
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
                //Meme correctif que dans DownloadMod : un seul nom,decode,partout.
                string fileName = Uri.UnescapeDataString(Path.GetFileName(new Uri(modUrl).AbsolutePath));
                string filePath = Path.Combine(_downloadDirectory, fileName);

                if (_downloadedFilenames.Contains(fileName))
                {
                    if (File.Exists(filePath))
                        return filePath;

                    Plugin.Logger.Debug($"Cache perime pour {fileName}, retelechargement.");
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
                //L'URL est percent-encodee : "kitty%20city.zip".On decode UNE fois et on se sert
                //du nom decode partout,cle de cache comprise.Auparavant le fichier etait ecrit
                //decode mais memorise encode : au second telechargement le cache renvoyait un
                //chemin inexistant et l'installation echouait sur "Invalid file path".
                string fileName = Uri.UnescapeDataString(Path.GetFileName(new Uri(modUrl).AbsolutePath));
                string filePath = Path.Combine(_downloadDirectory, fileName);

                if (_downloadedFilenames.Contains(fileName))
                {
                    if (File.Exists(filePath))
                        return filePath;

                    //Entree periemee (fichier supprime a la main,ou cache ecrit par une version
                    //anterieure au correctif) : on l'oublie et on retelecharge.
                    Plugin.Logger.Debug($"Cache perime pour {fileName}, retelechargement.");
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

        public void InstallMod(string filePath,string imagepath)
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
                Plugin.Logger.Debug($"Installing mod directly: {filePath}");
                plugin.penumbra.InstallMod(filePath);
                Plugin.Logger.Debug($"Saving thumbnail: {imagepath}");
                File.Copy(imagepath, Path.Combine(_thumbnailDirectory,Path.GetFileName(imagepath)), true);
                //the penumbra mod directory will have the same name as the file
                _modNameToThumbnail.Add(Path.GetFileNameWithoutExtension(filePath), Path.Combine(_thumbnailDirectory,Path.GetFileName(imagepath)));
                UpdateTextures();
                plugin.penumbra.OpenModWindow();
            }
            //Extract .ttmp2 and .pmp files, queue everything
            else if (extension == ".zip" || extension == ".rar" || extension == ".7z")
            {
                Plugin.Logger.Debug($"Extracting mod from archive: {filePath}");
                List<string> modFiles = ExtractModFiles(filePath);

                //Une archive XMA ne contient pas forcement un modpack : beaucoup d'auteurs y
                //deposent leurs sources (.psd, .blend, textures en vrac).Sans ce garde-fou,
                //la boucle ne tournait pas et l'utilisateur ne voyait strictement rien.
                if (modFiles.Count == 0)
                {
                    Plugin.ReportError(
                        $"L'archive {Path.GetFileName(filePath)} ne contient aucun .pmp ni .ttmp2 " +
                        "(ce sont probablement les fichiers sources de l'auteur). Rien a installer.",
                        null);
                    return;
                }

                // Install each extracted mod file
                foreach (var modFile in modFiles)
                {
                    Plugin.Logger.Debug($"Installing extracted mod: {modFile}");
                    plugin.penumbra.InstallMod(modFile);
                    Plugin.Logger.Debug($"Saving thumbnail: {imagepath}");
                    File.Copy(imagepath, Path.Combine(_thumbnailDirectory,Path.GetFileName(imagepath)), true);
                    //Indexeur plutot que Add : une archive peut porter plusieurs modpacks,et
                    //Add aurait leve une ArgumentException des le deuxieme tour de boucle.
                    _modNameToThumbnail[Path.GetFileNameWithoutExtension(modFile)] = Path.Combine(_thumbnailDirectory,Path.GetFileName(imagepath));
                    UpdateTextures();
                    plugin.penumbra.OpenModWindow();
                }
            }
            else
            {
                Plugin.ReportError($"Unsupported file format: {extension}",null);
            }
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
                            //Chemin aplati : l'entree vit souvent dans un sous-dossier qui n'existe
                            //pas ici,et un nom du type "../.." sortirait du dossier (zip slip).
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
                            //Chemin aplati, meme raison que pour le .zip.
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
                            //Chemin aplati, meme raison que pour le .zip.
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
