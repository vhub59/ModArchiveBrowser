using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace ModArchiveBrowser.Utils
{
    /// <summary>
    /// Renseigne l'installabilité des mods affichés, avant qu'on ne clique dessus.
    ///
    /// L'index construit a l'usage avait un defaut de conception : la pastille n'apparaissait
    /// qu'apres avoir ouvert un mod, donc exactement quand elle ne servait plus a rien. Elle est
    /// pourtant faite pour eviter d'ouvrir ce qu'on ne pourra pas installer.
    ///
    /// XMA n'expose cette information nulle part ailleurs que sur la page d'un mod : ni la page de
    /// resultats, ni ses endpoints JSON — verifies un par un — ne mentionnent l'hebergeur. Il faut
    /// donc charger une page par carte. Mise en regard, la depense est modeste : annoter une
    /// grille de trente cartes represente environ un megaoctet, quand un seul mod installe pese
    /// entre trente et deux cents. On peut annoter deux cents grilles pour le prix d'un mod.
    ///
    /// Le nombre de requetes, lui, merite de la retenue : elles sont espacees, et changer de page
    /// annule le chargement de la precedente plutot que de le poursuivre dans le vide.
    /// </summary>
    public sealed class AvailabilityPrefetcher
    {
        private static readonly TimeSpan Delay = TimeSpan.FromMilliseconds(250);

        private readonly Plugin _plugin;
        private CancellationTokenSource? _cancellation;

        public AvailabilityPrefetcher(Plugin plugin) => _plugin = plugin;

        /// <summary>Mods encore a verifier dans la page courante.</summary>
        public int Pending { get; private set; }

        /// <summary>
        /// Renseigne les mods de cette page, en ignorant ceux deja connus.
        ///
        /// Remplace le chargement en cours : quand l'utilisateur tourne la page, les cartes qu'il
        /// vient de quitter n'ont plus d'interet.
        /// </summary>
        public void Prefetch(IEnumerable<ModThumb>? thumbs)
        {
            Cancel();

            if (thumbs == null)
                return;

            var pending = thumbs
                .Select(t => AvailabilityIndex.ModIdFromUrl(t.url))
                .Where(id => id != null && !_plugin.Configuration.KnownAvailability.ContainsKey(id!))
                .Distinct()
                .ToList();

            if (pending.Count == 0)
            {
                Pending = 0;
                return;
            }

            _cancellation = new CancellationTokenSource();
            var token = _cancellation.Token;
            Pending = pending.Count;

            Task.Run(async () =>
            {
                var learned = false;

                try
                {
                    foreach (var id in pending)
                    {
                        token.ThrowIfCancellationRequested();

                        var facts = WebClient.GetModFacts(id!);

                        if (AvailabilityIndex.Record(
                                _plugin.Configuration,
                                $"/modid/{id}",
                                facts.DownloadUrl,
                                save: false,
                                adult: facts.IsAdult))
                            learned = true;

                        Pending--;
                        await Task.Delay(Delay, token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    //Page quittee : rien a signaler.
                }
                catch (Exception e)
                {
                    Plugin.Logger.Debug($"Availability prefetch stopped: {e.Message}");
                }
                finally
                {
                    Pending = 0;

                    //Une seule ecriture pour toute la page, au lieu d'une par carte.
                    if (learned)
                        _plugin.Configuration.Save();
                }
            }, token);
        }

        public void Cancel()
        {
            _cancellation?.Cancel();
            _cancellation?.Dispose();
            _cancellation = null;
            Pending = 0;
        }
    }
}
