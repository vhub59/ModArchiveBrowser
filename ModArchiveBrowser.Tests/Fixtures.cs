using System;
using System.IO;
using HtmlAgilityPack;

namespace ModArchiveBrowser.Tests
{
    /// <summary>
    /// Pages de xivmodarchive enregistrees sur disque.
    ///
    /// Les tests portent sur les selecteurs XPath, pas sur le reseau : ils doivent tourner sans
    /// connexion, sans solliciter le site, et donner le meme resultat aujourd'hui et dans six
    /// mois. Une page figee est exactement cela — un contrat entre le plugin et le HTML tel qu'il
    /// etait le jour de la capture.
    ///
    /// Leur contrepartie est qu'elles ne peuvent pas detecter une refonte du site : c'est le role
    /// de LiveSelectorTests, qui rejoue les memes verifications sur les pages en ligne.
    /// </summary>
    internal static class Fixtures
    {
        /// <summary>Date de capture, pour savoir a quel point la reference a vieilli.</summary>
        public const string CapturedOn = "2026-08-23";

        public static HtmlDocument Homepage() => Load("homepage.html");

        public static HtmlDocument SearchResults() => Load("search.html");

        public static HtmlDocument ModPage() => Load("modpage.html");

        /// <summary>Identifiant du mod capture dans modpage.html.</summary>
        public const string ModPageId = "167526";

        private static HtmlDocument Load(string name)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
            if (!File.Exists(path))
                throw new FileNotFoundException(
                    $"Reference page '{name}' is missing. It should be copied next to the test assembly by the build.", path);

            var document = new HtmlDocument();
            document.Load(path);
            return document;
        }
    }
}
