using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Profiles.Credentials;
using MyHomelabBrowser.classes.Security;
using System;
using System.Windows;
using System.Windows.Threading;

namespace MyHomelabBrowser.controles
{
    public partial class TotpDialog : DialogWindow
    {
        private readonly CredentialVaultService _vault;
        private readonly CredentialEntry _credential;
        private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
        private TotpParameters? _parameters;

        public TotpDialog(CredentialVaultService vault, CredentialEntry credential)
        {
            InitializeComponent();
            _vault = vault;
            _credential = credential;

            AccountText.Text = $"{credential.DisplayHost} · {credential.Username}";
            _timer.Tick += (_, _) => RefreshCode();
            Closed += (_, _) => _timer.Stop();

            if (credential.HasTotp && Totp.TryParse(credential.TotpSecret, out TotpParameters parameters, out _))
                ShowCode(parameters);
            else
                ShowEditor();
        }

        /// <summary>
        /// Vrai si la clé a été ajoutée, modifiée ou retirée.
        /// </summary>
        public bool Changed { get; private set; }

        private void ShowCode(TotpParameters parameters)
        {
            _parameters = parameters;
            CountdownBar.Maximum = parameters.PeriodSeconds;
            EditPanel.Visibility = Visibility.Collapsed;
            CodePanel.Visibility = Visibility.Visible;
            RefreshCode();
            _timer.Start();
        }

        private void ShowEditor()
        {
            _timer.Stop();
            CodePanel.Visibility = Visibility.Collapsed;
            EditPanel.Visibility = Visibility.Visible;
            SecretBox.Text = string.Empty;
            Dispatcher.BeginInvoke(() => SecretBox.Focus(), DispatcherPriority.Input);
        }

        private void RefreshCode()
        {
            if (_parameters == null)
                return;

            DateTimeOffset now = DateTimeOffset.UtcNow;
            int remaining = Totp.SecondsRemaining(_parameters, now);
            CodeText.Text = Totp.FormatForDisplay(Totp.Generate(_parameters, now));
            CountdownBar.Value = remaining;
            CountdownText.Text = remaining == 1 ? "Nouveau code dans 1 seconde" : $"Nouveau code dans {remaining} secondes";
        }

        private void SecretBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
        {
            ErrorText.Visibility = Visibility.Collapsed;
            bool valid = Totp.TryParse(SecretBox.Text, out TotpParameters parameters, out _);
            SaveButton.IsEnabled = valid;
            PreviewText.Text = valid
                ? $"Code actuel : {Totp.FormatForDisplay(Totp.Generate(parameters, DateTimeOffset.UtcNow))} — vérifiez qu’il correspond à celui du service."
                : string.Empty;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _vault.SetTotpSecret(_credential.Host, _credential.Username, SecretBox.Text);
                _credential.TotpSecret = SecretBox.Text.Trim();
                Changed = true;

                if (Totp.TryParse(_credential.TotpSecret, out TotpParameters parameters, out _))
                    ShowCode(parameters);
            }
            catch (Exception ex)
            {
                ErrorText.Text = ex.Message;
                ErrorText.Visibility = Visibility.Visible;
            }
        }

        private void EditKey_Click(object sender, RoutedEventArgs e) => ShowEditor();

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            _vault.SetTotpSecret(_credential.Host, _credential.Username, null);
            _credential.TotpSecret = null;
            Changed = true;
            Close();
        }

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (_parameters == null)
                return;

            string code = Totp.Generate(_parameters, DateTimeOffset.UtcNow);
            if (!ClipboardHelper.TryCopyWithAutoClear(code))
            {
                ShowStatus("Le presse-papiers est momentanément indisponible.");
                return;
            }

            ShowStatus("Code copié — le presse-papiers sera effacé dans 30 secondes.");
        }

        private void ShowStatus(string message)
        {
            StatusText.Text = message;
            StatusText.Visibility = Visibility.Visible;
        }
    }
}
