using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.controles;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        /// <summary>
        /// Ajoute ou retire la page courante des favoris sans supposer qu'un
        /// WebTabContent possède obligatoirement un WebView2 actif.
        /// </summary>
        private void ToggleFavoriteSafe_Click(object sender, RoutedEventArgs e)
        {
            if (Tabs.SelectedItem is not TabItem tab ||
                tab.Tag is not WebTabContent web)
            {
                return;
            }

            // Les onglets internes, suspendus, personnalisés ou Legacy peuvent garder
            // un WebTabContent alors que la propriété Web vaut temporairement null.
            WebView2? webView = web.Web;
            string? url = webView?.Source?.AbsoluteUri;

            if (string.IsNullOrWhiteSpace(url) &&
                Uri.TryCreate(web.LegacyUrl, UriKind.Absolute, out Uri? legacyUri))
            {
                url = legacyUri.AbsoluteUri;
            }

            if (string.IsNullOrWhiteSpace(url))
                return;

            FavoriteItem? existing = _favorites.FirstOrDefault(f =>
                string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                RemoveFavorite(existing);
                return;
            }

            string title = webView?.CoreWebView2?.DocumentTitle?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(title) &&
                tab.Header is BrowserTabHeader header)
            {
                title = header.TabTitle.Trim();
            }

            if (string.IsNullOrWhiteSpace(title))
                title = url;

            AddFavorite(new FavoriteItem
            {
                Url = url,
                Title = title
            });
        }
    }
}
