using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Penumbra.Api.Enums;

namespace ModArchiveBrowser.Utils
{
    /// <summary>Un mod que la mise a jour groupee n'a pas pu traiter, et pourquoi.</summary>
    public readonly record struct SkippedUpdate(string Name, string Reason);

    /// <summary>
    /// Applique les mises a jour detectees, en remplacant chaque mod plutot qu'en l'empilant.
    ///
    /// Detecter les mises a jour ne servait a rien tant qu'il fallait ouvrir vingt fiches pour les
    /// appliquer, et le bouton d'une fiche ne fait qu'installer : Penumbra ne remplace pas, il
    /// cree un dossier "Mod (2)" et laisse l'ancien actif. Une bibliotheque "mise a jour" de cette
    /// facon contient donc les deux versions, dont l'ancienne toujours en service.
    ///
    /// Le remplacement en place tient a trois appels enchaines — installer, reporter les reglages,
    /// supprimer l'ancien — qui vivent dans PenumbraService. Ici on ne fait que les ordonner, un
    /// mod a la fois : l'attente de Penumbra ne designe qu'un seul ancien mod, et deux
    /// installations concurrentes feraient supprimer le mauvais.
    ///
    /// Ce qui ne peut pas etre traite est nomme plutot que passe sous silence. Un tiers du
    /// catalogue est heberge ailleurs : annoncer "tout est a jour" apres avoir ignore ces mods
    /// serait faux, et c'est le genre de mensonge qu'on ne remarque que des mois plus tard.
    /// </summary>
    public sealed class UpdateInstaller
    {
        /// <summary>Delai entre deux mods, pour ne pas enchainer les requetes sans respirer.</summary>
        private static readonly TimeSpan Spacing = TimeSpan.FromMilliseconds(500);

        /// <summary>
        /// Temps laisse a Penumbra pour depaqueter un mod et l'annoncer.
        ///
        /// Genereux a dessein : un modpack de deux cents megaoctets prend son temps, et abandonner
        /// trop tot ferait passer le remplacement a la mise a jour suivante — donc supprimerait le
        /// mauvais mod.
        /// </summary>
        private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(2);

        private readonly Plugin _plugin;
        private CancellationTokenSource? _cancellation;

        public UpdateInstaller(Plugin plugin) => _plugin = plugin;

        public bool IsRunning { get; private set; }

        /// <summary>Mod en cours de traitement, pour que l'interface puisse le nommer.</summary>
        public string Current { get; private set; } = string.Empty;

        public int Done { get; private set; }
        public int Total { get; private set; }

        /// <summary>Mods effectivement remplaces lors du dernier passage.</summary>
        public int Updated { get; private set; }

        /// <summary>Ce qui n'a pas pu etre traite, et la raison.</summary>
        public IReadOnlyList<SkippedUpdate> Skipped { get; private set; } = Array.Empty<SkippedUpdate>();

        /// <summary>Vrai quand un passage s'est termine, pour distinguer "rien fait" de "rien a faire".</summary>
        public bool HasRun { get; private set; }

        public void Cancel() => _cancellation?.Cancel();

        public void Start(IEnumerable<ModUpdate> updates)
        {
            if (IsRunning || !_plugin.penumbra.Available)
                return;

            var queue = updates.ToList();
            if (queue.Count == 0)
                return;

            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;

            IsRunning = true;
            Done = 0;
            Updated = 0;
            Total = queue.Count;
            Skipped = Array.Empty<SkippedUpdate>();

            Task.Run(async () =>
            {
                var skipped = new List<SkippedUpdate>();

                try
                {
                    foreach (var update in queue)
                    {
                        token.ThrowIfCancellationRequested();

                        Current = update.Name;
                        var reason = await ApplyAsync(update, token).ConfigureAwait(false);

                        if (reason == null)
                        {
                            Updated++;
                            //Le mod disparait de la liste des mises a jour en attente : sans cela
                            //il y resterait jusqu'a la prochaine verification complete, et
                            //paraitrait n'avoir pas ete traite.
                            _plugin.updateChecker.Forget(update.ModId);
                        }
                        else
                        {
                            skipped.Add(new SkippedUpdate(update.Name, reason));
                        }

                        Done++;
                        Skipped = skipped.ToList();

                        if (Done < Total)
                            await Task.Delay(Spacing, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    //Arret demande : ce qui a ete fait le reste, le reste attendra.
                }
                catch (Exception e)
                {
                    Plugin.Logger.Warning($"Batch update stopped: {e.Message}");
                }
                finally
                {
                    //Une attente laissee ouverte s'appliquerait au prochain mod que l'utilisateur
                    //installerait lui-meme, et supprimerait un mod sans rapport.
                    _plugin.penumbra.CancelComingReplacement();

                    Skipped = skipped.ToList();
                    Current = string.Empty;
                    IsRunning = false;
                    HasRun = true;
                }
            }, token);
        }

        /// <summary>
        /// Traite un mod. Renvoie null en cas de succes, sinon la raison de l'echec.
        /// </summary>
        private async Task<string?> ApplyAsync(ModUpdate update, CancellationToken token)
        {
            var facts = WebClient.GetModFacts(update.ModId);

            var reason = WhyNotUpdatable(facts.DownloadUrl);
            if (reason != null)
                return reason;

            var path = await _plugin.modHandler
                .DownloadModAsync(WebClient.xivmodarchiveRoot + facts.DownloadUrl)
                .ConfigureAwait(false);

            //Le fichier doit etre la, entier, avant qu'on ne touche a l'installation existante :
            //c'est ce qui separe une mise a jour d'une perte seche.
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || new FileInfo(path).Length == 0)
                return "the download failed";

            //L'ancien part avant que le nouveau n'arrive. Penumbra ne remplace pas : installer
            //d'abord lui fait creer un dossier suffixe — "Mod (2)" — et ce suffixe restait ensuite
            //pour toujours, alors que plus rien ne le justifiait.
            //
            //Les reglages ne sont pas perdus pour autant : Penumbra les conserve en "unused
            //settings", ranges sous le nom du dossier, et les rend au mod qui reprend ce nom.
            var removed = _plugin.penumbra.RemoveMod(update.Directory);
            if (removed != PenumbraApiEc.Success)
                return $"the old version could not be removed ({removed})";

            //Arme avant l'installation : Penumbra annonce le mod des qu'il l'a depaquete, ce qui
            //peut arriver avant qu'InstallMod nous ait rendu la main.
            _plugin.penumbra.ReattachSettingsFrom(update.Directory);

            //Le nouveau dossier doit heriter du lien vers XMA, sans quoi le mod ne serait plus
            //verifiable des la mise a jour suivante.
            _plugin.penumbra.NoteComingInstall(update.ModId);

            var thumbnail = string.IsNullOrEmpty(facts.ThumbUrl)
                ? string.Empty
                : _plugin.imageHandler.GetImage(facts.ThumbUrl);

            _plugin.modHandler.InstallMod(path, thumbnail, replacing: true);

            return await WaitForReplacement(token).ConfigureAwait(false);
        }

        /// <summary>
        /// Attend que Penumbra ait annonce le mod et que le remplacement soit fait.
        ///
        /// Sans cette attente, la mise a jour suivante armerait sa propre substitution avant que
        /// celle-ci n'ait ete consommee : le mod annonce par Penumbra serait alors rapporte au
        /// mauvais ancien, qui serait supprime a sa place.
        /// </summary>
        private async Task<string?> WaitForReplacement(CancellationToken token)
        {
            var deadline = DateTime.UtcNow + InstallTimeout;

            while (_plugin.penumbra.ReplacementPending)
            {
                if (DateTime.UtcNow > deadline)
                {
                    _plugin.penumbra.CancelComingReplacement();

                    //L'ancienne version a deja ete retiree a ce stade. On le dit franchement : le
                    //mod est absent de Penumbra, et l'utilisateur doit savoir qu'il lui manque
                    //quelque chose plutot que de le decouvrir en jeu.
                    return "Penumbra did not report the mod in time — the old version was already removed, reinstall it from its page";
                }

                await Task.Delay(200, token).ConfigureAwait(false);
            }

            return null;
        }

        /// <summary>
        /// Pourquoi ce mod ne peut pas etre mis a jour sans intervention, ou null s'il le peut.
        ///
        /// Seuls les modpacks hebergés par XMA sont traitables. Une archive est ecartee bien
        /// qu'elle soit installable a la main : elle peut contenir plusieurs modpacks, et lequel
        /// remplace l'ancien mod ne se devine pas — se tromper le supprimerait, et la suppression
        /// est irreversible.
        /// </summary>
        public static string? WhyNotUpdatable(string? downloadUrl)
        {
            if (string.IsNullOrEmpty(downloadUrl))
                return "its page could not be read";

            return Classify(downloadUrl) switch
            {
                ModAvailability.Installable => null,
                ModAvailability.Archive => "it is an archive: update it from its page",
                ModAvailability.Heliosphere => "it is published on Heliosphere",
                ModAvailability.External => $"it is hosted on {HostOf(downloadUrl)}",
                _ => "its file is not something Penumbra can read",
            };
        }

        /// <summary>Reutilise la classification de l'index, qui connait deja les cas de figure.</summary>
        private static ModAvailability Classify(string downloadUrl)
        {
            var probe = new Configuration();
            AvailabilityIndex.Record(probe, "/modid/1", downloadUrl, save: false);
            return AvailabilityIndex.Get(probe, "/modid/1");
        }

        private static string HostOf(string url)
        {
            try
            {
                var host = new Uri(url).Host.Replace("www.", string.Empty);
                return string.IsNullOrEmpty(host) ? "another site" : host;
            }
            catch
            {
                return "another site";
            }
        }
    }
}
