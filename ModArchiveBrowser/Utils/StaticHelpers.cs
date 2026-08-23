using Dalamud.Bindings.ImGui;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Threading.Tasks;
using Dalamud.Interface.Textures.TextureWraps;


namespace ModArchiveBrowser.Utils
{
    internal static class StaticHelpers
    {
        public static double CalculateFolderSizeInMB(string path)
        {
            if (!Directory.Exists(path))
            {
                //Plugin.ReportError("Directory does not exist.",null);
                return 0;
            }

            // Get all files in the directory and sum up their sizes
            var files = Directory.GetFiles(path, "*", SearchOption.AllDirectories);
            long totalSizeBytes = files.Select(file => new FileInfo(file)).Sum(fileInfo => fileInfo.Length);

            // Convert the size from bytes to megabytes (1 MB = 1024 * 1024 bytes)
            double totalSizeMB = totalSizeBytes / (1024.0 * 1024.0);
            return totalSizeMB;
        }

        public static void ClearCacheFully(string path)
        {
            try
            {
                // Get all files in the download directory
                var files = Directory.GetFiles(path);
                int howmuch = 0;
                foreach (var file in files)
                {
                    File.Delete(file);
                    howmuch++;
                }
                Plugin.Logger.Debug($"Deleted {howmuch} files");
            }
            catch (Exception ex)
            {
                Plugin.Logger.Debug("Error deleting files: " + ex.Message);
            }
        }

        public static void CenteredText(string text)
        {
            CenterCursorForText(text);
            ImGui.TextUnformatted(text);
        }
        //from https://github.com/heliosphere-xiv/plugin/blob/dev/Util/ImGuiHelper.cs#L114
        //
        public static void ImageFullWidth(IDalamudTextureWrap wrap, float maxHeight = 0f, bool centred = false)
        {
            // get the available area
            var widthAvail = centred && ImGui.GetScrollMaxY() == 0
                                 ? ImGui.GetContentRegionAvail().X - ImGui.GetStyle().ScrollbarSize
                                 : ImGui.GetContentRegionAvail().X;
            widthAvail = Math.Max(0, widthAvail);

            // set max height to image height if unspecified
            if (maxHeight == 0f)
            {
                maxHeight = wrap.Height;
            }

            // clamp height at the actual image height
            maxHeight = Math.Min(wrap.Height, maxHeight);

            // for the width, either use the whole space available
            // or the actual image's width, whichever is smaller
            var width = widthAvail == 0
                            ? wrap.Width
                            : Math.Min(widthAvail, wrap.Width);
            // determine the ratio between the actual width and the
            // image's width and multiply the image's height by that
            // to determine the height
            var height = wrap.Height * (width / wrap.Width);

            // check if the height is greater than the max height,
            // in which case we'll have to scale the width down
            if (height > maxHeight)
            {
                width *= maxHeight / height;
                height = maxHeight;
            }

            if (centred && width < widthAvail)
            {
                var cursor = ImGui.GetCursorPos();
                ImGui.SetCursorPos(cursor with
                {
                    X = widthAvail / 2 - width / 2,
                });
            }

            ImGui.Image(wrap.Handle, new Vector2(width, height));
        }

        /// <summary>
        /// Center the ImGui cursor for a certain text.
        /// </summary>
        /// <param name="text">The text to center for.</param>
        public static void CenterCursorForText(string text) => CenterCursorFor(ImGui.CalcTextSize(text).X);

        /// <summary>
        /// Center the ImGui cursor for an item with a certain width.
        /// </summary>
        /// <param name="itemWidth">The width to center for.</param>
        public static void CenterCursorFor(float itemWidth) =>
            ImGui.SetCursorPosX((int)((ImGui.GetWindowWidth() - itemWidth) / 2));

        /// <summary>
        /// Ramene un dossier de cache sous la taille autorisee, en supprimant les fichiers les
        /// plus anciennement utilises.
        ///
        /// Aucun des deux caches du plugin n'etait borne : le dossier de mods atteignait 532 Mo
        /// apres une seule journee d'utilisation, et rien ne l'aurait jamais arrete. Un cache dont
        /// on ne peut pas prevoir la taille finit par etre un probleme pour l'utilisateur.
        ///
        /// La suppression part des fichiers les plus vieux : ce sont ceux qu'on a le moins de
        /// chances de redemander, et un mod recemment installe reste ainsi disponible.
        /// </summary>
        public static void PruneCache(string directory, long maxMegabytes)
        {
            try
            {
                if (maxMegabytes <= 0 || !Directory.Exists(directory))
                    return;

                var budget = maxMegabytes * 1024L * 1024L;
                var files = new DirectoryInfo(directory)
                    .GetFiles("*", SearchOption.TopDirectoryOnly)
                    .OrderByDescending(f => f.LastAccessTimeUtc)
                    .ToList();

                long kept = 0;
                var removed = 0;

                foreach (var file in files)
                {
                    kept += file.Length;
                    if (kept <= budget)
                        continue;

                    try
                    {
                        file.Delete();
                        removed++;
                    }
                    catch (IOException)
                    {
                        //Fichier verrouille : on le laisse et on continue avec les suivants.
                        kept -= file.Length;
                    }
                }

                if (removed > 0)
                    Plugin.Logger.Information($"Cache pruned: {removed} file(s) removed from {directory}.");
            }
            catch (Exception e)
            {
                Plugin.Logger.Warning($"Could not prune {directory}: {e.Message}");
            }
        }

        /// <summary>
        /// Cadre neutre occupant la place d'une image absente ou en cours de chargement.
        ///
        /// Remplace les boutons "Failed to ..." qui traînaient dans l'interface : une image qui
        /// met une seconde à arriver n'est pas une erreur, et l'afficher comme telle donne
        /// l'impression que le plugin est cassé. La mise en page ne bouge pas non plus, puisque
        /// le cadre occupe exactement les mêmes dimensions que l'image attendue.
        /// </summary>
        public static void PlaceholderBox(Vector2 size, string label = "")
        {
            var start = ImGui.GetCursorScreenPos();
            var draw = ImGui.GetWindowDrawList();

            draw.AddRectFilled(start, start + size, ImGui.GetColorU32(ImGuiCol.FrameBg), 4f);
            draw.AddRect(start, start + size, ImGui.GetColorU32(ImGuiCol.Border), 4f);

            if (!string.IsNullOrEmpty(label))
            {
                var textSize = ImGui.CalcTextSize(label);
                draw.AddText(
                    start + (size - textSize) / 2,
                    ImGui.GetColorU32(ImGuiCol.TextDisabled),
                    label);
            }

            ImGui.Dummy(size);
        }
    }
}
