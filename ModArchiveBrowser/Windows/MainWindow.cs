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

    private void DrawHomePageTable()
    {
        NavBar.Context(NavBar.TitleOf(NavTarget.Home), modThumbs?.Count ?? 0);

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

            if (ModGrid.Draw($"##homecard{i}", thumb, texture, cardWidth, availability))
            {
                try
                {
                    plugin.modWindow.ChangeMod(thumb);
                    if (!plugin.modWindow.IsOpen)
                    {
                        plugin.modWindow.Toggle();
                    }

                    plugin.modWindow.BringToFront();
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

    public override void Draw()
    {
        //Une seule fenetre pour toutes les vues. La barre change le contenu sur place au lieu
        //de fermer une fenetre pour en ouvrir une autre, d'aspect identique et sans transition.
        NavBar.Draw(plugin, CurrentTarget);
        ImGui.Separator();

        if (CurrentTarget == NavTarget.Home)
        {
            DrawHomePageTable();
        }
        else
        {
            plugin.searchWindow.DrawEmbedded(NavBar.TitleOf(CurrentTarget));
        }
    }
}
