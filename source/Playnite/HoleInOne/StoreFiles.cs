using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Playnite.HoleInOne
{
    public static class StoreFiles
    {
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
