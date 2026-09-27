using MyHomelabBrowser.classes.Profiles;
using MyHomelabBrowser.classes.Profiles.Credentials;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles
{
    /// <summary>
    /// Logique d'interaction pour ProfileSettingsDialog.xaml
    /// </summary>
    public partial class ProfileSettingsDialog : Window
    {
        readonly ProfileService _profileService;
        readonly CredentialVaultService _vault;


        public ProfileSettingsDialog(ProfileService profileService, CredentialVaultService vault)
        {
            InitializeComponent();

            _profileService = profileService;
            _vault = vault;

            if (_profileService.Current != null)
                UsernameBox.Text = _profileService.Current.Username;
        }

        private void Passwords_Click(object sender, RoutedEventArgs e)
        {
            if (_profileService.Current == null)
                return;

            // Vault inexistant => création
            if (!_vault.VaultExists)
            {
                var create = new MyHomelabBrowser.controles.SimplePasswordDialog(Tr("Créer le mot de passe du coffre"))
                {
                    Owner = this
                };

                if (create.ShowDialog() != true)
                    return;

                var pw1 = create.Password;
                if (string.IsNullOrWhiteSpace(pw1))
                    return;

                var confirm = new MyHomelabBrowser.controles.SimplePasswordDialog(Tr("Confirmer le mot de passe du coffre"))
                {
                    Owner = this
                };

                if (confirm.ShowDialog() != true)
                    return;

                var pw2 = confirm.Password;
                if (!string.Equals(pw1, pw2, StringComparison.Ordinal))
                {
                    MessageBox.Show(Tr("Les mots de passe ne correspondent pas."));
                    return;
                }

                if (!_vault.TryInitializeNewVault(pw1))
                {
                    MessageBox.Show(Tr("Impossible de créer le coffre."));
                    return;
                }
            }
            else
            {
                // Vault existant => unlock au mot de passe du VAULT
                var ask = new MyHomelabBrowser.controles.SimplePasswordDialog(Tr("Déverrouiller le coffre"))
                {
                    Owner = this
                };

                if (ask.ShowDialog() != true)
                    return;

                if (!_vault.TryUnlock(ask.Password))
                {
                    MessageBox.Show(Tr("Mot de passe du coffre incorrect (ou trop de tentatives)."));
                    return;
                }
            }

            var win = new PasswordVaultWindow(_vault) { Owner = this };
            win.ShowDialog();
        }

        private void ChangeVaultPassword_Click(object sender, RoutedEventArgs e)
        {
            if (_profileService.Current == null)
                return;

            if (!_vault.VaultExists)
            {
                MessageBox.Show(Tr("Le coffre n'est pas encore créé. Ouvrez-le une première fois pour définir un mot de passe."));
                return;
            }

            // 1) Vérif mot de passe PROFIL (autorisation)
            var verifyProfile = new LoginDialog(_profileService.Current.Username)
            {
                Owner = this,
                Title = Tr("Confirmer le mot de passe du profil"),
                ValidateLogin = (_, p) =>
                    _profileService.Current != null &&
                    _profileService.VerifyPassword(_profileService.Current, p)
            };

            if (verifyProfile.ShowDialog() != true)
                return;

            // 2) Ancien mdp VAULT
            var oldDlg = new MyHomelabBrowser.controles.SimplePasswordDialog(Tr("Mot de passe actuel du coffre"))
            {
                Owner = this
            };
            if (oldDlg.ShowDialog() != true)
                return;

            // 3) Nouveau mdp VAULT + confirmation
            var newDlg = new MyHomelabBrowser.controles.SimplePasswordDialog(Tr("Nouveau mot de passe du coffre"))
            {
                Owner = this
            };
            if (newDlg.ShowDialog() != true)
                return;

            var confirm = new MyHomelabBrowser.controles.SimplePasswordDialog(Tr("Confirmer le nouveau mot de passe du coffre"))
            {
                Owner = this
            };
            if (confirm.ShowDialog() != true)
                return;

            if (!string.Equals(newDlg.Password, confirm.Password, StringComparison.Ordinal))
            {
                MessageBox.Show(Tr("Les mots de passe ne correspondent pas."));
                return;
            }

            if (!_vault.TryChangeVaultPassword(oldDlg.Password, newDlg.Password))
            {
                MessageBox.Show(Tr("Impossible de changer le mot de passe du coffre (ancien mot de passe incorrect ?)."));
                return;
            }

            MessageBox.Show(Tr("Mot de passe du coffre mis à jour."));
        }



        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var username = UsernameBox.Text.Trim();
            var password = PasswordBox.Password;

            if (username.Length == 0)
            {
                MessageBox.Show(Tr("Le nom du profil est obligatoire."), Tr("Profil"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            if (!string.IsNullOrEmpty(password) && password.Length < 6)
            {
                MessageBox.Show(Tr("Le mot de passe doit faire au moins 6 caractères."), Tr("Profil"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                _profileService.UpdateProfile(
                    username,
                    string.IsNullOrWhiteSpace(password) ? null : password
                );
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(
                    ex is InvalidOperationException
                        ? ex.Message
                        : Tr("Impossible de renommer le dossier du profil. Fermez les onglets Flash Legacy puis réessayez.\n\n") + ex.Message,
                    Tr("Profil"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var res = MessageBox.Show(
                Tr("Supprimer ce profil définitivement ?"),
                Tr("Confirmation"),
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
