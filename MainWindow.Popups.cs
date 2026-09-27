using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // Fenêtres de connexion (OAuth) ouvertes par les pages.

        private OAuthPopupWindow? _oauthPopup;

        private CoreWebView2Environment? _oauthPopupEnvironment;

        private bool _oauthPopupIsPrivate;

        private bool _oauthHooksAttached = false;

        private WebView2? _oauthReturnWeb;

        private bool _oauthFinishing = false;

        /// <summary>
        /// La fenêtre ouverte par window.open doit partager l'environnement WebView2
        /// de l'onglet qui l'ouvre (profil ou navigation privée) : WebView2 refuse un
        /// NewWindow issu d'un autre environnement, et la session de connexion doit
        /// arriver dans les cookies de l'onglet d'origine.
        /// </summary>
        private async Task<OAuthPopupWindow> GetOrCreateOAuthPopupAsync(CoreWebView2Environment environment, bool isPrivate)
        {
            if (_oauthPopup != null &&
                (!ReferenceEquals(_oauthPopupEnvironment, environment) || _oauthPopupIsPrivate != isPrivate))
            {
                var previous = _oauthPopup;
                _oauthPopup = null;
                try { previous.Close(); } catch { }
            }

            if (_oauthPopup == null)
            {
                var popup = new OAuthPopupWindow { Owner = this };
                _oauthPopup = popup;
                _oauthPopupEnvironment = environment;
                _oauthPopupIsPrivate = isPrivate;
                _oauthHooksAttached = false;

                popup.Closed += (_, __) =>
                {
                    if (!ReferenceEquals(_oauthPopup, popup))
                        return;

                    _oauthPopup = null;
                    _oauthPopupEnvironment = null;
                    _oauthHooksAttached = false;
                    _oauthFinishing = false;
                    _oauthReturnWeb = null;
                };
            }

            if (!_oauthPopup.IsVisible)
                _oauthPopup.Show();

            await _oauthPopup.EnsureReadyAsync(
                environment,
                isPrivate ? CreatePrivateControllerOptions(environment) : null);

            if (!_oauthHooksAttached && _oauthPopup.Web?.CoreWebView2 != null)
            {
                _oauthHooksAttached = true;
                var popupWindow = _oauthPopup;

                popupWindow.Web.SourceChanged += async (_, __) =>
                {
                    try
                    {
                        var u = popupWindow.Web.Source?.ToString() ?? "";
                        FlashDbg($"[OAuthPopup] SourceChanged: {u}");

                        // Fin du parcours Gameforge : la page /message transmet le jeton
                        // à l'onglet d'origine puis la fenêtre peut disparaître.
                        bool done =
                            u.Contains("gameforge.com/service/external-auth", StringComparison.OrdinalIgnoreCase) &&
                            u.EndsWith("/message", StringComparison.OrdinalIgnoreCase);

                        if (!done || _oauthFinishing)
                            return;

                        _oauthFinishing = true;
                        await Task.Delay(500);
                        popupWindow.Hide();
                        await Task.Delay(200);
                        _oauthFinishing = false;
                    }
                    catch { }
                };
            }

            return _oauthPopup;
        }

        /// <summary>
        /// Détourne un window.open vers la fenêtre de connexion. Retourne false si
        /// WebView2 doit finalement gérer la fenêtre lui-même.
        /// </summary>
        private async Task<bool> TryRouteToPopupAsync(WebView2 opener, CoreWebView2NewWindowRequestedEventArgs ev)
        {
            var environment = opener.CoreWebView2?.Environment;
            if (environment == null)
                return false;

            bool isPrivate = opener.CoreWebView2!.Profile.IsInPrivateModeEnabled;
            _oauthReturnWeb = opener;

            var popup = await GetOrCreateOAuthPopupAsync(environment, isPrivate);
            if (popup.Web?.CoreWebView2 == null)
                return false;

            if (!popup.IsVisible)
                popup.Show();

            ev.NewWindow = popup.Web.CoreWebView2;
            ev.Handled = true;
            return true;
        }
    }
}
