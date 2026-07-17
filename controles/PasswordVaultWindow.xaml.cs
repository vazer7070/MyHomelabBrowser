using MyHomelabBrowser.classes.Profiles.Credentials;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MyHomelabBrowser.controles
{
    public partial class PasswordVaultWindow : Window
    {
        private readonly CredentialVaultService _vault;
        private readonly DispatcherTimer _statusTimer;
        private List<CredentialEntry> _allItems = new();

        public PasswordVaultWindow(CredentialVaultService vault)
        {
            InitializeComponent();
            _vault = vault ?? throw new ArgumentNullException(nameof(vault));

            _statusTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(4)
            };
            _statusTimer.Tick += (_, _) =>
            {
                _statusTimer.Stop();
                StatusBorder.Visibility = Visibility.Collapsed;
            };

            Refresh();
            SearchBox.Focus();
        }

        private void Refresh()
        {
            _allItems = _vault.GetAll()
                .OrderBy(x => x.Host, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Username, StringComparer.OrdinalIgnoreCase)
                .ToList();

            CountText.Text = _allItems.Count switch
            {
                0 => "Aucun identifiant",
                1 => "1 identifiant",
                _ => $"{_allItems.Count} identifiants"
            };

            ApplyFilter();
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Visible
                : Visibility.Collapsed;

            ClearSearchButton.Visibility = string.IsNullOrEmpty(SearchBox.Text)
                ? Visibility.Collapsed
                : Visibility.Visible;

            ApplyFilter();
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Clear();
            SearchBox.Focus();
        }

        private void ApplyFilter()
        {
            var query = (SearchBox.Text ?? string.Empty).Trim();

            var filtered = string.IsNullOrWhiteSpace(query)
                ? _allItems
                : _allItems.Where(x =>
                    (x.Host?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (x.Username?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                    .ToList();

            CredentialList.ItemsSource = filtered;

            var hasItems = filtered.Count > 0;
            CredentialList.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
            EmptyState.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;

            if (_allItems.Count == 0)
            {
                EmptyTitle.Text = "Aucun identifiant enregistré";
                EmptyDescription.Text = "Les identifiants enregistrés apparaîtront ici.";
            }
            else
            {
                EmptyTitle.Text = "Aucun résultat";
                EmptyDescription.Text = "Modifie ou efface les termes de recherche.";
            }
        }

        private void Reveal_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CredentialEntry credential)
                return;

            var dialog = new VaultSecretDialog(credential)
            {
                Owner = this
            };

            dialog.ShowDialog();
        }

        private async void Copy_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CredentialEntry credential)
                return;

            try
            {
                Clipboard.SetText(credential.Password);
                ShowStatus("Mot de passe copié — le presse-papiers sera effacé dans 30 secondes.");
                await ClearClipboardLaterAsync(credential.Password);
            }
            catch
            {
                ShowStatus("Impossible d’accéder au presse-papiers.");
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CredentialEntry credential)
                return;

            var confirm = new VaultDeleteConfirmDialog(credential.Host, credential.Username)
            {
                Owner = this
            };

            if (confirm.ShowDialog() != true)
                return;

            _vault.Delete(credential.Host, credential.Username);
            Refresh();
            ShowStatus("Identifiant supprimé du coffre.");
        }

        private void ShowStatus(string message)
        {
            StatusText.Text = message;
            StatusBorder.Visibility = Visibility.Visible;
            _statusTimer.Stop();
            _statusTimer.Start();
        }

        private static async Task ClearClipboardLaterAsync(string copiedPassword)
        {
            await Task.Delay(TimeSpan.FromSeconds(30));

            try
            {
                await Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    if (Clipboard.ContainsText() &&
                        string.Equals(Clipboard.GetText(), copiedPassword, StringComparison.Ordinal))
                    {
                        Clipboard.Clear();
                    }
                });
            }
            catch
            {
                // Le presse-papiers peut être verrouillé par une autre application.
            }
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); }
                catch { }
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
