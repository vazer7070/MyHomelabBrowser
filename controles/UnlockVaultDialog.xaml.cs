using System.Windows;
using System.Windows.Controls;

namespace MyHomelabBrowser.controles
{
    public partial class UnlockVaultDialog : Window
    {
        public string EnteredPassword { get; private set; } = "";

        public UnlockVaultDialog()
        {
            InitializeComponent();
            Loaded += (_, _) => PasswordBox.Focus();
        }

        void Unlock_Click(object sender, RoutedEventArgs e)
        {
            EnteredPassword = PasswordBox.Password;
            DialogResult = true;
        }

        void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
