using ModArchiveBrowser.Utils;
using Xunit;

namespace ModArchiveBrowser.Tests
{
    /// <summary>
    /// Ce que la mise a jour groupee accepte de traiter, et ce qu'elle refuse.
    ///
    /// Le refus compte autant que l'acceptation : chaque mod ecarte doit l'etre pour une raison
    /// nommee. Un mod silencieusement saute laisserait croire que tout a ete mis a jour, et
    /// l'utilisateur ne s'en apercevrait que des mois plus tard.
    ///
    /// Une erreur ici ne se contente pas de ne rien faire : le mod remplace est supprime apres
    /// coup, et la suppression est irreversible.
    /// </summary>
    public class UpdateTests
    {
        [Theory]
        [InlineData("/private/12345/modpack.pmp")]
        [InlineData("/private/12345/modpack.ttmp2")]
        public void A_modpack_hosted_by_xma_can_be_replaced(string url)
        {
            Assert.Null(UpdateInstaller.WhyNotUpdatable(url));
        }

        /// <summary>
        /// Une archive s'installe pourtant depuis une fiche. Elle est ecartee ici parce qu'elle
        /// peut contenir plusieurs modpacks : lequel remplace l'ancien mod ne se devine pas, et se
        /// tromper le supprimerait.
        /// </summary>
        [Theory]
        [InlineData("/private/12345/sources.zip")]
        [InlineData("/private/12345/sources.7z")]
        public void An_archive_is_left_to_the_user(string url)
        {
            Assert.Contains("archive", UpdateInstaller.WhyNotUpdatable(url));
        }

        [Fact]
        public void An_externally_hosted_mod_names_its_host()
        {
            Assert.Contains("mega.nz", UpdateInstaller.WhyNotUpdatable("https://mega.nz/file/abcdef"));
        }

        [Fact]
        public void A_heliosphere_mod_says_so()
        {
            Assert.Contains("Heliosphere", UpdateInstaller.WhyNotUpdatable("https://heliosphere.app/mod/abcdef"));
        }

        [Fact]
        public void An_unreadable_file_type_is_refused()
        {
            Assert.NotNull(UpdateInstaller.WhyNotUpdatable("/private/12345/readme.txt"));
        }

        /// <summary>
        /// Aucun lien : la page n'a pas pu etre lue, ou son selecteur a casse. On ne traite pas,
        /// et surtout on ne supprime rien.
        /// </summary>
        [Theory]
        [InlineData("")]
        [InlineData(null)]
        public void No_link_at_all_is_refused(string? url)
        {
            Assert.NotNull(UpdateInstaller.WhyNotUpdatable(url));
        }
    }
}
