using CefSharp;
using Playnite.Controls;
using System.Windows;
using System.Windows.Input;

namespace Playnite.Windows
{
    /// <summary>
    /// Interaction logic for WebViewWindow.xaml
    /// </summary>
    public partial class WebViewWindow :  WindowBase
    {
        private bool gameInputEnabled;

        public WebViewWindow() : base()
        {
            InitializeComponent();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            Browser.Focus();
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
            Dispatcher.BeginInvoke(new System.Action(() =>
            {
                if (IsActive && !Browser.IsDisposed) Browser.Focus();
            }));
        }

        private void WindowBase_KeyUp(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (Browser.IsInitialized && e.Key == System.Windows.Input.Key.F12)
            {
                Browser.ShowDevTools();
            }
        }
    }
}
