using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Security;
using MyHomelabBrowser.classes.Profiles;
using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using static MyHomelabBrowser.classes.Localization.Loc;

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
                    Tr("DNS du système Windows")),
                new DnsOption<BrowserSettings.SecureDnsMode>(
                    BrowserSettings.SecureDnsMode.Automatic,
                    Tr("DNS sécurisé automatique")),
                new DnsOption<BrowserSettings.SecureDnsMode>(
                    BrowserSettings.SecureDnsMode.Secure,
                    Tr("DNS sécurisé avec un fournisseur choisi"))
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
                    Tr("Personnalisé"))
            };

            ThemeBox.ItemsSource = new[]
            {
                new DnsOption<AppTheme>(AppTheme.System, Tr("Comme Windows")),
                new DnsOption<AppTheme>(AppTheme.Dark, Tr("Sombre")),
                new DnsOption<AppTheme>(AppTheme.Light, Tr("Clair"))
            };
            LanguageBox.ItemsSource = new[]
            {
                new DnsOption<string>("fr", Tr("Français")),
                new DnsOption<string>("en", "English")
            };

            AppearanceSettings appearance = AppearanceSettings.Load();
            _appearanceLoading = true;
            ThemeBox.SelectedValue = appearance.Theme;
            LanguageBox.SelectedValue = appearance.Language;
            _appearanceLoading = false;

            ServiceIntervalBox.ItemsSource = new[]
            {
                new DnsOption<int>(30, Tr("Toutes les 30 secondes")),
                new DnsOption<int>(60, Tr("Toutes les minutes")),
                new DnsOption<int>(300, Tr("Toutes les 5 minutes")),
                new DnsOption<int>(900, Tr("Toutes les 15 minutes"))
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
                    Tr("PommeBrowser utilise actuellement la résolution DNS configurée dans Windows."),

                BrowserSettings.SecureDnsMode.Automatic =>
                    Tr("WebView2 tentera d’utiliser DNS-over-HTTPS et pourra revenir au DNS système si le réseau l’exige."),

                BrowserSettings.SecureDnsMode.Secure when provider == BrowserSettings.SecureDnsProvider.Custom =>
                    SecureDnsConfiguration.IsValidHttpsTemplate(CustomDnsTemplateBox.Text)
                        ? Tr("DNS-over-HTTPS strict : {0}", CustomDnsTemplateBox.Text.Trim())
                        : Tr("Le DNS personnalisé ne sera pas appliqué tant que son adresse n’est pas valide."),

                BrowserSettings.SecureDnsMode.Secure =>
                    Tr("DNS-over-HTTPS strict : {0}.", GetProviderLabel(provider)),

                _ => Tr("DNS du système Windows.")
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
                : Tr("Saisissez une URL HTTPS valide vers un service DNS-over-HTTPS.");
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
                _ => Tr("fournisseur personnalisé")
            };

        private async Task UpdateCertificateSummaryAsync()
        {
            try
            {
                CertificateSummaryText.Text = Tr("Chargement des certificats du profil…");

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
                    Tr("{0} CA propre(s) au profil · ", browserCount) +
                    Tr("{0} utilisateur Windows · ", windowsUserCount) +
                    Tr("{0} ordinateur", windowsMachineCount);
            }
            catch (Exception ex)
            {
                CertificateSummaryText.Text = Tr("Lecture impossible : {0}", ex.Message);
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

        private bool _appearanceLoading;

        /// <summary>
        /// Enregistré tout de suite (réglage commun à tous les profils), appliqué au redémarrage.
        /// </summary>
        private void Appearance_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_appearanceLoading || ThemeBox.SelectedValue is not AppTheme theme || LanguageBox.SelectedValue is not string language)
                return;

            var appearance = new AppearanceSettings { Theme = theme, Language = language };
            try
            {
                appearance.Save();
            }
            catch
            {
                return;
            }

            bool pending = theme != ThemeManager.Appearance.Theme ||
                           !string.Equals(language, ThemeManager.Appearance.Language, StringComparison.OrdinalIgnoreCase);
            AppearanceRestartPanel.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        }

        private void RestartNow_Click(object sender, RoutedEventArgs e)
        {
            if (Application.Current.MainWindow is MainWindow main)
                main.RestartApplication();
        }

        private void OpenPinnedCertificates_Click(object sender, RoutedEventArgs e)
            => new PinnedCertificatesDialog().ShowFor(Window.GetWindow(this));

        public sealed record DnsOption<T>(T Value, string Label)
        {
            public override string ToString() => Label;
        }
    }
}
