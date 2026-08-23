using System;
using System.Numerics;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Interface.Windowing;
using Dalamud.Interface.Internal;
using Dalamud.Interface.Utility;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;
using Penumbra.Api.IpcSubscribers;
using Penumbra.Api.Enums;
using Dalamud.Utility;
using ModArchiveBrowser.Utils;
using System.IO;
using HtmlAgilityPack;
using Dalamud.Interface.Utility.Raii;
using System.Net;
using System.Diagnostics;
namespace ModArchiveBrowser.Windows
{
    public class ModWindow : Window, IDisposable
    {
        private Plugin plugin;
        private Mod? mod;
        private HtmlNodeCollection descriptionNodes;
        private bool _isLoading = false;
        private string _statusMessage = string.Empty;
        private bool lastNodeWasBr = false;
        private bool _alreadyInstalled = false;
        public ModWindow(Plugin plugin): base("Mod view window##")
        {
            this.plugin = plugin;
            SizeConstraints = new WindowSizeConstraints
            {
                MinimumSize = new Vector2(375, 330),
                MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
            };
        }

        public void ChangeMod(ModThumb modThumb)
        {
            (this.mod,this.descriptionNodes) = WebClient.GetModPage(modThumb);
            RefreshInstalledState();
        }

        public void ChangeMod(string modId)
        {
            (this.mod, this.descriptionNodes) = WebClient.GetModPage(modId);
            RefreshInstalledState();
        }

        /// <summary>Vrai si le fichier est heberge par XMA, donc installable directement.</summary>
        private bool HostedByXma =>
            mod.HasValue && mod.Value.url_download_button.Contains("private");

        /// <summary>Nom de l'hebergeur externe, pour l'expliquer a l'utilisateur.</summary>
        private string ExternalHost()
        {
            try
            {
                var host = new Uri(mod!.Value.url_download_button).Host.Replace("www.", string.Empty);
                return string.IsNullOrEmpty(host) ? "another site" : host;
            }
            catch
            {
                return "another site";
            }
        }

        /// <summary>
        /// Determine une fois si Penumbra connait deja ce mod, plutot qu'a chaque frame.
        ///
        /// IsModInstalled interroge Penumbra par IPC et lui fait construire la liste complete de
        /// ses mods : appele depuis la boucle de rendu, ce serait soixante fois par seconde.
        /// L'etat ne change qu'a deux moments, changement de mod et fin d'installation.
        /// </summary>
        private void RefreshInstalledState()
        {
            if (!HostedByXma)
            {
                _alreadyInstalled = false;
                return;
            }

            try
            {
                var fileName = Path.GetFileNameWithoutExtension(
                    Uri.UnescapeDataString(new Uri(WebClient.xivmodarchiveRoot + mod!.Value.url_download_button).AbsolutePath));
                _alreadyInstalled = plugin.penumbra.IsModInstalled(fileName);
            }
            catch
            {
                _alreadyInstalled = false;
            }
        }
        public void Dispose()
        {

        }

        private void DrawDescHtmlFromNode(HtmlNode node)
        {
            switch (node.NodeType)
            {
                case HtmlNodeType.Text:
                    // Reached the text of the node
                    ImGui.TextWrapped(WebUtility.HtmlDecode(node.InnerText.Trim()));
                    lastNodeWasBr = false;
                    break;

                case HtmlNodeType.Element:
                    if (node.Name == "p")
                    {
                        bool isLead = node.GetAttributeValue("class", string.Empty).Contains("lead");

                        if (isLead)
                        {
                            // Make text larger for lead paragraphs
                            ImGui.TextWrapped(node.InnerText.Trim());
                            //gotta do something with fonts,I'll figure it out later
                        }
                        else
                        {
                            // Paragraphs
                            foreach (var child in node.ChildNodes)
                            {
                                DrawDescHtmlFromNode(child);
                            }
                        }
                        ImGui.NewLine(); // Add space after paragraphs
                        lastNodeWasBr = false;
                    }
                    else if (node.Name == "br")
                    {// Line break
                        if (!lastNodeWasBr)
                        {
                            ImGui.NewLine();
                            lastNodeWasBr = true;
                        }
                        else { lastNodeWasBr = false; }
                    }
                    else if (node.Name == "a")
                    {
                        DrawLink(node);
                        lastNodeWasBr = false;
                    }
                    else
                    {
                        // Others html elements for later
                        foreach (var child in node.ChildNodes)
                        {
                            DrawDescHtmlFromNode(child);
                        }
                    }
                    break;

                default:
                    // Keep going if node is not recognized
                    foreach (var child in node.ChildNodes)
                    {
                        DrawDescHtmlFromNode(child);
                    }
                    break;
            }
        }

        private void DrawLink(HtmlNode node)
        {
            string url = node.GetAttributeValue("href", string.Empty);
            string linkText = node.InnerText.Trim();

            // Render link text as a clickable item
            ImGui.TextColored(new Vector4(0.1f, 0.4f, 1.0f, 1.0f), linkText);
            if (ImGui.IsItemClicked())
            {
                //later
            }

            ImGui.SameLine(); // Ensure links are inline
        }

        private void StartInstall()
        {
            _isLoading = true;
            Task.Run(() =>
            {
                _statusMessage = "Downloading...";
                string modpath = plugin.modHandler.DownloadModAsync(WebClient.xivmodarchiveRoot + mod.Value.url_download_button).Result;
                _statusMessage = "Installing...";
                plugin.modHandler.InstallMod(modpath, plugin.imageHandler.GetImage(mod.Value.modThumb.url_thumb));

            }).ContinueWith(task =>
            {
                _isLoading = false;
                //Le bouton doit passer a "Already installed" sans attendre un changement de mod.
                RefreshInstalledState();
            });
        }

        private void DrawLoading()
        {
            using var loadingChild = ImRaii.Child("###modbrowserinstallingLoadingFrame", new Vector2(-1, -1), false);
            if (loadingChild)
            {
                ImGui.GetWindowDrawList().PushClipRectFullScreen();
                ImGui.GetWindowDrawList().AddRectFilled(
                    ImGui.GetWindowPos() + new Vector2(0, (ImGui.GetFontSize() + (ImGui.GetStyle().FramePadding.Y * 2))),
                    ImGui.GetWindowPos() + ImGui.GetWindowSize(),
                    0xCC000000,
                    ImGui.GetStyle().WindowRounding,
                    ImDrawFlags.RoundCornersBottom);
                ImGui.PopClipRect();

                ImGui.SetCursorPosY(ImGui.GetWindowSize().Y / 2);
                StaticHelpers.CenteredText(_statusMessage);
            }
        }

        private void DrawModPage()
        {
            if (_isLoading)
            {
                DrawLoading();
            }

            // DT compatiblity
            switch (mod.Value.modMeta.dTCompatibility)
            {
                case DTCompatibility.FullyCompatible: ImGui.TextColored(new Vector4(0.0f, 1.0f, 0.0f, 1.0f), "DT Compatibility: ✅ This mod is compatible with Dawntrail.");break;
                case DTCompatibility.TexToolsCompatible: ImGui.TextColored(new Vector4(0.0f, 0.0f, 0.0f, 1.0f), "DT Compatibility: This mod is not Penumbra-Compatible in Dawntrail, but may be made so via TexTools."); break;
                case DTCompatibility.PartiallyCompatible: ImGui.TextColored(new Vector4(1.0f, 1.0f, 0.0f, 1.0f), "DT Compatibility: This mod is only partially functional in Dawntrail. Some parts may be significantly broken or require TT to fix."); break;
                case DTCompatibility.NotCompatible: ImGui.TextColored(new Vector4(1.0f, 0.0f, 0.0f, 1.0f), "DT Compatibility:❌ This mod does NOT work in Dawntrail, and is entirely non-functional. It will be eventually removed if not updated by the author."); break;
            }
            //Deux enfants cote a cote plutot qu'ImGui.Columns. Columns est une API historique qui
            //memorise ses offsets par fenetre et les tenait a une largeur fixe : la colonne de
            //gauche restait etroite quelle que soit la taille de la fenetre, laissant une bande
            //vide au milieu par laquelle on voyait la fenetre du dessous. Ici les largeurs sont
            //recalculees a chaque frame et suivent donc le redimensionnement.
            var avail = ImGui.GetContentRegionAvail();
            var spacing = ImGui.GetStyle().ItemSpacing.X;
            var leftWidth = MathF.Max(220f, (avail.X - spacing) * 0.58f);
            var rightWidth = MathF.Max(200f, avail.X - leftWidth - spacing);

            // Left Column (Mod Information)
            {
                ImGui.BeginChild("LeftColumn", new Vector2(leftWidth, 0), true);
                //TextWrapped et non Text : les titres de mods sont longs et se faisaient couper
                //en plein milieu, sans meme une ellipse.
                ImGui.TextWrapped(mod.Value.modThumb.name);

                ImGui.Separator();

                // Author
                ImGui.TextWrapped($"{mod.Value.modThumb.type} by {mod.Value.modThumb.author}");
                ImGui.Spacing();

                var thumbPath = plugin.imageHandler.GetImage(mod.Value.modThumb.url_thumb);
                var modThumbnail = thumbPath.IsNullOrEmpty()
                    ? null
                    : Plugin.TextureProvider.GetFromFile(thumbPath).GetWrapOrDefault();

                if (modThumbnail != null)
                {
                    //ImageFullWidth respecte le ratio de l'image et occupe la largeur disponible.
                    //L'ancien appel forcait 300x200 : les previews, souvent larges, se
                    //retrouvaient ecrasees. Cette aide existait deja dans le projet, inutilisee.
                    StaticHelpers.ImageFullWidth(modThumbnail, 320f);
                }
                else
                {
                    StaticHelpers.PlaceholderBox(new Vector2(ImGui.GetContentRegionAvail().X, 200), "Loading preview...");
                }

                ImGui.Spacing();
                ImGui.Separator();

                // Tabs (Info, Files, History)
                DrawDescHtmlFromNode(descriptionNodes.First());

                ImGui.EndChild();
            }

            ImGui.SameLine();

            // Right Column (Author Info, Download, Stats)
            {
                ImGui.BeginChild("RightColumn", new Vector2(rightWidth, 0), true);

                // Author Card
                ImGui.TextWrapped(mod.Value.modThumb.author);

                //L'avatar arrive de façon asynchrone : GetImage renvoie une chaîne vide tant
                //qu'il n'est pas là, puis son chemin. Plus de verrou d'échec ici : l'ancien
                //failedAvatarUrl se posait dès la première frame, forcément sans image, et ne se
                //relâchait jamais — l'avatar restait donc condamné même une fois téléchargé.
                var authorpicpath = plugin.imageHandler.GetImage(mod.Value.url_author_profilepic);
                var authorpicThumbnail = authorpicpath.IsNullOrEmpty()
                    ? null
                    : Plugin.TextureProvider.GetFromFile(authorpicpath).GetWrapOrDefault();

                if (authorpicThumbnail != null)
                {
                    ImGui.Image(authorpicThumbnail.Handle, new Vector2(100, 100));
                }
                else
                {
                    StaticHelpers.PlaceholderBox(new Vector2(100, 100));
                }
                ImGui.Separator();

                // Download button
                //url_download_button pointe vers /private/... quand XMA heberge le fichier, vers
                //Mega, Drive ou Patreon sinon. Environ un tiers du catalogue est dans ce second
                //cas et reste hors de portee : mieux vaut nommer l'hebergeur que laisser un
                //bouton grise sans explication.
                if (_alreadyInstalled)
                {
                    ImGui.BeginDisabled();
                    ImGui.Button("Already installed");
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip("Penumbra already has a mod with this name.");
                }
                else if (HostedByXma)
                {
                    if (ImGui.Button("Install using Penumbra"))
                    {
                        StartInstall();
                    }
                }
                else
                {
                    ImGui.BeginDisabled();
                    ImGui.Button("Not available");
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Hosted on {ExternalHost()}, outside xivmodarchive.\nUse \"Open in browser\" to get it manually.");
                }

                ImGui.SameLine();
                if(ImGui.Button("Open in browser"))
                {
                    Process.Start(new ProcessStartInfo(WebClient.xivmodarchiveRoot + mod.Value.modThumb.url) { UseShellExecute = true });
                }

                ImGui.Separator();

                // Stats
                ImGui.Text($"Views: {mod.Value.modMeta.views}");
                ImGui.Text($"Downloads: {mod.Value.modMeta.downloads}");
                ImGui.Text($"Followers: {mod.Value.modMeta.pins}");

                ImGui.Separator();

                // Metadata
                var race_str = string.Empty;
                for (int i = 0; i < mod.Value.modMeta.races.Length; i++)
                {
                    race_str = race_str + mod.Value.modMeta.races[i] + " ,";
                }
                var tag_str = string.Empty;
                for (int i = 0; i < mod.Value.modMeta.tags.Length; i++)
                {
                    tag_str = tag_str + mod.Value.modMeta.tags[i]+ " ,";
                }
                ImGui.Text($"Last Version Update: {mod.Value.modMeta.last_update}");
                ImGui.NewLine();
                ImGui.Text($"Affects / Replaces: {WebUtility.HtmlDecode(mod.Value.modMeta.affectReplace)}");
                ImGui.NewLine();
                ImGui.Text($"Races: {WebUtility.HtmlDecode(race_str)}");
                ImGui.NewLine();
                ImGui.TextWrapped($"{WebUtility.HtmlDecode(mod.Value.modThumb.genders)}");
                ImGui.NewLine();
                ImGui.TextWrapped($"Tags: {tag_str}");

                ImGui.EndChild();
            }

            ImGui.Columns(1); // End columns
        }
        public override void Draw()
        {
           if(mod is not null)
            {
                DrawModPage();
            }
            else
            {
                ImGui.Text("No mod selected,use the main window to browse some mods");
            }
           

        }
    }
}
