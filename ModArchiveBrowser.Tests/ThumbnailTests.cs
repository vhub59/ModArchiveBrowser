using System;
using System.Collections.Generic;
using System.IO;
using Xunit;

namespace ModArchiveBrowser.Tests
{
    public class ThumbnailTests : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "mab-thumbnails-" + Guid.NewGuid().ToString("N"));

        public ThumbnailTests() => Directory.CreateDirectory(_root);

        public void Dispose() => Directory.Delete(_root, true);

        [Fact]
        public void Missing_thumbnails_are_removed_without_loading_them()
        {
            var thumbnails = new Dictionary<string, string>
            {
                ["[Bibo+] Sweetheart - SFW Smallclothes"] = Path.Combine(_root, "deleted.jpg"),
                ["Another mod"] = Path.Combine(_root, "also-deleted.jpg"),
            };
            var textures = new Dictionary<string, string>();

            ModHandler.UpdateTextures(thumbnails, textures,
                _ => throw new InvalidOperationException("A missing file must not be loaded."));

            Assert.Empty(thumbnails);
            Assert.Empty(textures);
        }

        [Fact]
        public void A_missing_thumbnail_does_not_prevent_loading_the_next_mod()
        {
            var existing = Path.Combine(_root, "existing.jpg");
            File.WriteAllText(existing, "Texture loading is supplied by the test.");
            var thumbnails = new Dictionary<string, string>
            {
                ["Missing"] = Path.Combine(_root, "deleted.jpg"),
                ["Existing"] = existing,
            };
            var textures = new Dictionary<string, string>();
            var loadedPaths = new List<string>();

            ModHandler.UpdateTextures(thumbnails, textures, path =>
            {
                loadedPaths.Add(path);
                return "loaded texture";
            });

            Assert.Equal(existing, Assert.Single(loadedPaths));
            Assert.Equal(existing, Assert.Single(thumbnails).Value);
            Assert.Equal("loaded texture", textures["Existing"]);
            Assert.Single(textures);
        }

        [Fact]
        public void Refreshing_the_cache_does_not_reload_existing_textures()
        {
            var existing = Path.Combine(_root, "existing.jpg");
            File.WriteAllText(existing, "Texture loading is supplied by the test.");
            var thumbnails = new Dictionary<string, string> { ["Mod"] = existing };
            var textures = new Dictionary<string, string>();
            var loads = 0;

            for (var i = 0; i < 2; i++)
                ModHandler.UpdateTextures(thumbnails, textures, _ => $"texture {++loads}");

            Assert.Equal(1, loads);
            Assert.Equal("texture 1", textures["Mod"]);
        }
    }
}
