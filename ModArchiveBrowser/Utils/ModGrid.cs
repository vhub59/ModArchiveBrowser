using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures.TextureWraps;

namespace ModArchiveBrowser.Utils
{
    /// <summary>
    /// Grille de cartes de mods, partagée par la page d'accueil et la recherche.
    ///
    /// La carte n'est pas faite de widgets ImGui mais peinte a la main dans la liste de dessin.
    /// C'est la que se trouve la marge de manoeuvre du framework : les widgets standards donnent
    /// des rectangles gris a angles vifs, la liste de dessin permet des images rognees a coins
    /// arrondis, des degrades, et des transitions au survol. Le plugin Heliosphere, souvent cite
    /// en exemple, tourne lui aussi sur ImGui — la difference tient au dessin, pas au framework.
    /// </summary>
    public static class ModGrid
    {
        /// <summary>Largeur visée pour une carte. Le nombre de colonnes en découle.</summary>
        private const float TargetCardWidth = 300f;

        /// <summary>Proportion de la vignette (16:9).</summary>
        private const float ImageRatio = 9f / 16f;

        /// <summary>Arrondi des angles de la carte.</summary>
        private const float Rounding = 8f;

        /// <summary>
        /// Avancement de l'animation de survol, par carte.
        ///
        /// ImGui ne fournit pas de systeme d'animation, mais il redessine a chaque frame : il
        /// suffit d'interpoler soi-meme une valeur en s'appuyant sur le temps ecoule. C'est ce
        /// qui separe un survol qui claque d'un survol qui s'allume.
        /// </summary>
        private static readonly Dictionary<string, float> HoverProgress = new();

        public static int ColumnCount(float availableWidth)
        {
            var spacing = ImGui.GetStyle().ItemSpacing.X;
            var count = (int)MathF.Floor((availableWidth + spacing) / (TargetCardWidth + spacing));
            return Math.Max(1, count);
        }

        public static float CardWidth(float availableWidth, int columns)
        {
            var spacing = ImGui.GetStyle().ItemSpacing.X;
            return (availableWidth - spacing * (columns - 1)) / columns;
        }

        /// <summary>Hauteur d'une carte : la vignette, puis une ligne d'informations.</summary>
        public static float CardHeight(float width)
            => MathF.Round(width * ImageRatio) + ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().FramePadding.Y * 2f;

        public static int Capacity(Vector2 available)
        {
            var columns = ColumnCount(available.X);
            var cardHeight = CardHeight(CardWidth(available.X, columns));
            var rows = Math.Max(1, (int)(available.Y / (cardHeight + ImGui.GetStyle().ItemSpacing.Y)));

            return columns * rows;
        }

        public static bool Draw(string id, ModThumb thumb, IDalamudTextureWrap? texture, float width,
                                ModAvailability availability = ModAvailability.Unknown,
                                bool obscure = false)
        {
            var style = ImGui.GetStyle();
            var pad = style.FramePadding;
            var lineHeight = ImGui.GetTextLineHeightWithSpacing();

            var imageHeight = MathF.Round(width * ImageRatio);
            var height = imageHeight + lineHeight + pad.Y * 2f;

            var origin = ImGui.GetCursorScreenPos();
            var clicked = ImGui.InvisibleButton(id, new Vector2(width, height));
            var hovered = ImGui.IsItemHovered();

            var lift = Animate(id, hovered);
            var draw = ImGui.GetWindowDrawList();

            //La carte se souleve legerement au survol. Deux pixels suffisent : au-dela, la grille
            //se met a bouger et devient penible a parcourir.
            var top = origin - new Vector2(0f, 2f * lift);
            var bottom = top + new Vector2(width, height);

            DrawShadow(draw, top, bottom, lift);
            draw.AddRectFilled(top, bottom, Blend(0xFF1A1C20u, 0xFF23262Cu, lift), Rounding);
            //Le survol revele : c'est un geste deliberé, contrairement au simple defilement.
            DrawThumbnail(draw, texture, top, width, imageHeight, obscure && lift < 0.9f);
            DrawScrim(draw, top, width, imageHeight);
            DrawTitle(draw, thumb, top, width, imageHeight, pad);
            DrawBadge(draw, availability, top, width);
            DrawFooter(draw, thumb, top, width, imageHeight, pad, lineHeight);

            //Bordure par-dessus tout le reste, pour que l'image ne deborde pas sur l'arrondi.
            draw.AddRect(top, bottom, Blend(0x40FFFFFFu, ImGui.GetColorU32(ImGuiCol.ButtonHovered), lift), Rounding, ImDrawFlags.None, 1f + lift);

            if (hovered && availability != ModAvailability.Unknown)
                ImGui.SetTooltip(AvailabilityIndex.Describe(availability));

            return clicked;
        }

        /// <summary>
        /// Fait progresser l'animation de survol vers 0 ou 1 et renvoie sa valeur.
        ///
        /// DeltaTime est le temps ecoule depuis la frame precedente : l'animation dure le meme
        /// temps quel que soit le nombre d'images par seconde.
        /// </summary>
        private static float Animate(string id, bool hovered)
        {
            const float duration = 0.14f;

            HoverProgress.TryGetValue(id, out var value);
            var step = ImGui.GetIO().DeltaTime / duration;
            value = Math.Clamp(hovered ? value + step : value - step, 0f, 1f);

            if (value <= 0f)
                HoverProgress.Remove(id);
            else
                HoverProgress[id] = value;

            return value;
        }

        private static void DrawShadow(ImDrawListPtr draw, Vector2 top, Vector2 bottom, float lift)
        {
            if (lift <= 0f)
                return;

            //Ombre portee approximee par quelques rectangles concentriques de plus en plus pales.
            //ImGui n'a pas de flou, mais l'empilement suffit a suggerer l'elevation.
            for (var i = 3; i >= 1; i--)
            {
                var spread = i * 2f * lift;
                var alpha = (uint)(18f * lift) << 24;
                draw.AddRect(top - new Vector2(spread, spread), bottom + new Vector2(spread, spread),
                    alpha, Rounding + spread, ImDrawFlags.None, 2f);
            }
        }

        private static void DrawThumbnail(ImDrawListPtr draw, IDalamudTextureWrap? texture, Vector2 top, float width, float imageHeight, bool obscure = false)
        {
            var imageEnd = top + new Vector2(width, imageHeight);

            if (texture == null || texture.Width <= 0 || texture.Height <= 0)
            {
                draw.AddRectFilled(top, imageEnd, 0xFF15171Au, Rounding, ImDrawFlags.RoundCornersTop);

                const string label = "Loading...";
                var size = ImGui.CalcTextSize(label);
                draw.AddText(top + new Vector2((width - size.X) / 2f, (imageHeight - size.Y) / 2f),
                    ImGui.GetColorU32(ImGuiCol.TextDisabled), label);
                return;
            }

            //Cadrage "cover" : l'image remplit tout le cadre et deborde sur le cote le plus long,
            //plutot que de laisser des bandes noires. Le rognage se fait dans les coordonnees de
            //texture, ce qui evite de deformer l'image.
            var scale = MathF.Max(width / texture.Width, imageHeight / texture.Height);
            var drawnWidth = texture.Width * scale;
            var drawnHeight = texture.Height * scale;

            var uvWidth = width / drawnWidth;
            var uvHeight = imageHeight / drawnHeight;
            var uv0 = new Vector2((1f - uvWidth) / 2f, (1f - uvHeight) / 2f);
            var uv1 = uv0 + new Vector2(uvWidth, uvHeight);

            if (obscure)
            {
                DrawObscured(draw, texture, top, imageEnd, uv0, uv1, width, imageHeight);
                return;
            }

            draw.AddImageRounded(texture.Handle, top, imageEnd, uv0, uv1, 0xFFFFFFFFu, Rounding, ImDrawFlags.RoundCornersTop);
        }

        /// <summary>
        /// Vignette rendue illisible sans etre cachee.
        ///
        /// ImGui n'offre pas de flou : il n'y a ni shader accessible depuis la liste de dessin,
        /// ni cible de rendu intermediaire. On l'approche en superposant la meme image plusieurs
        /// fois, decalee et transparente — un etalement qui brouille les contours — puis en
        /// assombrissant l'ensemble. Le resultat n'est pas un flou gaussien, mais il remplit le
        /// meme office : on devine une image sans en distinguer le contenu.
        /// </summary>
        private static void DrawObscured(ImDrawListPtr draw, IDalamudTextureWrap texture,
                                         Vector2 top, Vector2 end, Vector2 uv0, Vector2 uv1,
                                         float width, float imageHeight)
        {
            //Pixellisation plutot qu'etalement. La premiere version superposait neuf copies
            //decalees de quelques pixels : bien trop peu pour cacher quoi que ce soit, le sujet
            //restait parfaitement reconnaissable.
            //
            //Ici, l'image est decoupee en cases et chaque case est dessinee en n'echantillonnant
            //qu'un point de la texture : elle se remplit donc d'une couleur unique. C'est une
            //vraie mosaique, la seule facon d'obtenir cet effet dans ImGui, qui n'expose ni
            //shader ni cible de rendu intermediaire.
            const int columns = 12;
            var rows = Math.Max(4, (int)(columns * imageHeight / width));

            var cell = new Vector2(width / columns, imageHeight / rows);
            var uvSpan = uv1 - uv0;

            draw.PushClipRect(top, end, true);

            for (var y = 0; y < rows; y++)
            {
                for (var x = 0; x < columns; x++)
                {
                    var cellTop = top + new Vector2(x * cell.X, y * cell.Y);

                    //Centre de la case, ramene dans les coordonnees de texture.
                    var center = uv0 + new Vector2(
                        (x + 0.5f) / columns * uvSpan.X,
                        (y + 0.5f) / rows * uvSpan.Y);

                    //Une fenetre minuscule autour de ce point : la case entiere prend sa couleur.
                    var point = new Vector2(0.0015f, 0.0015f);
                    draw.AddImage(texture.Handle, cellTop, cellTop + cell + Vector2.One, center - point, center + point);
                }
            }

            draw.PopClipRect();

            //Un voile sombre acheve de casser les contrastes, et porte le libelle.
            draw.AddRectFilled(top, end, 0x66000000u, Rounding, ImDrawFlags.RoundCornersTop);

            const string label = "Adult content";
            var size = ImGui.CalcTextSize(label);
            var position = top + new Vector2((width - size.X) / 2f, (imageHeight - size.Y) / 2f);

            draw.AddRectFilled(position - new Vector2(8f, 4f), position + size + new Vector2(8f, 4f), 0xAA000000u, 4f);
            draw.AddText(position, 0xEEFFFFFFu, label);
        }

        /// <summary>
        /// Degrade sombre sur le bas de la vignette.
        ///
        /// Le titre est ecrit par-dessus l'image : sans ce voile, il devient illisible des que la
        /// vignette est claire — et beaucoup le sont, les auteurs y mettant souvent des fonds
        /// blancs ou des captures en plein jour.
        /// </summary>
        private static void DrawScrim(ImDrawListPtr draw, Vector2 top, float width, float imageHeight)
        {
            var scrimHeight = imageHeight * 0.5f;
            var scrimTop = top + new Vector2(0f, imageHeight - scrimHeight);
            var scrimBottom = top + new Vector2(width, imageHeight);

            draw.AddRectFilledMultiColor(scrimTop, scrimBottom, 0x00000000u, 0x00000000u, 0xE6000000u, 0xE6000000u);
        }

        private static void DrawTitle(ImDrawListPtr draw, ModThumb thumb, Vector2 top, float width, float imageHeight, Vector2 pad)
        {
            var lineHeight = ImGui.GetTextLineHeight();
            var textWidth = width - pad.X * 2f;

            var title = Ellipsize(thumb.name, textWidth);
            var author = Ellipsize($"by {thumb.author}", textWidth);

            draw.AddText(top + new Vector2(pad.X, imageHeight - pad.Y - lineHeight * 2f - 2f), 0xFFFFFFFFu, title);
            draw.AddText(top + new Vector2(pad.X, imageHeight - pad.Y - lineHeight), 0xB0FFFFFFu, author);
        }

        private static void DrawFooter(ImDrawListPtr draw, ModThumb thumb, Vector2 top, float width, float imageHeight, Vector2 pad, float lineHeight)
        {
            var y = top.Y + imageHeight + pad.Y;
            var muted = ImGui.GetColorU32(ImGuiCol.TextDisabled);

            var views = thumb.views?.Trim() ?? string.Empty;
            var viewsWidth = string.IsNullOrEmpty(views) ? 0f : ImGui.CalcTextSize(views).X;
            var metaWidth = MathF.Max(0f, width - pad.X * 2f - viewsWidth - pad.X);

            draw.AddText(new Vector2(top.X + pad.X, y), muted, Ellipsize($"{thumb.type} · {thumb.genders}", metaWidth));

            if (!string.IsNullOrEmpty(views))
                draw.AddText(new Vector2(top.X + width - pad.X - viewsWidth, y), muted, views);
        }

        /// <summary>
        /// Pastille d'installabilité, posee dans le coin de la vignette.
        ///
        /// Rien n'est dessine tant que le mod n'a pas ete consulte : l'index se construisant a
        /// l'usage, marquer les inconnus couvrirait la grille de signes sans information.
        /// </summary>
        private static void DrawBadge(ImDrawListPtr draw, ModAvailability availability, Vector2 top, float width)
        {
            if (availability == ModAvailability.Unknown)
                return;

            var (color, label) = availability switch
            {
                ModAvailability.Installable => (0xFF4CAF50u, "READY"),
                ModAvailability.Archive => (0xFF3BA5EBu, "ZIP"),
                ModAvailability.Heliosphere => (0xFFD9822Bu, "HELIO"),
                ModAvailability.External => (0xFF6B6B6Bu, "LINK"),
                _ => (0xFF4C4CE0u, "N/A"),
            };

            var textSize = ImGui.CalcTextSize(label);
            var padding = new Vector2(6f, 3f);
            var size = textSize + padding * 2f;
            var position = top + new Vector2(width - size.X - 8f, 8f);

            draw.AddRectFilled(position, position + size, color, size.Y / 2f);
            draw.AddText(position + padding, 0xFF101010u, label);
        }

        /// <summary>Interpole deux couleurs ARGB empaquetees.</summary>
        private static uint Blend(uint from, uint to, float t)
        {
            t = Math.Clamp(t, 0f, 1f);
            uint Channel(int shift)
            {
                var a = (from >> shift) & 0xFF;
                var b = (to >> shift) & 0xFF;
                return (uint)(a + (b - (float)a) * t) & 0xFF;
            }

            return Channel(0) | (Channel(8) << 8) | (Channel(16) << 16) | (Channel(24) << 24);
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
