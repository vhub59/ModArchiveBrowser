using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility.Raii;

namespace ModArchiveBrowser.Utils
{
    /// <summary>
    /// Ce qu'on affiche quand Penumbra n'est pas la.
    ///
    /// Le plugin ne sait rien faire seul : il telecharge un fichier et le passe a Penumbra. Sans
    /// Penumbra, un clic sur "Install" telechargeait quand meme le modpack, puis l'appel IPC
    /// echouait dans le journal — l'utilisateur voyait "Downloading...", puis plus rien, sans
    /// jamais apprendre ce qui manquait.
    ///
    /// PenumbraService.Available existait deja et disait exactement cela ; il n'etait consulte
    /// nulle part dans l'interface.
    /// </summary>
    public static class PenumbraNotice
    {
        private const string Explanation =
            "This plugin downloads mods and hands them to Penumbra, which does the installing.\n" +
            "Install Penumbra from its own Dalamud repository, then use Retry.";

        /// <summary>
        /// Bandeau en tete de fenetre, avec de quoi retenter l'attache.
        ///
        /// Reattach est utile parce que Penumbra peut arriver apres nous : il previent bien de son
        /// demarrage par une IPC, mais un rechargement manuel depuis le gestionnaire de plugins ne
        /// passe pas toujours par la. Le bouton evite d'avoir a relancer le jeu pour un doute.
        /// </summary>
        public static void Banner(Plugin plugin)
        {
            if (plugin.penumbra.Available)
                return;

            var draw = ImGui.GetWindowDrawList();
            var start = ImGui.GetCursorScreenPos();
            var width = ImGui.GetContentRegionAvail().X;
            var height = ImGui.GetFrameHeight() + ImGui.GetStyle().FramePadding.Y * 2f;

            //Un fond ambre tres dilue : assez pour que le bandeau se detache du reste, pas assez
            //pour concurrencer les vignettes qu'il surplombe.
            draw.AddRectFilled(start, start + new Vector2(width, height),
                ImGui.GetColorU32(new Vector4(Theme.Warning.X, Theme.Warning.Y, Theme.Warning.Z, 0.12f)), 6f);
            draw.AddRect(start, start + new Vector2(width, height),
                ImGui.GetColorU32(new Vector4(Theme.Warning.X, Theme.Warning.Y, Theme.Warning.Z, 0.45f)), 6f);

            ImGui.Dummy(new Vector2(0, ImGui.GetStyle().FramePadding.Y * 0.5f));
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + ImGui.GetStyle().FramePadding.X);

            ImGui.TextColored(Theme.Warning, "Penumbra is not running — nothing can be installed.");

            ImGui.SameLine();
            if (ImGui.SmallButton("Retry##penumbraattach"))
                plugin.penumbra.Reattach();

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip(Explanation);

            ImGui.Dummy(new Vector2(0, ImGui.GetStyle().FramePadding.Y));
        }

        /// <summary>
        /// Bouton inerte qui remplace celui d'installation, avec la raison en infobulle.
        ///
        /// On garde un bouton grise plutot que de masquer la commande : une place vide ne se
        /// remarque pas, et l'utilisateur chercherait pourquoi la fiche n'a pas de bouton.
        /// </summary>
        public static void DisabledInstallButton()
        {
            using (ImRaii.Disabled(true))
                ImGui.Button("Penumbra not found");

            //AllowWhenDisabled : sans ce drapeau, IsItemHovered renvoie faux sur un element grise,
            //et l'infobulle qui porte l'explication ne s'affiche jamais. C'est precisement sur ces
            //boutons-la qu'elle est indispensable.
            if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
                ImGui.SetTooltip(Explanation);
        }
    }
}
