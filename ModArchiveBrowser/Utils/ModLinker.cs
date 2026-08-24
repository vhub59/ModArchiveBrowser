using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace ModArchiveBrowser.Utils
{
    /// <summary>
    /// Retrouve sur XMA un mod deja installe, pour le rendre verifiable.
    ///
    /// Le plugin note l'origine des mods qu'il installe, mais rien ne peut la deviner pour ceux
    /// qui etaient la avant, ni pour ceux poses a la main : leur meta.json ne porte que ce que
    /// l'auteur du modpack y a mis, presque toujours un Ko-fi ou un Patreon. Ces mods restent donc
    /// hors de portee de la verification, et le resteraient indefiniment.
    ///
    /// La recherche par nom comble ce trou sans rien deviner : elle propose des candidats, et
    /// c'est l'utilisateur qui tranche. Un rattachement automatique aurait ete tentant, mais un
    /// homonyme suffirait a lier le mauvais mod — et un mauvais lien fait installer, puis
    /// supprimer, le mauvais mod a la mise a jour suivante.
    /// </summary>
    public sealed class ModLinker
    {
        private readonly Plugin _plugin;

        public ModLinker(Plugin plugin) => _plugin = plugin;

        /// <summary>Dossier du mod dont on cherche la page, ou vide si aucune recherche n'est ouverte.</summary>
        public string Target { get; private set; } = string.Empty;

        public bool IsRunning { get; private set; }

        /// <summary>Pages proposees pour ce mod.</summary>
        public IReadOnlyList<ModThumb> Candidates { get; private set; } = Array.Empty<ModThumb>();

        /// <summary>Vrai quand la recherche est finie et n'a rien donne.</summary>
        public bool NothingFound { get; private set; }

        public void Close()
        {
            Target = string.Empty;
            Candidates = Array.Empty<ModThumb>();
            NothingFound = false;
        }

        public void Search(InstalledMod mod)
        {
            Target = mod.Directory;
            Candidates = Array.Empty<ModThumb>();
            NothingFound = false;
            IsRunning = true;

            var query = QueryFor(mod.Name);

            Task.Run(() =>
            {
                try
                {
                    //Sans filtre de type ni de compatibilite : on cherche une page precise, pas
                    //une selection. Le contenu adulte suit le reglage global — sans la session
                    //anonyme, XMA repond 403 sur ces pages et elles resteraient introuvables.
                    var url = WebClient.BuildSearchURL(
                        SortBy.Rank, SortOrder.Desc,
                        basicText: query,
                        nsfw: _plugin.Configuration.AllowNsfw ? NSFW.Both : NSFW.False,
                        dtCompatibility: DTCompatibility.NotCompatible);

                    var results = WebClient.DoSearch(url);
                    Candidates = results.Mods.Take(8).ToList();
                    NothingFound = Candidates.Count == 0;
                }
                catch (Exception e)
                {
                    Plugin.Logger.Warning($"Could not look up \"{mod.Name}\": {e.Message}");
                    NothingFound = true;
                }
                finally
                {
                    IsRunning = false;
                }
            });
        }

        /// <summary>
        /// Rattache ce dossier a cette page, et rend le mod verifiable.
        /// </summary>
        public void Link(string directory, string modId)
        {
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(modId))
                return;

            _plugin.Configuration.InstalledFromXma[directory] = modId;
            _plugin.Configuration.Save();

            //La bibliotheque doit refleter le rattachement sans attendre une verification : le mod
            //passe de "non suivi" a "pas encore verifie" sous les yeux de l'utilisateur.
            _plugin.updateChecker.RefreshLibrary();
            Close();
        }

        /// <summary>
        /// Ce qu'on envoie a la recherche, a partir du nom du mod.
        ///
        /// Le nom d'un dossier de Penumbra vient du fichier telecharge, et il est charge de choses
        /// qui ne figurent pas dans le titre publie : etiquettes entre crochets, qualificatifs
        /// entre parentheses, numero de version, parfois une traduction accolee. On garde donc la
        /// tete du nom, jusqu'au premier de ces separateurs.
        ///
        /// Volontairement large : mieux vaut huit candidats parmi lesquels choisir qu'une
        /// recherche trop precise qui ne rend rien.
        /// </summary>
        public static string QueryFor(string? name)
        {
            var stripped = InstalledMods.BaseName(name);
            if (string.IsNullOrWhiteSpace(stripped))
                return string.Empty;

            //Les noms bilingues collent souvent les deux titres — "Classic DNC skill remake=舞者职业包".
            //On s'arrete au separateur : chercher la chaine entiere ne donne rien.
            stripped = stripped.Split('=', '/', '|')[0];

            //Ponctuation de fin laissee par les decoupes precedentes.
            return Regex.Replace(stripped, @"[\s\-_,;:]+$", string.Empty).Trim();
        }
    }
}
