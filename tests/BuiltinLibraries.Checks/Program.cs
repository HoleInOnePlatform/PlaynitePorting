using Moq;
using Playnite.SDK;
using Playnite.SDK.Events;
using SteamLibrary.Services;
using System;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    private const string ValidPage = "<div id='application_config' data-userinfo='{\"logged_in\":true,\"steamid\":\"76561198000000000\"}' data-store_user_config='{\"webapi_token\":\"fixture-token\"}'></div>";

    [STAThread]
    private static int Main()
    {
        try
        {
            CheckLogin("successful login closes browser", ValidPage, "https://store.steampowered.com/explore/", true);
            CheckLogin("logged out does not succeed", ValidPage.Replace("true", "false"), "https://store.steampowered.com/explore/", false);
            CheckLogin("login redirect is not completion", ValidPage, "https://store.steampowered.com/login/", false);
            CheckLogin("missing token is not completion", ValidPage.Replace("\"fixture-token\"", "null"), "https://store.steampowered.com/explore/", false);
            CheckLogin("empty token is not completion", ValidPage.Replace("fixture-token", ""), "https://store.steampowered.com/explore/", false);
            CheckLogin("changed markup is not completion", "<html>unrecognized</html>", "https://store.steampowered.com/explore/", false);
            Console.WriteLine("6 upstream Steam authentication checks passed.");
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
    }

    private static void CheckLogin(string name, string html, string address, bool shouldSucceed)
    {
        using (var closed = new ManualResetEventSlim())
        {
            var view = new Mock<IWebView>();
            var factory = new Mock<IWebViewFactory>();
            var api = new Mock<IPlayniteAPI>();
            api.SetupGet(x => x.WebViews).Returns(factory.Object);
            factory.Setup(x => x.CreateView(600, 720)).Returns(view.Object);
            view.Setup(x => x.GetCurrentAddress()).Returns(address);
            view.Setup(x => x.GetPageSourceAsync()).ReturnsAsync(html);
            view.Setup(x => x.Close()).Callback(() => closed.Set());
            view.Setup(x => x.OpenDialog()).Returns(() =>
            {
                view.Raise(x => x.LoadingChanged += null, view.Object, new WebViewLoadingChangedEventArgs { IsLoading = false });
                closed.Wait(shouldSucceed ? 3000 : 200);
                return (bool?)true;
            });
            var result = new SteamStoreService(api.Object).Login();
            if (result.HasValue != shouldSucceed || closed.IsSet != shouldSucceed)
                throw new Exception(name);
            if (shouldSucceed && (result.Value.UserId != 76561198000000000UL || result.Value.AccessToken != "fixture-token"))
                throw new Exception("Wrong authenticated account returned.");
            view.Verify(x => x.Dispose(), Times.Once);
            Console.WriteLine("PASS " + name);
        }
    }
}
