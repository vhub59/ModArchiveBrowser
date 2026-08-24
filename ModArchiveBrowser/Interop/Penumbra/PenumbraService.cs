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
        private global::Penumbra.Api.IpcSubscribers.GetModDirectory? _getModDirectory;
        private global::Penumbra.Api.IpcSubscribers.OpenMainWindow? _openModPage;
        private global::Penumbra.Api.IpcSubscribers.InstallMod? _installMod;
        private global::Penumbra.Api.IpcSubscribers.GetCollections? _getCollections;
        private global::Penumbra.Api.IpcSubscribers.GetCollection? _getCollection;
        private global::Penumbra.Api.IpcSubscribers.TrySetMod? _trySetMod;
        private global::Penumbra.Api.IpcSubscribers.CopyModSettings? _copyModSettings;
        private global::Penumbra.Api.IpcSubscribers.DeleteMod? _deleteMod;
        private EventSubscriber<string>? _modAdded;
        private EventSubscriber<string, float, float>? _preSettingsTabBarDraw;

        //Collection dans laquelle activer les mods qui vont arriver, et jusqu'a quand.
        private Guid? _pendingCollection;
        private DateTime _pendingUntil;

        //Mod que le prochain installe vient remplacer. Consomme une seule fois, la ou l'activation
        //en collection vaut pour toute la fenetre : une mise a jour designe un mod precis.
        private string? _pendingReplace;
        private DateTime _replaceUntil;

        //Mod de XMA en cours d'installation. Vaut pour toute la fenetre, et c'est voulu : une
        //archive peut donner plusieurs dossiers, qui viennent tous de la meme page.
        private string? _pendingXmaId;
        private DateTime _xmaIdUntil;

        /// <summary>
        /// Signale le dossier cree par Penumbra pour un mod venu de XMA.
        ///
        /// Porte l'identifiant du mod et le dossier qui vient d'apparaitre : c'est le seul instant
        /// ou les deux sont connus ensemble.
        /// </summary>
        public event Action<string, string>? XmaModInstalled;

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

        /// <summary>Une collection de Penumbra : son identifiant et le nom que l'utilisateur lui a donne.</summary>
        public readonly record struct PenumbraCollection(Guid Id, string Name);

        /// <summary>
        /// Collections existantes, par ordre alphabetique.
        ///
        /// La collection vide en est exclue par Penumbra lui-meme, ce qui tombe bien : y activer
        /// un mod n'aurait aucun effet.
        /// </summary>
        public IReadOnlyList<PenumbraCollection> GetCollections()
        {
            if (!Available)
                return Array.Empty<PenumbraCollection>();

            try
            {
                return _getCollections!.Invoke()
                    .Select(pair => new PenumbraCollection(pair.Key, pair.Value))
                    .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase)
                    .ToList();
            }
            catch (Exception ex)
            {
                Plugin.Logger.Debug($"Could not read Penumbra collections:\n{ex}");
                return Array.Empty<PenumbraCollection>();
            }
        }

        /// <summary>
        /// Collection assignee au personnage du joueur, s'il y en a une.
        ///
        /// Sert de proposition par defaut : c'est celle qui s'applique a soi-meme, donc celle
        /// qu'on veut dans l'immense majorite des cas.
        /// </summary>
        public PenumbraCollection? YourCollection()
        {
            if (!Available)
                return null;

            try
            {
                var current = _getCollection!.Invoke(ApiCollectionType.Yourself);
                return current == null ? null : new PenumbraCollection(current.Value.Id, current.Value.Name);
            }
            catch (Exception ex)
            {
                Plugin.Logger.Debug($"Could not read the current collection:\n{ex}");
                return null;
            }
        }

        /// <summary>
        /// Demande que les mods installes dans les deux prochaines minutes soient actives dans
        /// cette collection.
        ///
        /// Penumbra n'installe pas "dans" une collection : un mod arrive dans le dossier commun,
        /// et chaque collection decide seulement s'il est actif. InstallMod ne rend d'ailleurs pas
        /// la main sur le mod cree — il ne fait que le mettre en file — et le nom du dossier n'est
        /// connu que lorsque l'evenement ModAdded le livre.
        ///
        /// D'ou cette attente plutot qu'un appel direct. Elle vaut pour toute la fenetre et non
        /// pour le premier mod venu : une archive XMA peut contenir plusieurs modpacks, que
        /// ModHandler installe l'un apres l'autre. Le risque est d'attraper au passage un mod que
        /// l'utilisateur installerait lui-meme dans le meme intervalle ; il serait alors active
        /// dans la collection qu'il vient de choisir ici, ce qui reste sans dommage.
        /// </summary>
        public void EnableComingInstalls(Guid collection)
        {
            _pendingCollection = collection;
            _pendingUntil = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        }

        /// <summary>Annule l'attente, quand l'installation n'a pas eu lieu.</summary>
        public void CancelComingInstalls()
        {
            _pendingCollection = null;
        }

        /// <summary>Nom du dernier mod active par ce biais, pour que l'interface puisse le dire.</summary>
        public string? LastEnabled { get; private set; }

        /// <summary>
        /// Retire un mod. Ses reglages survivent, ranges par Penumbra en "unused settings".
        ///
        /// C'est ce qui rend la mise a jour propre possible : on supprime avant d'installer, si
        /// bien que le nom de dossier se libere et que le nouveau mod le reprend tel quel.
        /// Installer d'abord laissait Penumbra suffixer le dossier — "Mod (2)" — et ce suffixe
        /// restait apres coup, alors que rien ne le justifiait plus.
        /// </summary>
        public PenumbraApiEc RemoveMod(string directory)
        {
            if (!Available || string.IsNullOrEmpty(directory))
                return PenumbraApiEc.UnknownError;

            try
            {
                return _deleteMod!.Invoke(directory, string.Empty);
            }
            catch (Exception ex)
            {
                Plugin.Logger.Debug($"Could not remove \"{directory}\":\n{ex}");
                return PenumbraApiEc.UnknownError;
            }
        }

        /// <summary>
        /// Demande que le prochain mod installe reprenne les reglages de celui-ci.
        ///
        /// A armer apres RemoveMod : quand le nouveau dossier porte le meme nom que l'ancien,
        /// Penumbra rattache ses reglages tout seul et il n'y a rien a faire. Le report n'est
        /// necessaire que si l'auteur a renomme son modpack entre deux versions.
        /// </summary>
        public void ReattachSettingsFrom(string oldDirectory)
        {
            _pendingReplace = oldDirectory;
            _replaceUntil = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        }

        /// <summary>
        /// Annonce de quel mod de XMA vient l'installation qui commence.
        ///
        /// Sans cela, un mod installe d'ici devient indiscernable des autres une fois dans
        /// Penumbra : le meta.json ne porte que ce que l'auteur du modpack y a mis, et il y met
        /// son Ko-fi ou son Patreon bien plus souvent que sa page XMA.
        /// </summary>
        public void NoteComingInstall(string xmaModId)
        {
            _pendingXmaId = xmaModId;
            _xmaIdUntil = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        }

        /// <summary>
        /// Vrai tant qu'un report de reglages attend le mod qui doit le declencher.
        ///
        /// Une mise a jour groupee doit les enchainer un par un : l'attente ne designe qu'un seul
        /// ancien mod, et lancer la suivante avant que ModAdded n'ait livre la precedente
        /// appliquerait les reglages du mauvais mod.
        /// </summary>
        public bool ReplacementPending => _pendingReplace != null;

        /// <summary>Abandonne le report en attente, quand le mod n'est jamais arrive.</summary>
        public void CancelComingReplacement() => _pendingReplace = null;

        /// <summary>Dernier remplacement abouti : ancien dossier vers nouveau.</summary>
        public (string From, string To)? LastReplacement { get; private set; }

        /// <summary>
        /// Rend au mod fraichement installe les reglages de celui qu'il remplace.
        ///
        /// Le plus souvent il n'y a rien a faire : l'ancien ayant ete supprime avant l'installation,
        /// le nouveau reprend son nom de dossier, et Penumbra lui rend ses reglages de lui-meme —
        /// c'est le propre des "unused settings", qu'il conserve precisement pour cela.
        ///
        /// Le report ne sert que lorsque le dossier a change de nom, l'auteur ayant renomme son
        /// modpack entre deux versions. CopyModSettings lit alors ces memes reglages orphelins :
        /// "if the source mod does not exist, it will use unused settings if available". Avec une
        /// collection nulle, l'operation couvre toutes les collections d'un coup, et Penumbra
        /// corrige au passage les options qui n'existent plus dans la nouvelle version.
        /// </summary>
        private void Replace(string oldDirectory, string newDirectory)
        {
            if (string.Equals(oldDirectory, newDirectory, StringComparison.Ordinal))
            {
                LastReplacement = (oldDirectory, newDirectory);
                return;
            }

            try
            {
                var copied = _copyModSettings!.Invoke(null, oldDirectory, newDirectory);
                if (copied != PenumbraApiEc.Success)
                {
                    Plugin.Logger.Warning(
                        $"\"{newDirectory}\" was installed, but the settings of \"{oldDirectory}\" could not be carried over ({copied}).");
                    return;
                }

                LastReplacement = (oldDirectory, newDirectory);
            }
            catch (Exception ex)
            {
                Plugin.Logger.Debug($"Could not carry settings from \"{oldDirectory}\":\n{ex}");
            }
        }

        private void OnModAdded(string modDirectory)
        {
            //Avant tout le reste : c'est le seul instant ou l'on sait de quelle page XMA vient ce
            //dossier, et l'information sert bien apres, a chaque verification de mise a jour.
            if (_pendingXmaId is { } xmaId)
            {
                if (DateTime.UtcNow <= _xmaIdUntil)
                    XmaModInstalled?.Invoke(xmaId, modDirectory);
                else
                    _pendingXmaId = null;
            }

            if (_pendingReplace is { } old)
            {
                var expired = DateTime.UtcNow > _replaceUntil;
                _pendingReplace = null;

                if (!expired)
                {
                    Replace(old, modDirectory);

                    //On s'arrete la. L'activation en collection forcerait ce mod a l'etat actif,
                    //alors que la copie des reglages vient justement de reproduire celui de la
                    //version precedente : un mod volontairement desactive se rallumerait a chaque
                    //mise a jour.
                    return;
                }
            }

            if (_pendingCollection is not { } collection)
                return;

            if (DateTime.UtcNow > _pendingUntil)
            {
                _pendingCollection = null;
                return;
            }

            try
            {
                //Le troisieme argument porte le nom "inherit" dans l'enveloppe IPC, mais il occupe
                //la place du parametre "enabled" de TrySetMod(collectionId, modDirectory, modName,
                //enabled) : c'est bien l'activation qu'il commande. Verifie par reflexion sur les
                //deux signatures, l'erreur passerait sinon la compilation sans un mot.
                //
                //Le nom du mod reste vide : le dossier suffit a l'identifier, et c'est la seule
                //des deux formes que ModAdded nous donne.
                var result = _trySetMod!.Invoke(collection, modDirectory, true, string.Empty);

                if (result is PenumbraApiEc.Success or PenumbraApiEc.NothingChanged)
                    LastEnabled = modDirectory;
                else
                    Plugin.Logger.Warning($"Could not enable \"{modDirectory}\" in the chosen collection: {result}.");
            }
            catch (Exception ex)
            {
                Plugin.Logger.Debug($"Could not enable \"{modDirectory}\":\n{ex}");
            }
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

        /// <summary>
        /// Dossier ou Penumbra range ses mods, ou une chaine vide s'il est indisponible.
        ///
        /// Chaque sous-dossier y porte un meta.json decrivant le mod : son nom, sa version, et le
        /// site dont il provient. C'est la seule source fiable pour savoir ce qui est deja
        /// installe — l'IPC ne renvoie que des noms, sans version ni origine.
        /// </summary>
        public string GetModDirectory()
        {
            if (!Available)
                return string.Empty;

            try
            {
                return _getModDirectory!.Invoke();
            }
            catch (Exception ex)
            {
                Plugin.Logger.Debug($"Could not read Penumbra's mod directory:\n{ex}");
                return string.Empty;
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

        /// <summary>
        /// Nom debarrasse de ses etiquettes "[...]", de ses qualificatifs "(...)" et de son
        /// numero de version.
        ///
        /// Le nom compare vient du fichier propose au telechargement, qui porte souvent sa
        /// version : "Bibo+ (Bibo+ Base Install) v3.1.5.pmp" face a "[HS] Bibo+ (Bibo+ Base
        /// Install)" deja installe. Sans retirer le "v3.1.5", les deux restaient distincts.
        ///
        /// Seules les formes non ambigues sont retirees — "v3", "V2.1", "1.0.4" — jamais un
        /// entier isole : "Adidas Superstar 2" doit rester different d'"Adidas Superstar".
        /// </summary>
        private static string BaseName(string? name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            var stripped = System.Text.RegularExpressions.Regex.Replace(name, @"\[[^\]]*\]|\([^\)]*\)", " ");
            stripped = System.Text.RegularExpressions.Regex.Replace(stripped, @"\b[vV]\d+(?:\.\d+)*\b", " ");
            stripped = System.Text.RegularExpressions.Regex.Replace(stripped, @"\b\d+(?:\.\d+)+\b", " ");

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
                _getModDirectory = new global::Penumbra.Api.IpcSubscribers.GetModDirectory(_pluginInterface);
                _openModPage = new global::Penumbra.Api.IpcSubscribers.OpenMainWindow(_pluginInterface);
                _installMod = new global::Penumbra.Api.IpcSubscribers.InstallMod(_pluginInterface);
                _getCollections = new global::Penumbra.Api.IpcSubscribers.GetCollections(_pluginInterface);
                _getCollection = new global::Penumbra.Api.IpcSubscribers.GetCollection(_pluginInterface);
                _trySetMod = new global::Penumbra.Api.IpcSubscribers.TrySetMod(_pluginInterface);
                _copyModSettings = new global::Penumbra.Api.IpcSubscribers.CopyModSettings(_pluginInterface);
                _deleteMod = new global::Penumbra.Api.IpcSubscribers.DeleteMod(_pluginInterface);

                //Seul moyen d'apprendre le nom du dossier d'un mod qu'on vient de faire installer :
                //InstallMod se contente de le mettre en file et ne rend rien.
                _modAdded = global::Penumbra.Api.IpcSubscribers.ModAdded.Subscriber(_pluginInterface, OnModAdded);
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
                _getModDirectory = null;
                _getCollections = null;
                _getCollection = null;
                _trySetMod = null;
                _copyModSettings = null;
                _deleteMod = null;

                _modAdded?.Dispose();
                _modAdded = null;

                //Une attente survivant au detachement s'appliquerait a la reconnexion suivante,
                //bien apres l'installation qui l'avait armee. Celle du remplacement plus encore :
                //elle supprimerait un mod sans rapport.
                _pendingCollection = null;
                _pendingReplace = null;
                _pendingXmaId = null;

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
