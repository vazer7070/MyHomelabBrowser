using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MyHomelabBrowser.classes;
using PommeBrowser.Core;
using PommeBrowser.Linux.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>Proposition de la barre d'adresse.</summary>
    public sealed record OmniboxEntry(string Icon, string Primary, string? Secondary, Action Run);

    /// <summary>
    /// Barre d'adresse : adresse ou recherche, suggestions (services, favoris, historique),
    /// commandes « : » (:new, :history…), onglets « @ » et favoris seuls « * ».
    /// </summary>
    public sealed partial class MainWindow
    {
        bool _addressEditing;
        bool _settingAddress;

        void InitializeAddressBar()
        {
            SuggestionList.ItemTemplate = new Avalonia.Controls.Templates.FuncDataTemplate<OmniboxEntry>((entry, _) => BuildSuggestion(entry), supportsRecycling: false);
            SuggestionList.AddHandler(PointerReleasedEvent, (_, e) =>
            {
                if (SuggestionList.SelectedItem is OmniboxEntry entry)
                    RunSuggestion(entry);
            }, RoutingStrategies.Tunnel | RoutingStrategies.Bubble, handledEventsToo: true);

            AddressBar.TextChanged += (_, _) =>
            {
                if (_settingAddress || !AddressBar.IsFocused)
                    return;
                _addressEditing = true;
                ShowSuggestions(AddressBar.Text);
            };
            AddressBar.GotFocus += (_, _) =>
            {
                // Adresse complète pendant la saisie, sélectionnée pour être remplacée d'un coup.
                if (!_addressEditing && _selected?.Page == TabPage.Web && _selected.WebUrl.Length > 0)
                    SetAddressText(UrlDisplay.ForDisplay(_selected.WebUrl));
                Avalonia.Threading.Dispatcher.UIThread.Post(AddressBar.SelectAll);
            };
            AddressBar.LostFocus += (_, _) =>
            {
                HideSuggestions();
                _addressEditing = false;
                ShowAddress(_selected);
            };
            AddressBar.AddHandler(KeyDownEvent, AddressBar_KeyDown, RoutingStrategies.Tunnel);
        }

        void SetAddressText(string text)
        {
            _settingAddress = true;
            AddressBar.Text = text;
            _settingAddress = false;
        }

        /// <summary>Adresse de l'onglet (vide pour les pages de PommeBrowser, qui affichent le texte d'aide).</summary>
        void ShowAddress(BrowserTab? tab)
        {
            if (_addressEditing)
                return;
            string url = tab?.Url ?? string.Empty;
            SetAddressText(url.Length == 0 ? string.Empty : AddressBar.IsFocused ? UrlDisplay.ForDisplay(url) : UrlDisplay.Short(url));
        }

        public void FocusAddressBar()
        {
            Engine.EngineHost.ReclaimKeyboard(this);
            AddressBar.Focus();
            AddressBar.SelectAll();
        }

        void AddressBar_KeyDown(object? sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;
                    if (SuggestionsPopup.IsOpen && SuggestionList.SelectedItem is OmniboxEntry selected && SuggestionList.SelectedIndex > 0)
                        RunSuggestion(selected);
                    else
                        SubmitInput(AddressBar.Text ?? string.Empty, newTab: e.KeyModifiers.HasFlag(KeyModifiers.Alt));
                    break;

                case Key.Escape:
                    e.Handled = true;
                    if (SuggestionsPopup.IsOpen)
                    {
                        HideSuggestions();
                    }
                    else
                    {
                        _addressEditing = false;
                        ShowAddress(_selected);
                        _selected?.Engine?.Focus();
                    }
                    break;

                case Key.Down:
                case Key.Up:
                    if (!SuggestionsPopup.IsOpen || SuggestionList.ItemCount == 0)
                        return;
                    e.Handled = true;
                    int index = SuggestionList.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                    SuggestionList.SelectedIndex = Math.Clamp(index, 0, SuggestionList.ItemCount - 1);
                    break;
            }
        }

        /// <summary>Validation de la barre d'adresse : commande, onglet, adresse ou recherche.</summary>
        internal void SubmitInput(string input, bool newTab = false)
        {
            input = input.Trim();
            HideSuggestions();
            _addressEditing = false;
            if (input.Length == 0)
                return;

            if (input.StartsWith(':'))
            {
                ExecuteCommand(input[1..]);
                ShowAddress(_selected);
                return;
            }

            if (input.StartsWith('@'))
            {
                FocusTab(input[1..]);
                return;
            }

            string url = UrlResolver.ResolveOrSearch(input.TrimStart('*').Trim(), App.Settings.Search);
            if (newTab || _selected == null)
                NewTab(url, select: true);
            else
                _selected.Navigate(url);
            _selected?.Engine?.Focus();
        }

        /// <summary>Ouvre une adresse dans l'onglet actif (ou un nouvel onglet s'il n'y en a pas).</summary>
        public void Navigate(string url)
        {
            if (_selected == null)
                NewTab(url, select: true);
            else
                _selected.Navigate(url);
        }

        void ShowSuggestions(string? text)
        {
            List<OmniboxEntry> entries = BuildSuggestions((text ?? string.Empty).Trim());
            SuggestionList.ItemsSource = entries;
            SuggestionList.SelectedIndex = entries.Count > 0 ? 0 : -1;
            SuggestionsPopup.IsOpen = entries.Count > 0;
        }

        void HideSuggestions() => SuggestionsPopup.IsOpen = false;

        void RunSuggestion(OmniboxEntry entry)
        {
            HideSuggestions();
            _addressEditing = false;
            entry.Run();
            ShowAddress(_selected);
        }

        internal List<OmniboxEntry> BuildSuggestions(string query)
        {
            var entries = new List<OmniboxEntry>();
            if (query.Length == 0)
                return entries;

            if (query.StartsWith(':'))
            {
                string command = query[1..].Trim();
                entries.AddRange(EnabledCommands()
                    .Where(c => c.Key.StartsWith(command, StringComparison.OrdinalIgnoreCase))
                    .Select(c => new OmniboxEntry("IconChevronDown", ":" + c.Key, Tr(c.Description), () => ExecuteCommand(c.Key))));
                return entries;
            }

            if (query.StartsWith('@'))
            {
                string search = query[1..].Trim();
                foreach (BrowserTab tab in _tabs.Where(t => search.Length == 0 || t.Title.Contains(search, StringComparison.OrdinalIgnoreCase)))
                    entries.Add(new OmniboxEntry("IconTab", tab.Title, Tr("Aller à l’onglet"), () => SelectTab(tab)));
                return entries;
            }

            bool favoritesOnly = query.StartsWith('*');
            string text = favoritesOnly ? query[1..].Trim() : query;
            if (favoritesOnly)
            {
                foreach (FavoriteItem favorite in App.Favorites.All.Where(f => text.Length == 0 ||
                             f.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                             f.Url.Contains(text, StringComparison.OrdinalIgnoreCase)).Take(8))
                {
                    entries.Add(new OmniboxEntry("IconStar", favorite.Title, UrlDisplay.ForDisplay(favorite.Url), () => Navigate(favorite.Url)));
                }
                return entries;
            }

            foreach (Suggestion suggestion in OmniboxSuggestions.Build(text, App.Settings.Search, App.Services.GetAll(), App.Favorites.All, App.History.Recent))
            {
                string url = suggestion.Url;
                (string icon, string? secondary) = suggestion.Kind switch
                {
                    SuggestionKind.Search => ("IconSearch", (string?)null),
                    SuggestionKind.Address => ("IconGlobe", null),
                    SuggestionKind.Service => ("IconServer", UrlDisplay.ForDisplay(url)),
                    SuggestionKind.Favorite => ("IconStar", UrlDisplay.ForDisplay(url)),
                    _ => ("IconHistory", UrlDisplay.ForDisplay(url))
                };
                entries.Add(new OmniboxEntry(icon, suggestion.Kind == SuggestionKind.Address ? UrlDisplay.ForDisplay(url) : suggestion.Title, secondary, () => Navigate(url)));
            }
            return entries;
        }

        internal Control BuildSuggestion(OmniboxEntry? entry)
        {
            // Le modèle peut être appelé sans élément (conteneur recyclé).
            if (entry == null)
                return new Panel();

            var icon = new PathIcon
            {
                Data = this.FindResource(entry.Icon) as Geometry,
                Width = 14,
                Height = 14,
                VerticalAlignment = VerticalAlignment.Center
            };
            icon.Bind(PathIcon.ForegroundProperty, this.GetResourceObservable("TextSecondaryBrush"));

            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = entry.Primary, FontWeight = FontWeight.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(entry.Secondary))
            {
                var secondary = new TextBlock { Text = entry.Secondary, FontSize = 11.5, Margin = new Avalonia.Thickness(0, 1, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
                secondary.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextTertiaryBrush"));
                texts.Children.Add(secondary);
            }

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("28,*"), Margin = new Avalonia.Thickness(4, 2) };
            grid.Children.Add(icon);
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);
            return grid;
        }

        // ---------------------------------------------------------------
        // Commandes (:new, :close…)
        // ---------------------------------------------------------------

        IEnumerable<CommandSetting> EnabledCommands()
            => App.Settings.EnableCommands
                ? App.Settings.Commands.Where(c => c.Enabled).OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase)
                : Enumerable.Empty<CommandSetting>();

        void ExecuteCommand(string command)
        {
            command = command.Trim().ToLowerInvariant();
            if (!EnabledCommands().Any(c => string.Equals(c.Key, command, StringComparison.OrdinalIgnoreCase)))
            {
                ShowToast(Tr("Commande inconnue") + " — :" + command);
                return;
            }

            switch (command)
            {
                case "new":
                    NewTab(App.NewTabUrl, select: true);
                    break;
                case "close":
                    if (_selected != null)
                        CloseTab(_selected);
                    break;
                case "close others":
                    if (_selected != null)
                        CloseOtherTabs(_selected);
                    break;
                case "reload":
                    _selected?.Reload();
                    break;
                case "suspend":
                    if (_selected != null)
                        SuspendTab(_selected);
                    break;
                case "resume":
                    _selected?.LoadPendingIfNeeded();
                    break;
                case "suspend inactive":
                    SuspendInactiveTabs(force: true);
                    break;
                case "history":
                    OpenHistory();
                    break;
                case "diagnostic":
                    OpenDiagnostics();
                    break;
            }
        }

        void FocusTab(string query)
        {
            query = query.Trim();
            BrowserTab? match = _tabs.FirstOrDefault(t => query.Length > 0 && t.Title.Contains(query, StringComparison.OrdinalIgnoreCase));
            if (match != null)
                SelectTab(match);
            else
                ShowToast(Tr("Aucun onglet ne correspond à « {0} ».", query));
        }
    }
}
