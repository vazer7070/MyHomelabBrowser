using MyHomelabBrowser.controles;
using System.Windows;
using System.Windows.Threading;

namespace MyHomelabBrowser
{
    public partial class AddLegacySiteDialog : DialogWindow
    {
        public string? ResultDomain { get; private set; }

        public AddLegacySiteDialog()
        {
            InitializeComponent();
            Loaded += (_, _) => Dispatcher.BeginInvoke(() => DomainBox.Focus(), DispatcherPriority.Input);
        }

        private void DomainBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
            => OkBtn.IsEnabled = DomainBox.Text.Trim().Length > 0;

        private void OkBtn_Click(object sender, RoutedEventArgs e)
        {
            ResultDomain = DomainBox.Text.Trim();
            DialogResult = true;
        }
    }
}
