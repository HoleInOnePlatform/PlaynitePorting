using CefSharp;
using Playnite.Controls;
using Playnite.Native;
using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Playnite.Windows
{
    /// <summary>
    /// Interaction logic for WebViewWindow.xaml
    /// </summary>
    public partial class WebViewWindow :  WindowBase
    {
        private bool gameInputEnabled;
        private bool gameFullscreen;
        private HwndSource fullscreenSource;

        public WebViewWindow() : base()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            if (gameFullscreen) ApplyGameFullscreen();
            Browser.Focus();
        }

        public void EnableGameMode()
        {
            if (gameFullscreen) return;
            gameFullscreen = true;
            AddressBar.Visibility = Visibility.Collapsed;
            // Remove the application theme's caption, resize frame and content margins too.
            Style = null;
            WindowChrome.SetWindowChrome(this, null);
            Template = (System.Windows.Controls.ControlTemplate)Resources["GameWindowTemplate"];
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            BorderThickness = new Thickness(0);
            Background = Brushes.Black;
            Browser.JsDialogHandler = new Playnite.WebView.GameJsDialogHandler();
            Browser.MenuHandler = new Playnite.WebView.GameContextMenuHandler();
            SourceInitialized += GameSourceInitialized;
            Deactivated += GameWindowDeactivated;
            Closed += GameWindowClosed;
            EnableGameInput();
            if (new WindowInteropHelper(this).Handle != IntPtr.Zero) GameSourceInitialized(this, EventArgs.Empty);
        }

        private void GameSourceInitialized(object sender, EventArgs e)
        {
            if (fullscreenSource != null) return;
            var handle = new WindowInteropHelper(this).Handle;
            fullscreenSource = HwndSource.FromHwnd(handle);
            fullscreenSource?.AddHook(GameFullscreenHook);
            ApplyGameFullscreen();
        }

        private void ApplyGameFullscreen()
        {
            if (!gameFullscreen || WasClosed || WindowState == WindowState.Minimized) return;
            var handle = new WindowInteropHelper(this).Handle;
            if (handle == IntPtr.Zero) return;
            var screenHandle = !IsLoaded && Owner != null ? new WindowInteropHelper(Owner).Handle : handle;
            var bounds = System.Windows.Forms.Screen.FromHandle(screenHandle).Bounds;
            var dpi = VisualTreeHelper.GetDpi(this);
            WindowState = WindowState.Normal;
            Left = bounds.Left / dpi.DpiScaleX;
            Top = bounds.Top / dpi.DpiScaleY;
            Width = bounds.Width / dpi.DpiScaleX;
            Height = bounds.Height / dpi.DpiScaleY;
            Topmost = IsActive;
            // Use the full physical monitor, not WorkingArea (which excludes the taskbar).
            User32.SetWindowPos(handle, IsActive ? Winuser.HWND_TOPMOST : Winuser.HWND_NOTOPMOST,
                bounds.Left, bounds.Top, bounds.Width, bounds.Height, 0x0010u /* SWP_NOACTIVATE */);
        }

        private IntPtr GameFullscreenHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (message == 0x007E /* WM_DISPLAYCHANGE */ || message == 0x02E0 /* WM_DPICHANGED */)
                Dispatcher.BeginInvoke(new Action(ApplyGameFullscreen));
            return IntPtr.Zero;
        }

        private void GameWindowDeactivated(object sender, EventArgs e) => Topmost = false;

        private void GameWindowClosed(object sender, EventArgs e)
        {
            fullscreenSource?.RemoveHook(GameFullscreenHook);
            fullscreenSource = null;
            SourceInitialized -= GameSourceInitialized;
            Deactivated -= GameWindowDeactivated;
            Closed -= GameWindowClosed;
        }

        public void EnableGameInput()
        {
            if (gameInputEnabled) return;
            gameInputEnabled = true;
            // Preserve native scan codes so DOM KeyboardEvent.code identifies game keys.
            InstallGameKeyboardHandler();
            InputLanguageManager.Current.InputLanguageChanged += GameInputLanguageChanged;
            Activated += GameWindowActivated;
            Closed += (_, __) => InputLanguageManager.Current.InputLanguageChanged -= GameInputLanguageChanged;
        }

        private void GameInputLanguageChanged(object sender, InputLanguageEventArgs e)
        {
            // CefSharp may install its IME handler after a keyboard-layout change.
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                if (!Browser.IsDisposed) InstallGameKeyboardHandler();
            }));
        }

        private void InstallGameKeyboardHandler()
        {
            if (Browser.WpfKeyboardHandler is Playnite.WebView.GameKeyboardHandler) return;
            Browser.WpfKeyboardHandler.Dispose();
            Browser.WpfKeyboardHandler = new Playnite.WebView.GameKeyboardHandler(Browser);
            Browser.WpfKeyboardHandler.Setup(System.Windows.PresentationSource.FromVisual(Browser) as System.Windows.Interop.HwndSource);
        }

        private void GameWindowActivated(object sender, System.EventArgs e)
        {
            if (gameFullscreen) ApplyGameFullscreen();
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                if (IsActive && !Browser.IsDisposed) Browser.Focus();
            }));
        }

        private void WindowBase_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (!gameFullscreen && Browser.IsInitialized && e.Key == System.Windows.Input.Key.F12)
            {
                Browser.ShowDevTools();
            }
        }
    }
}
