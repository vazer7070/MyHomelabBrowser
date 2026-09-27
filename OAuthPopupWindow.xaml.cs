using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
using MyHomelabBrowser.classes.Profiles;
using MyHomelabBrowser.classes.Security;
using System.Threading.Tasks;
using System.Windows;

namespace MyHomelabBrowser
{
    public partial class OAuthPopupWindow : Window
    {
        public WebView2 Web => PopupWeb;

        public OAuthPopupWindow()
        {
            InitializeComponent();
        }

        public async Task EnsureReadyAsync(CoreWebView2Environment env, CoreWebView2ControllerOptions? options = null)
        {
            if (PopupWeb.CoreWebView2 == null)
            {
                if (options != null)
                    await PopupWeb.EnsureCoreWebView2Async(env, options);
                else
                    await PopupWeb.EnsureCoreWebView2Async(env);
            }

            if (PopupWeb.CoreWebView2 is not null)
            {
                BrowserCertificateTrustHost.Current.Attach(
                    PopupWeb.CoreWebView2,
                    AppDataContext.Root);
            }
        }
    }
}
