using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System;
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

        public async Task EnsureReadyAsync(CoreWebView2Environment env)
        {
            await PopupWeb.EnsureCoreWebView2Async(env);
        }
        public void EnableAutoCloseOnSuccess()
        {
            PopupWeb.CoreWebView2.NavigationCompleted += (_, __) =>
            {
                var uri = PopupWeb.Source?.ToString() ?? "";
                if (uri.Contains("gameforge.com", StringComparison.OrdinalIgnoreCase) &&
                    !uri.Contains("/external-auth/", StringComparison.OrdinalIgnoreCase))
                {
                    Close();
                }
            };
        }

    }
}
