using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Profiles;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // ---------------------------
        // Zoom mémorisé par site
        // ---------------------------
        private readonly SiteZoomStore _siteZoom = new(() => Path.Combine(AppDataContext.Root, "zoom.json"));

        /// <summary>
        /// Le zoom WebView2 appartient au contrôle, pas au site : on le restaure à chaque
        /// changement d'adresse et on mémorise celui choisi par l'utilisateur (Ctrl+molette,
        /// Ctrl+/−). Les onglets privés lisent les zooms mémorisés sans en enregistrer.
        /// </summary>
        void AttachSiteZoom(WebView2 web, WebTabContent content)
        {
            double? applied = null;

            void Apply()
            {
                double target = _siteZoom.Get(web.Source);
                if (Math.Abs(web.ZoomFactor - target) > 0.005)
                {
                    applied = target;
                    web.ZoomFactor = target;
                }

                if (IsActiveTab(content))
                    UpdateZoomIndicator();
            }

            web.SourceChanged += (_, _) => Apply();

            web.ZoomFactorChanged += (_, _) =>
            {
                // Changement provoqué par Apply : rien à mémoriser.
                if (applied.HasValue && Math.Abs(web.ZoomFactor - applied.Value) < 0.005)
                    applied = null;
                else if (!content.IsPrivate)
                    _siteZoom.Set(web.Source, web.ZoomFactor);

                if (IsActiveTab(content))
                    UpdateZoomIndicator();
            };

            Apply();
        }

        private WebView2? GetActiveZoomableWeb()
            => Tabs.SelectedItem is TabItem { Tag: WebTabContent { IsLegacyExternal: false, IsCustomView: false, Web: { CoreWebView2: not null } web } }
                ? web
                : null;

        void ZoomActivePage(int direction)
        {
            WebView2? web = GetActiveZoomableWeb();
            if (web == null)
                return;

            web.ZoomFactor = direction == 0 ? 1.0 : SiteZoomStore.Step(web.ZoomFactor, direction);
        }

        void UpdateZoomIndicator()
        {
            WebView2? web = GetActiveZoomableWeb();
            double zoom = web?.ZoomFactor ?? 1.0;
            bool visible = web != null && Math.Abs(zoom - 1.0) > 0.005;

            ZoomButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
            ZoomButtonText.Text = SiteZoomStore.Format(zoom);
            ZoomButton.ToolTip = Tr("Zoom : {0} — cliquer pour revenir à 100 %", SiteZoomStore.Format(zoom));
        }

        private void ZoomButton_Click(object sender, RoutedEventArgs e) => ZoomActivePage(0);

        private void MainMenu_ZoomIn_Click(object sender, RoutedEventArgs e) => ZoomActivePage(1);

        private void MainMenu_ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomActivePage(-1);

        private void MainMenu_ZoomReset_Click(object sender, RoutedEventArgs e) => ZoomActivePage(0);

        // ---------------------------
        // Impression (Ctrl+P)
        // ---------------------------
        void PrintActivePage()
        {
            if (GetActiveZoomableWeb()?.CoreWebView2 is not CoreWebView2 core)
                return;

            try
            {
                core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser);
            }
            catch (Exception ex)
            {
                ShowToast(Tr("Impression impossible"), ex.Message, ToastKind.Warning);
            }
        }

        private void MainMenu_Print_Click(object sender, RoutedEventArgs e) => PrintActivePage();

        private void MainMenu_ImportFavorites_Click(object sender, RoutedEventArgs e) => OpenImportFavorites();

        private void MainMenu_ClearData_Click(object sender, RoutedEventArgs e) => OpenClearBrowsingData();
    }
}
