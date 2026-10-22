using Playnite.HoleInOne;

static class StoreChecks
{
    public static void Run(Action<bool, string> check)
    {
        var root = Path.Combine(Path.GetTempPath(), "HoleInOne-Stores");
        check(StoreFiles.SafeChild(root, "game/save") == Path.Combine(root, "game", "save"),
            "save paths remain inside the requested root");
        foreach (var path in new[] { "../outside", "../HoleInOne-Stores-other/save", root, "" })
        {
            var rejected = false;
            try { StoreFiles.SafeChild(root, path); }
            catch (ArgumentException) { rejected = true; }
            check(rejected, "rooted, empty and escaping paths are rejected");
        }
        var vdf = VdfNode.Read("\"libraryfolders\" { // comment\n \"1\" { \"path\" \"D:\\\\Games\" } }");
        check(vdf.Children["libraryfolders"].Children["1"].Get("path") == @"D:\Games",
            "shared VDF parser preserves nested values and escaped paths");
        var incomplete = false;
        try { VdfNode.Read("\"AppState\" {"); }
        catch (ArgumentException) { incomplete = true; }
        check(incomplete, "partial store metadata is rejected");
    }
}
