using Microsoft.Web.WebView2.Core;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Localization;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        private CoreWebView2EnvironmentOptions CreateWebViewEnvironmentOptions()
        {
            // Les protections SameSite des cookies (contre les requêtes forgées) restent
            // actives : elles étaient désactivées pour tous les sites.
            var options = new CoreWebView2EnvironmentOptions(
                SecureDnsConfiguration.BuildAdditionalBrowserArguments(
                    _settings.Settings,
                    string.Empty));

            // Menus contextuels, boîtes de dialogue et Accept-Language du moteur dans la langue
            // de l'interface. En français, on garde le comportement historique (langue de Windows).
            if (Loc.Language == "en")
                options.Language = "en-US";

            return options;
        }
    }
}
