using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Homelab;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles
{
    public partial class EmptyStartPage : UserControl
    {
        private const int MaxFavoriteTiles = 10;

        readonly DispatcherTimer _debounce;
        bool _mouseDownInSuggestions;
        public event Action<string>? NavigateRequested;

        // Services du homelab
        public event Action<string, bool>? ServiceOpenRequested;
        public event Action? AddServiceRequested;
        public event Action<Guid>? EditServiceRequested;
        public event Action<Guid>? DeleteServiceRequested;
        public event Action<Guid>? CheckServiceRequested;
        public event Action? CheckAllServicesRequested;
        public event Action? ImportLocalFavoritesRequested;

        ObservableCollection<ServiceTile>? _serviceTiles;

        IReadOnlyList<HistoryEntry> _history = Array.Empty<HistoryEntry>();
        List<OmniboxSuggestion> _currentSuggestions = new();

        public BrowserSettings.SearchEngine SearchEngine { get; set; } = BrowserSettings.SearchEngine.Google;

        public EmptyStartPage()
        {
            InitializeComponent();

            _debounce = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(120)
            };
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                UpdateSuggestions();
            };
        }

        private void StartPage_Loaded(object sender, RoutedEventArgs e)
        {
            int hour = DateTime.Now.Hour;
            GreetingText.Text = hour switch
            {
                < 5 => Tr("Bonne nuit"),
                < 12 => Tr("Bonjour"),
                < 18 => Tr("Bon après-midi"),
                _ => Tr("Bonsoir")
            };

            if (_serviceTiles != null)
            {
                _serviceTiles.CollectionChanged -= ServiceTiles_CollectionChanged;
                _serviceTiles.CollectionChanged += ServiceTiles_CollectionChanged;
                UpdateServicesState();
            }

            // Focus direct dans le champ, comme la page de nouvel onglet des autres navigateurs.
            Dispatcher.BeginInvoke(() => SearchBox.Focus(), DispatcherPriority.Input);
        }

        // =========================
        // Données
        // =========================
        public void SetHistory(IReadOnlyList<HistoryEntry> history)
        {
            _history = history ?? Array.Empty<HistoryEntry>();
        }

        public void SetFavorites(IEnumerable<FavoriteItem> favorites)
        {
            var tiles = (favorites ?? Enumerable.Empty<FavoriteItem>())
                .Where(f => !string.IsNullOrWhiteSpace(f.Url))
                .Take(MaxFavoriteTiles)
                .Select(f => new FavoriteTile(f))
                .ToList();

            FavoritesTiles.ItemsSource = tiles;
            FavoritesSection.Visibility = tiles.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // =========================
        // Services du homelab
        // =========================

        /// <summary>
        /// Les tuiles sont partagées entre toutes les pages d'accueil : leur état se met à
        /// jour partout à chaque vérification. Chaque page a sa propre vue groupée.
        /// </summary>
        public void SetServices(ObservableCollection<ServiceTile> tiles, int localFavoriteCount)
        {
            if (!ReferenceEquals(_serviceTiles, tiles))
            {
                if (_serviceTiles != null)
                    _serviceTiles.CollectionChanged -= ServiceTiles_CollectionChanged;

                _serviceTiles = tiles;
                _serviceTiles.CollectionChanged += ServiceTiles_CollectionChanged;

                var view = new ListCollectionView(tiles);
                view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(ServiceTile.Group)));
                view.SortDescriptions.Add(new SortDescription(nameof(ServiceTile.Group), ListSortDirection.Ascending));
                view.SortDescriptions.Add(new SortDescription(nameof(ServiceTile.Name), ListSortDirection.Ascending));
                view.IsLiveSorting = true;
                view.IsLiveGrouping = true;
                ServicesTiles.ItemsSource = view;
            }

            ImportLocalFavoritesButton.Content = localFavoriteCount == 1
                ? Tr("Importer 1 favori local")
                : Tr("Importer {0} favoris locaux", localFavoriteCount);
            ImportLocalFavoritesButton.Visibility = localFavoriteCount > 0 ? Visibility.Visible : Visibility.Collapsed;

            UpdateServicesState();
        }

        private void ServiceTiles_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e) => UpdateServicesState();

        private void UpdateServicesState()
        {
            bool hasServices = _serviceTiles is { Count: > 0 };
            ServicesTiles.Visibility = hasServices ? Visibility.Visible : Visibility.Collapsed;
            ServicesEmptyState.Visibility = hasServices ? Visibility.Collapsed : Visibility.Visible;
            CheckAllServicesButton.Visibility = hasServices ? Visibility.Visible : Visibility.Collapsed;
        }

        private static ServiceTile? TileFrom(object sender)
            => (sender as FrameworkElement)?.DataContext as ServiceTile;

        private void ServiceTile_Click(object sender, RoutedEventArgs e)
        {
            if (TileFrom(sender) is { } tile)
                ServiceOpenRequested?.Invoke(tile.Url, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        }

        private void ServiceTile_PreviewMouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && TileFrom(sender) is { } tile)
            {
                ServiceOpenRequested?.Invoke(tile.Url, true);
                e.Handled = true;
            }
        }

        private void ServiceOpenInNewTab_Click(object sender, RoutedEventArgs e)
        {
            if (TileFrom(sender) is { } tile)
                ServiceOpenRequested?.Invoke(tile.Url, true);
        }

        private void ServiceCheck_Click(object sender, RoutedEventArgs e)
        {
            if (TileFrom(sender) is { } tile)
                CheckServiceRequested?.Invoke(tile.Id);
        }

        private void ServiceEdit_Click(object sender, RoutedEventArgs e)
        {
            if (TileFrom(sender) is { } tile)
                EditServiceRequested?.Invoke(tile.Id);
        }

        private void ServiceDelete_Click(object sender, RoutedEventArgs e)
        {
            if (TileFrom(sender) is { } tile)
                DeleteServiceRequested?.Invoke(tile.Id);
        }

        private void AddService_Click(object sender, RoutedEventArgs e) => AddServiceRequested?.Invoke();

        private void CheckAllServices_Click(object sender, RoutedEventArgs e) => CheckAllServicesRequested?.Invoke();

        private void ImportLocalFavorites_Click(object sender, RoutedEventArgs e) => ImportLocalFavoritesRequested?.Invoke();

        private void StartPage_Unloaded(object sender, RoutedEventArgs e)
        {
            // La collection partagée survit à la page : on se désabonne pour ne pas la retenir.
            if (_serviceTiles != null)
                _serviceTiles.CollectionChanged -= ServiceTiles_CollectionChanged;
        }

        private void FavoriteTile_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is string url && !string.IsNullOrWhiteSpace(url))
                Navigate(url);
        }

        // =========================
        // Suggestions
        // =========================
        void Navigate(string url)
        {
            HideSuggestions();
            NavigateRequested?.Invoke(url);
        }

        void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (_mouseDownInSuggestions)
                return;

            HideSuggestions();
        }

        void SuggestionsList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            _mouseDownInSuggestions = true;
        }

        void SuggestionsList_MouseUp(object sender, MouseButtonEventArgs e)
        {
            _mouseDownInSuggestions = false;

            if (e.OriginalSource is DependencyObject source &&
                ItemsControl.ContainerFromElement(SuggestionsList, source) is ListBoxItem { DataContext: OmniboxSuggestion s })
            {
                e.Handled = true;
                Navigate(s.Url);
            }
        }

        void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _debounce.Stop();
            _debounce.Start();
        }

        void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                if (SuggestionsBorder.Visibility == Visibility.Visible &&
                    SuggestionsList.SelectedItem is OmniboxSuggestion s)
                {
                    Navigate(s.Url);
                }
                else
                {
                    NavigateFromInput(SearchBox.Text);
                }

                e.Handled = true;
                return;
            }

            if (SuggestionsBorder.Visibility != Visibility.Visible)
                return;

            // La sélection au clavier ne navigue plus immédiatement :
            // auparavant, la première flèche ouvrait déjà la suggestion.
            if (e.Key == Key.Down)
            {
                SuggestionsList.SelectedIndex =
                    Math.Min(SuggestionsList.SelectedIndex + 1,
                             SuggestionsList.Items.Count - 1);
                if (SuggestionsList.SelectedItem != null)
                    SuggestionsList.ScrollIntoView(SuggestionsList.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                SuggestionsList.SelectedIndex =
                    Math.Max(SuggestionsList.SelectedIndex - 1, -1);
                if (SuggestionsList.SelectedItem != null)
                    SuggestionsList.ScrollIntoView(SuggestionsList.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                HideSuggestions();
                e.Handled = true;
            }
        }

        void ClearSearch_Click(object sender, RoutedEventArgs e)
        {
            SearchBox.Clear();
            HideSuggestions();
            SearchBox.Focus();
        }

        void UpdateSuggestions()
        {
            var input = SearchBox.Text?.Trim();

            if (string.IsNullOrEmpty(input))
            {
                HideSuggestions();
                return;
            }

            _currentSuggestions = BuildSuggestions(input);
            SuggestionsList.ItemsSource = _currentSuggestions;
            SuggestionsList.SelectedIndex = -1;

            SuggestionsBorder.Visibility =
                _currentSuggestions.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        List<OmniboxSuggestion> BuildSuggestions(string input)
        {
            var list = new List<OmniboxSuggestion>();
            var seenUrls = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Adresse directe (nas:5000, 192.168.1.10, exemple.fr…)
            string? directUrl = UrlResolver.TryResolveUrl(input);
            if (directUrl != null && seenUrls.Add(directUrl))
            {
                list.Add(new OmniboxSuggestion
                {
                    Icon = "",
                    Prefix = Tr("Ouvrir "),
                    Match = directUrl,
                    Url = directUrl
                });
            }

            // Historique : parcours chronologique inverse, sans tri complet à chaque frappe.
            for (int i = _history.Count - 1; i >= 0 && list.Count < 8; i--)
            {
                HistoryEntry h = _history[i];
                string title = string.IsNullOrWhiteSpace(h.Title) ? h.Url : h.Title;

                bool matches =
                    title.Contains(input, StringComparison.OrdinalIgnoreCase) ||
                    h.Url.Contains(input, StringComparison.OrdinalIgnoreCase);

                if (!matches || !seenUrls.Add(h.Url))
                    continue;

                int idx = title.IndexOf(input, StringComparison.OrdinalIgnoreCase);
                list.Add(idx < 0
                    ? new OmniboxSuggestion { Icon = "", Prefix = title, Suffix = "  —  " + h.Url, Url = h.Url }
                    : new OmniboxSuggestion
                    {
                        Icon = "",
                        Prefix = title[..idx],
                        Match = title.Substring(idx, input.Length),
                        Suffix = title[(idx + input.Length)..],
                        Url = h.Url
                    });
            }

            list.Add(new OmniboxSuggestion
            {
                Icon = "",
                Prefix = Tr("Rechercher "),
                Match = $"« {input} »",
                Suffix = Tr(" sur ") + UrlResolver.GetSearchEngineName(SearchEngine),
                Url = UrlResolver.BuildSearchUrl(input, SearchEngine)
            });

            return list;
        }

        void NavigateFromInput(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return;

            Navigate(UrlResolver.ResolveOrSearch(input, SearchEngine));
        }

        void HideSuggestions()
        {
            _mouseDownInSuggestions = false;
            SuggestionsBorder.Visibility = Visibility.Collapsed;
            SuggestionsList.ItemsSource = null;
        }

        sealed class FavoriteTile
        {
            public FavoriteTile(FavoriteItem favorite)
            {
                Url = favorite.Url;
                Title = string.IsNullOrWhiteSpace(favorite.Title) ? FaviconStore.HostFromUrl(favorite.Url) : favorite.Title;
                Icon = FaviconStore.TryGet(favorite.Url);
                string source = FaviconStore.HostFromUrl(favorite.Url);
                Initial = source.Length > 0 ? source[..1].ToUpperInvariant() : "?";
            }

            public string Url { get; }
            public string Title { get; }
            public ImageSource? Icon { get; }
            public string Initial { get; }
            public bool HasNoIcon => Icon == null;
        }
    }

    class OmniboxSuggestion
    {
        public string Icon { get; set; } = "";
        public string Prefix { get; set; } = "";
        public string Match { get; set; } = "";
        public string Suffix { get; set; } = "";
        public string Url { get; set; } = "";
    }
}
