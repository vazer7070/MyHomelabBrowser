using MyHomelabBrowser.classes.AdBlock.Models;
using MyHomelabBrowser.classes.AdBlock.Services;
using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles.settings
{
    public partial class SettingsAdBlockView : UserControl
    {
        private readonly AdBlockModuleService _module;
        private bool _loading;
        private bool _updating;

        public SettingsAdBlockView(AdBlockModuleService module)
        {
            InitializeComponent();
            _module = module;

            Loaded += SettingsAdBlockView_Loaded;
            Unloaded += SettingsAdBlockView_Unloaded;
        }


        private void SettingsAdBlockView_Loaded(object sender, RoutedEventArgs e)
        {
            _module.StateChanged -= RefreshOnUiThread;
            _module.RulesChanged -= RefreshOnUiThread;
            _module.StatusChanged -= Module_StatusChanged;
            _module.StateChanged += RefreshOnUiThread;
            _module.RulesChanged += RefreshOnUiThread;
            _module.StatusChanged += Module_StatusChanged;
            Refresh();
        }

        private void SettingsAdBlockView_Unloaded(object sender, RoutedEventArgs e)
        {
            _module.StateChanged -= RefreshOnUiThread;
            _module.RulesChanged -= RefreshOnUiThread;
            _module.StatusChanged -= Module_StatusChanged;
        }

        private void Module_StatusChanged(string message)
        {
            Dispatcher.BeginInvoke(() =>
            {
                UpdateStatusText.Text = message;
                RefreshStatusOnly();
            });
        }

        private void Refresh()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(Refresh));
                return;
            }

            _loading = true;
            try
            {
                AdBlockSettings settings = _module.Settings;
                EnabledCheckBox.IsChecked = settings.Enabled;
                CosmeticFilteringCheckBox.IsChecked = settings.CosmeticFiltering;
                AutoUpdateCheckBox.IsChecked = settings.AutoUpdate;
                BypassPrivateNetworksCheckBox.IsChecked = settings.BypassPrivateNetworks;

                EasyListCheckBox.IsChecked = settings.Subscriptions
                    .FirstOrDefault(item => item.Id.Equals("easylist", StringComparison.OrdinalIgnoreCase))?.Enabled == true;
                EasyPrivacyCheckBox.IsChecked = settings.Subscriptions
                    .FirstOrDefault(item => item.Id.Equals("easyprivacy", StringComparison.OrdinalIgnoreCase))?.Enabled == true;

                AllowlistList.ItemsSource = settings.AllowlistedDomains.ToArray();
                RefreshStatusOnly();
            }
            finally
            {
                _loading = false;
            }
        }

        private void RefreshStatusOnly()
        {
            AdBlockModuleSnapshot snapshot = _module.GetSnapshot();
            RuleCountText.Text = Tr("{0:N0} règles réseau et {1:N0} règles visuelles sont chargées.", snapshot.NetworkRuleCount, snapshot.CosmeticRuleCount);
            LastUpdateText.Text = snapshot.LastSuccessfulUpdateUtc.HasValue
                ? Tr("Dernière mise à jour réussie : ") + snapshot.LastSuccessfulUpdateUtc.Value.ToLocalTime().ToString(Tr("dd/MM/yyyy à HH:mm"))
                : Tr("Les listes distantes n’ont pas encore été téléchargées. Les règles intégrées de secours restent actives.");

            if (string.IsNullOrWhiteSpace(UpdateStatusText.Text))
                UpdateStatusText.Text = snapshot.StatusMessage;

            UpdateNowButton.IsEnabled = !_updating;
            UpdateNowButton.Content = _updating ? Tr("Mise à jour en cours…") : Tr("Mettre à jour maintenant");
        }

        private void GeneralSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            _module.SettingsService.Update(settings =>
            {
                settings.Enabled = EnabledCheckBox.IsChecked == true;
                settings.CosmeticFiltering = CosmeticFilteringCheckBox.IsChecked == true;
                settings.AutoUpdate = AutoUpdateCheckBox.IsChecked == true;
                settings.BypassPrivateNetworks = BypassPrivateNetworksCheckBox.IsChecked == true;
            });
        }

        private void SubscriptionSetting_Changed(object sender, RoutedEventArgs e)
        {
            if (_loading)
                return;

            _module.SetDefaultSubscriptions(
                EasyListCheckBox.IsChecked == true,
                EasyPrivacyCheckBox.IsChecked == true);
        }

        private async void UpdateNowButton_Click(object sender, RoutedEventArgs e)
        {
            if (_updating)
                return;

            _updating = true;
            UpdateStatusText.Text = Tr("Téléchargement des listes…");
            RefreshStatusOnly();
            try
            {
                AdBlockUpdateResult result = await _module.UpdateListsAsync(force: true);
                UpdateStatusText.Text = result.Message;
            }
            catch (Exception ex)
            {
                UpdateStatusText.Text = Tr("Échec de la mise à jour : ") + ex.Message;
            }
            finally
            {
                _updating = false;
                Refresh();
            }
        }

        private void AddAllowlistButton_Click(object sender, RoutedEventArgs e)
            => AddAllowlistDomain();

        private void AllowlistDomainBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
                return;

            AddAllowlistDomain();
            e.Handled = true;
        }

        private void AddAllowlistDomain()
        {
            string domain = AdBlockDomain.NormalizeHost(AllowlistDomainBox.Text);
            if (domain.Length == 0)
            {
                UpdateStatusText.Text = Tr("Saisissez un domaine valide.");
                return;
            }

            _module.SetSiteAllowed(domain, allowed: true);
            AllowlistDomainBox.Clear();
            UpdateStatusText.Text = domain + Tr(" a été ajouté aux sites autorisés.");
            Refresh();
        }

        private void RemoveAllowlistButton_Click(object sender, RoutedEventArgs e)
        {
            if (AllowlistList.SelectedItem is not string domain)
                return;

            _module.SetSiteAllowed(domain, allowed: false);
            UpdateStatusText.Text = domain + Tr(" a été retiré des sites autorisés.");
            Refresh();
        }

        private void RefreshOnUiThread()
            => Dispatcher.BeginInvoke(new Action(Refresh));
    }
}
