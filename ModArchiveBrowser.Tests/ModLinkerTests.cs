using ModArchiveBrowser.Utils;
using Xunit;

namespace ModArchiveBrowser.Tests
{
    /// <summary>
    /// Ce qu'on envoie a xivmodarchive pour retrouver un mod deja installe.
    ///
    /// Le nom d'un dossier de Penumbra vient du fichier telecharge, jamais du titre publie : il
    /// porte des etiquettes, des qualificatifs, un numero de version, parfois une traduction
    /// accolee. Envoye tel quel, il ne trouve rien — et un mod introuvable reste hors de portee de
    /// la verification des mises a jour.
    ///
    /// Les cas ci-dessous viennent tous d'une bibliotheque reelle.
    /// </summary>
    public class ModLinkerTests
    {
        /// <summary>
        /// Le cas qui a motive la decoupe : le titre anglais et sa traduction chinoise colles par
        /// un signe egal. La chaine entiere ne correspond a rien sur le site.
        /// </summary>
        [Fact]
        public void A_bilingual_name_is_cut_at_its_separator()
        {
            Assert.Equal("Classic DNC skill remake",
                ModLinker.QueryFor("Classic DNC skill remake[SHB+EW]=舞者职业包"));
        }

        [Theory]
        [InlineData("[HS] Bibo+ (Bibo+ Base Install)", "Bibo+")]
        [InlineData("Iconic Bride", "Iconic Bride")]
        [InlineData("Some Mod v3.1.5", "Some Mod")]
        [InlineData("Some Mod 1.0.4", "Some Mod")]
        public void Tags_qualifiers_and_versions_are_stripped(string folderName, string expected)
        {
            Assert.Equal(expected, ModLinker.QueryFor(folderName));
        }

        /// <summary>
        /// Un entier isole fait souvent partie du nom : le retirer confondrait deux mods
        /// distincts, et un mauvais rattachement fait supprimer le mauvais mod a la mise a jour.
        /// </summary>
        [Fact]
        public void A_trailing_number_is_kept()
        {
            Assert.Equal("Adidas Superstar 2", ModLinker.QueryFor("Adidas Superstar 2"));
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void An_empty_name_yields_an_empty_query(string? name)
        {
            Assert.Equal(string.Empty, ModLinker.QueryFor(name));
        }

        /// <summary>La ponctuation laissee par les decoupes ne doit pas partir dans la requete.</summary>
        [Fact]
        public void Leftover_punctuation_is_trimmed()
        {
            Assert.Equal("Some Mod", ModLinker.QueryFor("Some Mod - (DT Update)"));
        }
    }
}
