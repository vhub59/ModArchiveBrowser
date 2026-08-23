using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;

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
            Tab(plugin, current, NavTarget.Home, "Home", "The archive homepage");

            ImGui.SameLine();
            ImGui.TextDisabled("|");
            ImGui.SameLine();

            Tab(plugin, current, NavTarget.Search, "Search", "Search the archive with filters");
            ImGui.SameLine();
            Tab(plugin, current, NavTarget.Trending, "Trending", "Today's most viewed mods");
            ImGui.SameLine();
            Tab(plugin, current, NavTarget.Newest, "Newest", "Newest mods from all users");
            ImGui.SameLine();
            Tab(plugin, current, NavTarget.Sponsored, "Sponsored", "New and updated mods from Patreon subscribers");
        }

        /// <summary>
        /// Ligne de contexte : nom de la vue, puis nombre d'éléments et page en gris.
        /// C'est la reponse a "on ne sait meme pas ou on est".
        /// </summary>
        public static void Context(string title, int count, int page = 0, int pageCount = 0)
        {
            ImGui.TextUnformatted(title);

            ImGui.SameLine();
            var detail = count switch
            {
                0 => "no mods",
                1 => "1 mod",
                _ => $"{count:N0} mods",
            };

            //Le total de la recherche, pas le contenu de la page : afficher "15 mods" pour une
            //recherche en comptant 1854 laissait croire que le filtre avait tout balaye.
            if (page > 0)
                detail += pageCount > 0 ? $"  ·  page {page} of {pageCount}" : $"  ·  page {page}";

            ImGui.TextDisabled($"·  {detail}");
        }

        private static void Tab(Plugin plugin, NavTarget current, NavTarget target, string label, string tooltip)
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

            if (ImGui.Button(label) && !active)
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
            NavTarget.Trending => "Trending today",
            NavTarget.Newest => "Newest mods",
            NavTarget.Sponsored => "Sponsored mods",
            _ => "Search results",
        };
    }
}
