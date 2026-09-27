using MyHomelabBrowser.classes.Profiles.Credentials;
using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles
{
    public partial class VaultSecretDialog : Window
    {
        private readonly DispatcherTimer _closeTimer;
        private string _password;
        private int _secondsRemaining = 20;

        public VaultSecretDialog(CredentialEntry credential)
        {
            InitializeComponent();

            if (credential == null)
                throw new ArgumentNullException(nameof(credential));

            _password = credential.Password ?? string.Empty;
            SiteText.Text = credential.Host ?? string.Empty;
            UsernameText.Text = credential.Username ?? string.Empty;
            PasswordTextBox.Text = _password;

            _closeTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _closeTimer.Tick += CloseTimer_Tick;

            Loaded += (_, _) =>
            {
                UpdateCountdown();
                _closeTimer.Start();
            };

            Closed += (_, _) =>
            {
                _closeTimer.Stop();
                PasswordTextBox.Text = string.Empty;
                _password = string.Empty;
            };
        }

        private void CloseTimer_Tick(object? sender, EventArgs e)
        {
            _secondsRemaining--;
            if (_secondsRemaining <= 0)
            {
                _closeTimer.Stop();
                Close();
                return;
            }

            UpdateCountdown();
        }

        private void UpdateCountdown()
        {
            CountdownText.Text = Tr("Fermeture automatique dans {0} s", _secondsRemaining);
        }

        private async void Copy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(_password);
                CopyStatusText.Text = Tr("Mot de passe copié — effacement du presse-papiers dans 30 s");
                CopyButtonText.Text = Tr("Copié");
                CopyButton.IsEnabled = false;

                await ClearClipboardLaterAsync(_password);
            }
            catch
            {
                CopyStatusText.Text = Tr("Le presse-papiers est momentanément indisponible.");
                CopyButtonText.Text = Tr("Réessayer");
                CopyButton.IsEnabled = true;
            }
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
                // Une autre application peut momentanément verrouiller le presse-papiers.
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
