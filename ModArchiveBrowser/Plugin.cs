using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Plugin;
using System.IO;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using ModArchiveBrowser.Windows;
using HtmlAgilityPack;
using ModArchiveBrowser.Interop.Penumbra;
using Dalamud.Interface.ImGuiFileDialog;
using Dalamud.Utility;
using Dalamud.Game.Text.SeStringHandling;
using System;
using ModArchiveBrowser.Utils;

namespace ModArchiveBrowser;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Logger { get; private set; } = null!;

    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;

    public Configuration Configuration { get; init; }

    public readonly WindowSystem WindowSystem = new("ModArchiveBrowser");

    public readonly PenumbraService penumbra;
    private ConfigWindow ConfigWindow { get; init; }
    public MainWindow MainWindow { get; init; }

    public SearchWindow searchWindow { get; init; }
    public ModWindow modWindow { get; init; }

    /// <summary>Compare les mods installes a ce que XMA publie.</summary>
    public readonly UpdateChecker updateChecker;

    public ImageHandler imageHandler = null!;
    public ModHandler modHandler = null!;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        //La session anonyme XMA n'est ouverte que si l'utilisateur a donné son accord pour le
        //contenu adulte. Sans elle, XMA répond 403 sur ces pages : le filtrage tient côté serveur.
        //On n'attend pas : la session sera prête bien avant la première recherche.
        if (Configuration.AllowNsfw)
            _ = XmaSession.EnsureAsync();

        // you might normally want to embed resources and load them from the manifest stream
        //var goatImagePath = Path.Combine(PluginInterface.AssemblyLocation.Directory?.FullName!, "goat.png");
        imageHandler = new ImageHandler(Configuration.CacheImagePath);
        modHandler = new ModHandler(Configuration.CacheModPath,Configuration.ThumbnailsFolder,this);
        ConfigWindow = new ConfigWindow(this);
        modWindow = new ModWindow(this);
        MainWindow = new MainWindow(this);
        searchWindow = new SearchWindow(this);
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(MainWindow);
        WindowSystem.AddWindow(modWindow);
        WindowSystem.AddWindow(searchWindow);
        penumbra = new PenumbraService(PluginInterface,this);
        updateChecker = new UpdateChecker(this);

        CommandManager.AddHandler("/archive", new CommandInfo(OnCommand)
        {
            HelpMessage = "Display the homepage"
        });
        CommandManager.AddHandler("/modsearch", new CommandInfo(OnCommand)
        {
            HelpMessage = "Display the search page"
        });
        CommandManager.AddHandler("/archiveconfig", new CommandInfo(OnCommand)
        {
            HelpMessage = "Display the config page"
        });
        CommandManager.AddHandler("/modid", new CommandInfo(OnCommand)
        {
            HelpMessage = "Manually display the corresponding mod in the mod window"
        });
        PluginInterface.UiBuilder.Draw += DrawUI;

        // This adds a button to the plugin installer entry of this plugin which allows
        // to toggle the display status of the configuration ui
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUI;

        // Adds another button that is doing the same but for the main ui of the plugin
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUI;
    }

    public static void ReportError(string msg,Exception? ex)
    {
        SeStringBuilder sb = new SeStringBuilder().AddText("[ModArchiveBrowser] Error:"+msg);
        ChatGui.PrintError(sb.BuiltString);
        if (ex is not null)
        {
            Plugin.Logger.Error(ex.ToString());
        }
    }

    public void Dispose()
    {
        WindowSystem.RemoveAllWindows();

        ConfigWindow.Dispose();
        MainWindow.Dispose();
        modWindow.Dispose();
        searchWindow.Dispose();
        CommandManager.RemoveHandler("/archive");
        CommandManager.RemoveHandler("/modsearch");
        CommandManager.RemoveHandler("/archiveconfig");
        CommandManager.RemoveHandler("/modid");
        modHandler.Dispose();
        imageHandler.Dispose();
        penumbra.Dispose();
    }

    private void OnCommand(string command, string args)
    {
        switch(command)
        {
            case "/archive":
                //On revient toujours sur la grille : rester sur une fiche consultee il y a une
                //heure serait deroutant.
                MainWindow.CurrentTarget = Utils.NavTarget.Home;
                MainWindow.ShowingMod = false;
                MainWindow.Toggle();
                break;
            //La recherche n'a plus sa propre fenetre : la commande ouvre la fenetre principale
            //sur l'onglet correspondant.
            case "/modsearch":
                MainWindow.CurrentTarget = Utils.NavTarget.Search;
                MainWindow.ShowingMod = false;
                MainWindow.IsOpen = true;
                MainWindow.BringToFront();
                break;
            case "/archiveconfig":ConfigWindow.Toggle();break;
            //La fiche n'a plus sa propre fenetre : la commande ouvre la fenetre principale
            //directement sur le mod demande.
            case "/modid": if (!args.IsNullOrEmpty())
                {
                    modWindow.ChangeMod(args);
                    MainWindow.ShowingMod = true;
                    MainWindow.IsOpen = true;
                    MainWindow.BringToFront();
                }
                else
                {
                    ReportError("No argument",null);
                }
                break;
            default:break;
        }
    }

    private void DrawUI() => WindowSystem.Draw();

    public void ToggleConfigUI() => ConfigWindow.Toggle();
    public void ToggleMainUI() => MainWindow.Toggle();
}
