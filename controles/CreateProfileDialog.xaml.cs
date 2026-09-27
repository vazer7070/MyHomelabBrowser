using System.Windows;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles
{
    public partial class CreateProfileDialog : Window
    {
        public string Username => UsernameBox.Text?.Trim() ?? "";
        public string Password => PasswordBox.Password ?? "";

        public Func<string, bool>? UsernameExists { get; set; }

        public CreateProfileDialog()
        {
            InitializeComponent();
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            if (!ProfileService.TryValidateUsername(Username, out string usernameError))
            {
                MessageBox.Show(
                    usernameError,
                    Tr("Erreur"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (UsernameExists?.Invoke(Username) == true)
            {
                MessageBox.Show(
                    Tr("Un profil porte déjà ce nom."),
                    Tr("Erreur"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (Password.Length < 6)
            {
                MessageBox.Show(
                    Tr("Le mot de passe doit faire au moins 6 caractères."),
                    Tr("Erreur"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (Password != ConfirmBox.Password)
            {
                MessageBox.Show(
                    Tr("Les mots de passe ne correspondent pas."),
                    Tr("Erreur"),
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
