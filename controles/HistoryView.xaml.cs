using MyHomelabBrowser.classes;
using System;
using System.Collections.Generic;
using System.Linq;
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
        readonly List<HistoryEntry> _all;
        readonly Action<string, bool> _navigate;
        readonly Action<HistoryEntry> _delete;

        public HistoryView(
            List<HistoryEntry> history,
            Action<string, bool> navigate,
            Action<HistoryEntry> delete)
        {
            InitializeComponent();

            _all = history;
            _navigate = navigate;
            _delete = delete;

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
            // 🚫 si on clique sur le bouton delete → ne pas naviguer
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


        void Refresh(string? filter = null)
        {
            IEnumerable<HistoryEntry> list = _all
                .OrderByDescending(h => h.VisitedAt);

            if (!string.IsNullOrWhiteSpace(filter))
            {
                filter = filter.ToLowerInvariant();
                list = list.Where(h =>
                    h.Title.ToLowerInvariant().Contains(filter) ||
                    h.Url.ToLowerInvariant().Contains(filter));
            }

            var vms = list
                .Select(h => new HistoryItemVM(h))
                .ToList();

            var view = CollectionViewSource.GetDefaultView(vms);
            view.GroupDescriptions.Clear();
            view.GroupDescriptions.Add(
                new PropertyGroupDescription(nameof(HistoryItemVM.Group)));

            HistoryList.ItemsSource = view;
        }

        private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
            => Refresh(SearchBox.Text);

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as Button)?.DataContext is HistoryItemVM vm)
            {
                _delete(vm.Entry);
                Refresh(SearchBox.Text);
            }
        }

        private void ClearAll_Click(object sender, RoutedEventArgs e)
        {
           

            _all.Clear();
            Refresh();
        }
    }

    class HistoryItemVM
    {
        public HistoryEntry Entry { get; }

        public string Title => Entry.Title;
        public string Url => Entry.Url;

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

        public ImageSource Favicon
        {
            get
            {
                try
                {
                    var uri = new Uri(Entry.Url);
                    return new BitmapImage(
                        new Uri($"https://www.google.com/s2/favicons?sz=64&domain={uri.Host}")
                    );
                }
                catch
                {
                    return null!;
                }
            }
        }

        public HistoryItemVM(HistoryEntry entry)
        {
            Entry = entry;
        }
    }
}
