using System.Windows;
using System.Windows.Threading;

namespace MyHomelabBrowser.controles
{
    public partial class UnlockVaultDialog : DialogWindow
    {
        public string EnteredPassword { get; private set; } = "";

        public UnlockVaultDialog()
        {
            InitializeComponent();
            Loaded += (_, _) => Dispatcher.BeginInvoke(() => PasswordBox.Focus(), DispatcherPriority.Input);
        }

        private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
            => UnlockButton.IsEnabled = PasswordBox.Password.Length > 0;

        private void Unlock_Click(object sender, RoutedEventArgs e)
        {
            EnteredPassword = PasswordBox.Password;
            DialogResult = true;
        }
    }
}
