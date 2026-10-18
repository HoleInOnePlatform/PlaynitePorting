using SteamKit2;
using SteamLibrary.Services;
using System;
using System.IO;
using System.Text;

internal static class SteamInstallChecks
{
    internal static void Run()
    {
        var modern = Parse("\"libraryfolders\" { \"0\" { \"path\" \"C:/Steam\" } \"7\" { \"path\" \"D:/Games\" } }");
        var locations = SteamInstallService.ReadLocations(modern, "C:/Steam");
        Check(locations.Count == 2 && locations[1].Index == 7 && locations[1].Path == "D:/Games", "sparse Steam volume IDs remain intact");
        locations = SteamInstallService.ReadLocations(Parse("\"libraryfolders\" { \"TimeNextStatsReport\" \"123\" \"3\" \"E:/Games\" }"), "C:/Steam");
        Check(locations.Count == 2 && locations[0].Index == 0 && locations[0].Path == "C:/Steam" && locations[1].Index == 3, "legacy folders retain default Steam library");
        locations = SteamInstallService.ReadLocations(Parse("\"libraryfolders\" { \"0\" { \"path\" \"F:/Library\" } }"), "C:/Steam");
        Check(locations.Count == 1 && locations[0].Path == "F:/Library", "explicit volume zero is not overwritten");
        locations = SteamInstallService.ReadLocations(Parse("\"libraryfolders\" { \"-1\" \"E:/Games\" \"4\" {} \"5\" \"relative/path\" }"), "C:/Steam");
        Check(locations.Count == 1 && locations[0].Index == 0, "invalid location entries are excluded");
        Expect<InvalidDataException>(() => SteamInstallService.ReadLocations(Parse("\"libraryfolders\" { \"1\" \"D:/One\" \"1\" \"E:/Two\" }"), "C:/Steam"), "ambiguous volume IDs fail safely");
        Check(SteamInstallService.BuildArguments(1086940, 7) == "-silent +app_install 1086940 7", "silent command targets selected volume without install URL");
        Expect<ArgumentOutOfRangeException>(() => SteamInstallService.BuildArguments(0, 0), "invalid app ID rejected");
        Expect<ArgumentOutOfRangeException>(() => SteamInstallService.BuildArguments(1, -1), "invalid volume ID rejected");
        Console.WriteLine("8 Steam installation checks passed.");
    }

    private static KeyValue Parse(string text)
    {
        var data = new KeyValue();
        using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(text)))
            if (!data.ReadAsText(stream)) throw new Exception("Invalid test fixture.");
        return data;
    }

    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception(name);
        Console.WriteLine("PASS " + name);
    }

    private static void Expect<T>(Action action, string name) where T : Exception
    {
        try { action(); }
        catch (T) { Console.WriteLine("PASS " + name); return; }
        throw new Exception(name);
    }
}
