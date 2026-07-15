using Microsoft.Win32;
using MyHomelabBrowser.classes.Profiles;
using MyHomelabBrowser.classes.Security;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace MyHomelabBrowser.controles.settings
{
    public partial class CertificateManagerWindow : Window, INotifyPropertyChanged
    {
        private readonly CertificateStoreService _certificateService;
        private CertificateEntry? _selectedCertificate;

        public ObservableCollection<CertificateEntry> Certificates { get; } = new();
        public ICollectionView CertificatesView { get; }

        public CertificateEntry? SelectedCertificate
        {
            get => _selectedCertificate;
            set
            {
                if (ReferenceEquals(_selectedCertificate, value))
                    return;

                _selectedCertificate = value;
                OnPropertyChanged();
                UpdateDetailsVisibility();
            }
        }

        public CertificateManagerWindow()
        {
            InitializeComponent();
            _certificateService = new CertificateStoreService(AppDataContext.Root);
            CertificatesView = CollectionViewSource.GetDefaultView(Certificates);
            CertificatesView.Filter = FilterCertificate;
            DataContext = this;

            Loaded += (_, _) => ReloadCertificates();
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void ReloadCertificates()
        {
            string? selectedThumbprint = SelectedCertificate?.Thumbprint;
            CertificateSource? selectedSource = SelectedCertificate?.Source;

            Certificates.Clear();
            foreach (var certificate in _certificateService.GetAuthorities(includeSystem: true))
                Certificates.Add(certificate);

            CertificatesView.Refresh();

            SelectedCertificate = !string.IsNullOrWhiteSpace(selectedThumbprint)
                ? Certificates.FirstOrDefault(certificate =>
                    certificate.Source == selectedSource
                    && string.Equals(
                        certificate.Thumbprint,
                        selectedThumbprint,
                        StringComparison.OrdinalIgnoreCase))
                : CertificatesView.Cast<CertificateEntry>().FirstOrDefault();

            CertificateList.SelectedItem = SelectedCertificate;
        }

        private bool FilterCertificate(object item)
        {
            if (item is not CertificateEntry certificate)
                return false;

            string search = (SearchBox?.Text ?? string.Empty).Trim();
            if (search.Length > 0)
            {
                bool matches = certificate.DisplayName.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                               || certificate.Subject.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                               || certificate.Issuer.Contains(search, StringComparison.CurrentCultureIgnoreCase)
                               || certificate.Thumbprint.Contains(search, StringComparison.OrdinalIgnoreCase);

                if (!matches)
                    return false;
            }

            string scope = (ScopeBox?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Browser";
            return scope switch
            {
                "Browser" => certificate.Source == CertificateSource.Browser,
                "User" => certificate.Source == CertificateSource.WindowsUser,
                "Machine" => certificate.Source == CertificateSource.WindowsMachine,
                _ => true
            };
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
            => CertificatesView?.Refresh();

        private void ScopeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            CertificatesView?.Refresh();
            SelectedCertificate = CertificatesView?.Cast<CertificateEntry>().FirstOrDefault();
            if (CertificateList is not null)
                CertificateList.SelectedItem = SelectedCertificate;
        }

        private void CertificateList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            SelectedCertificate = CertificateList.SelectedItem as CertificateEntry;
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
            => ReloadCertificates();

        private async void ImportCertificate_Click(object sender, RoutedEventArgs e)
        {
            string? filePath = SelectCertificateFile("Ajouter une autorité à PommeBrowser");
            if (filePath is null)
                return;

            CertificateImportResult result = _certificateService.ImportAuthorityForBrowser(filePath);
            MessageBox.Show(
                result.Message,
                result.Success ? "Certificat ajouté" : "Import impossible",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);

            if (!result.Success)
                return;

            await BrowserCertificateTrustHost.Current.NotifyStoreChangedAsync(
                _certificateService.ProfileRoot);
            ReloadCertificates();
        }

        private async void RemoveCertificate_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedCertificate is null || !SelectedCertificate.CanRemove)
                return;

            MessageBoxResult confirmation = MessageBox.Show(
                $"Retirer « {SelectedCertificate.DisplayName} » des autorités de confiance de ce profil PommeBrowser ?\n\n" +
                "Les sites qui dépendent de cette CA pourront à nouveau afficher une erreur TLS.",
                "Retirer l’autorité",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirmation != MessageBoxResult.Yes)
                return;

            CertificateOperationResult result =
                _certificateService.RemoveBrowserAuthority(SelectedCertificate);

            MessageBox.Show(
                result.Message,
                result.Success ? "Certificat retiré" : "Suppression impossible",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);

            if (!result.Success)
                return;

            await BrowserCertificateTrustHost.Current.NotifyStoreChangedAsync(
                _certificateService.ProfileRoot);
            ReloadCertificates();
        }

        private void InstallInWindows_Click(object sender, RoutedEventArgs e)
        {
            string? filePath = SelectCertificateFile("Installer une autorité dans Windows");
            if (filePath is null)
                return;

            MessageBoxResult confirmation = MessageBox.Show(
                "Cette opération est différente de l’ajout à PommeBrowser.\n\n" +
                "L’autorité sera installée dans le magasin Windows de l’utilisateur courant et pourra être utilisée par d’autres applications de ce compte.\n\n" +
                "Continuer ?",
                "Installer dans Windows",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (confirmation != MessageBoxResult.Yes)
                return;

            CertificateImportResult result =
                _certificateService.ImportAuthorityToWindowsCurrentUser(filePath);

            MessageBox.Show(
                result.Message,
                result.Success ? "Certificat installé" : "Installation impossible",
                MessageBoxButton.OK,
                result.Success ? MessageBoxImage.Information : MessageBoxImage.Error);

            if (result.Success)
                ReloadCertificates();
        }

        private static string? SelectCertificateFile(string title)
        {
            var dialog = new OpenFileDialog
            {
                Title = title,
                Filter = "Certificats publics (*.cer;*.crt;*.der;*.pem)|*.cer;*.crt;*.der;*.pem|Tous les fichiers (*.*)|*.*",
                CheckFileExists = true,
                Multiselect = false
            };

            return dialog.ShowDialog() == true ? dialog.FileName : null;
        }

        private void CopyThumbprint_Click(object sender, RoutedEventArgs e)
        {
            if (SelectedCertificate is null || string.IsNullOrWhiteSpace(SelectedCertificate.Thumbprint))
                return;

            Clipboard.SetText(SelectedCertificate.Thumbprint);
        }

        private void OpenWindowsCertificateManager_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Process.Start(new ProcessStartInfo("certmgr.msc")
                {
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    $"Impossible d’ouvrir le gestionnaire Windows : {ex.Message}",
                    "Certificats",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        private void UpdateDetailsVisibility()
        {
            if (EmptyDetails is null || DetailsScroll is null || RemoveCertificateButton is null)
                return;

            bool hasSelection = SelectedCertificate is not null;
            EmptyDetails.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
            DetailsScroll.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
            RemoveCertificateButton.Visibility = SelectedCertificate?.CanRemove == true
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
