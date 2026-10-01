using System;
using System.IO;
using System.Threading;
using CefSharp;
using CefSharp.OffScreen;
using Playnite;

public class CefStartupProbe
{
    [STAThread]
    public static int Main(string[] args)
    {
        bool initialized = false;
        try
        {
            PlaynitePaths.UpdateUserDataDir(Path.GetFullPath(args[0]));
            CefTools.ConfigureCef(false);
            initialized = CefTools.IsInitialized;
            if (!initialized) throw new Exception("Production ConfigureCef failed.");
            using (var browser = new ChromiumWebBrowser("data:text/html,<html><title>HoleInOne startup probe</title><body>ready</body></html>"))
            {
                var deadline = DateTime.UtcNow.AddSeconds(25);
                while ((!browser.IsBrowserInitialized || browser.IsLoading) && DateTime.UtcNow < deadline)
                    Thread.Sleep(50);
                if (!browser.IsBrowserInitialized) throw new Exception("CEF browser initialization timed out.");
                JavascriptResponse result = null;
                while (DateTime.UtcNow < deadline)
                {
                    var task = browser.EvaluateScriptAsync("document.title + ':' + document.body.textContent");
                    if (!task.Wait(3000)) throw new Exception("CEF JavaScript execution timed out.");
                    result = task.Result;
                    if (result.Success && Convert.ToString(result.Result) == "HoleInOne startup probe:ready") break;
                    Thread.Sleep(50);
                }
                if (result == null || !result.Success || Convert.ToString(result.Result) != "HoleInOne startup probe:ready")
                    throw new Exception("CEF subprocess did not load the expected page.");
                Console.WriteLine("PASS: production ConfigureCef + custom subprocess + offscreen page/JavaScript (x86).");
                Console.WriteLine(PlaynitePaths.BrowserProcessExecutablePath);
            }
            return 0;
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 1; }
        finally { if (initialized) CefTools.Shutdown(); }
    }
}
