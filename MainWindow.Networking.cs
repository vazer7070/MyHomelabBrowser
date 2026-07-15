using Microsoft.Web.WebView2.Core;
using MyHomelabBrowser.classes;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        private CoreWebView2EnvironmentOptions CreateWebViewEnvironmentOptions()
        {
            const string existingArguments =
                "--disable-features=SameSiteByDefaultCookies,CookiesWithoutSameSiteMustBeSecure";

            return new CoreWebView2EnvironmentOptions(
                SecureDnsConfiguration.BuildAdditionalBrowserArguments(
                    _settings.Settings,
                    existingArguments));
        }
    }
}
