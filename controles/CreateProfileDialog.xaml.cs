using System.Windows;

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
                    "Erreur",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (UsernameExists?.Invoke(Username) == true)
            {
                MessageBox.Show(
                    "Un profil porte déjà ce nom.",
                    "Erreur",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (Password.Length < 6)
            {
                MessageBox.Show(
                    "Le mot de passe doit faire au moins 6 caractères.",
                    "Erreur",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            if (Password != ConfirmBox.Password)
            {
                MessageBox.Show(
                    "Les mots de passe ne correspondent pas.",
                    "Erreur",
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
