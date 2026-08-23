using Dalamud.Plugin.Services;

namespace ModArchiveBrowser.Utils
{
    /// <summary>
    /// Journal du plugin, utilisable hors du jeu.
    ///
    /// Plugin.Logger est injecte par Dalamud au chargement : hors du jeu il vaut null, et le
    /// moindre appel leve. Or c'est precisement quand un selecteur casse que le code journalise —
    /// une suite de tests sur ces selecteurs echouerait donc sur une NullReferenceException au
    /// lieu de nommer le champ fautif.
    ///
    /// La facade ne fait rien de plus que verifier la presence du journal. Elle ne remplace pas
    /// Plugin.Logger ailleurs dans le plugin : seul le parsing, qui doit tourner sous test, passe
    /// par ici.
    /// </summary>
    internal static class Log
    {
        private static IPluginLog? Sink => Plugin.Logger;

        public static void Debug(string message) => Sink?.Debug(message);

        public static void Information(string message) => Sink?.Information(message);

        public static void Warning(string message) => Sink?.Warning(message);
    }
}
