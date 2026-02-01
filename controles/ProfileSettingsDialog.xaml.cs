using MyHomelabBrowser.classes.Profiles;
using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace MyHomelabBrowser.controles
{
    /// <summary>
    /// Logique d'interaction pour ProfileSettingsDialog.xaml
    /// </summary>
    public partial class ProfileSettingsDialog : Window
    {
        readonly ProfileService _profileService;
        public event Action? PasswordsRequested;

        public ProfileSettingsDialog(ProfileService service)
        {
            InitializeComponent();
            _profileService = service;

            if (_profileService.Current != null)
                UsernameBox.Text = _profileService.Current.Username;
        }
        private void Passwords_Click(object sender, RoutedEventArgs e)
        {
            PasswordsRequested?.Invoke();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var username = UsernameBox.Text.Trim();
            var password = PasswordBox.Password;

            if (username.Length == 0)
            {
                MessageBox.Show("Nom invalide");
                return;
            }

            _profileService.UpdateProfile(
                username,
                string.IsNullOrWhiteSpace(password) ? null : password
            );

            DialogResult = true;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show(
                "Supprimer ce profil définitivement ?",
                "Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning
            );

            if (res != MessageBoxResult.Yes)
                return;

            _profileService.DeleteCurrentProfile();
            DialogResult = true;
        }
    }
}
