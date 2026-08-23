using Dalamud.Configuration;
using Dalamud.Plugin;
using System;
using System.Collections.Generic;
using System.IO;

namespace ModArchiveBrowser;

[Serializable]
public class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 0;

    public int CacheSize { get; set; } = 2000;

    public string CacheModPath { get; set; } = Path.Combine(System.IO.Path.GetTempPath(), "modarchivebrowser\\modCache");
    public string CacheImagePath { get; set; } = Path.Combine(System.IO.Path.GetTempPath(), "modarchivebrowser\\imageCache");

    public string ThumbnailsFolder { get; set; } = Path.Combine(System.IO.Path.GetTempPath(), "modarchivebrowser\\thumbnails");
    public HashSet<string> CacheFiles { get; set; } = new HashSet<string>();
    public Dictionary<string, string> modNameToThumbnail = new Dictionary<string, string>();
    public bool penumbraDispThumb = true;

    /// <summary>
    /// Accord explicite de l'utilisateur pour le contenu adulte. Désactivé par défaut.
    ///
    /// Tant qu'il est faux, la session anonyme n'est pas ouverte : XMA répond 403 sur ces pages
    /// et le plugin ne peut donc ni les afficher ni les installer, même par accident. C'est plus
    /// solide qu'un filtre côté client, qu'un oubli de condition suffirait à contourner.
    /// </summary>
    public bool AllowNsfw { get; set; } = false;

    /// <summary>
    /// Index d'installabilité, construit à l'usage : identifiant du mod vers ModAvailability.
    ///
    /// L'information ne figure que sur la page d'un mod. La connaître pour tout le catalogue
    /// supposerait de parcourir les 52 000 pages de XMA ; on retient donc simplement ce que
    /// l'on apprend en consultant les fiches, et la grille s'en sert ensuite gratuitement.
    /// </summary>
    public Dictionary<string, int> KnownAvailability { get; set; } = new Dictionary<string, int>();

    /// <summary>
    /// Mods reconnus comme adultes, appris en meme temps que leur installabilite.
    ///
    /// Les resultats de recherche ne les marquent pas ; seule la fiche prefixe son champ type.
    /// Le prechargement ouvrant deja cette page, l'information ne coute aucune requete de plus.
    /// </summary>
    public HashSet<string> KnownAdult { get; set; } = new HashSet<string>();

    /// <summary>
    /// Masque les vignettes des mods adultes au lieu de les cacher entierement.
    ///
    /// Permet de parcourir tout le catalogue d'un seul tenant sans qu'une image explicite
    /// s'affiche a l'improviste. Survoler une carte la revele.
    /// </summary>
    public bool BlurAdultThumbnails { get; set; } = true;

    /// <summary>
    /// Masque toutes les vignettes, sans distinction.
    ///
    /// Le marqueur adulte de XMA vient de l'auteur du mod : rien ne l'oblige a le renseigner, et
    /// beaucoup ne le font pas. Un masquage selectif laisse donc passer ce qui n'a pas ete
    /// declare, et donne une assurance qu'il ne peut pas tenir. Aucune detection ne comblera ce
    /// trou — ni les tags, ni le type, ni le champ "Affects" ne garantissent quoi que ce soit.
    ///
    /// Tout masquer est la seule regle qui ne puisse pas rater. Le survol revele au cas par cas.
    /// </summary>
    public bool ObscureAllThumbnails { get; set; } = false;

    /// <summary>
    /// Retire de la grille les mods dont on sait qu'ils ne s'installeront pas d'ici.
    ///
    /// Le filtre ne cache que ce qui est etabli : un mod heberge sur Mega ou dans un format que
    /// Penumbra ne lit pas. Tout ce qui reste inconnu continue de s'afficher — l'index se
    /// construit page par page, et masquer l'inconnu reviendrait a vider la grille puis a la voir
    /// se remplir au fil du prechargement.
    ///
    /// Faux par defaut : le catalogue entier reste la vue de reference, et environ un mod sur
    /// quatre disparait quand on l'active.
    /// </summary>
    public bool HideUnavailable { get; set; } = false;

    // the below exist just to make saving less cumbersome
    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
