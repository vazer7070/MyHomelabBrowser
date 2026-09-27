using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles
{
    public partial class LoginDialog : DialogWindow
    {
        public string Username
        {
            get => UsernameBox.Text?.Trim() ?? "";
            set => UsernameBox.Text = value;
        }
        public Func<string, string, bool>? ValidateLogin { get; set; }

        // Message affiché à la place de « Mot de passe incorrect » (verrouillage temporaire…).
        public Func<string, string?>? FailureMessageProvider { get; set; }

        public string Password => PasswordBox.Password ?? "";

        public LoginDialog(string? presetUsername = null)
        {
            InitializeComponent();

            if (!string.IsNullOrWhiteSpace(presetUsername))
                UsernameBox.Text = presetUsername;

            // Nom déjà connu : on saisit directement le mot de passe.
            Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                if (string.IsNullOrWhiteSpace(UsernameBox.Text))
                    UsernameBox.Focus();
                else
                    PasswordBox.Focus();
            }, DispatcherPriority.Input);
        }

        private void Input_Changed(object sender, RoutedEventArgs e)
        {
            LoginButton.IsEnabled =
                !string.IsNullOrWhiteSpace(UsernameBox.Text) &&
                PasswordBox.Password.Length > 0;

            // Retour au style du thème.
            PasswordBox.ClearValue(Control.BorderBrushProperty);
            PasswordErrorText.Visibility = Visibility.Collapsed;
        }

        void ShakePasswordBox()
        {
            var animation = new DoubleAnimation
            {
                From = -6,
                To = 6,
                Duration = TimeSpan.FromMilliseconds(50),
                AutoReverse = true,
                RepeatBehavior = new RepeatBehavior(3)
            };

            PasswordShakeTransform.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        public void ShowPasswordError(string? message = null)
        {
            PasswordBox.SetResourceReference(Control.BorderBrushProperty, "DangerBrush");
            PasswordErrorText.Text = string.IsNullOrWhiteSpace(message) ? Tr("Mot de passe incorrect") : message;
            PasswordErrorText.Visibility = Visibility.Visible;

            ShakePasswordBox();
            PasswordBox.Focus();
            PasswordBox.SelectAll();
        }

        private void Login_Click(object sender, RoutedEventArgs e)
        {
            if (ValidateLogin == null || !ValidateLogin(Username, Password))
            {
                ShowPasswordError(FailureMessageProvider?.Invoke(Username));
                return;
            }

            DialogResult = true;
        }
    }
}
