using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ModArchiveBrowser.Utils
{
    /// <summary>Ce qu'on sait d'un mod installe face a ce que publie XMA.</summary>
    public enum ModCheckState
    {
        /// <summary>Origine inconnue : on ne sait pas de quelle page il vient, donc rien a comparer.</summary>
        NotTracked,

        /// <summary>Rattache a une page, mais pas encore interroge.</summary>
        NotChecked,

        /// <summary>Interroge : la version publiee est celle qui est installee.</summary>
        UpToDate,

        /// <summary>Interroge : XMA publie une autre version.</summary>
        UpdateAvailable,

        /// <summary>Interroge sans reponse exploitable — page retiree, ou selecteur casse.</summary>
        Unreadable,
    }

    /// <summary>Un mod de la bibliotheque, avec son etat.</summary>
    public readonly record struct LibraryEntry(InstalledMod Mod, ModCheckState State, string PublishedVersion);

    /// <summary>
    /// Un mod installe pour lequel XMA propose une autre version.
    ///
    /// Directory est le nom du dossier de Penumbra : c'est par lui, et non par le nom affiche,
    /// que le mod se designe pour etre remplace et retrouver ses reglages.
    /// </summary>
    public readonly record struct ModUpdate(string ModId, string Directory, string Name, string InstalledVersion, string PublishedVersion);

    /// <summary>
    /// Compare les mods installes a ce que XMA publie aujourd'hui.
    ///
    /// C'est le service qui manquait le plus. Heliosphere prévient ses utilisateurs quand un mod
    /// evolue ; sur XMA, personne ne l'est, et une bibliotheque vieillit sans qu'on le sache.
    ///
    /// L'onglet montre la bibliotheque entiere et non le seul delta. Presenter la liste des mises
    /// a jour seule avait un defaut qu'on ne voyait pas venir : quand rien n'est rattache a une
    /// page XMA, l'ecran affiche "Everything is up to date" — un mensonge parfaitement credible.
    /// La liste complete rend la couverture visible : on voit d'un coup d'oeil combien de mods
    /// sont suivis, et combien ne le sont pas.
    ///
    /// Le cout reste proportionnel a la bibliotheque, pas au catalogue : seuls les mods rattaches
    /// sont interroges, soit quelques dizaines de requetes la ou indexer le site en demanderait
    /// 96 000.
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

        /// <summary>Tous les mods installes, avec leur etat.</summary>
        public IReadOnlyList<LibraryEntry> Library { get; private set; } = Array.Empty<LibraryEntry>();

        /// <summary>Mods pour lesquels une autre version est publiee.</summary>
        public IReadOnlyList<ModUpdate> Updates =>
            Library.Where(e => e.State == ModCheckState.UpdateAvailable)
                .Select(e => new ModUpdate(e.Mod.XmaModId!, e.Mod.Directory, e.Mod.Name, e.Mod.Version, e.PublishedVersion))
                .ToList();

        /// <summary>Mods dont on ignore l'origine, et qu'on ne peut donc pas verifier.</summary>
        public int UntrackedCount => Library.Count(e => e.State == ModCheckState.NotTracked);

        public bool IsRunning { get; private set; }

        /// <summary>Nombre de mods deja verifies, et total a verifier.</summary>
        public int Checked { get; private set; }
        public int Total { get; private set; }

        /// <summary>Date de la derniere verification complete, ou null si aucune.</summary>
        public DateTime? LastRun { get; private set; }

        /// <summary>
        /// Relit la bibliotheque depuis le disque, sans toucher au reseau.
        ///
        /// Assez rapide pour etre appelee a l'ouverture de l'onglet : elle ne fait que parcourir
        /// les meta.json du dossier de Penumbra. L'etat deja connu d'un mod est conserve tant que
        /// sa version installee n'a pas bouge, faute de quoi ouvrir l'onglet effacerait le
        /// resultat de la verification precedente.
        /// </summary>
        private DateTime _lastLibraryRead = DateTime.MinValue;

        /// <summary>
        /// Relit la bibliotheque si elle date, pour pouvoir etre appelee depuis la boucle de rendu.
        ///
        /// Lire quelques dizaines de meta.json soixante fois par seconde serait absurde ; ne les
        /// lire qu'une fois le serait tout autant, un mod installe pendant que l'onglet est ouvert
        /// n'apparaitrait jamais.
        /// </summary>
        public void RefreshLibraryIfStale()
        {
            if (IsRunning || DateTime.UtcNow - _lastLibraryRead < TimeSpan.FromSeconds(2))
                return;

            RefreshLibrary();
        }

        public void RefreshLibrary()
        {
            _lastLibraryRead = DateTime.UtcNow;

            var installed = InstalledMods.Read(
                _plugin.penumbra.GetModDirectory(), _plugin.Configuration.InstalledFromXma);

            var previous = new Dictionary<string, LibraryEntry>();
            foreach (var entry in Library)
                previous[entry.Mod.Directory] = entry;

            Library = installed.Select(mod =>
            {
                if (string.IsNullOrEmpty(mod.XmaModId))
                    return new LibraryEntry(mod, ModCheckState.NotTracked, string.Empty);

                if (previous.TryGetValue(mod.Directory, out var old)
                    && old.State != ModCheckState.NotTracked
                    && old.Mod.Version == mod.Version
                    && old.Mod.XmaModId == mod.XmaModId)
                    return new LibraryEntry(mod, old.State, old.PublishedVersion);

                return new LibraryEntry(mod, ModCheckState.NotChecked, string.Empty);
            }).ToList();
        }

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
        /// Marque un mod comme a jour, sa mise a jour venant d'etre appliquee.
        ///
        /// Sans cela il resterait signale jusqu'a la verification suivante et paraitrait n'avoir
        /// pas ete traite, alors qu'il vient de l'etre.
        /// </summary>
        public void Forget(string modId)
            => Library = Library.Select(e => e.Mod.XmaModId == modId
                ? e with { State = ModCheckState.UpToDate }
                : e).ToList();

        private async Task RunAsync(CancellationToken token)
        {
            RefreshLibrary();

            var toCheck = Library.Where(e => e.State != ModCheckState.NotTracked).ToList();
            Total = toCheck.Count;

            foreach (var entry in toCheck)
            {
                token.ThrowIfCancellationRequested();

                //L'historique JSON est prefere : 1,2 Ko contre 37 pour la page complete, et sa
                //premiere entree porte la version courante. Il est vide pour un mod jamais mis a
                //jour depuis sa publication ; on retombe alors sur la page.
                var history = WebClient.GetVersionHistory(entry.Mod.XmaModId!);
                var published = history.Count > 0 ? history[0].To : WebClient.GetModVersion(entry.Mod.XmaModId!);
                Checked++;

                var state = string.IsNullOrEmpty(published)
                    ? ModCheckState.Unreadable
                    : SameVersion(entry.Mod.Version, published)
                        ? ModCheckState.UpToDate
                        : ModCheckState.UpdateAvailable;

                //On remet la liste a jour au fil de l'eau plutot qu'a la fin : sur une grosse
                //bibliotheque, l'utilisateur voit les resultats arriver au lieu d'attendre.
                Apply(entry.Mod.Directory, state, published);

                if (Checked < Total)
                    await Task.Delay(Delay, token).ConfigureAwait(false);
            }

            LastRun = DateTime.Now;
        }

        private void Apply(string directory, ModCheckState state, string published)
            => Library = Library.Select(e => e.Mod.Directory == directory
                ? e with { State = state, PublishedVersion = published ?? string.Empty }
                : e).ToList();

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
