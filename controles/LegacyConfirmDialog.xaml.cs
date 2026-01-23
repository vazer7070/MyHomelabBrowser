using System.Windows;

namespace MyHomelabBrowser.controles
{
    public partial class LegacyConfirmDialog : Window
    {
        public bool AddRule { get; private set; } = false;

        public LegacyConfirmDialog()
        {
            InitializeComponent();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            AddRule = AddRuleCheck.IsChecked == true;
            DialogResult = true;
            Close();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }
    }
}
