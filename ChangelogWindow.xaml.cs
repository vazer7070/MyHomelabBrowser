using System.Windows;

namespace MyHomelabBrowser
{
    public partial class ChangelogWindow : Window
    {
        public ChangelogWindow(string version, string changelog)
        {
            InitializeComponent();
            TitleText.Text = $"Changelog - {version}";
            ChangelogText.Text = changelog;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}