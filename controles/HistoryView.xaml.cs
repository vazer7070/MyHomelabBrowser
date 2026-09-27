using MyHomelabBrowser.classes;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles
{
    public partial class HistoryView : UserControl
    {
        readonly List<HistoryItemVM> _allVm;
        readonly Action<string, bool> _navigate;
        readonly Action<HistoryEntry> _delete;
        readonly Action<IReadOnlyCollection<HistoryEntry>> _deleteMany;
        readonly System.Windows.Threading.DispatcherTimer _searchDebounce;
        bool _onlyToday = false;

        public HistoryView(
            List<HistoryEntry> history,
            Action<string, bool> navigate,
            Action<HistoryEntry> delete,
            Action<IReadOnlyCollection<HistoryEntry>> deleteMany)
        {
            InitializeComponent();

            _navigate = navigate;
            _delete = delete;
            _deleteMany = deleteMany;

            _searchDebounce = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(180)
            };
            _searchDebounce.Tick += (_, _) =>
            {
                _searchDebounce.Stop();
                Refresh(SearchBox.Text);
            };

            _allVm = history
                .OrderByDescending(h => h.VisitedAt)
                .Select(h => new HistoryItemVM(h))
                .ToList();

            Refresh();
        }


        private void OpenEntry_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is HistoryItemVM vm)
            {
                _navigate(vm.Entry.Url, true);
                e.Handled = true;
            }
        }


        private void HistoryItem_Click(object sender, MouseButtonEventArgs e)
        {
            if (HistoryList.SelectedItems.Count > 1)
                return;
            if (e.ChangedButton == MouseButton.Right)
                return;


            if (e.OriginalSource is DependencyObject source && FindParent<Button>(source) != null)
                return;

            if (sender is not ListViewItem item)
                return;

            if (item.DataContext is not HistoryItemVM vm)
                return;

            _navigate(vm.Entry.Url, true);
            e.Handled = true;
        }

        static T? FindParent<T>(DependencyObject? child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T t)
                    return t;

                // Un Run (texte) n'est pas un Visual : VisualTreeHelper lèverait une exception.
                child = child is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(child)
                    : LogicalTreeHelper.GetParent(child);
            }
            return null;
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            if (_allVm.Count == 0)
                return;

            var answer = MessageBox.Show(
                Window.GetWindow(this),
                Tr("Effacer tout l’historique de navigation de ce profil ?"),
                Tr("Historique"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (answer != MessageBoxResult.Yes)
                return;

            // Suppression groupée sur le thread UI : l'historique est une List<>
            // partagée avec la navigation, elle ne doit pas être modifiée ailleurs.
            var toDelete = _allVm.Select(vm => vm.Entry).ToList();
            _allVm.Clear();
            _deleteMany(toDelete);
            Refresh();
        }

        private void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Text = "";
            SearchBox.Focus();
            Refresh();
        }

        private void OnlyTodayChk_Changed(object sender, RoutedEventArgs e)
        {
            _onlyToday = OnlyTodayChk.IsChecked == true;
            Refresh(SearchBox.Text);
        }

        private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            
        }
        private void HistoryItem_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            // ✅ empêche la sélection visuelle / logique
            if (sender is ListViewItem item)
                item.IsSelected = false;
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button btn)
                return;

            var item = FindParent<ListViewItem>(btn);
            if (item?.DataContext is not HistoryItemVM vm)
                return;

            _delete(vm.Entry);

            // ✅ remove de la liste VM (sinon ça réapparaît)
            _allVm.Remove(vm);

            Refresh(SearchBox.Text);

            e.Handled = true;
        }


        void Refresh(string? filter = null)
        {
            // _allVm est trié une seule fois à la construction.
            IEnumerable<HistoryItemVM> list = _allVm;

            if (_onlyToday)
            {
                var today = DateTime.Today;
                list = list.Where(vm => vm.Entry.VisitedAt.Date == today);
            }

            if (!string.IsNullOrWhiteSpace(filter))
            {
                string query = filter.Trim();
                list = list.Where(vm =>
                    (vm.Title ?? "").Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    (vm.Url ?? "").Contains(query, StringComparison.OrdinalIgnoreCase));
            }

            var vms = list.ToList();

            var view = CollectionViewSource.GetDefaultView(vms);
            view.GroupDescriptions.Clear();
            view.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(HistoryItemVM.Group)));

            HistoryList.ItemsSource = view;

            // UI
            if (CountText != null)
                CountText.Text = Tr("{0} élément(s)", vms.Count);

            if (ClearSearchBtn != null)
                ClearSearchBtn.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
                    ? Visibility.Collapsed
                    : Visibility.Visible;

        }


        private void CtxOpen_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not HistoryItemVM vm)
                return;

            _navigate(vm.Entry.Url, true);
        }

        private void CtxCopyUrl_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not HistoryItemVM vm)
                return;

            try
            {
                Clipboard.SetText(vm.Entry.Url);
            }
            catch { }
        }

        private void CtxDelete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not HistoryItemVM vm)
                return;

            _delete(vm.Entry);
            _allVm.Remove(vm);
            Refresh(SearchBox.Text);
        }


        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _searchDebounce.Stop();
            _searchDebounce.Start();
        }

        

    }

    class HistoryItemVM : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public HistoryEntry Entry { get; }

        public string Title => string.IsNullOrWhiteSpace(Entry.Title) ? Entry.Url : Entry.Title;
        public string Url => Entry.Url;

        // Icône issue du cache local (aucune requête vers un service tiers).
        // Évaluée à l'affichage : avec la virtualisation, seules les lignes visibles la chargent.
        public ImageSource? Favicon => _favicon ??= FaviconStore.TryGet(Entry.Url);
        ImageSource? _favicon;

        public bool HasFavicon => Favicon != null;

        public string RelativeDate
        {
            get
            {
                var d = Entry.VisitedAt.Date;
                var today = DateTime.Today;

                if (d == today) return Tr("Aujourd’hui");
                if (d == today.AddDays(-1)) return Tr("Hier");
                return Entry.VisitedAt.ToString("dd/MM/yyyy");
            }
        }

        public string Group
        {
            get
            {
                var d = Entry.VisitedAt.Date;
                var today = DateTime.Today;

                if (d == today) return Tr("Aujourd’hui");
                if (d == today.AddDays(-1)) return Tr("Hier");
                return Tr("Plus ancien");
            }
        }

        public HistoryItemVM(HistoryEntry entry)
        {
            Entry = entry;
        }
    }

}
