using Microsoft.Web.WebView2.Core;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Localization;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        private CoreWebView2EnvironmentOptions CreateWebViewEnvironmentOptions()
        {
            const string existingArguments =
                "--disable-features=SameSiteByDefaultCookies,CookiesWithoutSameSiteMustBeSecure";

            var options = new CoreWebView2EnvironmentOptions(
                SecureDnsConfiguration.BuildAdditionalBrowserArguments(
                    _settings.Settings,
                    existingArguments));

            // Menus contextuels, boîtes de dialogue et Accept-Language du moteur dans la langue
            // de l'interface. En français, on garde le comportement historique (langue de Windows).
            if (Loc.Language == "en")
                options.Language = "en-US";

            return options;
        }
    }
}
