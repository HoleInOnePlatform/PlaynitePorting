using CefSharp;
using CefSharp.Wpf;
using CefSharp.Wpf.Internals;
using System;
using System.Runtime.InteropServices;
using System.Windows.Input;

namespace Playnite.WebView
{
    // CEF 151's TranslateUiKeyEvent expects a scan code (e.g. 0xE048), not WM_KEYDOWN's LPARAM.
    internal sealed class GameKeyboardHandler : WpfLegacyKeyboardHandler
    {
        private readonly ChromiumWebBrowser browser;
        public GameKeyboardHandler(ChromiumWebBrowser browser) : base(browser) { this.browser = browser; }

        [DllImport("user32.dll")]
        private static extern uint MapVirtualKey(uint code, uint mapType);

        protected override IntPtr SourceHook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (handled || !browser.IsKeyboardFocused || browser.IsDisposed || !browser.IsBrowserInitialized)
                return IntPtr.Zero;
            if (message != 0x100 && message != 0x101 && message != 0x102
                && message != 0x104 && message != 0x105 && message != 0x106 && message != 0x286)
                return IntPtr.Zero;
            var key = wParam.ToInt32();
            if (message == 0x104 && key == 0x73) return IntPtr.Zero; // Keep Alt+F4 available to Windows.
            var bits = lParam.ToInt32();
            var scanCode = ((bits >> 16) & 0xff) | ((bits & (1 << 24)) != 0 ? 0xe000 : 0);
            var type = message == 0x100 || message == 0x104 ? KeyEventType.RawKeyDown
                : message == 0x101 || message == 0x105 ? KeyEventType.KeyUp : KeyEventType.Char;
            Send(key, scanCode, type, message >= 0x104 && message <= 0x106, (bits & (1 << 30)) != 0 && type == KeyEventType.RawKeyDown);
            handled = true;
            return IntPtr.Zero;
        }

        public override void HandleKeyPress(KeyEventArgs e)
        {
            // WPF may consume navigation keys before dispatching a native window message.
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key != Key.Tab && key != Key.Home && key != Key.End && key != Key.Up && key != Key.Down
                && key != Key.Left && key != Key.Right && !(key == Key.A && Keyboard.Modifiers == ModifierKeys.Control)) return;
            var virtualKey = KeyInterop.VirtualKeyFromKey(key);
            Send(virtualKey, (int)MapVirtualKey((uint)virtualKey, 4), e.IsDown ? KeyEventType.RawKeyDown : KeyEventType.KeyUp,
                e.Key == Key.System, e.IsRepeat);
            e.Handled = true;
        }

        private void Send(int key, int scan, KeyEventType type, bool system, bool repeat)
        {
            var modifiers = CefEventFlags.None;
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0) modifiers |= CefEventFlags.ShiftDown;
            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0) modifiers |= CefEventFlags.ControlDown;
            if ((Keyboard.Modifiers & ModifierKeys.Alt) != 0) modifiers |= CefEventFlags.AltDown;
            if ((Keyboard.Modifiers & ModifierKeys.Windows) != 0) modifiers |= CefEventFlags.CommandDown;
            if (Keyboard.IsKeyToggled(Key.CapsLock)) modifiers |= CefEventFlags.CapsLockOn;
            if (Keyboard.IsKeyToggled(Key.NumLock)) modifiers |= CefEventFlags.NumLockOn;
            if (repeat) modifiers |= CefEventFlags.IsRepeat;
            if (scan == 0x2a || scan == 0x1d || scan == 0x38) modifiers |= CefEventFlags.IsLeft;
            if (scan == 0x36 || scan == 0xe01d || scan == 0xe038) modifiers |= CefEventFlags.IsRight;
            if ((key >= 0x60 && key <= 0x6f) || scan == 0xe01c
                || (scan < 0xe000 && key >= 0x21 && key <= 0x2e && key != 0x2c)) modifiers |= CefEventFlags.IsKeyPad;
            if (!browser.IsDisposed && browser.IsBrowserInitialized)
                browser.GetBrowserHost().SendKeyEvent(new CefSharp.KeyEvent
                {
                    WindowsKeyCode = key, NativeKeyCode = scan, Type = type,
                    IsSystemKey = system, Modifiers = modifiers
                });
        }
    }
}
