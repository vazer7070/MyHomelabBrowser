using System.Windows;

namespace MyHomelabBrowser.controles
{
    public partial class SaveCredentialDialog : Window
    {
        public string Host { get; }
        public string Username { get; }

        public bool NeverSave => AlwaysSaveCheckBox.IsChecked == true;


        public SaveCredentialDialog(string host, string username)
        {
            InitializeComponent();
            Host = host;
            Username = string.IsNullOrWhiteSpace(username)
                ? "(aucun utilisateur)"
                : username;

            DataContext = this;
        }

        void Yes_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
        }

        void No_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
