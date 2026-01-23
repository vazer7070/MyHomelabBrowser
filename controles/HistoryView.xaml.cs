using MyHomelabBrowser.classes;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyHomelabBrowser.controles
{
    public partial class HistoryView : UserControl
    {
        readonly List<HistoryItemVM> _allVm;
        readonly Action<string, bool> _navigate;
        readonly Action<HistoryEntry> _delete;
        bool _onlyToday = false;

        public HistoryView(
     List<HistoryEntry> history,
     Action<string, bool> navigate,
     Action<HistoryEntry> delete)
        {
            InitializeComponent();

            _navigate = navigate;
            _delete = delete;

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


            if (FindParent<Button>(e.OriginalSource as DependencyObject) != null)
                return;

            if (sender is not ListViewItem item)
                return;

            if (item.DataContext is not HistoryItemVM vm)
                return;

            _navigate(vm.Entry.Url, true);
            e.Handled = true;
        }

        static T? FindParent<T>(DependencyObject child) where T : DependencyObject
        {
            while (child != null)
            {
                if (child is T t)
                    return t;

                child = VisualTreeHelper.GetParent(child);
            }
            return null;
        }
        private async void ClearAll_Click(object sender, RoutedEventArgs e)
        {
            // ✅ UI instant
            var toDelete = _allVm.Select(vm => vm.Entry).ToList();
            _allVm.Clear();
            Refresh();

            // ✅ delete en arrière-plan
            await Task.Run(() =>
            {
                foreach (var entry in toDelete)
                    _delete(entry);
            });
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
            IEnumerable<HistoryItemVM> list = _allVm
                .OrderByDescending(vm => vm.Entry.VisitedAt);

            if (_onlyToday)
            {
                var today = DateTime.Today;
                list = list.Where(vm => vm.Entry.VisitedAt.Date == today);
            }

            if (!string.IsNullOrWhiteSpace(filter))
            {
                filter = filter.ToLowerInvariant();
                list = list.Where(vm =>
                    (vm.Title ?? "").ToLowerInvariant().Contains(filter) ||
                    (vm.Url ?? "").ToLowerInvariant().Contains(filter));
            }

            var vms = list.ToList();

            var view = CollectionViewSource.GetDefaultView(vms);
            view.GroupDescriptions.Clear();
            view.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(HistoryItemVM.Group)));

            HistoryList.ItemsSource = view;

            // UI
            if (CountText != null)
                CountText.Text = $"{vms.Count} élément(s)";

            if (ClearSearchBtn != null)
                ClearSearchBtn.Visibility = string.IsNullOrWhiteSpace(SearchBox.Text)
                    ? Visibility.Collapsed
                    : Visibility.Visible;

            

            _ = Task.Run(async () =>
            {
                foreach (var vm in vms)
                    await vm.LoadFaviconAsync();
            });

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
            => Refresh(SearchBox.Text);

        

    }

    class HistoryItemVM : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public HistoryEntry Entry { get; }
        static readonly HttpClient _http = new HttpClient();

        public string Title => Entry.Title;
        public string Url => Entry.Url;

        ImageSource? _favicon;
        public ImageSource? Favicon
        {
            get => _favicon;
            private set
            {
                if (_favicon == value) return;
                _favicon = value;
                OnPropertyChanged(nameof(Favicon));
            }
        }

        public string RelativeDate
        {
            get
            {
                var d = Entry.VisitedAt.Date;
                var today = DateTime.Today;

                if (d == today) return "Aujourd’hui";
                if (d == today.AddDays(-1)) return "Hier";
                return Entry.VisitedAt.ToString("dd/MM/yyyy");
            }
        }

        public string Group
        {
            get
            {
                var d = Entry.VisitedAt.Date;
                var today = DateTime.Today;

                if (d == today) return "Aujourd’hui";
                if (d == today.AddDays(-1)) return "Hier";
                return "Plus ancien";
            }
        }

        static readonly Dictionary<string, ImageSource> FaviconCache =
            new(StringComparer.OrdinalIgnoreCase);

        static readonly HashSet<string> FaviconLoading =
            new(StringComparer.OrdinalIgnoreCase);

        public async Task LoadFaviconAsync()
        {
            string host = "";

            try
            {
                host = new Uri(Entry.Url).Host;

                if (FaviconCache.TryGetValue(host, out var cached))
                {
                    Favicon = cached;
                    return;
                }

                lock (FaviconLoading)
                {
                    if (FaviconLoading.Contains(host))
                        return;

                    FaviconLoading.Add(host);
                }

                var faviconUrl = $"https://www.google.com/s2/favicons?sz=64&domain={host}";

                // ✅ téléchargement bytes (async, pas UI thread)
                var bytes = await _http.GetByteArrayAsync(faviconUrl);

                // ✅ decode image depuis stream => Freeze OK 100%
                var img = await Task.Run(() =>
                {
                    using var ms = new MemoryStream(bytes);

                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();

                    return (ImageSource)bmp;
                });

                FaviconCache[host] = img;
                Favicon = img;
            }
            catch
            {
                // ignore
            }
            finally
            {
                if (!string.IsNullOrWhiteSpace(host))
                {
                    lock (FaviconLoading)
                        FaviconLoading.Remove(host);
                }
            }
        }



        public HistoryItemVM(HistoryEntry entry)
        {
            Entry = entry;
        }
    }

}
