using MyHomelabBrowser.classes.CloudTorrent.Models;
using MyHomelabBrowser.classes.CloudTorrent.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MyHomelabBrowser.controles.settings
{
    public partial class SettingsCloudTorrentView : UserControl
    {
        private readonly CloudTorrentModuleService _module;
        private bool _initialValuesLoaded;
        private bool _showApiKey;

        public SettingsCloudTorrentView(CloudTorrentModuleService module)
        {
            InitializeComponent();
            _module = module ?? throw new ArgumentNullException(nameof(module));

            Loaded += SettingsCloudTorrentView_Loaded;
            Unloaded += SettingsCloudTorrentView_Unloaded;
        }

        private async void SettingsCloudTorrentView_Loaded(object sender, RoutedEventArgs e)
        {
            _module.StateChanged -= Module_StateChanged;
            _module.StateChanged += Module_StateChanged;

            LoadConfigurationFieldsOnce();
            RenderSnapshot(_module.Snapshot);

            // Initialise ou recharge la configuration du profil courant.
            // L'appel est sans effet réseau lorsque le site ou la clé ne sont pas configurés.
            await InitializeModuleSafelyAsync();
        }

        private void SettingsCloudTorrentView_Unloaded(object sender, RoutedEventArgs e)
        {
            _module.StateChanged -= Module_StateChanged;
        }

        private async Task InitializeModuleSafelyAsync()
        {
            try
            {
                await _module.InitializeAsync();
            }
            catch
            {
                RenderSnapshot(_module.Snapshot);
            }
        }

        private void LoadConfigurationFieldsOnce()
        {
            if (_initialValuesLoaded)
                return;

            CloudTorrentConfiguration configuration = _module.Configuration;
            ServerUrlBox.Text = configuration.ServerUrl;
            AutoAnalyzeCheckBox.IsChecked = configuration.AutoAnalyzePages;
            _initialValuesLoaded = true;
        }

        private async void ConnectButton_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(true);
            try
            {
                string apiKey = _showApiKey
                    ? ApiKeyTextBox.Text.Trim()
                    : ApiKeyPasswordBox.Password.Trim();

                await _module.ConfigureAsync(
                    ServerUrlBox.Text,
                    apiKey,
                    AutoAnalyzeCheckBox.IsChecked == true);

                ApiKeyPasswordBox.Clear();
                ApiKeyTextBox.Clear();
                RenderSnapshot(_module.Snapshot);
            }
            catch (Exception ex)
            {
                ShowLocalError(ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private async void DisconnectButton_Click(object sender, RoutedEventArgs e)
        {
            SetBusy(true);
            try
            {
                await _module.DisconnectAsync();
                ApiKeyPasswordBox.Clear();
                ApiKeyTextBox.Clear();
                RenderSnapshot(_module.Snapshot);
            }
            catch (Exception ex)
            {
                ShowLocalError(ex.Message);
            }
            finally
            {
                SetBusy(false);
            }
        }

        private void ToggleKeyButton_Click(object sender, RoutedEventArgs e)
        {
            _showApiKey = !_showApiKey;

            if (_showApiKey)
            {
                ApiKeyTextBox.Text = ApiKeyPasswordBox.Password;
                ApiKeyPasswordBox.Visibility = Visibility.Collapsed;
                ApiKeyTextBox.Visibility = Visibility.Visible;
                ToggleKeyButton.Content = "Masquer";
                ApiKeyTextBox.Focus();
                ApiKeyTextBox.CaretIndex = ApiKeyTextBox.Text.Length;
            }
            else
            {
                ApiKeyPasswordBox.Password = ApiKeyTextBox.Text;
                ApiKeyTextBox.Visibility = Visibility.Collapsed;
                ApiKeyPasswordBox.Visibility = Visibility.Visible;
                ToggleKeyButton.Content = "Afficher";
                ApiKeyPasswordBox.Focus();
            }
        }

        private void Module_StateChanged(CloudTorrentModuleSnapshot snapshot)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => RenderSnapshot(snapshot));
                return;
            }

            RenderSnapshot(snapshot);
        }

        private void RenderSnapshot(CloudTorrentModuleSnapshot snapshot)
        {
            StoredKeyLabel.Text = snapshot.HasStoredApiKey
                ? "Une clé API chiffrée est enregistrée pour ce profil."
                : "Aucune clé API n’est enregistrée pour ce profil.";

            DisconnectButton.IsEnabled = snapshot.HasStoredApiKey || snapshot.IsActive;
            AutoAnalyzeCheckBox.IsChecked = snapshot.AutoAnalyzePages;

            switch (snapshot.Status)
            {
                case CloudTorrentConnectionStatus.Active:
                    SetStatus("Module actif", snapshot.Message, Color.FromRgb(59, 190, 106));
                    break;
                case CloudTorrentConnectionStatus.Validating:
                    SetStatus("Vérification en cours", snapshot.Message, Color.FromRgb(65, 143, 222));
                    break;
                case CloudTorrentConnectionStatus.InvalidApiKey:
                    SetStatus("Clé API refusée", snapshot.Message, Color.FromRgb(211, 74, 91));
                    break;
                case CloudTorrentConnectionStatus.Forbidden:
                    SetStatus("Droit account.read manquant", snapshot.Message, Color.FromRgb(211, 145, 55));
                    break;
                case CloudTorrentConnectionStatus.ServerUnavailable:
                    SetStatus("Site inaccessible", snapshot.Message, Color.FromRgb(211, 145, 55));
                    break;
                case CloudTorrentConnectionStatus.InvalidConfiguration:
                    SetStatus("Configuration invalide", snapshot.Message, Color.FromRgb(211, 74, 91));
                    break;
                case CloudTorrentConnectionStatus.Error:
                    SetStatus("Erreur CloudTorrent", snapshot.Message, Color.FromRgb(211, 74, 91));
                    break;
                default:
                    SetStatus("Module inactif", snapshot.Message, Color.FromRgb(116, 116, 116));
                    break;
            }

            CloudTorrentAccount? account = snapshot.Account;
            if (!snapshot.IsActive || account == null)
            {
                AccountCard.Visibility = Visibility.Collapsed;
                return;
            }

            AccountCard.Visibility = Visibility.Visible;
            AccountUsername.Text = account.User.Username;
            AccountVersion.Text = string.IsNullOrWhiteSpace(account.Version) ? "Inconnue" : account.Version;
            AccountActiveJobs.Text = account.Counts.TotalActive.ToString();
            AccountQuota.Text = FormatQuota(account.Quota);

            IEnumerable<string> labels = account.Permissions
                .Select(permission => CloudTorrentPermission.Labels.TryGetValue(permission, out string? label)
                    ? label
                    : permission)
                .OrderBy(label => label, StringComparer.CurrentCultureIgnoreCase);

            PermissionsList.ItemsSource = labels.ToArray();
        }

        private void ShowLocalError(string message)
        {
            SetStatus("Connexion refusée", message, Color.FromRgb(211, 74, 91));
        }

        private void SetStatus(string title, string message, Color color)
        {
            StatusTitle.Text = title;
            StatusMessage.Text = message;
            StatusDot.Background = new SolidColorBrush(color);
        }

        private void SetBusy(bool busy)
        {
            ConnectButton.IsEnabled = !busy;
            DisconnectButton.IsEnabled = !busy && (_module.Snapshot.HasStoredApiKey || _module.Snapshot.IsActive);
            ServerUrlBox.IsEnabled = !busy;
            ApiKeyPasswordBox.IsEnabled = !busy;
            ApiKeyTextBox.IsEnabled = !busy;
            ToggleKeyButton.IsEnabled = !busy;
            AutoAnalyzeCheckBox.IsEnabled = !busy;
            ConnectButton.Content = busy ? "Vérification…" : "Tester et enregistrer";
        }

        private static string FormatQuota(CloudTorrentQuota quota)
        {
            if (!quota.Enabled || quota.MaxBytes <= 0)
                return "Illimité";

            return $"{FormatBytes(quota.UsedBytes)} utilisés sur {FormatBytes(quota.MaxBytes)}";
        }

        private static string FormatBytes(long value)
        {
            double bytes = Math.Max(0, value);
            string[] units = { "o", "Ko", "Mo", "Go", "To" };
            int index = 0;

            while (bytes >= 1024 && index < units.Length - 1)
            {
                bytes /= 1024;
                index++;
            }

            return index == 0 ? $"{bytes:0} {units[index]}" : $"{bytes:0.0} {units[index]}";
        }
    }
}
