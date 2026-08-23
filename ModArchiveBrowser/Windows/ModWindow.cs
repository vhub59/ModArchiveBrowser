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
                MinimumSize = new Vector2(700, 460),
                MaximumSize = new Vector2(float.MaxValue, float.MaxValue)
            };

            //Fond opaque. La fenetre laissait voir la grille de mods au travers, si bien que la
            //description se lisait par-dessus des vignettes : illisible des que le texte etait un
            //peu long. La taille minimale monte aussi, deux colonnes ne tenant pas dans 375 pixels.
            BgAlpha = 1f;
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

        /// <summary>Vrai si le fichier est heberge par XMA, donc telechargeable directement.</summary>
        private bool HostedByXma =>
            mod.HasValue && mod.Value.url_download_button.Contains("private");

        /// <summary>Extension du fichier propose au telechargement, en minuscules (".pmp", ".zip"...).</summary>
        private string DownloadExtension()
        {
            try
            {
                var path = new Uri(WebClient.xivmodarchiveRoot + mod!.Value.url_download_button).AbsolutePath;
                return Path.GetExtension(Uri.UnescapeDataString(path)).ToLowerInvariant();
            }
            catch
            {
                return string.Empty;
            }
        }

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
                    //Le HTML est indente : entre deux balises, chaque saut de ligne et chaque
                    //tabulation forme un noeud de texte a part entiere. Sans ce filtre, chacun
                    //produisait une ligne vide et la description se retrouvait aeree a l'exces.
                    var text = WebUtility.HtmlDecode(node.InnerText).Trim();
                    if (text.Length == 0)
                        break;

                    ImGui.TextWrapped(text);
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
                        //Spacing et non NewLine : NewLine insere une ligne entiere apres chaque
                        //paragraphe, ce qui doublait deja l'interligne, et se cumulait avec les
                        //noeuds de texte vides pour donner ces grands trous dans la description.
                        ImGui.Spacing();
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
                else if (!HostedByXma)
                {
                    ImGui.BeginDisabled();
                    ImGui.Button("Not available");
                    ImGui.EndDisabled();
                    if (ImGui.IsItemHovered())
                        ImGui.SetTooltip($"Hosted on {ExternalHost()}, outside xivmodarchive.\nUse \"Open in browser\" to get it manually.");
                }
                else
                {
                    //Etre heberge par XMA ne suffit pas : encore faut-il que le fichier soit un
                    //modpack. Un .pmp ou un .ttmp2 s'installe a coup sur ; une archive peut tout
                    //aussi bien contenir les sources de l'auteur (.psd, .blend) et ne rien
                    //donner. Le bouton promettait "Install using Penumbra" dans tous les cas.
                    var extension = DownloadExtension();
                    switch (extension)
                    {
                        case ".pmp":
                        case ".ttmp2":
                            if (ImGui.Button("Install using Penumbra"))
                                StartInstall();
                            break;

                        case ".zip":
                        case ".rar":
                        case ".7z":
                            if (ImGui.Button($"Try installing ({extension})"))
                                StartInstall();
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip(
                                    "This is an archive, not a modpack. It will be searched for a\n" +
                                    ".pmp or .ttmp2, but may only hold the author's source files.");
                            break;

                        default:
                            ImGui.BeginDisabled();
                            ImGui.Button("Not installable");
                            ImGui.EndDisabled();
                            if (ImGui.IsItemHovered())
                                ImGui.SetTooltip($"Penumbra cannot use a {(extension.IsNullOrEmpty() ? "file of this type" : extension)} file.");
                            break;
                    }
                }

                ImGui.SameLine();
                if(ImGui.Button("Open in browser"))
                {
                    Process.Start(new ProcessStartInfo(WebClient.xivmodarchiveRoot + mod.Value.modThumb.url) { UseShellExecute = true });
                }

                ImGui.Separator();

                // Stats
                ImGui.TextWrapped($"Views: {mod.Value.modMeta.views}");
                ImGui.TextWrapped($"Downloads: {mod.Value.modMeta.downloads}");
                ImGui.TextWrapped($"Followers: {mod.Value.modMeta.pins}");

                ImGui.Separator();

                //string.Join plutot qu'une concatenation en boucle : l'ancienne version laissait
                //une virgule orpheline en fin de liste ("Highlander ,Elezen ,Roegadyn ,").
                var raceList = string.Join(", ", mod.Value.modMeta.races);
                var tagList = string.Join(", ", mod.Value.modMeta.tags);

                //TextWrapped partout : la date de mise a jour de XMA est une chaine tres longue
                //("Sat Aug 22 2026 18:22:56 GMT+0000 (Coordinated Universal Time)") et se faisait
                //couper net au bord du panneau. Les NewLine qui separaient chaque ligne sont
                //remplaces par Spacing, bien plus discret.
                ImGui.TextWrapped($"Updated: {mod.Value.modMeta.last_update}");
                ImGui.Spacing();
                ImGui.TextWrapped($"Affects / Replaces: {WebUtility.HtmlDecode(mod.Value.modMeta.affectReplace)}");
                ImGui.Spacing();
                ImGui.TextWrapped($"Races: {WebUtility.HtmlDecode(raceList)}");
                ImGui.Spacing();
                ImGui.TextWrapped(WebUtility.HtmlDecode(mod.Value.modThumb.genders));
                ImGui.Spacing();
                ImGui.TextWrapped($"Tags: {tagList}");

                ImGui.EndChild();
            }
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
