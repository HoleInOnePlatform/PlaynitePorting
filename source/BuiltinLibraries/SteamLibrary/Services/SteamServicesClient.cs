using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace SteamLibrary.Services
{
    // HoleInOne: use the upstream Steam store client instead of Playnite's metadata server.
    public sealed class SteamServicesClient : IDisposable
    {
        private readonly string language;
        public SteamServicesClient(string language) { this.language = language; }

        public Task<List<SteamAppInfo>> GetAppInfos(List<uint> appIds)
        {
            return Task.Run(() =>
            {
                var items = new List<SteamAppInfo>();
                using (var store = new global::Steam.WebApiClient())
                {
                    foreach (var id in appIds)
                    {
                        var info = store.GetStoreAppDetail(id, language);
                        if (info != null) items.Add(new SteamAppInfo { AppId = id, Name = info.name, Type = info.type });
                    }
                }
                return items;
            });
        }
        public void Dispose() { }
    }

    public sealed class SteamAppInfo
    {
        public uint AppId { get; set; }
        public string Name { get; set; }
        public string Type { get; set; }
        public Dictionary<string, string> LocalizedNames { get; set; }
    }
}
