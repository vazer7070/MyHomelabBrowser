using System.Windows;
using System.Windows.Threading;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles
{
    public partial class CreateProfileDialog : DialogWindow
    {
        public string Username => UsernameBox.Text?.Trim() ?? "";
        public string Password => PasswordBox.Password ?? "";

        public Func<string, bool>? UsernameExists { get; set; }

        public CreateProfileDialog()
        {
            InitializeComponent();
            Loaded += (_, _) => Dispatcher.BeginInvoke(() => UsernameBox.Focus(), DispatcherPriority.Input);
        }

        private void Input_Changed(object sender, RoutedEventArgs e)
        {
            CreateButton.IsEnabled = Username.Length > 0 && Password.Length > 0 && ConfirmBox.Password.Length > 0;
            ErrorText.Visibility = Visibility.Collapsed;
        }

        private void Create_Click(object sender, RoutedEventArgs e)
        {
            string? error = null;

            if (!ProfileService.TryValidateUsername(Username, out string usernameError))
                error = usernameError;
            else if (UsernameExists?.Invoke(Username) == true)
                error = Tr("Un profil porte déjà ce nom.");
            else if (Password.Length < 6)
                error = Tr("Le mot de passe doit faire au moins 6 caractères.");
            else if (Password != ConfirmBox.Password)
                error = Tr("Les mots de passe ne correspondent pas.");

            if (error != null)
            {
                ErrorText.Text = error;
                ErrorText.Visibility = Visibility.Visible;
                return;
            }

            DialogResult = true;
        }
    }
}
