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
    ConcurrentDictionary<string,Task> imagesTasks = new ConcurrentDictionary<string,Task>();
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
        imagesTasks.Clear();
        foreach (ModThumb modThumb in modThumbs)
        {
            Task thumbnailTask = Task.Run((async () =>
                                              {
                                                  string path = await plugin.imageHandler.DownloadImage(modThumb.url_thumb);
                                                  ISharedImmediateTexture sharedTexture = Plugin.TextureProvider.GetFromFile(path);
                                                  images.TryAdd(modThumb.url_thumb, sharedTexture);
                                              }));
            imagesTasks.TryAdd(modThumb.url_thumb, thumbnailTask);
        }
    }
    /// <summary>Vue affichée par la fenêtre. Les onglets la changent sur place.</summary>
    public NavTarget CurrentTarget { get; set; } = NavTarget.Home;

    /// <summary>Vrai quand on consulte la fiche d'un mod plutot que la grille.</summary>
    public bool ShowingMod { get; set; }

    private void DrawHomePageTable()
    {
        NavBar.Context(NavBar.TitleOf(NavTarget.Home), modThumbs?.Count ?? 0, 0, plugin.prefetcher.Pending);

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

        //Le nombre de colonnes suit la largeur de la fenetre au lieu d'etre fige a trois : sur
        //une fenetre large, la grille laissait un vide equivalent a deux colonnes sur sa droite.
        var available = ImGui.GetContentRegionAvail().X;
        var columns = ModGrid.ColumnCount(available);
        var cardWidth = ModGrid.CardWidth(available, columns);

        for (var i = 0; i < modThumbs.Count; i++)
        {
            var thumb = modThumbs[i];

            //TryGetValue et non l'indexeur : RebuildSharedTextures lance les telechargements sans
            //etre attendu, si bien que refreshTask est terminee bien avant les vignettes. Une
            //image encore absente, ou dont le telechargement a echoue, faisait lever une
            //KeyNotFoundException en pleine boucle de rendu.
            IDalamudTextureWrap? texture = null;
            if (images.TryGetValue(thumb.url_thumb, out var shared))
                texture = shared.GetWrapOrDefault();

            var availability = AvailabilityIndex.Get(plugin.Configuration, thumb.url);

            if (ModGrid.Draw($"##homecard{i}", thumb, texture, cardWidth, availability,
                obscure: plugin.Configuration.BlurAdultThumbnails && AvailabilityIndex.IsAdult(plugin.Configuration, thumb.url)))
            {
                try
                {
                    plugin.modWindow.ChangeMod(thumb);
                    ShowingMod = true;
                }
                catch (Exception e)
                {
                    Plugin.ReportError("Error while loading mod,check /xllog for details", e);
                }
            }

            if ((i + 1) % columns != 0 && i < modThumbs.Count - 1)
            {
                ImGui.SameLine();
            }
        }
    }

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

        var label = checker.IsRunning ? $"Checking {checker.Checked}/{checker.Total}" : "Check now";
        var buttonWidth = ImGui.CalcTextSize(label).X + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 3f;
        ImGui.SameLine(ImGui.GetContentRegionMax().X - buttonWidth);

        using (ImRaii.Disabled(checker.IsRunning))
        {
            if (ImGuiComponents.IconButtonWithText(FontAwesomeIcon.SyncAlt, label))
                checker.Start();
        }

        ImGui.Separator();

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
            return;
        }

        foreach (var update in checker.Updates)
        {
            ImGui.TextUnformatted(update.Name);
            ImGui.SameLine();
            ImGui.TextDisabled($"·  {update.InstalledVersion}  →  {update.PublishedVersion}");

            ImGui.SameLine(ImGui.GetContentRegionMax().X - 100f);

            //Ouvrir la fiche plutot qu'installer d'ici : son bouton connait deja l'etat exact du
            //mod, et l'utilisateur voit ce qu'il installe avant de le faire.
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
}
