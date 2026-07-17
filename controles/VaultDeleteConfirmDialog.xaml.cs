using System.Windows;
using System.Windows.Input;

namespace MyHomelabBrowser.controles
{
    public partial class VaultDeleteConfirmDialog : Window
    {
        public VaultDeleteConfirmDialog(string site, string username)
        {
            InitializeComponent();
            SiteText.Text = site ?? string.Empty;
            UsernameText.Text = username ?? string.Empty;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ButtonState == MouseButtonState.Pressed)
            {
                try { DragMove(); }
                catch { }
            }
        }
    }
}
