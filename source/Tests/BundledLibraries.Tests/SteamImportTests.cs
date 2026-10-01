using NUnit.Framework;
using Playnite.SDK.Models;
using SteamLibrary.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace BundledLibraries.Tests
{
    public class SteamImportTests
    {
        [Test]
        public void InstalledScanSkipsIncompleteMissingAndMalformedGames()
        {
            var root = Path.Combine(Path.GetTempPath(), "HoleInOne-SteamScan-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(root, "common", "STS2"));
            try
            {
                File.WriteAllText(Path.Combine(root, "appmanifest_2868840.acf"),
                    "\"AppState\" { \"appid\" \"2868840\" \"name\" \"Slay the Spire 2\" \"StateFlags\" \"4\" \"installdir\" \"STS2\" }");
                File.WriteAllText(Path.Combine(root, "appmanifest_1.acf"),
                    "\"AppState\" { \"appid\" \"1\" \"name\" \"Downloading\" \"StateFlags\" \"2\" \"installdir\" \"STS2\" }");
                File.WriteAllText(Path.Combine(root, "appmanifest_2.acf"),
                    "\"AppState\" { \"appid\" \"2\" \"name\" \"Removed\" \"StateFlags\" \"4\" \"installdir\" \"Missing\" }");
                File.WriteAllText(Path.Combine(root, "appmanifest_bad.acf"), "not a manifest");
                var method = typeof(SteamLocalService).GetMethod("GetInstalledGamesFromFolder", BindingFlags.NonPublic | BindingFlags.Static);
                var games = (IEnumerable<GameMetadata>)method.Invoke(null, new object[] { root });
                var game = games.Single();
                Assert.That(game.GameId, Is.EqualTo("2868840"));
                Assert.That(game.IsInstalled, Is.True);
                Assert.That(game.InstallDirectory, Is.EqualTo(Path.Combine(root, "common", "STS2")));
            }
            finally
            {
                Directory.Delete(root, true);
            }
        }

        [Test]
        public void StoreDetailsUseSteamDirectlyAndKeepLocalizedName()
        {
            string requestedUrl = null;
            using (var client = new global::Steam.WebApiClient(url =>
            {
                requestedUrl = url;
                return "{\"2868840\":{\"success\":true,\"data\":{\"type\":\"game\",\"name\":\"Slay the Spire 2\"}}}";
            }))
            {
                var game = client.GetStoreAppDetail(2868840, "koreana");
                Assert.That(game.name, Is.EqualTo("Slay the Spire 2"));
                Assert.That(requestedUrl, Is.EqualTo("https://store.steampowered.com/api/appdetails?appids=2868840&l=koreana"));
            }
        }

        [TestCase("{}")]
        [TestCase("{\"2868840\":{\"success\":false}}")]
        public void UnavailableStoreItemDoesNotThrow(string response)
        {
            using (var client = new global::Steam.WebApiClient(_ => response))
            {
                Assert.That(client.GetStoreAppDetail(2868840, "english"), Is.Null);
            }
        }

        [Test]
        public void BothBundledModulesAreCompatibleLibraryPlugins()
        {
            Assert.That(typeof(SteamLibrary.SteamLibrary).IsSubclassOf(typeof(Playnite.SDK.Plugins.LibraryPlugin)), Is.True);
            Assert.That(typeof(EpicLibrary.EpicLibrary).IsSubclassOf(typeof(Playnite.SDK.Plugins.LibraryPlugin)), Is.True);
        }
    }
}
