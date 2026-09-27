using MyHomelabBrowser.classes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace MyHomelabBrowser.controles
{
    public partial class EmptyStartPage : UserControl
    {
        private const int MaxFavoriteTiles = 10;

        readonly DispatcherTimer _debounce;
        bool _mouseDownInSuggestions;
        public event Action<string>? NavigateRequested;

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
                < 5 => "Bonne nuit",
                < 12 => "Bonjour",
                < 18 => "Bon après-midi",
                _ => "Bonsoir"
            };

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
                    Prefix = "Ouvrir ",
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
                Prefix = "Rechercher ",
                Match = $"« {input} »",
                Suffix = " sur " + UrlResolver.GetSearchEngineName(SearchEngine),
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
