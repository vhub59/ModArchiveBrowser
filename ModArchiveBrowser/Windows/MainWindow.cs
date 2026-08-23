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
    /// <summary>
    /// Barre de navigation.
    ///
    /// Les libellés d'origine ("New and Updated from Patreon Subscribers", "Today Most Viewed
    /// Mods"...) formaient une ligne de boutons gris si large qu'elle débordait sur deux rangées.
    /// Ils sont ramenés à un mot, l'intitulé complet passant en infobulle, et le rafraîchissement
    /// va se ranger à droite plutôt que de casser la ligne.
    /// </summary>
    private void DrawToolbar()
    {
        if (ImGui.Button("Search"))
        {
            OpenSearch(null);
        }
        Tooltip("Search the archive with filters");

        ImGui.SameLine();
        ImGui.TextDisabled("|");
        ImGui.SameLine();

        if (ImGui.Button("Trending"))
        {
            OpenSearch(WebClient.today_most_viewed);
        }
        Tooltip("Today's most viewed mods");

        ImGui.SameLine();
        if (ImGui.Button("Newest"))
        {
            OpenSearch(WebClient.newest_mods_from_all_users);
        }
        Tooltip("Newest mods from all users");

        ImGui.SameLine();
        if (ImGui.Button("Sponsored"))
        {
            OpenSearch(WebClient.new_and_updated_from_patreon_subs);
        }
        Tooltip("New and updated mods from Patreon subscribers");

        //Le bouton de rafraichissement est aligne a droite : il n'appartient pas a la navigation
        //et occupait auparavant une deuxieme ligne a lui tout seul.
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
        Tooltip(busy ? "Loading..." : "Reload the homepage");
    }

    private static void Tooltip(string text)
    {
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(text);
    }

    private void OpenSearch(string? presetSearch)
    {
        plugin.searchWindow.IsOpen = true;
        plugin.searchWindow.BringToFront();

        if (!presetSearch.IsNullOrEmpty())
        {
            Plugin.Logger.Debug(presetSearch);
            plugin.searchWindow.UpdateSearch(presetSearch);
        }

        this.IsOpen = false;
    }

    private void DrawHomePageTable()
    {
        DrawToolbar();
        if (modThumbs == null)
            return;

        ImGui.Separator();

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

            if (ModGrid.Draw($"##homecard{i}", thumb, texture, cardWidth))
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
        DrawHomePageTable();  
    }
}
