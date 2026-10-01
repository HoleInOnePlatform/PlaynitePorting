using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using Playnite.Common.Web;
using Playnite.SDK.Data;
using Steam.Models;
using Newtonsoft.Json;

namespace Steam
{
    public class WebApiClient : IDisposable
    {
        private readonly WebClient webClient;
        private readonly Func<string, string> downloadString;

        public WebApiClient(Func<string, string> downloadString = null)
        {
            webClient = new CustomWebClient { Encoding = Encoding.UTF8 };
            this.downloadString = downloadString ?? webClient.DownloadString;
        }

        public AppReviewsResult GetUserRating(uint appId, bool allLanguages)
        {
            var url = $"https://store.steampowered.com/appreviews/{appId}?json=1&purchase_type=all";
            if (allLanguages)
                url += "&language=all";

            return JsonConvert.DeserializeObject<AppReviewsResult>(downloadString(url));
        }

        public StoreAppDetailsResult.AppDetails GetStoreAppDetail(uint appId, string languageKey)
        {
            var url = $"https://store.steampowered.com/api/appdetails?appids={appId}&l={Uri.EscapeDataString(languageKey ?? "english")}";
            var parsedData = JsonConvert.DeserializeObject<Dictionary<string, StoreAppDetailsResult>>(downloadString(url));
            if (parsedData == null || !parsedData.TryGetValue(appId.ToString(), out var response) || response == null)
            {
                return null;
            }

            // No store data for this appid
            if (response.success != true)
            {
                return null;
            }

            return response.data;
        }

        public void Dispose()
        {
            webClient.Dispose();
        }
    }
}
