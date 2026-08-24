using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ModArchiveBrowser.Utils
{
    /// <summary>Un mod present dans le dossier de Penumbra.</summary>
    public readonly record struct InstalledMod(string Directory, string Name, string Version, string? XmaModId);

    /// <summary>Ce qu'on peut dire d'un mod face a ce qui est deja installe.</summary>
    public enum InstallState
    {
        /// <summary>Rien de comparable dans Penumbra.</summary>
        Absent,

        /// <summary>Deja installe, dans la meme version.</summary>
        SameVersion,

        /// <summary>Installe dans une autre version : c'est une mise a jour.</summary>
        DifferentVersion,

        /// <summary>Un mod y ressemble, mais les versions ne sont pas comparables.</summary>
        Similar,
    }

    /// <summary>
    /// Lit ce que Penumbra sait de ses propres mods.
    ///
    /// L'IPC ne renvoie que des noms. Or chaque mod porte, dans son meta.json, sa version et
    /// l'adresse dont il provient — deux informations decisives que l'on ne peut obtenir
    /// autrement, et qui evitent deux erreurs symetriques : proposer d'installer un mod deja
    /// present, et masquer une mise a jour en la prenant pour un doublon.
    /// </summary>
    public static class InstalledMods
    {
        private static readonly Regex XmaModIdPattern =
            new(@"xivmodarchive\.com/modid/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex VersionPattern =
            new(@"\b[vV]?(\d+(?:\.\d+)+)\b", RegexOptions.Compiled);

        /// <param name="known">
        /// Registre des mods installes par ce plugin, dossier vers identifiant XMA.
        ///
        /// Il prime sur le meta.json, car il est le seul a dire quelque chose de sur. Le champ
        /// Website qu'on y lisait est rempli par l'auteur du modpack, qui y met ce qu'il veut :
        /// son Ko-fi, son Patreon, rien du tout. Sur une bibliotheque reelle, aucun mod venu de
        /// XMA ne portait l'adresse de XMA.
        /// </param>
        public static List<InstalledMod> Read(string modDirectory, IReadOnlyDictionary<string, string>? known = null)
        {
            var result = new List<InstalledMod>();
            if (string.IsNullOrEmpty(modDirectory) || !Directory.Exists(modDirectory))
                return result;

            foreach (var folder in Directory.EnumerateDirectories(modDirectory))
            {
                var meta = Path.Combine(folder, "meta.json");
                if (!File.Exists(meta))
                    continue;

                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(meta));
                    var root = document.RootElement;

                    var name = Text(root, "Name");
                    var version = Text(root, "Version");
                    var website = Text(root, "Website");

                    var directory = Path.GetFileName(folder);

                    //Notre registre d'abord, le meta.json ensuite : le second ne renseigne
                    //l'origine que si l'auteur a pense a y mettre sa page XMA, ce qui est rare.
                    string? modId = null;
                    if (known != null && known.TryGetValue(directory, out var recorded))
                        modId = recorded;

                    if (modId == null)
                    {
                        var match = XmaModIdPattern.Match(website);
                        if (match.Success)
                            modId = match.Groups[1].Value;
                    }

                    result.Add(new InstalledMod(directory, name, version, modId));
                }
                catch (Exception e)
                {
                    //Un meta.json illisible ne doit pas priver l'utilisateur des autres.
                    Plugin.Logger.Debug($"Unreadable meta.json in {folder}: {e.Message}");
                }
            }

            return result;
        }

        /// <summary>
        /// Compare un mod de XMA a ce qui est installe.
        ///
        /// L'identifiant XMA est essaye en premier : c'est une correspondance exacte, la seule
        /// qui ne puisse pas se tromper. Le rapprochement par nom ne sert que pour les mods
        /// installes par un autre canal, dont le meta.json ne pointe pas vers XMA — Heliosphere
        /// y inscrit sa propre adresse, par exemple.
        /// </summary>
        public static (InstallState State, InstalledMod? Match) Compare(
            IReadOnlyList<InstalledMod> installed, string? xmaModId, string candidateName, string? candidateVersion)
        {
            InstalledMod? found = null;

            if (!string.IsNullOrEmpty(xmaModId))
                found = installed.Cast<InstalledMod?>().FirstOrDefault(m => m!.Value.XmaModId == xmaModId);

            found ??= installed.Cast<InstalledMod?>()
                .FirstOrDefault(m => BaseName(m!.Value.Name).Equals(BaseName(candidateName), StringComparison.OrdinalIgnoreCase)
                                     && BaseName(candidateName).Length > 0);

            if (found == null)
                return (InstallState.Absent, null);

            var installedVersion = Normalize(found.Value.Version);
            var wantedVersion = Normalize(candidateVersion) ?? Normalize(candidateName);

            if (installedVersion == null || wantedVersion == null)
                return (InstallState.Similar, found);

            return installedVersion == wantedVersion
                ? (InstallState.SameVersion, found)
                : (InstallState.DifferentVersion, found);
        }

        /// <summary>Numero de version contenu dans un texte, ou null s'il n'y en a pas.</summary>
        public static string? Normalize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var match = VersionPattern.Match(text);
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>
        /// Nom debarrasse de ses etiquettes "[...]", qualificatifs "(...)" et numeros de version.
        ///
        /// "Bibo+ (Bibo+ Base Install) v3.1.5" et "[HS] Bibo+ (Bibo+ Base Install)" designent le
        /// meme mod sans partager un caractere. Seules les versions non ambigues sont retirees —
        /// "v3", "V2.1", "1.0.4" — jamais un entier isole, sans quoi "Adidas Superstar 2"
        /// deviendrait "Adidas Superstar".
        /// </summary>
        public static string BaseName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            var stripped = Regex.Replace(name, @"\[[^\]]*\]|\([^\)]*\)", " ");
            stripped = Regex.Replace(stripped, @"\b[vV]\d+(?:\.\d+)*\b", " ");
            stripped = Regex.Replace(stripped, @"\b\d+(?:\.\d+)+\b", " ");

            return Regex.Replace(stripped, @"\s+", " ").Trim();
        }

        private static string Text(JsonElement root, string property)
            => root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }
}
