using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Penumbra.Api;
using Dalamud.Plugin;
using static FFXIVClientStructs.FFXIV.Client.Game.Character.Character.Delegates;
using Penumbra.Api.Enums;
using Penumbra.Api.Helpers;
using Penumbra.Api.IpcSubscribers;


namespace ModArchiveBrowser.Interop.Penumbra
{

    //trying to model this after Glamourer inplementation of PenumbraService
    //https://github.com/Ottermandias/Glamourer/blob/main/Glamourer/Interop/Penumbra/PenumbraService.cs
    //
    public class PenumbraService : IDisposable
    {
        public const int RequiredPenumbraBreakingVersion = 5;
        public const int RequiredPenumbraFeatureVersion = 0;

        private readonly IDalamudPluginInterface _pluginInterface;
        private global::Penumbra.Api.IpcSubscribers.GetModList? _getMods;
        private global::Penumbra.Api.IpcSubscribers.OpenMainWindow? _openModPage;
        private global::Penumbra.Api.IpcSubscribers.InstallMod? _installMod;
        private EventSubscriber<string, float, float>? _preSettingsTabBarDraw;

        private readonly IDisposable _initializedEvent;
        private readonly IDisposable _disposedEvent;

        private PenumbraWindowIntegration _windowIntegration;
        public bool Available { get; private set; }
        public int CurrentMajor { get; private set; }
        public int CurrentMinor { get; private set; }
        public DateTime AttachTime { get; private set; }
        public PenumbraService(IDalamudPluginInterface pi,Plugin plugin)
        {
            _pluginInterface = pi;
            _initializedEvent = global::Penumbra.Api.IpcSubscribers.Initialized.Subscriber(pi, Reattach);
            _disposedEvent = global::Penumbra.Api.IpcSubscribers.Disposed.Subscriber(pi, Unattach);
            _windowIntegration = new PenumbraWindowIntegration(plugin);
            _preSettingsTabBarDraw = global::Penumbra.Api.IpcSubscribers.PreSettingsTabBarDraw.Subscriber(pi,_windowIntegration.PreSettingsTabBarDraw);
            Reattach();
        }

        public PenumbraApiEc InstallMod(in string path)
        {
            if (!Available)
            {
                return PenumbraApiEc.UnknownError;
            }

            try
            {
                return(_installMod!.Invoke(path));
            }catch (Exception ex)
            {
                Plugin.Logger.Debug($"Could not queue mod for install:\n{ex}");
                return PenumbraApiEc.UnknownError;
            }
        }

        /// <summary>Mods déjà connus de Penumbra : répertoire -> nom affiché.</summary>
        public Dictionary<string, string> GetInstalledMods()
        {
            if (!Available)
                return new Dictionary<string, string>();

            try
            {
                return _getMods!.Invoke();
            }
            catch (Exception ex)
            {
                Plugin.Logger.Debug($"Could not read Penumbra mod list:\n{ex}");
                return new Dictionary<string, string>();
            }
        }

        /// <summary>
        /// Vrai si Penumbra connaît déjà un mod portant ce nom.
        ///
        /// Penumbra n'écrase pas et ne refuse pas : sur collision il crée un dossier suffixé
        /// "(2)", "(3)"... Sans cette vérification, chaque clic sur installer duplique le mod
        /// sur le disque — trois copies de 95 Mo pour un seul mod, constaté en test.
        /// </summary>
        public bool IsModInstalled(string modName)
        {
            if (string.IsNullOrWhiteSpace(modName))
                return false;

            return GetInstalledMods().Values
                .Any(name => string.Equals(name, modName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Cherche un mod deja installe qui ressemble a celui-ci, et renvoie son nom.
        ///
        /// L'egalite stricte des noms ne suffit pas. Un mod installe par un autre canal porte
        /// souvent un nom different : Heliosphere prefixe les siens de "[HS]", et les auteurs
        /// publient des variantes entre parentheses. "Bibo+ (DT Update)" et
        /// "[HS] Bibo+ (Bibo+ Base Install)" designent ainsi le meme mod sans partager un
        /// caractere de leur libelle.
        ///
        /// On compare donc sur le nom debarrasse de ses etiquettes entre crochets et de ses
        /// qualificatifs entre parentheses. C'est volontairement large : le resultat sert a
        /// prevenir, jamais a empecher, et un faux positif coute moins cher qu'un doublon de
        /// plusieurs centaines de megaoctets.
        /// </summary>
        public string? FindSimilarMod(string modName)
        {
            var target = BaseName(modName);
            if (string.IsNullOrEmpty(target))
                return null;

            foreach (var installed in GetInstalledMods().Values)
            {
                if (string.Equals(BaseName(installed), target, StringComparison.OrdinalIgnoreCase))
                    return installed;
            }

            return null;
        }

        /// <summary>Nom debarrasse de ses etiquettes "[...]" et de ses qualificatifs "(...)".</summary>
        private static string BaseName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            var stripped = System.Text.RegularExpressions.Regex.Replace(name, @"\[[^\]]*\]|\([^\)]*\)", " ");
            return System.Text.RegularExpressions.Regex.Replace(stripped, @"\s+", " ").Trim();
        }

        public PenumbraApiEc OpenModWindow()
        {
            if (!Available)
            {
                return PenumbraApiEc.UnknownError;
            }
            else
            {
                try
                {
                    return (_openModPage!.Invoke(TabType.Mods));
                }
                catch (Exception ex)
                {
                    Plugin.Logger.Debug($"Could not open mod window:\n{ex}");
                    return PenumbraApiEc.UnknownError;
                }
            }
        }


        
        /// <summary> Reattach to the currently running Penumbra IPC provider. Unattaches before if necessary. </summary>
        public void Reattach()
        {
            try
            {
                Unattach();

                AttachTime = DateTime.UtcNow;
                try
                {
                    (CurrentMajor, CurrentMinor) = new global::Penumbra.Api.IpcSubscribers.ApiVersion(_pluginInterface).Invoke();
                }
                catch
                {
                    try
                    {
                        (CurrentMajor, CurrentMinor) = new global::Penumbra.Api.IpcSubscribers.Legacy.ApiVersions(_pluginInterface).Invoke();
                    }
                    catch
                    {
                        CurrentMajor = 0;
                        CurrentMinor = 0;
                        throw;
                    }
                }

                if (CurrentMajor != RequiredPenumbraBreakingVersion || CurrentMinor < RequiredPenumbraFeatureVersion)
                    throw new Exception(
                        $"Invalid Version {CurrentMajor}.{CurrentMinor:D4}, required major Version {RequiredPenumbraBreakingVersion} with feature greater or equal to {RequiredPenumbraFeatureVersion}.");

                _getMods = new global::Penumbra.Api.IpcSubscribers.GetModList(_pluginInterface);
                _openModPage = new global::Penumbra.Api.IpcSubscribers.OpenMainWindow(_pluginInterface);
                _installMod = new global::Penumbra.Api.IpcSubscribers.InstallMod(_pluginInterface);
                //_preSettingsTabBarDraw = global::Penumbra.Api.IpcSubscribers.PreSettingsTabBarDraw.Subscriber(_pluginInterface, _windowIntegration.PreSettingsTabBarDraw);
                Available = true;
                Plugin.Logger.Debug("modarchivebrowser attached to Penumbra.");
            }
            catch (Exception e)
            {
                Unattach();
                Plugin.Logger.Debug($"Could not attach to Penumbra:\n{e}");
            }
        }

        /// <summary> Unattach from the currently running Penumbra IPC provider. </summary>
        public void Unattach()
        {
            if (Available)
            {
                _openModPage = null;
                _installMod = null;
                _getMods = null;
                Available = false;
                //_preSettingsTabBarDraw?.Dispose();
                Plugin.Logger.Debug("modarchivebrowser detached from Penumbra.");
            }
        }

        public void Dispose()
        {
            Unattach();
            _preSettingsTabBarDraw?.Dispose();
            _initializedEvent.Dispose();
            _disposedEvent.Dispose();
        }



    }
}
