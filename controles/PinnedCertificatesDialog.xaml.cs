using MyHomelabBrowser.classes.Profiles;
using MyHomelabBrowser.classes.Security;
using System.Linq;
using System.Windows;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles
{
    public partial class PinnedCertificatesDialog : DialogWindow
    {
        private readonly CertificatePinStore _store;

        public PinnedCertificatesDialog()
        {
            InitializeComponent();
            _store = BrowserCertificateTrustHost.Current.GetPinStore(AppDataContext.Root);
            Refresh();
        }

        private void Refresh()
        {
            var items = _store.GetAll().Select(p => new
            {
                p.Authority,
                Detail = Tr("{0} · expire le {1:dd/MM/yyyy} · accepté le {2:dd/MM/yyyy}", p.Subject, p.NotAfter, p.PinnedAt)
            }).ToList();

            PinsList.ItemsSource = items;
            PinsList.Visibility = items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            EmptyText.Visibility = items.Count > 0 ? Visibility.Collapsed : Visibility.Visible;
        }

        private async void Remove_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is not string authority)
                return;

            _store.Remove(authority);
            Refresh();

            // Oublie les autorisations déjà données aux onglets ouverts.
            await BrowserCertificateTrustHost.Current.NotifyStoreChangedAsync(AppDataContext.Root);
        }
    }
}
