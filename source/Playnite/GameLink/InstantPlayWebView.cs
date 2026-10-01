using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Playnite.GameLink
{
    // Standard CefSharp cannot decode GameLift's H.264 stream.
    internal sealed class InstantPlayWebView : IDisposable
    {
        private readonly WebView2 browser = new WebView2();
        private readonly Window window;
        private readonly TextBlock status = new TextBlock
        {
            Text = "스트리밍 웹뷰 초기화 중…", Foreground = Brushes.White,
            Background = Brushes.Black, Padding = new Thickness(12)
        };
        private readonly DispatcherTimer loadingTimeout = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        private bool initializationStarted;
        private string address;
        private bool disposed;
        private bool reconnecting;
        internal bool HostedPlayback { get; set; }
        public Window WindowHost => window;
        public event EventHandler RetryRequested;

        public InstantPlayWebView()
        {
            var panel = new DockPanel { LastChildFill = true };
            var controls = new StackPanel { Orientation = Orientation.Horizontal, Background = Brushes.Black };
            var retry = new Button { Content = "연결 다시 시도", Margin = new Thickness(4) };
            var close = new Button { Content = "닫기", Margin = new Thickness(4) };
            retry.Click += (s, e) => RetryRequested?.Invoke(this, EventArgs.Empty);
            close.Click += (s, e) => Close(); controls.Children.Add(retry); controls.Children.Add(close);
            DockPanel.SetDock(controls, Dock.Top); panel.Children.Add(controls);
            DockPanel.SetDock(status, Dock.Top);
            panel.Children.Add(status);
            panel.Children.Add(browser);
            // Use the native WPF Window template so the HWND-backed browser is actually hosted.
            window = new Window
            {
                Title = "즉시 플레이", Content = panel, Background = Brushes.Black,
                AllowsTransparency = false,
                WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
                WindowState = WindowState.Maximized
            };
            browser.Loaded += OnLoaded;
            window.PreviewKeyUp += OnKeyUp;
            loadingTimeout.Tick += OnLoadingTimeout;
        }

        public void Navigate(string url)
        {
            reconnecting = false;
            address = url;
            if (browser.CoreWebView2 != null) browser.CoreWebView2.Navigate(url);
        }
        public string GetCurrentAddress() => browser.CoreWebView2?.Source ?? address;
        public void Disconnect() { reconnecting = true; browser.CoreWebView2?.Navigate("about:blank"); }
        public void Open() => window.Show();
        public void Close() => window.Close();

        private async void OnLoaded(object sender, RoutedEventArgs args)
        {
            if (initializationStarted || disposed) return;
            initializationStarted = true;
            loadingTimeout.Start();
            try
            {
                var environment = await CoreWebView2Environment.CreateAsync(null,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "HoleInOne", "StreamWebView2"));
                if (disposed) return;
                await browser.EnsureCoreWebView2Async(environment);
                if (disposed) return;
                browser.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                if (HostedPlayback) browser.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = false;
                browser.CoreWebView2.NewWindowRequested += OnNewWindowRequested;
                browser.CoreWebView2.NavigationStarting += OnNavigationStarting;
                browser.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                browser.CoreWebView2.ProcessFailed += OnProcessFailed;
                if (disposed) return;
                browser.CoreWebView2.Navigate(address);
                browser.Focus();
            }
            catch (Exception exc)
            {
                if (disposed) return;
                loadingTimeout.Stop();
                ShowStatus("웹뷰 초기화 실패 (" + exc.GetType().Name + "). WebView2 Runtime 설치 상태를 확인해 주세요.");
            }
        }

        internal void ShowStatus(string message)
        {
            status.Text = message;
            status.Visibility = Visibility.Visible;
        }

        private void OnNavigationStarting(object sender, CoreWebView2NavigationStartingEventArgs args)
        {
            if (reconnecting && args.Uri == "about:blank") return;
            if (HostedPlayback && (args.NavigationKind == CoreWebView2NavigationKind.Reload || args.NavigationKind == CoreWebView2NavigationKind.BackOrForward))
            {
                args.Cancel = true;
                ShowStatus("AWS 스트림 링크는 다시 로드할 수 없습니다. 새 세션을 시작하려면 창을 닫고 즉시 플레이를 다시 실행해 주세요.");
                return;
            }
            if (Uri.TryCreate(address, UriKind.Absolute, out var expected) && expected.Scheme != "about" &&
                (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var actual) || actual.GetLeftPart(UriPartial.Authority) != expected.GetLeftPart(UriPartial.Authority)))
            { args.Cancel = true; return; }
            ShowStatus("스트리밍 페이지 로딩 중…");
            loadingTimeout.Start();
        }

        private void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs args)
        {
            loadingTimeout.Stop();
            if (args.IsSuccess) status.Visibility = Visibility.Collapsed;
            else ShowStatus("페이지 로딩 실패: " + args.WebErrorStatus + " (HTTP " + args.HttpStatusCode + "). F5로 다시 시도하세요.");
        }

        private void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs args)
        {
            loadingTimeout.Stop();
            ShowStatus("웹뷰 프로세스 오류: " + args.ProcessFailedKind + ". 창을 닫고 다시 실행해 주세요.");
        }

        private void OnLoadingTimeout(object sender, EventArgs args)
        {
            loadingTimeout.Stop();
            ShowStatus("페이지 로딩이 지연되고 있습니다. 네트워크를 확인한 뒤 F5로 다시 시도하세요.");
        }

        private void OnNewWindowRequested(object sender, CoreWebView2NewWindowRequestedEventArgs args)
        {
            args.Handled = true;
        }

        private void OnKeyUp(object sender, KeyEventArgs args)
        {
            if (args.Key == Key.Escape) { Close(); args.Handled = true; return; }
            if (args.Key == Key.F5) { RetryRequested?.Invoke(this, EventArgs.Empty); args.Handled = true; return; }
            if (args.Key == Key.F12 && browser.CoreWebView2 != null)
            {
                browser.CoreWebView2.OpenDevToolsWindow();
                args.Handled = true;
            }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            RetryRequested = null;
            loadingTimeout.Stop();
            loadingTimeout.Tick -= OnLoadingTimeout;
            browser.Loaded -= OnLoaded;
            window.PreviewKeyUp -= OnKeyUp;
            if (browser.CoreWebView2 != null)
            {
                browser.CoreWebView2.NewWindowRequested -= OnNewWindowRequested;
                browser.CoreWebView2.NavigationStarting -= OnNavigationStarting;
                browser.CoreWebView2.NavigationCompleted -= OnNavigationCompleted;
                browser.CoreWebView2.ProcessFailed -= OnProcessFailed;
            }
            window.Close();
            browser.Dispose();
        }
    }
}
