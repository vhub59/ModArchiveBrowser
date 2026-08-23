using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ModArchiveBrowser.Utils
{
    /// <summary>
    /// Un mod installe pour lequel XMA propose une autre version.
    ///
    /// Directory est le nom du dossier de Penumbra : c'est par lui, et non par le nom affiche,
    /// que le mod se designe pour reporter ses reglages et le supprimer une fois remplace.
    /// </summary>
    public readonly record struct ModUpdate(string ModId, string Directory, string Name, string InstalledVersion, string PublishedVersion);

    /// <summary>
    /// Compare les mods installes a ce que XMA publie aujourd'hui.
    ///
    /// C'est le service qui manquait le plus. Heliosphere prévient ses utilisateurs quand un mod
    /// evolue ; sur XMA, personne ne l'est, et une bibliotheque vieillit sans qu'on le sache.
    ///
    /// Le cout reste proportionnel a la bibliotheque installee, pas au catalogue : Penumbra
    /// inscrit dans chaque meta.json l'adresse d'origine du mod, on sait donc lesquels viennent
    /// de XMA et avec quel identifiant. Quelques dizaines de requetes, la ou indexer tout le site
    /// en demanderait 52 000.
    /// </summary>
    public sealed class UpdateChecker
    {
        /// <summary>
        /// Delai entre deux requetes.
        ///
        /// Une bibliotheque fournie represente vite une centaine de pages : les enchainer sans
        /// pause ressemblerait a une attaque vue du serveur. Une demi-seconde reste imperceptible
        /// pour une verification qui tourne en fond.
        /// </summary>
        private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(500);

        private readonly Plugin _plugin;
        private CancellationTokenSource? _cancellation;

        public UpdateChecker(Plugin plugin) => _plugin = plugin;

        /// <summary>Mods pour lesquels une autre version est publiee.</summary>
        public IReadOnlyList<ModUpdate> Updates { get; private set; } = Array.Empty<ModUpdate>();

        public bool IsRunning { get; private set; }

        /// <summary>Nombre de mods deja verifies, et total a verifier.</summary>
        public int Checked { get; private set; }
        public int Total { get; private set; }

        /// <summary>Date de la derniere verification complete, ou null si aucune.</summary>
        public DateTime? LastRun { get; private set; }

        public void Start()
        {
            if (IsRunning)
                return;

            _cancellation?.Dispose();
            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;

            IsRunning = true;
            Checked = 0;

            Task.Run(async () =>
            {
                try
                {
                    await RunAsync(token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    //Arret demande : rien a signaler.
                }
                catch (Exception e)
                {
                    Plugin.Logger.Warning($"Update check failed: {e.Message}");
                }
                finally
                {
                    IsRunning = false;
                }
            }, token);
        }

        public void Cancel() => _cancellation?.Cancel();

        /// <summary>
        /// Retire un mod de la liste, une fois sa mise a jour appliquee.
        ///
        /// Sans cela il y resterait jusqu'a la verification suivante et paraitrait n'avoir pas ete
        /// traite, alors qu'il vient de l'etre.
        /// </summary>
        public void Forget(string modId)
            => Updates = Updates.Where(u => u.ModId != modId).ToList();

        private async Task RunAsync(CancellationToken token)
        {
            var installed = InstalledMods.Read(_plugin.penumbra.GetModDirectory())
                .Where(m => !string.IsNullOrEmpty(m.XmaModId))
                .ToList();

            Total = installed.Count;
            var found = new List<ModUpdate>();

            foreach (var mod in installed)
            {
                token.ThrowIfCancellationRequested();

                //L'historique JSON est prefere : 1,2 Ko contre 37 pour la page complete, et sa
                //premiere entree porte la version courante. Il est vide pour un mod jamais mis a
                //jour depuis sa publication ; on retombe alors sur la page.
                var history = WebClient.GetVersionHistory(mod.XmaModId!);
                var published = history.Count > 0 ? history[0].To : WebClient.GetModVersion(mod.XmaModId!);
                Checked++;

                if (!string.IsNullOrEmpty(published) && !SameVersion(mod.Version, published))
                    found.Add(new ModUpdate(mod.XmaModId!, mod.Directory, mod.Name, mod.Version, published));

                //On remet la liste a jour au fil de l'eau plutot qu'a la fin : sur une grosse
                //bibliotheque, l'utilisateur voit les resultats arriver au lieu d'attendre.
                Updates = found.ToList();

                if (Checked < Total)
                    await Task.Delay(Delay, token).ConfigureAwait(false);
            }

            LastRun = DateTime.Now;
        }

        /// <summary>
        /// Compare deux numeros de version en tolerant les zeros de fin.
        ///
        /// Penumbra enregistre "1.4.0" la ou XMA affiche "1.4" : une comparaison litterale
        /// signalerait une mise a jour a chaque verification, pour tous les mods.
        /// </summary>
        public static bool SameVersion(string? a, string? b)
        {
            var left = Split(a);
            var right = Split(b);
            var length = Math.Max(left.Length, right.Length);

            for (var i = 0; i < length; i++)
            {
                var x = i < left.Length ? left[i] : 0;
                var y = i < right.Length ? right[i] : 0;
                if (x != y)
                    return false;
            }

            return true;
        }

        private static int[] Split(string? version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return Array.Empty<int>();

            var match = System.Text.RegularExpressions.Regex.Match(version, @"([\d]+(?:\.[\d]+)*)");
            if (!match.Success)
                return Array.Empty<int>();

            return match.Groups[1].Value
                .Split('.')
                .Select(part => int.TryParse(part, out var value) ? value : 0)
                .ToArray();
        }
    }
}
