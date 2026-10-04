using CefSharp;
using CefSharp.Handler;

namespace Playnite.WebView
{
    // Game-session termination is owned by the launcher. A page cannot block leaving it.
    // Authentication and other non-game webviews keep CefSharp's default dialogs.
    internal sealed class GameJsDialogHandler : JsDialogHandler
    {
        protected override bool OnBeforeUnloadDialog(IWebBrowser chromiumWebBrowser, IBrowser browser,
            string messageText, bool isReload, IJsDialogCallback callback)
        {
            using (callback) callback.Continue(true);
            return true;
        }
    }

    internal sealed class GameContextMenuHandler : ContextMenuHandler
    {
        protected override void OnBeforeContextMenu(IWebBrowser chromiumWebBrowser, IBrowser browser,
            IFrame frame, IContextMenuParams parameters, IMenuModel model) => model.Clear();
    }
}
