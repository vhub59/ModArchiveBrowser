using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace ModArchiveBrowser.Utils
{
    /// <summary>
    /// Grille de cartes de mods, partagée par la page d'accueil et la recherche.
    ///
    /// Les deux fenêtres dessinaient auparavant le même code, dupliqué, avec trois défauts
    /// communs : l'image était affichée à sa taille d'origine, ce qui donnait des cartes de
    /// hauteurs différentes et une grille en dents de scie ; le nombre de colonnes était figé à
    /// trois quelle que soit la largeur de la fenêtre, laissant un large vide à droite ; et les
    /// vues étaient positionnées par un décalage de cent pixels en dur, donc jamais alignées.
    /// </summary>
    public static class ModGrid
    {
        /// <summary>Largeur visée pour une carte. Le nombre de colonnes en découle.</summary>
        private const float TargetCardWidth = 300f;

        /// <summary>Proportion de la carte occupée par l'image (16:9).</summary>
        private const float ImageRatio = 9f / 16f;

        /// <summary>Nombre de colonnes tenant dans la largeur disponible, au moins une.</summary>
        public static int ColumnCount(float availableWidth)
        {
            var spacing = ImGui.GetStyle().ItemSpacing.X;
            var count = (int)MathF.Floor((availableWidth + spacing) / (TargetCardWidth + spacing));
            return Math.Max(1, count);
        }

        /// <summary>
        /// Largeur d'une carte pour ce nombre de colonnes. Les cartes se partagent toute la
        /// largeur disponible plutôt que de laisser un vide sur le côté.
        /// </summary>
        public static float CardWidth(float availableWidth, int columns)
        {
            var spacing = ImGui.GetStyle().ItemSpacing.X;
            return (availableWidth - spacing * (columns - 1)) / columns;
        }

        /// <summary>Hauteur d'une carte pour une largeur donnée. Toutes ont la même.</summary>
        public static float CardHeight(float width)
        {
            var imageHeight = MathF.Round(width * ImageRatio);
            return imageHeight + ImGui.GetTextLineHeightWithSpacing() * 3f + ImGui.GetStyle().FramePadding.Y * 2f;
        }

        /// <summary>
        /// Nombre de cartes tenant dans la zone donnée, pour savoir combien de mods aller
        /// chercher. XMA n'en sert que quinze par requête : remplir un écran large en demande
        /// plusieurs.
        /// </summary>
        public static int Capacity(Vector2 available)
        {
            var columns = ColumnCount(available.X);
            var cardHeight = CardHeight(CardWidth(available.X, columns));
            var rows = Math.Max(1, (int)(available.Y / (cardHeight + ImGui.GetStyle().ItemSpacing.Y)));

            return columns * rows;
        }

        /// <summary>
        /// Dessine une carte et renvoie vrai si elle a été cliquée.
        ///
        /// La carte entière est cliquable, pas seulement la vignette : un bouton invisible occupe
        /// toute sa surface et le contenu est peint par-dessus.
        /// </summary>
        public static bool Draw(string id, ModThumb thumb, IDalamudTextureWrap? texture, float width,
                                ModAvailability availability = ModAvailability.Unknown)
        {
            var style = ImGui.GetStyle();
            var pad = style.FramePadding;
            var lineHeight = ImGui.GetTextLineHeightWithSpacing();

            var imageHeight = MathF.Round(width * ImageRatio);
            var height = imageHeight + lineHeight * 3f + pad.Y * 2f;

            var origin = ImGui.GetCursorScreenPos();
            var clicked = ImGui.InvisibleButton(id, new Vector2(width, height));
            var hovered = ImGui.IsItemHovered();

            var draw = ImGui.GetWindowDrawList();
            var end = origin + new Vector2(width, height);

            draw.AddRectFilled(origin, end, ImGui.GetColorU32(hovered ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg), 6f);
            draw.AddRect(origin, end, ImGui.GetColorU32(hovered ? ImGuiCol.ButtonHovered : ImGuiCol.Border), 6f);

            DrawThumbnail(draw, texture, origin, width, imageHeight);
            DrawAvailabilityBadge(draw, availability, origin);

            if (hovered && availability != ModAvailability.Unknown)
                ImGui.SetTooltip(AvailabilityIndex.Describe(availability));

            //Tout le contenu est peint par la liste de dessin, jamais par des widgets.
            //Un widget dessine apres le bouton invisible deviendrait le "dernier element" d'ImGui,
            //et le ImGui.SameLine() de l'appelant s'alignerait sur lui plutot que sur la carte :
            //les cartes se decalaient alors en escalier. Le bouton invisible doit rester le
            //dernier element de la carte.
            var textLeft = origin.X + pad.X;
            var textWidth = width - pad.X * 2f;
            var textColor = ImGui.GetColorU32(ImGuiCol.Text);
            var mutedColor = ImGui.GetColorU32(ImGuiCol.TextDisabled);
            var baseY = origin.Y + imageHeight + pad.Y;

            draw.AddText(new Vector2(textLeft, baseY), textColor, Ellipsize(thumb.name, textWidth));
            draw.AddText(new Vector2(textLeft, baseY + lineHeight), mutedColor, Ellipsize($"by {thumb.author}", textWidth));

            //Type, genre et vues sur une seule ligne : les vues alignées à droite, calculées et
            //non décalées de cent pixels au jugé.
            var views = thumb.views?.Trim() ?? string.Empty;
            var viewsWidth = string.IsNullOrEmpty(views) ? 0f : ImGui.CalcTextSize(views).X;
            var metaY = baseY + lineHeight * 2f;
            var metaWidth = MathF.Max(0f, textWidth - viewsWidth - pad.X);

            draw.AddText(new Vector2(textLeft, metaY), mutedColor, Ellipsize($"{thumb.type} · {thumb.genders}", metaWidth));

            if (!string.IsNullOrEmpty(views))
                draw.AddText(new Vector2(origin.X + width - pad.X - viewsWidth, metaY), mutedColor, views);

            return clicked;
        }

        /// <summary>
        /// Pastille d'installabilité, en haut à gauche de la vignette.
        ///
        /// Rien n'est dessiné tant que le mod n'a pas été consulté : l'index se construisant a
        /// l'usage, marquer les mods inconnus d'un point d'interrogation couvrirait la grille de
        /// signes sans information. L'absence de pastille se lit alors "pas encore su", ce qui
        /// est la verite.
        /// </summary>
        private static void DrawAvailabilityBadge(ImDrawListPtr draw, ModAvailability availability, Vector2 origin)
        {
            if (availability == ModAvailability.Unknown)
                return;

            var color = availability switch
            {
                ModAvailability.Installable => 0xFF4CAF50u, // vert
                ModAvailability.Archive => 0xFF3BA5EBu,     // ambre
                ModAvailability.External => 0xFF6B6B6Bu,    // gris
                ModAvailability.Heliosphere => 0xFFD9822Bu, // bleu : autre plateforme, pas un cul-de-sac
                _ => 0xFF4C4CE0u,                            // rouge
            };

            var position = origin + new Vector2(6f, 6f);
            var size = new Vector2(10f, 10f);

            //Un liseré sombre détache la pastille des vignettes claires.
            draw.AddRectFilled(position - Vector2.One, position + size + Vector2.One, 0xC0000000u, 3f);
            draw.AddRectFilled(position, position + size, color, 2f);
        }

        private static void DrawThumbnail(ImDrawListPtr draw, IDalamudTextureWrap? texture, Vector2 origin, float width, float imageHeight)
        {
            if (texture == null || texture.Width <= 0 || texture.Height <= 0)
            {
                const string label = "Loading...";
                var size = ImGui.CalcTextSize(label);
                draw.AddText(
                    origin + new Vector2((width - size.X) / 2f, (imageHeight - size.Y) / 2f),
                    ImGui.GetColorU32(ImGuiCol.TextDisabled),
                    label);
                return;
            }

            //Ratio préservé et image centrée dans son cadre. Auparavant elle était dessinée à sa
            //taille d'origine, d'où des cartes de hauteurs inégales.
            var scale = MathF.Min(width / texture.Width, imageHeight / texture.Height);
            var drawn = new Vector2(texture.Width * scale, texture.Height * scale);
            var position = origin + new Vector2((width - drawn.X) / 2f, (imageHeight - drawn.Y) / 2f);

            draw.AddImage(texture.Handle, position, position + drawn);
        }

        /// <summary>
        /// Tronque le texte à la largeur donnée, en terminant par une ellipse.
        ///
        /// Indispensable pour que toutes les cartes gardent la même hauteur : un titre trop long
        /// qui reviendrait à la ligne décalerait tout ce qui suit.
        /// </summary>
        private static string Ellipsize(string text, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || ImGui.CalcTextSize(text).X <= maxWidth)
                return text ?? string.Empty;

            const string suffix = "...";
            var suffixWidth = ImGui.CalcTextSize(suffix).X;
            if (suffixWidth >= maxWidth)
                return suffix;

            var length = text.Length;
            while (length > 0 && ImGui.CalcTextSize(text[..length]).X + suffixWidth > maxWidth)
                length--;

            return text[..length].TrimEnd() + suffix;
        }
    }
}
