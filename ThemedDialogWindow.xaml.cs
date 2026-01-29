using System.Windows;

namespace MyHomelabBrowser
{
    public partial class ThemedDialogWindow : Window
    {
        public ThemedDialogWindow(string title, string message)
        {
            InitializeComponent();
            TitleText.Text = title;
            MessageText.Text = message;
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        // Helper statique pratique
        public static void Show(Window owner, string title, string message)
        {
            new ThemedDialogWindow(title, message)
            {
                Owner = owner
            }.ShowDialog();
        }
    }
}