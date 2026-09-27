using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Security;
using MyHomelabBrowser.classes.Profiles;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MyHomelabBrowser.controles.settings
{
    public partial class SettingsGeneralView : UserControl
    {
        private bool _loaded;

        public SettingsGeneralView()
        {
            InitializeComponent();

            DnsModeBox.ItemsSource = new[]
            {
                new DnsOption<BrowserSettings.SecureDnsMode>(
                    BrowserSettings.SecureDnsMode.System,
                    "DNS du système Windows"),
                new DnsOption<BrowserSettings.SecureDnsMode>(
                    BrowserSettings.SecureDnsMode.Automatic,
                    "DNS sécurisé automatique"),
                new DnsOption<BrowserSettings.SecureDnsMode>(
                    BrowserSettings.SecureDnsMode.Secure,
                    "DNS sécurisé avec un fournisseur choisi")
            };

            DnsProviderBox.ItemsSource = new[]
            {
                new DnsOption<BrowserSettings.SecureDnsProvider>(
                    BrowserSettings.SecureDnsProvider.Cloudflare,
                    "Cloudflare"),
                new DnsOption<BrowserSettings.SecureDnsProvider>(
                    BrowserSettings.SecureDnsProvider.Google,
                    "Google Public DNS"),
                new DnsOption<BrowserSettings.SecureDnsProvider>(
                    BrowserSettings.SecureDnsProvider.Quad9,
                    "Quad9"),
                new DnsOption<BrowserSettings.SecureDnsProvider>(
                    BrowserSettings.SecureDnsProvider.AdGuard,
                    "AdGuard DNS"),
                new DnsOption<BrowserSettings.SecureDnsProvider>(
                    BrowserSettings.SecureDnsProvider.Custom,
                    "Personnalisé")
            };

            ServiceIntervalBox.ItemsSource = new[]
            {
                new DnsOption<int>(30, "Toutes les 30 secondes"),
                new DnsOption<int>(60, "Toutes les minutes"),
                new DnsOption<int>(300, "Toutes les 5 minutes"),
                new DnsOption<int>(900, "Toutes les 15 minutes")
            };

            SearchEngineBox.ItemsSource = Enum.GetValues<BrowserSettings.SearchEngine>()
                .Select(engine => new DnsOption<BrowserSettings.SearchEngine>(engine, UrlResolver.GetSearchEngineName(engine)))
                .ToArray();

            Loaded += SettingsGeneralView_Loaded;
        }

        private async void SettingsGeneralView_Loaded(object sender, RoutedEventArgs e)
        {
            _loaded = true;
            UpdateDnsUi();
            await UpdateCertificateSummaryAsync();
        }

        private void DnsSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_loaded)
                return;

            Dispatcher.BeginInvoke(new Action(UpdateDnsUi));
        }

        private void CustomDnsTemplateBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (!_loaded)
                return;

            ValidateCustomDnsTemplate();
            UpdateDnsStatus();
        }

        private void UpdateDnsUi()
        {
            var mode = GetSelectedMode();
            var provider = GetSelectedProvider();

            DnsProviderPanel.Visibility = mode == BrowserSettings.SecureDnsMode.Secure
                ? Visibility.Visible
                : Visibility.Collapsed;

            CustomDnsPanel.Visibility = mode == BrowserSettings.SecureDnsMode.Secure
                                        && provider == BrowserSettings.SecureDnsProvider.Custom
                ? Visibility.Visible
                : Visibility.Collapsed;

            ValidateCustomDnsTemplate();
            UpdateDnsStatus();
        }

        private void UpdateDnsStatus()
        {
            var mode = GetSelectedMode();
            var provider = GetSelectedProvider();

            DnsStatusText.Text = mode switch
            {
                BrowserSettings.SecureDnsMode.System =>
                    "PommeBrowser utilise actuellement la résolution DNS configurée dans Windows.",

                BrowserSettings.SecureDnsMode.Automatic =>
                    "WebView2 tentera d’utiliser DNS-over-HTTPS et pourra revenir au DNS système si le réseau l’exige.",

                BrowserSettings.SecureDnsMode.Secure when provider == BrowserSettings.SecureDnsProvider.Custom =>
                    SecureDnsConfiguration.IsValidHttpsTemplate(CustomDnsTemplateBox.Text)
                        ? $"DNS-over-HTTPS strict : {CustomDnsTemplateBox.Text.Trim()}"
                        : "Le DNS personnalisé ne sera pas appliqué tant que son adresse n’est pas valide.",

                BrowserSettings.SecureDnsMode.Secure =>
                    $"DNS-over-HTTPS strict : {GetProviderLabel(provider)}.",

                _ => "DNS du système Windows."
            };
        }

        private void ValidateCustomDnsTemplate()
        {
            bool mustValidate = GetSelectedMode() == BrowserSettings.SecureDnsMode.Secure
                                && GetSelectedProvider() == BrowserSettings.SecureDnsProvider.Custom;

            if (!mustValidate)
            {
                CustomDnsValidationText.Visibility = Visibility.Collapsed;
                return;
            }

            bool valid = SecureDnsConfiguration.IsValidHttpsTemplate(CustomDnsTemplateBox.Text);
            CustomDnsValidationText.Text = valid
                ? string.Empty
                : "Saisissez une URL HTTPS valide vers un service DNS-over-HTTPS.";
            CustomDnsValidationText.Visibility = valid ? Visibility.Collapsed : Visibility.Visible;
        }

        private BrowserSettings.SecureDnsMode GetSelectedMode()
        {
            if (DnsModeBox.SelectedValue is BrowserSettings.SecureDnsMode selected)
                return selected;

            return DataContext is BrowserSettings settings
                ? settings.DnsMode
                : BrowserSettings.SecureDnsMode.System;
        }

        private BrowserSettings.SecureDnsProvider GetSelectedProvider()
        {
            if (DnsProviderBox.SelectedValue is BrowserSettings.SecureDnsProvider selected)
                return selected;

            return DataContext is BrowserSettings settings
                ? settings.DnsProvider
                : BrowserSettings.SecureDnsProvider.Cloudflare;
        }

        private static string GetProviderLabel(BrowserSettings.SecureDnsProvider provider)
            => provider switch
            {
                BrowserSettings.SecureDnsProvider.Cloudflare => "Cloudflare",
                BrowserSettings.SecureDnsProvider.Google => "Google Public DNS",
                BrowserSettings.SecureDnsProvider.Quad9 => "Quad9",
                BrowserSettings.SecureDnsProvider.AdGuard => "AdGuard DNS",
                _ => "fournisseur personnalisé"
            };

        private async Task UpdateCertificateSummaryAsync()
        {
            try
            {
                CertificateSummaryText.Text = "Chargement des certificats du profil…";

                var certificates = await Task.Run(() =>
                    new CertificateStoreService(AppDataContext.Root)
                        .GetAuthorities(includeSystem: true));

                int browserCount = certificates.Count(certificate =>
                    certificate.Source == CertificateSource.Browser);
                int windowsUserCount = certificates.Count(certificate =>
                    certificate.Source == CertificateSource.WindowsUser);
                int windowsMachineCount = certificates.Count(certificate =>
                    certificate.Source == CertificateSource.WindowsMachine);

                CertificateSummaryText.Text =
                    $"{browserCount} CA propre(s) au profil · " +
                    $"{windowsUserCount} utilisateur Windows · " +
                    $"{windowsMachineCount} ordinateur";
            }
            catch (Exception ex)
            {
                CertificateSummaryText.Text = $"Lecture impossible : {ex.Message}";
            }
        }

        private void OpenCertificateManager_Click(object sender, RoutedEventArgs e)
        {
            var window = new CertificateManagerWindow
            {
                Owner = Window.GetWindow(this)
            };

            window.ShowDialog();
            _ = UpdateCertificateSummaryAsync();
        }

        private void OpenPinnedCertificates_Click(object sender, RoutedEventArgs e)
            => new PinnedCertificatesDialog().ShowFor(Window.GetWindow(this));

        public sealed record DnsOption<T>(T Value, string Label)
        {
            public override string ToString() => Label;
        }
    }
}
