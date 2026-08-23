using System;
using System.Collections.Generic;
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
    /// Mods installes pour lesquels XMA publie une autre version.
    ///
    /// Penumbra inscrit dans chaque meta.json l'adresse d'origine du mod : on sait donc lesquels
    /// viennent de XMA, et avec quel identifiant. La verification coute une requete par mod
    /// installe — quelques dizaines — la ou indexer le catalogue entier en demanderait 52 000.
    /// </summary>
    private void DrawUpdates()
    {
        var checker = plugin.updateChecker;

        NavBar.Context(NavBar.TitleOf(NavTarget.Updates), checker.Updates.Count);

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

        if (checker.LastRun == null && !checker.IsRunning)
        {
            ImGui.TextDisabled("Your installed mods have not been checked yet.");
            ImGui.Spacing();
            ImGui.TextDisabled("Only mods installed from xivmodarchive can be checked:");
            ImGui.TextDisabled("Penumbra records where each mod came from, and that is what this compares.");
            return;
        }

        if (checker.Updates.Count == 0)
        {
            ImGui.TextDisabled(checker.IsRunning ? "Checking..." : "Everything is up to date.");
            DrawUpdateReport();
            return;
        }

        DrawUpdateAll();
        DrawUpdateReport();
        ImGui.Separator();

        foreach (var update in checker.Updates)
        {
            ImGui.TextUnformatted(update.Name);
            ImGui.SameLine();
            ImGui.TextDisabled($"·  {update.InstalledVersion}  →  {update.PublishedVersion}");

            ImGui.SameLine(ImGui.GetContentRegionMax().X - 190f);

            using (ImRaii.Disabled(installer.IsRunning))
            {
                //Le mod est remplace en place : reglages reportes, ancienne version supprimee.
                if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.Download, $"Update##{update.ModId}"))
                    installer.Start(new[] { update });
            }

            ImGui.SameLine();

            //Ouvrir la fiche reste utile pour lire les notes de version avant de se decider.
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.ArrowRight, $"View##{update.ModId}"))
            {
                try
                {
                    plugin.modWindow.ChangeMod(update.ModId);
                    ShowingMod = true;
                }
                catch (Exception e)
                {
                    Plugin.ReportError("Error while loading mod,check /xllog for details", e);
                }
            }

            ImGui.Separator();
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
                "Replaces each mod in place: your settings and option choices are carried over,\n" +
                "and the old version is removed.\n\n" +
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
