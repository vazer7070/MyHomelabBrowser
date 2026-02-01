using MyHomelabBrowser.classes.Profiles.Credentials;
using System.Collections.Generic;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;

namespace MyHomelabBrowser.controles
{
    public partial class PasswordVaultWindow : Window
    {
        readonly CredentialVaultService _vault;
        List<CredentialEntry> _allItems = new();

        public PasswordVaultWindow(CredentialVaultService vault)
        {
            InitializeComponent();
            _vault = vault;

            Refresh();
        }

        void Refresh()
        {
            _allItems = _vault.GetAll().ToList();
            ApplyFilter();
        }
        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ApplyFilter();
        }

        void ApplyFilter()
        {
            var q = (SearchBox.Text ?? "").Trim().ToLowerInvariant();

            if (string.IsNullOrEmpty(q))
            {
                List.ItemsSource = _allItems;
                return;
            }

            List.ItemsSource = _allItems.Where(x =>
                (x.Host?.ToLowerInvariant().Contains(q) ?? false) ||
                (x.Username?.ToLowerInvariant().Contains(q) ?? false)
            ).ToList();
        }

        private void Reveal_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CredentialEntry c)
                return;

            MessageBox.Show(
                c.Password,
                $"Mot de passe pour {c.Host}",
                MessageBoxButton.OK,
                MessageBoxImage.Information
            );
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CredentialEntry c)
                return;

            Clipboard.SetText(c.Password);
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not CredentialEntry c)
                return;

            if (MessageBox.Show(
                $"Supprimer l’identifiant pour {c.Host} ?",
                "Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            _vault.Delete(c.Host, c.Username);
            Refresh();
            ApplyFilter();

        }
        public class BoolToVisibilityConverter : IValueConverter
        {
            public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
                => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;

            public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
                => throw new NotSupportedException();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
