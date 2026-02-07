using MyHomelabBrowser.classes;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MyHomelabBrowser.controles
{
    public partial class EmptyStartPage : UserControl
    {
        readonly DispatcherTimer _debounce;
        bool _mouseDownInSuggestions;
        public event Action<string>? NavigateRequested;


        IReadOnlyList<HistoryEntry> _history = Array.Empty<HistoryEntry>();
        List<OmniboxSuggestion> _currentSuggestions = new();

        public EmptyStartPage()
        {
            InitializeComponent();

            _debounce = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _debounce.Tick += (_, _) =>
            {
                _debounce.Stop();
                UpdateSuggestions();
            };
        }

        // =========================
        // 🔗 INJECTION HISTORIQUE
        // =========================
        public void SetHistory(IReadOnlyList<HistoryEntry> history)
        {
            _history = history ?? Array.Empty<HistoryEntry>();
        }
        void SuggestionsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SuggestionsList.SelectedItem is OmniboxSuggestion s)
                Navigate(s.Url);
        }

        void Navigate(string url)
        {
            HideSuggestions();

            if (NavigateRequested != null)
                NavigateRequested(url);
            else
                System.Diagnostics.Debug.WriteLine("[EmptyStartPage] NavigateRequested non branché: " + url);
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

        void SuggestionsList_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            _mouseDownInSuggestions = false;
            HideSuggestions();
        }

        void SuggestionsList_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (SuggestionsList.SelectedItem is OmniboxSuggestion s)
                Navigate(s.Url);
        }

        // =========================
        // UI EVENTS
        // =========================

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

            if (e.Key == Key.Down)
            {
                SuggestionsList.SelectedIndex =
                    Math.Min(SuggestionsList.SelectedIndex + 1,
                             SuggestionsList.Items.Count - 1);
                SuggestionsList.ScrollIntoView(SuggestionsList.SelectedItem);
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                SuggestionsList.SelectedIndex =
                    Math.Max(SuggestionsList.SelectedIndex - 1, 0);
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

        // =========================
        // OMNIBOX CORE
        // =========================

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

            SuggestionsBorder.Visibility =
                _currentSuggestions.Count > 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        List<OmniboxSuggestion> BuildSuggestions(string input)
        {
            var list = new List<OmniboxSuggestion>();

            // 1️⃣ HISTORIQUE DU PROFIL COURANT
            var historyMatches =
                _history
                .Where(h =>
                    !string.IsNullOrEmpty(h.Title) &&
                    h.Title.Contains(input, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(h => h.VisitedAt)
                .Take(8);

            foreach (var h in historyMatches)
            {
                var idx = h.Title.IndexOf(input, StringComparison.OrdinalIgnoreCase);
                if (idx < 0)
                    continue;

                list.Add(new OmniboxSuggestion
                {
                    Icon = "🕘",
                    Prefix = h.Title[..idx],
                    Match = h.Title.Substring(idx, input.Length),
                    Suffix = h.Title[(idx + input.Length)..],
                    Url = h.Url
                });
            }

            // 2️⃣ GOOGLE (fallback)
            list.Add(new OmniboxSuggestion
            {
                Icon = "🔍",
                Prefix = "Rechercher ",
                Match = $"\"{input}\"",
                Suffix = " sur Google",
                Url = $"https://www.google.com/search?q={Uri.EscapeDataString(input)}"
            });

            return list;
        }

        void NavigateFromInput(string input)
        {
            if (LooksLikeUrl(input))
                Navigate(NormalizeUrl(input));
            else
                Navigate($"https://www.google.com/search?q={Uri.EscapeDataString(input)}");
        }

        static bool LooksLikeUrl(string input)
        {
            return Uri.TryCreate(input, UriKind.Absolute, out _)
                   || input.Contains(".");
        }

        static string NormalizeUrl(string input)
        {
            if (Uri.TryCreate(input, UriKind.Absolute, out var uri))
                return uri.AbsoluteUri;

            return "https://" + input;
        }

        void HideSuggestions()
        {
            SuggestionsBorder.Visibility = Visibility.Collapsed;
            SuggestionsList.ItemsSource = null;
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
