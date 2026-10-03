using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Playnite.HoleInOne
{
    public sealed class InstalledStoreGame
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Directory { get; set; }
        public string LaunchUri { get; set; }
    }

    public static class StoreFiles
    {
        public static Dictionary<string, InstalledStoreGame> ReadSteam(string steamRoot)
        {
            var output = new Dictionary<string, InstalledStoreGame>(StringComparer.Ordinal);
            if (string.IsNullOrWhiteSpace(steamRoot) || !Directory.Exists(steamRoot)) return output;
            var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { steamRoot };
            var libraries = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
            if (File.Exists(libraries))
            {
                var root = VdfNode.Read(File.ReadAllText(libraries));
                if (root.Children.TryGetValue("libraryfolders", out var entries))
                {
                    foreach (var item in entries.Children)
                    {
                        if (!int.TryParse(item.Key, out _)) continue;
                        var path = item.Value.Get("path") ?? item.Value.Value;
                        if (!string.IsNullOrEmpty(path)) folders.Add(path);
                    }
                }
            }

            foreach (var folder in folders)
            {
                var apps = Path.Combine(folder, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (var file in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    try
                    {
                        var manifest = VdfNode.Read(File.ReadAllText(file)).Children["AppState"];
                        var id = manifest.Get("appid");
                        // Being present on disk is not enough: never accept downloading/update-required states.
                        if (!uint.TryParse(id, out _) || manifest.Get("StateFlags") != "4") continue;
                        var dir = SafeChild(Path.Combine(apps, "common"), manifest.Get("installdir"));
                        if (!Directory.Exists(dir)) continue;
                        output[id] = new InstalledStoreGame
                        {
                            Id = id, Name = manifest.Get("name"), Directory = dir,
                            LaunchUri = "steam://rungameid/" + id
                        };
                    }
                    catch (Exception e) when (e is IOException || e is ArgumentException || e is KeyNotFoundException) { }
                }
            }
            return output;
        }

        public static Dictionary<string, InstalledStoreGame> ReadEpic(string manifestDirectory)
        {
            var output = new Dictionary<string, InstalledStoreGame>(StringComparer.Ordinal);
            if (!Directory.Exists(manifestDirectory)) return output;
            foreach (var file in Directory.GetFiles(manifestDirectory, "*.item"))
            {
                try
                {
                    var item = JObject.Parse(File.ReadAllText(file));
                    var id = (string)item["AppName"];
                    var dir = (string)item["InstallLocation"];
                    var executable = (string)item["LaunchExecutable"];
                    if (string.IsNullOrEmpty(id) || (bool?)item["bIsIncompleteInstall"] == true ||
                        string.IsNullOrEmpty(dir) || !Path.IsPathRooted(dir) || string.IsNullOrEmpty(executable)) continue;
                    if (!File.Exists(SafeChild(dir, executable))) continue;
                    output[id] = new InstalledStoreGame
                    {
                        Id = id, Name = (string)item["DisplayName"], Directory = dir,
                        LaunchUri = "com.epicgames.launcher://apps/" + Uri.EscapeDataString(
                            (string)item["CatalogNamespace"] + ":" + (string)item["CatalogItemId"] + ":" + id) + "?action=launch&silent=true"
                    };
                }
                catch (Exception e) when (e is IOException || e is ArgumentException || e is Newtonsoft.Json.JsonException) { }
            }
            return output;
        }

        public static string SafeChild(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)) throw new ArgumentException("잘못된 게임 파일 경로임.");
            root = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            var result = Path.GetFullPath(Path.Combine(root, relative));
            if (!result.StartsWith(root, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("게임 폴더 밖의 경로임.");
            return result;
        }
    }

    // Steam's text KeyValues files. Handles escaped paths, nested objects and comments.
    public sealed class VdfNode
    {
        public string Value { get; private set; }
        public Dictionary<string, VdfNode> Children { get; } = new Dictionary<string, VdfNode>(StringComparer.OrdinalIgnoreCase);
        public string Get(string name) => Children.TryGetValue(name, out var item) ? item.Value : null;
        public static VdfNode Read(string text)
        {
            var tokens = Regex.Matches(text, "//[^\\r\\n]*|\"(?:\\\\.|[^\"\\\\])*\"|[{}]")
                .Cast<Match>().Select(m => m.Value).Where(t => !t.StartsWith("//")).ToArray();
            var index = 0;
            return Parse(tokens, ref index, false);
        }
        private static VdfNode Parse(string[] tokens, ref int index, bool nested)
        {
            var node = new VdfNode();
            while (index < tokens.Length)
            {
                if (tokens[index] == "}")
                {
                    if (!nested) throw new ArgumentException("Unexpected VDF object end.");
                    index++;
                    return node;
                }
                var key = Unquote(tokens[index++]);
                if (index >= tokens.Length) throw new ArgumentException("Incomplete VDF value.");
                if (tokens[index] == "{")
                {
                    index++;
                    node.Children[key] = Parse(tokens, ref index, true);
                }
                else node.Children[key] = new VdfNode { Value = Unquote(tokens[index++]) };
            }
            if (nested) throw new ArgumentException("Incomplete VDF object.");
            return node;
        }
        private static string Unquote(string value)
        {
            if (value.Length < 2 || value[0] != '"' || value[value.Length - 1] != '"') throw new ArgumentException("Invalid VDF token.");
            return value.Substring(1, value.Length - 2).Replace("\\\"", "\"").Replace("\\\\", "\\");
        }
    }
}
