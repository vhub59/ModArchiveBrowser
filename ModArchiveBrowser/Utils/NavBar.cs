using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;

namespace ModArchiveBrowser.Utils
{
    /// <summary>Vue actuellement affichée, pour que la barre sache quoi mettre en avant.</summary>
    public enum NavTarget
    {
        Home,
        Search,
        Trending,
        Newest,
        Sponsored,
        Updates,
    }

    /// <summary>
    /// Barre de navigation commune à la page d'accueil et à la recherche.
    ///
    /// Les deux fenêtres se ressemblaient au point qu'on ne savait plus laquelle on regardait :
    /// même grille, aucun titre de vue, aucun bouton mis en avant, et un simple clic sur
    /// "Trending" fermait l'accueil pour ouvrir la recherche sans que rien ne le signale.
    /// La barre est desormais identique partout, la vue courante y est surlignée, et une ligne
    /// de contexte rappelle ce qu'on est en train de regarder.
    /// </summary>
    public static class NavBar
    {
        public static void Draw(Plugin plugin, NavTarget current)
        {
            Tab(plugin, current, NavTarget.Home, FontAwesomeIcon.Home, "Home", "The archive homepage");

            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();

            Tab(plugin, current, NavTarget.Search, FontAwesomeIcon.Search, "Search", "Search the archive with filters");
            ImGui.SameLine();
            Tab(plugin, current, NavTarget.Trending, FontAwesomeIcon.FireAlt, "Trending", "Today's most viewed mods");
            ImGui.SameLine();
            Tab(plugin, current, NavTarget.Newest, FontAwesomeIcon.Certificate, "Newest", "Newest mods from all users");
            ImGui.SameLine();
            Tab(plugin, current, NavTarget.Sponsored, FontAwesomeIcon.Star, "Sponsored", "New and updated mods from Patreon subscribers");

            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();

            //Le nombre de mises a jour figure dans le libelle : c'est l'information qu'on veut
            //voir sans avoir a ouvrir l'onglet.
            var pending = plugin.updateChecker.Updates.Count;
            Tab(plugin, current, NavTarget.Updates, FontAwesomeIcon.SyncAlt,
                pending > 0 ? $"Updates ({pending})" : "Updates",
                "Compare your installed mods with what xivmodarchive publishes today");

            //Les deux filtres de la grille, alignes a droite. Le second est calcule en premier
            //pour connaitre la largeur a reserver : ImGui.SameLine positionne a partir du bord,
            //il faut donc savoir ou s'arrete le voisin de droite avant de placer celui de gauche.
            var adultWidth = DrawAdultToggle(plugin, current);
            DrawAvailabilityToggle(plugin, adultWidth);
        }

        /// <summary>
        /// Retire de la grille ce qu'on sait ne pas pouvoir installer.
        ///
        /// Le filtre ne peut pas etre pose cote XMA : le site n'expose l'hebergeur du fichier que
        /// sur la page d'un mod, jamais dans ses resultats. Il agit donc sur ce que le
        /// prechargement a appris de la page affichee, et laisse passer tout ce qui reste inconnu.
        /// Une carte peut ainsi disparaitre une seconde apres etre apparue — le contraire
        /// supposerait de bloquer l'affichage le temps d'interroger quinze pages.
        /// </summary>
        private static void DrawAvailabilityToggle(Plugin plugin, float rightOffset)
        {
            var config = plugin.Configuration;
            var enabled = config.HideUnavailable;

            var icon = enabled ? FontAwesomeIcon.Filter : FontAwesomeIcon.Bars;
            var label = enabled ? "Installable" : "All mods";

            var width = ImGui.CalcTextSize(label).X + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 3f;
            ImGui.SameLine(ImGui.GetContentRegionMax().X - rightOffset - width - ImGui.GetStyle().ItemSpacing.X);

            using (enabled ? Theme.Emphasis(Theme.Accent, Theme.AccentHovered) : Theme.Emphasis(Neutral, NeutralHovered))
            {
                if (ImGuiComponents.IconButtonWithText(icon, label))
                {
                    config.HideUnavailable = !enabled;
                    config.Save();
                }
            }

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(enabled
                    ? "Mods known to be hosted elsewhere, or in a format Penumbra cannot read, are hidden.\n" +
                      "Mods not yet checked stay visible: availability is only known once a page has been read."
                    : "Showing everything, including mods that cannot be installed from here.\n" +
                      "Their badge says where they live.");
        }

        /// <summary>
        /// Ligne de contexte : nom de la vue, puis nombre d'éléments et page en gris.
        /// C'est la reponse a "on ne sait meme pas ou on est".
        /// </summary>
        public static void Context(string title, int total, int shown = 0, int checking = 0)
        {
            ImGui.TextUnformatted(title);
            ImGui.SameLine();

            var detail = total switch
            {
                0 => "no mods",
                1 => "1 mod",
                _ => $"{total:N0} mods",
            };

            //On annonce le total de la recherche et ce qui est charge a l'ecran. Afficher le seul
            //contenu de la page ("15 mods") pour une recherche en comptant 60 521 laissait croire
            //que le filtre avait tout balaye.
            if (shown > 0 && shown < total)
                detail += $"  ·  showing {shown:N0}";

            //Le prechargement des pastilles est signale : sans cela, on les voit apparaitre une
            //a une sans comprendre pourquoi.
            if (checking > 0)
                detail += $"  ·  checking {checking}";

            ImGui.TextDisabled($"·  {detail}");
        }

        /// <summary>
        /// Contenu adulte : un interrupteur, deux etats.
        ///
        /// Un cycle a trois etats avait ete tente, et c'etait une mauvaise idee : il fallait
        /// cliquer pour decouvrir ce qu'il faisait, rien n'indiquait combien d'etats existaient
        /// ni dans quel ordre. Le genre de controle qu'on trouve malin en l'ecrivant et penible
        /// a l'usage.
        ///
        /// La barre ne porte donc plus que la question qu'on se pose souvent — voir ces mods ou
        /// non. Le degre de masquage est un reglage, dans la configuration ; le choix fin entre
        /// melanges, exclusifs et exclus reste dans les options de recherche, ou les trois
        /// libelles se lisent d'un coup d'oeil sans avoir a cliquer.
        /// </summary>
        /// <returns>Largeur occupee, pour que le filtre voisin sache ou se placer.</returns>
        private static float DrawAdultToggle(Plugin plugin, NavTarget current)
        {
            var config = plugin.Configuration;

            //La page d'accueil est la vitrine de XMA, servie telle quelle : elle n'accepte aucun
            //parametre de recherche, donc aucun filtre.
            var applicable = current != NavTarget.Home;
            var enabled = config.AllowNsfw;

            var icon = enabled ? FontAwesomeIcon.Eye : FontAwesomeIcon.EyeSlash;
            var label = enabled ? "Adult on" : "Adult off";

            var width = ImGui.CalcTextSize(label).X + ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.X * 3f;
            ImGui.SameLine(ImGui.GetContentRegionMax().X - width);

            using (ImRaii.Disabled(!applicable))
            using (enabled ? Theme.Emphasis(Warm, WarmHovered) : Theme.Emphasis(Neutral, NeutralHovered))
            {
                if (ImGuiComponents.IconButtonWithText(icon, label))
                    Toggle(plugin, !enabled);
            }

            if (ImGui.IsItemHovered())
            {
                var obscured = config.BlurAdultThumbnails
                    ? "Their thumbnails stay obscured until hovered."
                    : "Their thumbnails are shown as-is.";

                ImGui.SetTooltip(!applicable
                    ? "The homepage is served by xivmodarchive as-is and takes no filter.\nUse Trending, Newest or Search to browse adult mods."
                    : enabled
                        ? $"Adult mods are mixed in with the rest.\n{obscured}"
                        : "Adult mods are left out entirely.\nWhile off, xivmodarchive returns 403 for them: they cannot be browsed or installed at all.");
            }

            return width;
        }

        private static void Toggle(Plugin plugin, bool enabled)
        {
            var config = plugin.Configuration;
            config.AllowNsfw = enabled;
            config.Save();

            if (enabled)
            {
                _ = XmaSession.EnsureAsync();
            }
            else
            {
                XmaSession.Close();
                //Sans cette purge, les pages adultes deja consultees seraient resservies depuis
                //le cache disque sans jamais repasser par le 403 de XMA.
                WebClient.ClearHtmlCache();
            }

            plugin.searchWindow.ApplyAdultMode(enabled);
        }

        private static readonly Vector4 Warm = new(0.72f, 0.31f, 0.44f, 1f);
        private static readonly Vector4 WarmHovered = new(0.80f, 0.38f, 0.51f, 1f);
        private static readonly Vector4 Neutral = new(0.18f, 0.19f, 0.22f, 1f);
        private static readonly Vector4 NeutralHovered = new(0.26f, 0.28f, 0.32f, 1f);

        private static void Tab(Plugin plugin, NavTarget current, NavTarget target, FontAwesomeIcon icon, string label, string tooltip)
        {
            var active = current == target;

            //Le bouton actif reprend la couleur d'un bouton enfonce : c'est le seul repere visuel
            //qui indique la vue courante, les deux fenetres etant par ailleurs identiques.
            if (active)
            {
                var accent = ImGui.GetStyle().Colors[(int)ImGuiCol.ButtonActive];
                ImGui.PushStyleColor(ImGuiCol.Button, accent);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, accent);
            }

            //Icone plus libelle : le pictogramme se repere d'un coup d'oeil, le mot leve
            //l'ambiguite. Un onglet purement graphique obligerait a survoler pour comprendre.
            if (ImGuiComponents.IconButtonWithText(icon, label) && !active)
                Navigate(plugin, target);

            if (active)
                ImGui.PopStyleColor(2);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(tooltip);
        }

        /// <summary>
        /// Change de vue sans changer de fenêtre.
        ///
        /// Auparavant chaque onglet fermait la fenêtre courante pour en ouvrir une autre : cliquer
        /// sur "Trending" faisait disparaître l'accueil au profit d'une seconde fenêtre d'aspect
        /// identique, sans que rien ne l'annonce. Trending, Newest et Sponsored ne sont pourtant
        /// pas des destinations, seulement des listes ; elles s'affichent maintenant au même
        /// endroit, comme des onglets.
        /// </summary>
        private static void Navigate(Plugin plugin, NavTarget target)
        {
            plugin.MainWindow.IsOpen = true;
            plugin.MainWindow.BringToFront();
            plugin.MainWindow.CurrentTarget = target;

            if (target == NavTarget.Updates)
                return;

            var preset = target switch
            {
                NavTarget.Trending => WebClient.today_most_viewed,
                NavTarget.Newest => WebClient.newest_mods_from_all_users,
                NavTarget.Sponsored => WebClient.new_and_updated_from_patreon_subs,
                _ => null,
            };

            if (preset != null)
            {
                Plugin.Logger.Debug(preset);
                plugin.searchWindow.UpdateSearch(preset);
            }
        }

        /// <summary>Intitulé lisible d'une vue, pour la ligne de contexte.</summary>
        public static string TitleOf(NavTarget target) => target switch
        {
            NavTarget.Home => "Homepage",
            NavTarget.Updates => "Updates",
            NavTarget.Trending => "Trending today",
            NavTarget.Newest => "Newest mods",
            NavTarget.Sponsored => "Sponsored mods",
            _ => "Search results",
        };
    }
}
