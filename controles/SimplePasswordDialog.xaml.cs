using System.Windows;
using System.Windows.Threading;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles
{
    public partial class SimplePasswordDialog : DialogWindow
    {
        public string Password => PasswordInput.Password;

        public Func<string, bool>? ValidatePassword { get; set; }

        private int _failedAttempts;

        public SimplePasswordDialog(string title)
        {
            InitializeComponent();
            Title = title;
            Loaded += (_, _) => Dispatcher.BeginInvoke(() => PasswordInput.Focus(), DispatcherPriority.Input);
        }

        private void PasswordInput_PasswordChanged(object sender, RoutedEventArgs e)
        {
            OkButton.IsEnabled = PasswordInput.Password.Length > 0;
            ErrorText.Visibility = Visibility.Collapsed;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            if (ValidatePassword != null && !ValidatePassword(PasswordInput.Password))
            {
                _failedAttempts++;

                ErrorText.Text = _failedAttempts >= 3
                    ? Tr("Trop de tentatives. Veuillez patienter.")
                    : Tr("Mot de passe incorrect");

                PasswordInput.Clear();
                ErrorText.Visibility = Visibility.Visible;
                PasswordInput.Focus();
                return;
            }

            DialogResult = true;
        }
    }
}
