using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using ModArchiveBrowser.Utils;
using Xunit;

namespace ModArchiveBrowser.Tests
{
    /// <summary>
    /// D'ou vient le lien entre un mod installe et sa page sur xivmodarchive.
    ///
    /// La question parait secondaire ; elle decide en fait si la verification des mises a jour
    /// fonctionne. Elle s'appuyait sur le champ Website du meta.json, en le prenant pour l'origine
    /// du mod. Ce champ appartient a l'auteur du modpack, qui y met ce qu'il veut : sur une
    /// bibliotheque reelle, quatre mods sur sept pointaient vers un Ko-fi, un vers Heliosphere, et
    /// aucun vers XMA. L'onglet Updates restait donc vide en permanence — et un onglet vide se lit
    /// comme "tout est a jour".
    /// </summary>
    public class InstalledModsTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mab-tests-" + Guid.NewGuid().ToString("N"));

        public InstalledModsTests() => Directory.CreateDirectory(_root);

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch { /* le menage n'a pas a faire echouer un test */ }
        }

        private void WriteMod(string folder, string name, string version, string website)
        {
            var path = Path.Combine(_root, folder);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "meta.json"),
                $$"""{"Name":"{{name}}","Version":"{{version}}","Website":"{{website}}"}""");
        }

        [Fact]
        public void A_mod_the_plugin_installed_is_linked_to_its_page()
        {
            WriteMod("some-body-mod", "Some Body Mod", "1.2", "https://ko-fi.com/theauthor");

            var mods = InstalledMods.Read(_root, new Dictionary<string, string> { ["some-body-mod"] = "62528" });

            Assert.Equal("62528", mods.Single().XmaModId);
        }

        /// <summary>
        /// Le registre prime sur le meta.json : il dit ce que le plugin a fait, quand le champ
        /// Website ne dit que ce que l'auteur a bien voulu ecrire.
        /// </summary>
        [Fact]
        public void The_registry_wins_over_what_the_author_wrote()
        {
            WriteMod("a-mod", "A Mod", "1.0", "https://www.xivmodarchive.com/modid/111");

            var mods = InstalledMods.Read(_root, new Dictionary<string, string> { ["a-mod"] = "999" });

            Assert.Equal("999", mods.Single().XmaModId);
        }

        /// <summary>
        /// Le meta.json reste un secours pour les rares auteurs qui renseignent leur page XMA,
        /// notamment les mods installes avant que le registre n'existe.
        /// </summary>
        [Fact]
        public void An_author_who_did_point_at_xma_is_still_picked_up()
        {
            WriteMod("older-mod", "Older Mod", "2.0", "https://www.xivmodarchive.com/modid/4242");

            Assert.Equal("4242", InstalledMods.Read(_root).Single().XmaModId);
        }

        /// <summary>
        /// Le cas courant, et celui qui rendait la verification inoperante : rien n'est connu, donc
        /// le mod n'est pas interroge. Il ne doit surtout pas etre confondu avec un mod a jour.
        /// </summary>
        [Theory]
        [InlineData("https://ko-fi.com/theauthor")]
        [InlineData("https://heliosphere.app/mod/abcdef")]
        [InlineData("")]
        public void A_mod_of_unknown_origin_stays_unlinked(string website)
        {
            WriteMod("unknown-mod", "Unknown Mod", "1.0", website);

            Assert.Null(InstalledMods.Read(_root).Single().XmaModId);
        }

        [Fact]
        public void A_folder_without_metadata_is_skipped_rather_than_breaking_the_scan()
        {
            Directory.CreateDirectory(Path.Combine(_root, "no-meta"));
            WriteMod("good-mod", "Good Mod", "1.0", string.Empty);

            var mods = InstalledMods.Read(_root);

            Assert.Equal("Good Mod", mods.Single().Name);
        }

        [Fact]
        public void A_missing_mod_directory_yields_nothing_rather_than_throwing()
        {
            Assert.Empty(InstalledMods.Read(Path.Combine(_root, "does-not-exist")));
            Assert.Empty(InstalledMods.Read(string.Empty));
        }
    }
}
