using System;
using System.Numerics;
using System.Collections.Generic;
using Dalamud.Interface.Windowing;
using ModArchiveBrowser.Utils;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Interface.Utility.Raii;

namespace ModArchiveBrowser.Windows;

public class ConfigWindow : Window, IDisposable
{
    private Configuration Configuration;
    private Plugin plugin;
    private FileDialogManager dialogManager = new FileDialogManager();
    private bool _openFileDialog = false;

    // We give this window a constant ID using ###
    // This allows for labels being dynamic, like "{FPS Counter}fps###XYZ counter window",
    // and the window ID will always be "###XYZ counter window" for ImGui
    public ConfigWindow(Plugin plugin) : base("Mod Archive Browser Config###modbrowserconfig")
    {
        this.plugin = plugin;
        Size = new Vector2(600, 400);

        Configuration = plugin.Configuration;
    }

    public void Dispose() { }

    public override void PreDraw()
    {

    }

    private void FileDialogModCallBack(bool valid,string path)
    {
        if (valid)
        {
            Configuration.CacheModPath = path;
            Configuration.Save();
        }
        else
        {
            Plugin.ReportError("Error from filedialog,invalid folder", null);
        }
        ResetFileDialog();
    }

    private void FileDialogImageCallBack(bool valid,string path)
    {
        if (valid)
        {
            Configuration.CacheImagePath = path;
            Configuration.Save();
        }
        else
        {
            Plugin.ReportError("Error from filedialog,invalid folder", null);
        }
        ResetFileDialog();
    }

    private void FileDialogThumbsCallBack(bool valid, string path)
    {
        if (valid)
        {
            Configuration.ThumbnailsFolder = path;
            Configuration.Save();
        }
        else
        {
            Plugin.ReportError("Error from filedialog,invalid folder", null);
        }
        ResetFileDialog();
    }

    private void ResetFileDialog()
    {
        _openFileDialog = false;
        dialogManager.Reset();
    }

    public override void Draw()
    {
        using var theme = Theme.Scope();

        if (_openFileDialog)
        {
            dialogManager.Draw();
        }

        var penumbraDispThumb = Configuration.penumbraDispThumb;
        if (ImGui.Checkbox("Display mod thumbnails in Penumbra?", ref penumbraDispThumb))
        {
            Configuration.penumbraDispThumb = penumbraDispThumb;
            Configuration.Save();
        }

        //Cocher ouvre la session anonyme XMA, décocher la ferme et jette le cookie.
        //Le réglage ne se contente donc pas de masquer : il coupe l'accès à la source.
        var allowNsfw = Configuration.AllowNsfw;
        if (ImGui.Checkbox("Show adult (NSFW) mods", ref allowNsfw))
        {
            Configuration.AllowNsfw = allowNsfw;
            Configuration.Save();

            if (allowNsfw)
            {
                _ = XmaSession.EnsureAsync();
            }
            else
            {
                XmaSession.Close();
                //Fermer la session ne suffit pas : les pages NSFW deja consultees restent en
                //cache sur disque, completes et avec leur lien de telechargement. Sans cette
                //purge, elles seraient resservies sans jamais repasser par le 403 de XMA.
                WebClient.ClearHtmlCache();
            }
        }

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip(
                "Off by default. While off, xivmodarchive returns 403 for adult mods,\n" +
                "so they cannot be browsed or installed at all.");
        }
        var blur = Configuration.BlurAdultThumbnails;
        if (ImGui.Checkbox("Obscure adult thumbnails", ref blur))
        {
            Configuration.BlurAdultThumbnails = blur;
            Configuration.Save();
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Adult mods stay in the results, but their preview is smeared over\nuntil you hover the card.");

        ImGui.Separator();

        var cacheSize = Configuration.CacheSize;
        var thumbnailsPath = Configuration.ThumbnailsFolder;
        if ( ImGui.InputText("Thumbnails folder",ref thumbnailsPath,300))
        {
            Configuration.ThumbnailsFolder = thumbnailsPath;
            Configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Select Path....###thumbcachepath"))
        {
            dialogManager.OpenFolderDialog("Pick thumbnails folder", FileDialogThumbsCallBack, string.Empty, true);
            _openFileDialog = true;
        }
        ImGui.Separator();
        /*if(ImGui.InputInt("Cache Size", ref cacheSize))
        {
            Configuration.CacheSize = cacheSize;
            Configuration.Save();
        }*/
        var modCachePath = Configuration.CacheModPath;
        if (ImGui.InputText("Mod cache path",ref modCachePath,300))
        {
            Configuration.CacheModPath = modCachePath;
            plugin.modHandler = new ModHandler(modCachePath,Configuration.ThumbnailsFolder,plugin);
            Configuration.Save();
        }
        ImGui.SameLine();
        if(ImGui.Button("Select Path....###modcachepath")){
            dialogManager.OpenFolderDialog("Pick mod cache folder",FileDialogModCallBack,string.Empty,true);
            _openFileDialog = true;
        }
        var imageCachePath = Configuration.CacheImagePath;
        if (ImGui.InputText("Image cache part", ref imageCachePath, 300))
        {
            Configuration.CacheModPath = imageCachePath;
            plugin.imageHandler = new ImageHandler(imageCachePath);
            Configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.Button("Select Path....###imagecachepath"))
        {
            dialogManager.OpenFolderDialog("Pick image cache folder", FileDialogImageCallBack, string.Empty, true);
            _openFileDialog = true;
        }
        ImGui.Separator();
        ImGui.Text($"Current Image cache size:{StaticHelpers.CalculateFolderSizeInMB(Configuration.CacheImagePath):F2} MB");//:F2 disp up to 2 after float point
        ImGui.SameLine();
        if(ImGui.Button("Clear Image Cache")){
            StaticHelpers.ClearCacheFully(Configuration.CacheImagePath);
            plugin.imageHandler.ClearCache();
        }
        ImGui.Text($"Current Mod cache size:{StaticHelpers.CalculateFolderSizeInMB(Configuration.CacheModPath):F2} MB");
        ImGui.SameLine();
        if (ImGui.Button("Clear Mod Cache"))
        {
            StaticHelpers.ClearCacheFully(Configuration.CacheModPath);
            plugin.modHandler._downloadedFilenames.Clear();
        }
        ImGui.Text($"Current saved thumbnails size:{StaticHelpers.CalculateFolderSizeInMB(Configuration.ThumbnailsFolder):F2} MB");
        ImGui.SameLine();
        if (ImGui.Button("Clear thumbnails"))
        {
            Configuration.modNameToThumbnail = new Dictionary<string, string>();
            Configuration.Save();
            plugin.modHandler._modNameToThumbnail.Clear();
            plugin.modHandler._thumbnailToTextures.Clear();
            StaticHelpers.ClearCacheFully(Configuration.ThumbnailsFolder);
        }
    }
}
