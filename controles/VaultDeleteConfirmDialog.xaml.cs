using System.Windows;

namespace MyHomelabBrowser.controles
{
    public partial class VaultDeleteConfirmDialog : DialogWindow
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
    }
}
