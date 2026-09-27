using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes.AdBlock.Integration;
using MyHomelabBrowser.classes.AdBlock.Models;
using MyHomelabBrowser.classes.AdBlock.Services;
using MyHomelabBrowser.controles.AdBlock;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        private AdBlockModuleService _adBlock = null!;
        private AdBlockBrowserController _adBlockBrowser = null!;
        private AdBlockPanel _adBlockPanel = null!;
        private bool _adBlockUiInitialized;

        private void InitializeAdBlockModule()
        {
            if (_adBlockUiInitialized)
                return;

            _adBlockUiInitialized = true;
            _adBlock = AdBlockModuleHost.Current;
            _adBlockBrowser = new AdBlockBrowserController(_adBlock);
            _adBlockPanel = new AdBlockPanel(_adBlock, _adBlockBrowser);
            AdBlockPanelHost.Content = _adBlockPanel;

            _adBlockPanel.OpenSettingsRequested += () =>
            {
                AdBlockPopup.IsOpen = false;
                OpenSettingsSection("Bloqueur de publicités");
            };

            _adBlockBrowser.ActiveSessionChanged += _ => Dispatcher.BeginInvoke(new Action(UpdateAdBlockToolbar));
            _adBlockBrowser.ActiveSessionUpdated += _ => Dispatcher.BeginInvoke(new Action(UpdateAdBlockToolbar));
            _adBlockBrowser.ModuleStateChanged += () => Dispatcher.BeginInvoke(new Action(UpdateAdBlockToolbar));
            _adBlock.StateChanged += () => Dispatcher.BeginInvoke(new Action(UpdateAdBlockToolbar));
            _adBlock.StatisticsChanged += () => Dispatcher.BeginInvoke(new Action(UpdateAdBlockToolbar));

            Tabs.SelectionChanged += (_, _) => UpdateActiveAdBlockSession();
            _profileService.ProfileChanged += profile => _ = ReloadAdBlockForCurrentProfileAsync();

            Closed += (_, _) => _adBlockBrowser.Dispose();
            UpdateAdBlockToolbar();
        }

        private async Task AttachAdBlockToWebViewAsync(WebView2 webView, bool isPrivate)
        {
            if (!_adBlockUiInitialized || webView.CoreWebView2 == null)
                return;

            await _adBlockBrowser.AttachAsync(webView, isPrivate);
            UpdateActiveAdBlockSession();
            UpdateAdBlockToolbar();
        }

        private async Task ReloadAdBlockForCurrentProfileAsync()
        {
            if (!_adBlockUiInitialized)
                return;

            try
            {
                await _adBlock.ReloadForCurrentProfileAsync();
                await _adBlockBrowser.RefreshAllAsync();
            }
            catch { }

            _ = Dispatcher.BeginInvoke(new Action(UpdateAdBlockToolbar));
        }

        private void UpdateActiveAdBlockSession()
        {
            if (!_adBlockUiInitialized)
                return;

            if (Tabs.SelectedItem is TabItem tab
                && tab.Tag is WebTabContent content
                && content.Web != null
                && !content.IsLegacyExternal)
            {
                _adBlockBrowser.SetActiveWebView(content.Web);
            }
            else
            {
                _adBlockBrowser.SetActiveWebView(null);
            }
        }

        private void AdBlockButton_Click(object sender, RoutedEventArgs e)
        {
            UpdateActiveAdBlockSession();
            UpdateAdBlockToolbar();
            _adBlockPanel.Refresh();
            AdBlockPopup.IsOpen = !AdBlockPopup.IsOpen;
        }

        private void UpdateAdBlockToolbar()
        {
            if (!_adBlockUiInitialized || AdBlockButton == null)
                return;

            AdBlockSettings settings = _adBlock.Settings;
            AdBlockTabSession? session = _adBlockBrowser.ActiveSession;
            string host = session?.CurrentHost ?? string.Empty;
            bool allowed = host.Length > 0 && _adBlock.IsSiteAllowed(host);
            bool active = settings.Enabled && session?.IsProtectionActive == true;
            int blocked = session?.BlockedCount ?? 0;

            AdBlockBadge.Visibility = blocked > 0 ? Visibility.Visible : Visibility.Collapsed;
            AdBlockBadgeText.Text = Math.Min(99, blocked).ToString();

            Brush activeBrush = (Brush)FindResource("AccentBrush");
            Brush inactiveBrush = (Brush)FindResource("TextTertiaryBrush");
            Brush warningBrush = (Brush)FindResource("WarningBrush");

            AdBlockButton.Foreground = !settings.Enabled
                ? inactiveBrush
                : allowed
                    ? warningBrush
                    : active
                        ? activeBrush
                        : inactiveBrush;

            AdBlockButton.Opacity = settings.Enabled ? 1.0 : 0.68;

            string stateText = !settings.Enabled
                ? Tr("désactivée partout")
                : allowed
                    ? Tr("désactivée pour ce site")
                    : active
                        ? "active"
                        : Tr("en attente d’une page web");

            AdBlockButton.ToolTip = Tr("Protection web — {0}\n{1:N0} requête{2} bloquée{3} sur cette page", stateText, blocked, (blocked > 1 ? "s" : string.Empty), (blocked > 1 ? "s" : string.Empty));

            if (AdBlockPopup.IsOpen)
                _adBlockPanel.Refresh();
        }
    }
}
