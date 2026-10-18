using System;
using Playnite.SDK.Models;

namespace Playnite.HoleInOne
{
    public static class BuiltinStoreLibrary
    {
        public static readonly Guid SteamId = new Guid("cb91dfc9-b977-43bf-8e70-55f46e410fab");
        public static readonly Guid EpicId = new Guid("00000002-dbd1-46c6-b5d0-b1ba559d10e4");
        public static string GameKey(Game game) =>
            (game.PluginId == SteamId ? "steam-" : game.PluginId == EpicId ? "epic-" : throw new InvalidOperationException("지원하지 않는 스토어임.")) + game.GameId;
    }
}
