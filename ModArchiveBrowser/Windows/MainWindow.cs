using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using System.Net.Http;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using HtmlAgilityPack;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using System.Net.Http.Headers;
using Dalamud.Utility;
using Dalamud.Bindings.ImGuizmo;
using System.Drawing.Text;
using System.Linq;
using ModArchiveBrowser.Utils;
using System.Threading;
using Penumbra.Api.IpcSubscribers;
using System.Threading.Tasks;
using System.Collections.Concurrent;

namespace ModArchiveBrowser.Windows;

public class MainWindow : Window, IDisposable
{
    private Plugin plugin;
    private List<ModThumb> modThumbs;
    // We give this window a hidden ID using ##
    // So that the user will see "My Amazing Window" as window title,
    // but for ImGui the ID is "My Amazing Window##With a hidden ID"
    private Task refreshTask = null;
    ConcurrentDictionary<string,ISharedImmediateTexture> images = new ConcurrentDictionary<string, ISharedImmediateTexture>();
    public MainWindow(Plugin plugin)
        : base("XIV Mod Archive Browser##modarchivebrowserhome")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(600, 500),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
        };
        this.plugin = plugin;
        refreshTask = Task.Run(Refresh);
    }

    public void Dispose() {

    }

    private void Refresh()
    {
        modThumbs = WebClient.GetHomePageMods();
        modThumbs = modThumbs.Distinct().ToList();
        RebuildSharedTextures();
        plugin.prefetcher.Prefetch(modThumbs);
    }

    private async void RebuildSharedTextures()
    {
        foreach (ModThumb modThumb in modThumbs)
        {
            _ = Task.Run((async () =>
                                              {
                                                  string path = await plugin.imageHandler.DownloadImage(modThumb.url_thumb);
                                                  ISharedImmediateTexture sharedTexture = Plugin.TextureProvider.GetFromFile(path);
                                                  images.TryAdd(modThumb.url_thumb, sharedTexture);
                                              }));
        }
    }
    /// <summary>Vue affichée par la fenêtre. Les onglets la changent sur place.</summary>
    public NavTarget CurrentTarget { get; set; } = NavTarget.Home;

    /// <summary>Vrai quand on consulte la fiche d'un mod plutot que la grille.</summary>
    public bool ShowingMod { get; set; }

    private void DrawHomePageTable()
    {
        var visible = ModGrid.Visible(plugin.Configuration, modThumbs);

        NavBar.Context(NavBar.TitleOf(NavTarget.Home), modThumbs?.Count ?? 0,
            visible.Count == (modThumbs?.Count ?? 0) ? 0 : visible.Count,
            plugin.prefetcher.Pending);

        //Le rafraichissement ne concerne que l'accueil : les autres vues se rechargent par leur
        //propre bouton de recherche.
        var label = "Refresh";
        var buttonWidth = ImGui.CalcTextSize(label).X + ImGui.GetStyle().FramePadding.X * 2f;
        ImGui.SameLine(ImGui.GetContentRegionMax().X - buttonWidth);

        var busy = refreshTask is { IsCompleted: false };
        using (ImRaii.Disabled(busy))
        {
            if (ImGui.Button(label))
            {
                refreshTask = Task.Run(Refresh);
            }
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(busy ? "Loading..." : "Reload the homepage");

        ImGui.Separator();

        if (modThumbs == null)
        {
            ImGui.TextDisabled("Loading the homepage...");
            return;
        }

        if (visible.Count == 0)
        {
            ImGui.TextDisabled("Every mod on this page is hosted elsewhere and hidden by the filter.");
            return;
        }

        ModGrid.DrawPage(plugin, "homecard", visible, TextureFor, thumb =>
        {
            plugin.modWindow.ChangeMod(thumb);
            ShowingMod = true;
        });
    }

    /// <summary>
    /// Vignette deja chargee pour cette adresse, ou null.
    ///
    /// TryGetValue et non l'indexeur : RebuildSharedTextures lance les telechargements sans etre
    /// attendu, si bien que refreshTask est terminee bien avant les vignettes. Une image encore
    /// absente, ou dont le telechargement a echoue, faisait lever une KeyNotFoundException en
    /// pleine boucle de rendu.
    /// </summary>
    private IDalamudTextureWrap? TextureFor(string url)
        => images.TryGetValue(url, out var shared) ? shared.GetWrapOrDefault() : null;

    /// <summary>
    /// Fiche d'un mod, avec le chemin parcouru pour y revenir.
    ///
    /// Le retour est explicite plutot que confie a la croix de fermeture : la grille reste la
    /// destination naturelle, et on ne quitte pas le plugin en consultant un mod.
    /// </summary>
    private void DrawModDetail()
    {
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ArrowLeft, "Back"))
            ShowingMod = false;

        ImGui.SameLine();
        ImGui.TextDisabled($"·  {NavBar.TitleOf(CurrentTarget)}  ·");
        ImGui.SameLine();
        ImGui.TextUnformatted(plugin.modWindow.CurrentModName);

        ImGui.Separator();
        PenumbraNotice.Banner(plugin);
        plugin.modWindow.DrawEmbedded();
    }

    public override void Draw()
    {
        //Le theme est applique ici, autour de tout le contenu : chaque widget dessine en dessous
        //en herite, sans avoir a le repeter.
        using var theme = Theme.Scope();

        //Une seule fenetre pour toutes les vues, fiche de mod comprise. La barre change le
        //contenu sur place au lieu d'ouvrir des fenetres qui se recouvrent.
        if (ShowingMod && plugin.modWindow.HasMod)
        {
            DrawModDetail();
            return;
        }

        NavBar.Draw(plugin, CurrentTarget);
        ImGui.Separator();

        //Sous la barre plutot que tout en haut : on annonce d'abord ou l'on est, ensuite ce qui
        //manque. Le bandeau disparait de lui-meme des que Penumbra est la.
        PenumbraNotice.Banner(plugin);

        switch (CurrentTarget)
        {
            case NavTarget.Home:
                DrawHomePageTable();
                break;

            case NavTarget.Updates:
                DrawUpdates();
                break;

            default:
                plugin.searchWindow.DrawEmbedded(NavBar.TitleOf(CurrentTarget));
                break;
        }
    }

    /// <summary>
    /// La bibliotheque installee, et ce que XMA publie pour chacun de ses mods.
    ///
    /// L'onglet ne montrait que les mods a mettre a jour, ce qui paraissait suffisant et ne
    /// l'etait pas : quand aucun mod n'est rattache a une page XMA — le cas ordinaire — il
    /// affichait "Everything is up to date", une affirmation fausse et parfaitement credible.
    ///
    /// La bibliotheque entiere est donc listee, chaque mod portant son etat. La couverture devient
    /// visible : on voit combien de mods sont suivis, combien ne le sont pas, et on peut rattacher
    /// les seconds a leur page.
    ///
    /// La verification coute une requete par mod suivi — quelques dizaines — la ou indexer le
    /// catalogue entier en demanderait 96 000.
    /// </summary>
    private void DrawUpdates()
    {
        var checker = plugin.updateChecker;
        checker.RefreshLibraryIfStale();

        //La ligne de contexte porte la couverture, pas seulement le delta : "12 mods · 8 tracked"
        //dit en un coup d'oeil ce qu'une liste vide ne disait pas.
        NavBar.Context(NavBar.TitleOf(NavTarget.Updates), checker.Library.Count);

        if (checker.Library.Count > 0)
        {
            ImGui.SameLine();
            var tracked = checker.Library.Count - checker.UntrackedCount;
            ImGui.TextDisabled(checker.UntrackedCount == 0
                ? "·  all tracked"
                : $"·  {tracked} tracked, {checker.UntrackedCount} of unknown origin");
        }

        var installer = plugin.updateInstaller;
        var busy = checker.IsRunning || installer.IsRunning;

        var label = checker.IsRunning ? $"Checking {checker.Checked}/{checker.Total}" : "Check now";
        var buttonWidth = ImGui.CalcTextSize(label).X + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 3f;
        ImGui.SameLine(ImGui.GetContentRegionMax().X - buttonWidth);

        //La verification lit les meta.json du dossier de mods de Penumbra : sans lui, il n'y a
        //rien a comparer et le bouton lancerait un parcours de zero mod.
        using (ImRaii.Disabled(busy || !plugin.penumbra.Available))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.SyncAlt, label))
                checker.Start();
        }

        ImGui.Separator();

        if (!plugin.penumbra.Available)
        {
            ImGui.TextDisabled("Penumbra is not running, so there are no installed mods to check.");
            return;
        }

        if (checker.Updates.Count > 0)
            DrawUpdateAll();

        DrawUpdateReport();

        if (checker.Library.Count == 0)
        {
            ImGui.TextDisabled("Penumbra has no mods installed.");
            return;
        }

        //Une zone defilante : la bibliotheque peut compter des centaines de mods, la ou l'ancienne
        //liste ne montrait que le delta et tenait toujours a l'ecran.
        using var list = ImRaii.Child("updateslist", new Vector2(0, 0), false);
        if (!list)
            return;

        foreach (var entry in checker.Library)
            DrawLibraryRow(entry);
    }

    /// <summary>
    /// Une ligne de la bibliotheque : le mod, son etat, et ce qu'on peut en faire.
    ///
    /// L'onglet ne montrait que les mods a mettre a jour. C'etait insuffisant d'une facon qui ne
    /// se voyait pas : quand aucun mod n'est rattache a une page XMA, l'ecran affichait
    /// "Everything is up to date", ce qui est faux et parfaitement credible. La liste complete
    /// rend la couverture visible — combien de mods sont suivis, combien ne le sont pas.
    /// </summary>
    private void DrawLibraryRow(LibraryEntry entry)
    {
        var mod = entry.Mod;

        ImGui.TextUnformatted(mod.Name);
        ImGui.SameLine();

        switch (entry.State)
        {
            case ModCheckState.UpdateAvailable:
                ImGui.TextColored(Theme.Positive, $"·  {mod.Version}  →  {entry.PublishedVersion}");
                break;

            case ModCheckState.UpToDate:
                ImGui.TextDisabled($"·  {mod.Version}  ·  up to date");
                break;

            case ModCheckState.Unreadable:
                ImGui.TextColored(Theme.Warning, $"·  {mod.Version}  ·  its page could not be read");
                break;

            case ModCheckState.NotTracked:
                ImGui.TextDisabled($"·  {mod.Version}  ·  not tracked");
                break;

            default:
                ImGui.TextDisabled($"·  {mod.Version}  ·  not checked yet");
                break;
        }

        DrawRowActions(entry);

        if (plugin.modLinker.Target == mod.Directory)
            DrawLinkCandidates(mod);

        ImGui.Separator();
    }

    /// <summary>Boutons de fin de ligne, alignes a droite.</summary>
    private void DrawRowActions(LibraryEntry entry)
    {
        var installer = plugin.updateInstaller;
        var mod = entry.Mod;

        if (entry.State == ModCheckState.NotTracked)
        {
            var linker = plugin.modLinker;
            var open = linker.Target == mod.Directory;
            var label = open ? "Cancel" : "Find on XMA";

            ImGui.SameLine(ImGui.GetContentRegionMax().X - 150f);

            using (ImRaii.Disabled(linker.IsRunning && !open))
            {
                if (ImGuiComponents.IconButtonWithText(
                        open ? FontAwesomeIcon.Times : FontAwesomeIcon.Search, $"{label}##link{mod.Directory}"))
                {
                    if (open)
                        linker.Close();
                    else
                        linker.Search(mod);
                }
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(
                    "This mod cannot be checked: nothing says which xivmodarchive page it came from.\n" +
                    "Its own metadata points at the author's Ko-fi or Patreon, not at the archive.\n\n" +
                    "Search by name and pick the right page to start tracking it.");

            return;
        }

        if (entry.State != ModCheckState.UpdateAvailable)
            return;

        ImGui.SameLine(ImGui.GetContentRegionMax().X - 190f);

        using (ImRaii.Disabled(installer.IsRunning))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Download, $"Update##{mod.Directory}"))
                installer.Start(new[]
                {
                    new ModUpdate(mod.XmaModId!, mod.Directory, mod.Name, mod.Version, entry.PublishedVersion),
                });
        }

        ImGui.SameLine();

        //Ouvrir la fiche reste utile pour lire les notes de version avant de se decider.
        if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ArrowRight, $"View##{mod.Directory}"))
        {
            try
            {
                plugin.modWindow.ChangeMod(mod.XmaModId!);
                ShowingMod = true;
            }
            catch (Exception e)
            {
                Plugin.ReportError("Error while loading mod,check /xllog for details", e);
            }
        }
    }

    /// <summary>
    /// Pages proposees pour un mod dont l'origine est inconnue.
    ///
    /// C'est l'utilisateur qui tranche, et non une correspondance automatique par nom : un
    /// homonyme suffirait a lier le mauvais mod, et un mauvais lien fait installer puis supprimer
    /// le mauvais mod a la mise a jour suivante. Le nom de l'auteur est affiche pour cela — c'est
    /// souvent le seul moyen de departager deux mods au titre identique.
    /// </summary>
    private void DrawLinkCandidates(InstalledMod mod)
    {
        var linker = plugin.modLinker;

        using var indent = ImRaii.PushIndent();

        if (linker.IsRunning)
        {
            ImGui.TextDisabled("Searching xivmodarchive...");
            return;
        }

        if (linker.NothingFound)
        {
            ImGui.TextDisabled($"Nothing found for \"{ModLinker.QueryFor(mod.Name)}\".");
            ImGui.TextDisabled("The mod may have been removed, or published under another name.");
            return;
        }

        foreach (var candidate in linker.Candidates)
        {
            var modId = AvailabilityIndex.ModIdFromUrl(candidate.url);
            if (modId == null)
                continue;

            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Link, $"Link##pick{modId}"))
                linker.Link(mod.Directory, modId);

            ImGui.SameLine();
            ImGui.TextUnformatted(candidate.name);
            ImGui.SameLine();
            ImGui.TextDisabled($"·  by {candidate.author}");

            //Verifier avant de lier : deux mods peuvent porter le meme titre, et la page tranche.
            ImGui.SameLine(ImGui.GetContentRegionMax().X - 70f);
            if (ImGui.SmallButton($"Open##pick{modId}"))
                Process.Start(new ProcessStartInfo($"{WebClient.xivmodarchiveRoot}/modid/{modId}") { UseShellExecute = true });
        }
    }

    /// <summary>
    /// Applique toutes les mises a jour d'un coup.
    ///
    /// Les detecter ne servait pas a grand-chose tant qu'il fallait ouvrir chaque fiche pour les
    /// appliquer une par une. Le remplacement se fait en place — reglages reportes sur toutes les
    /// collections, ancienne version supprimee — et non par empilement, qui est ce que produit
    /// Penumbra quand on se contente de reinstaller.
    /// </summary>
    private void DrawUpdateAll()
    {
        var installer = plugin.updateInstaller;

        if (installer.IsRunning)
        {
            ImGui.TextDisabled($"Updating {installer.Done + 1}/{installer.Total} · {installer.Current}");
            ImGui.SameLine(ImGui.GetContentRegionMax().X - 90f);

            if (ImGui.Button("Stop##updateall"))
                installer.Cancel();

            return;
        }

        using (Theme.Emphasis(Theme.Positive, Theme.PositiveHovered))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Download,
                    $"Update all ({plugin.updateChecker.Updates.Count})"))
                installer.Start(plugin.updateChecker.Updates);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(
                "Replaces each mod in place: the old version is removed once the new one has been\n" +
                "downloaded, and Penumbra hands its settings and option choices to the replacement.\n\n" +
                "Mods hosted outside xivmodarchive cannot be updated from here and are listed below.");
    }

    /// <summary>
    /// Ce que la mise a jour groupee n'a pas pu traiter.
    ///
    /// Un tiers du catalogue est heberge ailleurs : passer ces mods sous silence reviendrait a
    /// laisser croire que tout a ete fait, et l'utilisateur ne s'en apercevrait que des mois plus
    /// tard, devant un mod resté a une vieille version.
    /// </summary>
    private void DrawUpdateReport()
    {
        var installer = plugin.updateInstaller;

        if (!installer.HasRun || installer.IsRunning)
            return;

        if (installer.Updated > 0)
            ImGui.TextColored(Theme.Positive, installer.Updated == 1
                ? "1 mod updated."
                : $"{installer.Updated} mods updated.");

        if (installer.Skipped.Count == 0)
            return;

        if (!ImGui.CollapsingHeader($"Could not be updated ({installer.Skipped.Count})"))
            return;

        foreach (var skipped in installer.Skipped)
        {
            ImGui.TextUnformatted(skipped.Name);
            ImGui.SameLine();
            ImGui.TextDisabled($"·  {skipped.Reason}");
        }
    }
}
