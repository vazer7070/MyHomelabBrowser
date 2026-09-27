using System.Windows;

namespace MyHomelabBrowser.controles
{
    public partial class LegacyConfirmDialog : DialogWindow
    {
        public bool AddRule { get; private set; }

        public LegacyConfirmDialog()
        {
            InitializeComponent();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            AddRule = AddRuleCheck.IsChecked == true;
            DialogResult = true;
        }
    }
}
