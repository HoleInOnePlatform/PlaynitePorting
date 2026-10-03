using CefSharp;
using Newtonsoft.Json;
using Playnite.Windows;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length != 1) throw new ArgumentException("Pass the built HoleInOne directory.");
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var settings = new CefSharp.Wpf.CefSettings
        {
            BrowserSubprocessPath = Path.Combine(Path.GetFullPath(args[0]), "Playnite.BrowserProcess.exe"),
            CachePath = Path.Combine(Path.GetTempPath(), "HoleInOne-input-checks-" + Guid.NewGuid().ToString("N")),
            WindowlessRenderingEnabled = true
        };
        settings.RootCachePath = settings.CachePath;
        settings.CefCommandLineArgs.Add("disable-gpu", "1");
        settings.CefCommandLineArgs.Add("do-not-de-elevate");
        if (!Cef.Initialize(settings)) return 1;
        var exitCode = 1;
        var window = new WebViewWindow { Width = 500, Height = 300, Left = -10000, Top = -10000, ShowInTaskbar = false };
        window.Template = (System.Windows.Controls.ControlTemplate)System.Windows.Markup.XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Window'><AdornerDecorator><ContentPresenter Content='{TemplateBinding Content}'/></AdornerDecorator></ControlTemplate>");
        var browser = (CefSharp.Wpf.ChromiumWebBrowser)window.FindName("Browser");
        browser.KeyboardHandler = new KeyProbe();
        window.EnableGameInput();
        window.Loaded += async (_, __) =>
        {
            try
            {
                browser.Address = "data:text/html,<html><body tabindex='0'>keyboard test<script>window.keys=[];['keydown','keyup'].forEach(t=>document.addEventListener(t,e=>{e.preventDefault();keys.push({type:e.type,code:e.code});}));</script></body></html>";
                await Until(async () =>
                {
                    if (!browser.CanExecuteJavascriptInMainFrame) return false;
                    return (await browser.EvaluateScriptAsync("Array.isArray(window.keys)")).Result is true;
                });
                Console.WriteLine("Browser test page ready.");
                window.Activate();
                browser.Focus();
                await Until(() => Task.FromResult(browser.IsKeyboardFocused));
                Console.WriteLine("Browser keyboard focus acquired.");
                await browser.EvaluateScriptAsync("document.body.focus()");
                await Task.Delay(100);
                Console.WriteLine("Keyboard handler: " + browser.WpfKeyboardHandler.GetType().Name);
                var handle = new WindowInteropHelper(window).Handle;
                // Windows key messages include scan codes and the extended-key bit.
                var keys = new[] { (0x57, 0x11, "KeyW", false), (0x1B, 0x01, "Escape", false),
                    (0x26, 0x48, "ArrowUp", true), (0x09, 0x0F, "Tab", false), (0x20, 0x39, "Space", false) };
                foreach (var key in keys)
                {
                    var bits = 1 | (key.Item2 << 16) | (key.Item4 ? 1 << 24 : 0);
                    SendMessage(handle, 0x0100, new IntPtr(key.Item1), new IntPtr(bits));
                    SendMessage(handle, 0x0101, new IntPtr(key.Item1), new IntPtr(unchecked(bits | (int)0xC0000000)));
                }
                Console.WriteLine("Native key messages sent.");
                await Until(async () => Convert.ToInt32((await browser.EvaluateScriptAsync("keys.length")).Result) >= keys.Length * 2);
                var response = await browser.EvaluateScriptAsync("JSON.stringify(keys)");
                var events = JsonConvert.DeserializeObject<List<KeyEvent>>((string)response.Result);
                if (events.Count != keys.Length * 2) throw new Exception("Duplicate key events.");
                for (var index = 0; index < keys.Length; index++)
                {
                    if (events[index * 2].type != "keydown" || events[index * 2 + 1].type != "keyup"
                        || events[index * 2].code != keys[index].Item3 || events[index * 2 + 1].code != keys[index].Item3)
                        throw new Exception("Incorrect DOM key event: " + JsonConvert.SerializeObject(events));
                    Console.WriteLine("PASS native down/up -> DOM " + keys[index].Item3);
                }
                exitCode = 0;
            }
            catch (Exception error) { Console.Error.WriteLine(error); }
            finally { window.Close(); browser.Dispose(); app.Shutdown(); }
        };
        window.Show();
        app.Run();
        Cef.Shutdown();
        return exitCode;
    }

    private static async Task Until(Func<Task<bool>> ready)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await ready()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Browser input check timed out.");
    }

    private sealed class KeyEvent { public string type { get; set; } public string code { get; set; } }

    private sealed class KeyProbe : IKeyboardHandler
    {
        public bool OnPreKeyEvent(IWebBrowser web, IBrowser browser, KeyType type, int key, int native,
            CefEventFlags modifiers, bool system, ref bool shortcut)
        {
            Console.WriteLine($"CEF key={key}, native={native:X8}, type={type}");
            return false;
        }
        public bool OnKeyEvent(IWebBrowser web, IBrowser browser, KeyType type, int key, int native,
            CefEventFlags modifiers, bool system) => false;
    }
}
