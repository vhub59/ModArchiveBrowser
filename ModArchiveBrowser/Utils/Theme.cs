using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility.Raii;

namespace ModArchiveBrowser.Utils
{
    /// <summary>
    /// Habillage commun aux fenêtres du plugin.
    ///
    /// ImGui a une signature visuelle dont on ne sort pas — ni animations, ni graisses de police
    /// multiples — mais l'essentiel de l'aspect date du theme par defaut, jamais touche : angles
    /// vifs, boutons gris uniformes, elements colles les uns aux autres. Arrondir, aerer et
    /// reserver une couleur aux seuls elements interactifs suffit a changer l'impression, sans
    /// rien exiger qu'ImGui ne sache faire.
    ///
    /// Les couleurs restent volontairement sobres : le plugin s'affiche par-dessus le jeu, a cote
    /// d'autres fenetres Dalamud, et une palette criarde y serait vite fatigante.
    /// </summary>
    public static class Theme
    {
        /// <summary>Couleur d'accent, reservee aux elements sur lesquels on peut agir.</summary>
        public static readonly Vector4 Accent = new(0.29f, 0.56f, 0.89f, 1.00f);
        public static readonly Vector4 AccentHovered = new(0.36f, 0.63f, 0.95f, 1.00f);
        public static readonly Vector4 AccentActive = new(0.22f, 0.47f, 0.80f, 1.00f);

        /// <summary>Vert des actions qui aboutissent : installer, mettre a jour.</summary>
        public static readonly Vector4 Positive = new(0.26f, 0.63f, 0.28f, 1.00f);
        public static readonly Vector4 PositiveHovered = new(0.33f, 0.72f, 0.35f, 1.00f);

        /// <summary>Ambre des avertissements : quelque chose manque, sans que rien soit casse.</summary>
        public static readonly Vector4 Warning = new(0.90f, 0.66f, 0.24f, 1.00f);

        /// <summary>
        /// Applique le style pour la duree du bloc. A utiliser avec "using".
        /// </summary>
        public static IDisposable Scope()
        {
            var style = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 5f)
                .Push(ImGuiStyleVar.ChildRounding, 8f)
                .Push(ImGuiStyleVar.PopupRounding, 6f)
                .Push(ImGuiStyleVar.GrabRounding, 4f)
                .Push(ImGuiStyleVar.ScrollbarRounding, 6f)
                .Push(ImGuiStyleVar.TabRounding, 5f)
                //Des marges plus genereuses : les elements se touchaient, ce qui donne toujours
                //une impression d'entassement quel que soit le reste.
                .Push(ImGuiStyleVar.FramePadding, new Vector2(10f, 6f))
                .Push(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 7f))
                .Push(ImGuiStyleVar.ItemInnerSpacing, new Vector2(7f, 5f))
                .Push(ImGuiStyleVar.WindowPadding, new Vector2(12f, 10f))
                .Push(ImGuiStyleVar.ScrollbarSize, 12f);

            var colors = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.18f, 0.19f, 0.22f, 1f))
                .Push(ImGuiCol.ButtonHovered, AccentHovered)
                .Push(ImGuiCol.ButtonActive, AccentActive)
                .Push(ImGuiCol.FrameBg, new Vector4(0.13f, 0.14f, 0.16f, 1f))
                .Push(ImGuiCol.FrameBgHovered, new Vector4(0.18f, 0.20f, 0.24f, 1f))
                .Push(ImGuiCol.FrameBgActive, new Vector4(0.21f, 0.24f, 0.29f, 1f))
                .Push(ImGuiCol.Border, new Vector4(0.26f, 0.28f, 0.32f, 0.7f))
                .Push(ImGuiCol.ChildBg, new Vector4(0.10f, 0.11f, 0.13f, 0.55f))
                //ImGuiCol.Header sert de fond aux CollapsingHeader et aux Selectable. Lui donner
                //la couleur d'accent transformait "Advanced Search Options" en un bandeau bleu
                //plein qui ecrasait tout le panneau. L'accent est reserve au survol, ou il
                //signale quelque chose ; au repos, un gris a peine plus clair que le fond suffit.
                .Push(ImGuiCol.Header, new Vector4(0.20f, 0.21f, 0.25f, 1f))
                .Push(ImGuiCol.HeaderHovered, new Vector4(0.26f, 0.29f, 0.35f, 1f))
                .Push(ImGuiCol.HeaderActive, AccentActive)
                .Push(ImGuiCol.CheckMark, AccentHovered)
                .Push(ImGuiCol.Separator, new Vector4(0.24f, 0.26f, 0.30f, 0.6f));

            return new Composite(style, colors);
        }

        /// <summary>Bouton colore, pour l'action principale d'un ecran.</summary>
        public static IDisposable Emphasis(Vector4 color, Vector4 hovered)
            => new Composite(ImRaii.PushColor(ImGuiCol.Button, color)
                .Push(ImGuiCol.ButtonHovered, hovered)
                .Push(ImGuiCol.ButtonActive, color));

        /// <summary>Regroupe plusieurs portees pour les liberer ensemble, dans l'ordre inverse.</summary>
        private sealed class Composite : IDisposable
        {
            private readonly List<IDisposable> _scopes;

            public Composite(params IDisposable[] scopes) => _scopes = new List<IDisposable>(scopes);

            public void Dispose()
            {
                for (var i = _scopes.Count - 1; i >= 0; i--)
                    _scopes[i].Dispose();
            }
        }
    }
}
