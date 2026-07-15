using MyHomelabBrowser.classes.AdBlock.Integration;
using MyHomelabBrowser.classes.AdBlock.Models;
using MyHomelabBrowser.classes.AdBlock.Services;
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyHomelabBrowser.controles.AdBlock
{
    public partial class AdBlockPanel : UserControl
    {
        private readonly AdBlockModuleService _module;
        private readonly AdBlockBrowserController _browser;
        private bool _updating;

        public event Action? OpenSettingsRequested;

        public AdBlockPanel(AdBlockModuleService module, AdBlockBrowserController browser)
        {
            InitializeComponent();
            _module = module;
            _browser = browser;

            Loaded += AdBlockPanel_Loaded;
            Unloaded += AdBlockPanel_Unloaded;
        }


        private void AdBlockPanel_Loaded(object sender, RoutedEventArgs e)
        {
            UnsubscribeEvents();
            _module.StateChanged += RefreshOnUiThread;
            _module.RulesChanged += RefreshOnUiThread;
            _module.StatisticsChanged += RefreshOnUiThread;
            _module.StatusChanged += Module_StatusChanged;
            _browser.ActiveSessionChanged += Browser_ActiveSessionChanged;
            _browser.ActiveSessionUpdated += Browser_ActiveSessionUpdated;
            Refresh();
        }

        private void AdBlockPanel_Unloaded(object sender, RoutedEventArgs e)
            => UnsubscribeEvents();

        private void UnsubscribeEvents()
        {
            _module.StateChanged -= RefreshOnUiThread;
            _module.RulesChanged -= RefreshOnUiThread;
            _module.StatisticsChanged -= RefreshOnUiThread;
            _module.StatusChanged -= Module_StatusChanged;
            _browser.ActiveSessionChanged -= Browser_ActiveSessionChanged;
            _browser.ActiveSessionUpdated -= Browser_ActiveSessionUpdated;
        }

        private void Module_StatusChanged(string message) => RefreshOnUiThread();
        private void Browser_ActiveSessionChanged(AdBlockTabSession? session) => RefreshOnUiThread();
        private void Browser_ActiveSessionUpdated(AdBlockTabSession session) => RefreshOnUiThread();

        public void Refresh()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(Refresh));
                return;
            }

            AdBlockSettings settings = _module.Settings;
            AdBlockModuleSnapshot snapshot = _module.GetSnapshot();
            AdBlockTabSession? session = _browser.ActiveSession;
            string host = session?.CurrentHost ?? string.Empty;
            bool hasSite = host.Length > 0;
            bool allowed = hasSite && _module.IsSiteAllowed(host);
            bool activeOnSite = hasSite && session?.IsProtectionActive == true;

            HeaderStatusText.Text = !settings.Enabled
                ? "Désactivée dans tout le navigateur"
                : allowed
                    ? "Désactivée pour ce site"
                    : activeOnSite
                        ? "Protection active"
                        : "En attente d’une page web";

            CurrentSiteText.Text = hasSite ? host : "Aucun site";
            SiteProtectionText.Text = !hasSite
                ? "Ouvrez une page web pour voir son état."
                : allowed
                    ? "Ce domaine figure dans les sites autorisés."
                    : activeOnSite
                        ? "Publicités et traqueurs sont filtrés avant leur chargement."
                        : "La protection ne s’applique pas à cette adresse.";

            ToggleSiteButton.IsEnabled = hasSite && settings.Enabled;
            ToggleSiteButton.Content = allowed ? "Réactiver ici" : "Autoriser ce site";

            PageBlockedText.Text = (session?.BlockedCount ?? 0).ToString("N0");
            SessionBlockedText.Text = snapshot.SessionBlockedCount.ToString("N0");
            RulesText.Text = $"{snapshot.NetworkRuleCount:N0} règles réseau · {snapshot.CosmeticRuleCount:N0} règles visuelles";
            UpdateText.Text = snapshot.LastSuccessfulUpdateUtc.HasValue
                ? "Dernière mise à jour : " + snapshot.LastSuccessfulUpdateUtc.Value.ToLocalTime().ToString("dd/MM/yyyy HH:mm")
                : snapshot.StatusMessage;

            GlobalToggleButton.Content = settings.Enabled ? "Désactiver partout" : "Activer partout";
            UpdateListsButton.IsEnabled = !_updating;
            UpdateListsButton.Content = _updating ? "Mise à jour…" : "Mettre à jour";

            Brush activeBrush = (Brush)FindResource("AccentBrush");
            Brush inactiveBrush = (Brush)FindResource("TextTertiaryBrush");
            ShieldPath.Fill = settings.Enabled && !allowed ? activeBrush : inactiveBrush;
            ShieldTile.Opacity = settings.Enabled ? 1.0 : 0.65;
        }

        private void ToggleSiteButton_Click(object sender, RoutedEventArgs e)
        {
            string host = _browser.ActiveSession?.CurrentHost ?? string.Empty;
            if (host.Length == 0)
                return;

            _module.SetSiteAllowed(host, !_module.IsSiteAllowed(host));
            _ = _browser.RefreshAllAsync();
            Refresh();
        }

        private void GlobalToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _module.SetGlobalEnabled(!_module.Settings.Enabled);
            _ = _browser.RefreshAllAsync();
            Refresh();
        }

        private async void UpdateListsButton_Click(object sender, RoutedEventArgs e)
        {
            if (_updating)
                return;

            _updating = true;
            Refresh();
            try
            {
                await _module.UpdateListsAsync(force: true);
                await _browser.RefreshAllAsync();
            }
            finally
            {
                _updating = false;
                Refresh();
            }
        }

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
            => OpenSettingsRequested?.Invoke();

        private void RefreshOnUiThread()
            => Dispatcher.BeginInvoke(new Action(Refresh));
    }
}
