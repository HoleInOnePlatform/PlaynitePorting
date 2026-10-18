using CefSharp;
using Newtonsoft.Json;
using Playnite.Windows;
using Playnite.DesktopApp.HoleInOne;
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Controls;
using System.Windows.Media;

internal static class Program
{
    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr handle, int message, IntPtr wParam, IntPtr lParam);

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length < 1 || args.Length > 2 || (args.Length == 2 && args[1] != "--fullscreen"))
            throw new ArgumentException("Pass the built HoleInOne directory and optionally --fullscreen.");
        var fullscreen = args.Length == 2;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        InstallationChecks.Run();
        var settings = new CefSharp.Wpf.CefSettings
        {
            BrowserSubprocessPath = Path.Combine(Path.GetFullPath(args[0]), "HoleInOne.BrowserProcess.exe"),
            CachePath = Path.Combine(Path.GetTempPath(), "HoleInOne-input-checks-" + Guid.NewGuid().ToString("N")),
            WindowlessRenderingEnabled = true
        };
        settings.RootCachePath = settings.CachePath;
        settings.CefCommandLineArgs.Add("disable-gpu", "1");
        settings.CefCommandLineArgs.Add("do-not-de-elevate");
        if (!Cef.Initialize(settings)) return 1;
        var exitCode = 1;
        var window = new WebViewWindow { Width = 1100, Height = 650, Left = -10000, Top = -10000, ShowInTaskbar = false };
        window.Template = (System.Windows.Controls.ControlTemplate)System.Windows.Markup.XamlReader.Parse(
            "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='Window'><AdornerDecorator><ContentPresenter Content='{TemplateBinding Content}'/></AdornerDecorator></ControlTemplate>");
        var browser = (CefSharp.Wpf.ChromiumWebBrowser)window.FindName("Browser");
        browser.KeyboardHandler = new KeyProbe();
        if (fullscreen)
        {
            window.EnableGameMode();
            foreach (var reload in new[] { false, true })
            {
                var callback = new DialogProbe();
                if (!browser.JsDialogHandler.OnBeforeUnloadDialog(browser, null, "Leave Site?", reload, callback) ||
                    callback.Continuations != 1 || !callback.Accepted || !callback.IsDisposed)
                    throw new Exception("Game page exit/reload must continue without a Leave Site dialog.");
            }
            Console.WriteLine("PASS game exit and reload dialogs are accepted exactly once without showing a popup");
        }
        else window.EnableGameInput();
        // Download finishes before the browser or the remote game has loaded.
        var prompt = new DownloadHandoffPrompt(window, (Panel)window.FindName("PanelOverlay"));
        var transfers = 0;
        prompt.TransferRequested += (_, __) => transfers++;
        window.Loaded += async (_, __) =>
        {
            try
            {
                if (fullscreen)
                {
                    window.Activate();
                    var fullscreenHandle = new WindowInteropHelper(window).Handle;
                    var bounds = System.Windows.Forms.Screen.FromHandle(fullscreenHandle).Bounds;
                    GetWindowRect(fullscreenHandle, out var rect);
                    if (rect.Left != bounds.Left || rect.Top != bounds.Top || rect.Right != bounds.Right || rect.Bottom != bounds.Bottom ||
                        window.WindowStyle != WindowStyle.None || window.ResizeMode != ResizeMode.NoResize || !window.Topmost ||
                        ((TextBox)window.FindName("AddressBar")).Visibility != Visibility.Collapsed)
                        throw new Exception("Game window must cover the full monitor including the taskbar without address bar or frame.");
                    Console.WriteLine("PASS borderless game window covers full monitor bounds including taskbar area");
                }
                AssertTransferVisible(window);
                Console.WriteLine("PASS handoff button visible before game page loads");
                browser.Address = "data:text/html,<html><body tabindex='0'>keyboard test<script>window.keys=[];['keydown','keyup'].forEach(t=>document.addEventListener(t,e=>{e.preventDefault();keys.push({type:e.type,code:e.code});}));</script></body></html>";
                await Until(async () =>
                {
                    if (!browser.CanExecuteJavascriptInMainFrame) return false;
                    return (await browser.EvaluateScriptAsync("Array.isArray(window.keys)")).Result is true;
                });
                Console.WriteLine("Browser test page ready.");
                AssertTransferVisible(window);
                await browser.EvaluateScriptAsync("window.keys = null");
                browser.Reload();
                await Until(async () => browser.CanExecuteJavascriptInMainFrame &&
                    (await browser.EvaluateScriptAsync("Array.isArray(window.keys)")).Result is true);
                AssertTransferVisible(window);
                var transfer = Find<Button>((Panel)window.FindName("PanelOverlay"));
                transfer.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                if (transfers != 1) throw new Exception("Handoff button did not request transfer.");
                prompt.Dispose();
                Console.WriteLine("PASS handoff button survives browser initialization and reload and remains clickable");
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

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr handle, out WindowRect rect);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowRect { public int Left, Top, Right, Bottom; }

    private sealed class DialogProbe : IJsDialogCallback
    {
        public int Continuations;
        public bool Accepted;
        public bool IsDisposed { get; private set; }
        public void Continue(bool success, string userInput) { Continuations++; Accepted = success; }
        public void Continue(bool success) => Continue(success, null);
        public void Dispose() => IsDisposed = true;
    }

    private static void AssertTransferVisible(WebViewWindow window)
    {
        window.UpdateLayout();
        var button = Find<Button>((Panel)window.FindName("PanelOverlay"));
        if (button == null || !button.IsVisible || button.ActualWidth <= 0 || !button.IsEnabled)
            throw new Exception("Handoff button is not visible and enabled.");
        var point = button.TranslatePoint(new Point(button.ActualWidth / 2, button.ActualHeight / 2), window);
        var hit = window.InputHitTest(point) as DependencyObject;
        while (hit != null && hit != button) hit = VisualTreeHelper.GetParent(hit);
        if (hit != button) throw new Exception("Browser obscured the handoff button.");
    }

    private static T Find<T>(DependencyObject root) where T : DependencyObject
    {
        if (root is T result) return result;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var found = Find<T>(VisualTreeHelper.GetChild(root, index));
            if (found != null) return found;
        }
        return null;
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
