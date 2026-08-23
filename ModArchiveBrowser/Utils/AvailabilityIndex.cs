using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;

namespace ModArchiveBrowser.Utils
{
    /// <summary>Ce que l'on sait de la possibilité d'installer un mod.</summary>
    public enum ModAvailability
    {
        /// <summary>Jamais consulté : on ne sait rien, et on n'affiche rien.</summary>
        Unknown = 0,

        /// <summary>Hébergé par XMA en .pmp ou .ttmp2 : installation certaine.</summary>
        Installable = 1,

        /// <summary>Hébergé par XMA, mais dans une archive : peut ne contenir que des sources.</summary>
        Archive = 2,

        /// <summary>Hébergé ailleurs (Mega, Drive, Patreon...) : hors de portée.</summary>
        External = 3,

        /// <summary>Hébergé par XMA dans un format que Penumbra ne sait pas lire.</summary>
        Unsupported = 4,

        /// <summary>
        /// Heberge par Heliosphere, une autre plateforme de mods dotee de son propre plugin
        /// Dalamud. Le fichier n'est pas telechargeable d'ici, mais le mod reste installable en
        /// un clic depuis leur site : ce n'est pas un cul-de-sac comme Mega ou Drive.
        /// </summary>
        Heliosphere = 5,
    }

    /// <summary>
    /// Retient, mod par mod, s'il est installable.
    ///
    /// Cette information ne figure que sur la page d'un mod, jamais dans les résultats de
    /// recherche : la connaître pour tout le catalogue supposerait de parcourir les 52 000 pages
    /// de XMA, soit une trentaine d'heures à un rythme respectueux, et de recommencer
    /// regulierement. L'index se construit donc a l'usage : chaque fiche ouverte est enregistree,
    /// et la grille en profite ensuite sans une seule requete supplementaire. Les mods les plus
    /// consultes sont couverts en premier, ce qui est exactement l'ordre utile.
    /// </summary>
    public static class AvailabilityIndex
    {
        private static readonly Regex ModIdPattern = new(@"/modid/(\d+)", RegexOptions.Compiled);

        /// <summary>Identifiant du mod tiré de son URL relative ("/modid/62528" donne "62528").</summary>
        public static string? ModIdFromUrl(string? relativeUrl)
        {
            if (string.IsNullOrEmpty(relativeUrl))
                return null;

            var match = ModIdPattern.Match(relativeUrl);
            return match.Success ? match.Groups[1].Value : null;
        }

        public static ModAvailability Get(Configuration config, string? relativeUrl)
        {
            var id = ModIdFromUrl(relativeUrl);
            if (id == null)
                return ModAvailability.Unknown;

            return config.KnownAvailability.TryGetValue(id, out var value)
                ? (ModAvailability)value
                : ModAvailability.Unknown;
        }

        /// <summary>
        /// Enregistre ce qu'on vient d'apprendre en ouvrant une fiche.
        ///
        /// N'ecrit la configuration que si la valeur change : ChangeMod est appele a chaque
        /// ouverture de fiche, y compris pour un mod deja connu.
        /// </summary>
        /// <param name="save">
        /// Faux pendant un prechargement : trente cartes signifieraient trente ecritures de la
        /// configuration sur disque. L'appelant sauvegarde une fois, a la fin.
        /// </param>
        /// <summary>Vrai si ce mod est connu comme adulte.</summary>
        public static bool IsAdult(Configuration config, string? relativeUrl)
        {
            var id = ModIdFromUrl(relativeUrl);
            return id != null && config.KnownAdult.Contains(id);
        }

        public static bool Record(Configuration config, string? relativeUrl, string? downloadUrl, bool save = true, bool? adult = null)
        {
            var id = ModIdFromUrl(relativeUrl);
            if (id == null)
                return false;

            var changed = false;

            //Le marqueur adulte est retenu meme quand l'installabilite reste inconnue : les deux
            //viennent de la meme visite, autant garder ce qu'on a appris.
            if (adult.HasValue)
            {
                changed = adult.Value ? config.KnownAdult.Add(id) : config.KnownAdult.Remove(id);
            }

            var availability = Classify(downloadUrl);
            if (availability == ModAvailability.Unknown)
            {
                if (changed && save)
                    config.Save();

                return changed;
            }

            if (!config.KnownAvailability.TryGetValue(id, out var existing) || existing != (int)availability)
            {
                config.KnownAvailability[id] = (int)availability;
                changed = true;
            }

            if (changed && save)
                config.Save();

            return changed;
        }

        private static ModAvailability Classify(string? downloadUrl)
        {
            if (string.IsNullOrEmpty(downloadUrl))
                return ModAvailability.Unknown;

            //"/private/" est la marque des fichiers servis par XMA ; tout le reste pointe vers un
            //hebergeur tiers, que le plugin ne peut pas telecharger.
            if (!downloadUrl.Contains("private"))
            {
                return downloadUrl.Contains("heliosphere.app", StringComparison.OrdinalIgnoreCase)
                    ? ModAvailability.Heliosphere
                    : ModAvailability.External;
            }

            string extension;
            try
            {
                var path = new Uri(WebClient.xivmodarchiveRoot + downloadUrl).AbsolutePath;
                extension = Path.GetExtension(Uri.UnescapeDataString(path)).ToLowerInvariant();
            }
            catch
            {
                return ModAvailability.Unknown;
            }

            return extension switch
            {
                ".pmp" or ".ttmp2" => ModAvailability.Installable,
                ".zip" or ".rar" or ".7z" => ModAvailability.Archive,
                _ => ModAvailability.Unsupported,
            };
        }

        /// <summary>
        /// Vrai si ce mod ne s'installera pas d'ici, et qu'on le sait.
        ///
        /// Archive et Heliosphere n'en sont pas : la premiere a des chances d'aboutir, la seconde
        /// s'installe en un clic depuis l'autre plateforme. Unknown non plus, evidemment — c'est
        /// l'absence d'information, pas une reponse negative.
        /// </summary>
        public static bool IsDeadEnd(ModAvailability availability)
            => availability is ModAvailability.External or ModAvailability.Unsupported;

        /// <summary>Libellé affiché en infobulle sur une carte.</summary>
        public static string Describe(ModAvailability availability) => availability switch
        {
            ModAvailability.Installable => "Installs into Penumbra in one click.",
            ModAvailability.Archive => "Archive: may only hold the author's source files.",
            ModAvailability.External => "Hosted outside xivmodarchive: cannot be installed from here.",
            ModAvailability.Heliosphere => "Published on Heliosphere: install it from there in one click.",
            ModAvailability.Unsupported => "Penumbra cannot use this file type.",
            _ => string.Empty,
        };
    }
}
