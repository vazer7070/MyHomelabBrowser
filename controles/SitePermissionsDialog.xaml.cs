using MyHomelabBrowser.classes.Security;
using System.Linq;
using System.Windows;
using System.Windows.Media;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles
{
    /// <summary>Autorisations et accès HTTP mémorisés pour le profil, révocables un par un.</summary>
    public partial class SitePermissionsDialog : DialogWindow
    {
        private readonly SiteSecurityStore _store;

        public SitePermissionsDialog(SiteSecurityStore store)
        {
            InitializeComponent();
            _store = store;
            Refresh();
        }

        private void Refresh()
        {
            var allowed = (Brush)FindResource("SuccessBrush");
            var denied = (Brush)FindResource("DangerBrush");

            var items = _store.All.Select(d => new
            {
                Decision = d,
                d.Site,
                Permission = MainWindow.DescribeSiteDecisionKind(d.Kind),
                State = d.Allowed ? Tr("Autorisé") : Tr("Bloqué"),
                StateBrush = d.Allowed ? allowed : denied
            }).ToList();

            DecisionsList.ItemsSource = items;
            DecisionsList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
            ClearButton.IsEnabled = items.Count > 0;
        }

        private void Remove_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not SiteDecision decision)
                return;

            _store.Remove(decision);
            Refresh();
        }

        private void Clear_Click(object sender, RoutedEventArgs e)
        {
            if (MessageDialog.Show(this, Tr("Retirer tous les choix mémorisés pour les sites ?"), Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;

            _store.Clear();
            Refresh();
        }
    }
}
