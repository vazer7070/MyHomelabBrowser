using MyHomelabBrowser.classes;
using MyHomelabBrowser.controles;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        private const int MaxOmniboxSuggestions = 8;

        // ---------------------------
        // Paramètres
        // ---------------------------
        void ApplySettings(BrowserSettings s)
        {
            _suspendTimer.IsEnabled = s.EnableSuspension;
            _SuspendDelay = TimeSpan.FromMinutes(Math.Max(1, s.SuspendDelayMinutes));

            DownloadManager.Instance.DownloadFolder = string.IsNullOrWhiteSpace(s.DownloadFolder)
                ? DownloadManager.DefaultDownloadFolder
                : s.DownloadFolder;

            // Les pages d'accueil ouvertes suivent le moteur de recherche choisi.
            foreach (var item in Tabs.Items)
            {
                if (item is TabItem { Tag: WebTabContent { IsCustomView: true } content } &&
                    content.HostGrid.Children.Count > 0 &&
                    content.HostGrid.Children[0] is EmptyStartPage page)
                {
                    page.SearchEngine = s.Search;
                }
            }
        }

        // ---------------------------
        // Souris globale
        // ---------------------------
        void OnGlobalMouseDown(object sender, MouseButtonEventArgs e)
        {
            if (!AddressBar.IsKeyboardFocusWithin &&
                !CommandSuggestionsPopup.IsMouseOver)
            {
                HideCommandSuggestions();
            }

            // Focus clavier Basilisk quand on clique dans la zone dockée.
            if (Tabs.SelectedItem is TabItem tab &&
                tab.Tag is WebTabContent wt &&
                wt.IsLegacyExternal &&
                wt.LegacyHost != null &&
                wt.LegacyHwnd != IntPtr.Zero &&
                wt.LegacyHost.IsMouseOver)
            {
                var embed = wt.LegacyEmbedHwnd != IntPtr.Zero ? wt.LegacyEmbedHwnd : wt.LegacyHwnd;
                FocusLegacyEmbedded(embed, wt.LegacyTopHwnd);
            }
        }

        // ---------------------------
        // Barre d'adresse
        // ---------------------------
        void UpdateAddressBarFromTab()
        {
            string url = string.Empty;

            if (Tabs.SelectedItem is TabItem tab && tab.Tag is WebTabContent webTab && !webTab.IsCustomView)
            {
                url = webTab.IsLegacyExternal && !string.IsNullOrWhiteSpace(webTab.LegacyUrl)
                    ? webTab.LegacyUrl
                    : webTab.PendingUrl ?? webTab.Web?.Source?.ToString() ?? string.Empty;

                if (url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                    url = string.Empty;
            }

            // L'utilisateur est en train de taper : on ne remplace pas sa saisie.
            if (!_addressBarEditing && !string.Equals(AddressBar.Text, url, StringComparison.Ordinal))
            {
                AddressBar.Text = url;
                HideCommandSuggestions();
            }

            UpdateSecurityIndicator(url);
            UpdateFavoriteButton();
            UpdateNavButtonsFast();
        }

        void UpdateSecurityIndicator(string? url)
        {
            string state;
            string tooltip;

            if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                state = "none";
                tooltip = "Page interne";
            }
            else if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            {
                state = "secure";
                tooltip = "Connexion sécurisée (HTTPS)";
            }
            else if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
            {
                bool local = UrlResolver.IsLocalHost(uri.Host);
                state = local ? "local" : "insecure";
                tooltip = local
                    ? "Service du réseau local (HTTP non chiffré)"
                    : "Connexion non sécurisée (HTTP) : les données transitent en clair";
            }
            else
            {
                state = "none";
                tooltip = uri.Scheme + ":";
            }

            SecurityIconSecure.Visibility = state == "secure" ? Visibility.Visible : Visibility.Collapsed;
            SecurityIconInsecure.Visibility = state == "insecure" ? Visibility.Visible : Visibility.Collapsed;
            SecurityIconLocal.Visibility = state == "local" ? Visibility.Visible : Visibility.Collapsed;
            SecurityIconNeutral.Visibility = state == "none" ? Visibility.Visible : Visibility.Collapsed;
            SecurityIndicator.ToolTip = tooltip;
        }

        private void AddressBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Premier clic : tout sélectionner (comme les autres navigateurs).
            if (!AddressBar.IsKeyboardFocusWithin)
            {
                AddressBar.Focus();
                e.Handled = true;
            }
        }

        private void AddressBar_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            AddressBar.SelectAll();
            ShowOmniboxSuggestions(filter: null);
        }

        private void AddressBar_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            // Le focus part dans la liste de suggestions : on garde le popup.
            if (e.NewFocus is DependencyObject target && CommandList.IsAncestorOf(target))
                return;

            _addressBarEditing = false;
            HideCommandSuggestions();
            UpdateAddressBarFromTab();
        }

        private void AddressBar_TextChanged(object sender, TextChangedEventArgs e)
        {
            // Seule une frappe de l'utilisateur (barre focalisée) active la saisie ;
            // les mises à jour programmatiques de l'URL ne doivent pas ouvrir le popup.
            if (!AddressBar.IsKeyboardFocusWithin)
                return;

            _addressBarEditing = true;
            ShowOmniboxSuggestions(AddressBar.Text);
        }

        private void AddressBar_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    e.Handled = true;

                    if (CommandSuggestionsPopup.IsOpen && CommandList.SelectedItem is OmniboxItem selected)
                        ActivateOmniboxItem(selected);
                    else
                        HandleOmnibox(AddressBar.Text);
                    break;

                case Key.Down:
                case Key.Up:
                    if (!CommandSuggestionsPopup.IsOpen)
                        ShowOmniboxSuggestions(_addressBarEditing ? AddressBar.Text : null);

                    if (CommandList.Items.Count > 0)
                    {
                        int index = CommandList.SelectedIndex + (e.Key == Key.Down ? 1 : -1);
                        CommandList.SelectedIndex = Math.Clamp(index, -1, CommandList.Items.Count - 1);
                        if (CommandList.SelectedItem != null)
                            CommandList.ScrollIntoView(CommandList.SelectedItem);
                    }
                    e.Handled = true;
                    break;

                case Key.Escape:
                    e.Handled = true;

                    if (CommandSuggestionsPopup.IsOpen)
                    {
                        HideCommandSuggestions();
                        return;
                    }

                    // Deuxième Échap : on rend l'URL courante et le focus à la page.
                    _addressBarEditing = false;
                    UpdateAddressBarFromTab();
                    FocusCurrentPage();
                    break;
            }
        }

        private void CommandList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (e.OriginalSource is not DependencyObject source)
                return;

            var container = ItemsControl.ContainerFromElement(CommandList, source) as ListBoxItem;
            if (container?.DataContext is OmniboxItem item)
            {
                e.Handled = true;
                ActivateOmniboxItem(item);
            }
        }

        void HideCommandSuggestions()
        {
            if (CommandSuggestionsPopup != null)
                CommandSuggestionsPopup.IsOpen = false;

            if (CommandList != null)
                CommandList.SelectedIndex = -1;
        }

        void FocusCurrentPage()
        {
            if (Tabs.SelectedItem is TabItem { Tag: WebTabContent { Web: not null } content } && !content.IsCustomView)
                content.Web.Focus();
            else
                Keyboard.ClearFocus();
        }

        void FocusAddressBar()
        {
            AddressBar.Focus();
            AddressBar.SelectAll();
        }

        /// <summary>
        /// Construit les suggestions : commandes (:), onglets (@), favoris (*),
        /// adresse directe, favoris et historique correspondants, puis recherche.
        /// </summary>
        void ShowOmniboxSuggestions(string? filter)
        {
            string query = (filter ?? string.Empty).Trim();
            var items = new List<OmniboxItem>();

            if (query.StartsWith(':'))
            {
                string cmd = query[1..].Trim();
                items.AddRange(GetEnabledCommands()
                    .Where(c => c.Key.StartsWith(cmd, StringComparison.OrdinalIgnoreCase))
                    .Select(c => new OmniboxItem
                    {
                        Type = OmniboxItemType.Command,
                        Primary = ":" + c.Key,
                        Secondary = c.Description,
                        Command = c
                    }));
            }
            else if (query.StartsWith('@'))
            {
                string search = query[1..].Trim();
                foreach (TabItem tab in Tabs.Items)
                {
                    if (tab.Header is not BrowserTabHeader header)
                        continue;

                    if (search.Length > 0 && !header.TabTitle.Contains(search, StringComparison.OrdinalIgnoreCase))
                        continue;

                    items.Add(new OmniboxItem
                    {
                        Type = OmniboxItemType.Tab,
                        Primary = header.TabTitle,
                        Secondary = "Aller à l’onglet",
                        Tab = tab
                    });
                }
            }
            else
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                bool favoritesOnly = query.StartsWith('*');
                string text = favoritesOnly ? query[1..].Trim() : query;

                if (!favoritesOnly && text.Length > 0)
                {
                    string? directUrl = UrlResolver.TryResolveUrl(text);
                    if (directUrl != null && seen.Add(directUrl))
                    {
                        items.Add(new OmniboxItem
                        {
                            Type = OmniboxItemType.Url,
                            Primary = directUrl,
                            Secondary = "Ouvrir l’adresse",
                            Url = directUrl
                        });
                    }
                }

                foreach (FavoriteItem fav in _favorites)
                {
                    if (items.Count >= MaxOmniboxSuggestions)
                        break;

                    bool matches = text.Length == 0
                        ? favoritesOnly
                        : fav.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                          fav.Url.Contains(text, StringComparison.OrdinalIgnoreCase);

                    if (matches && seen.Add(fav.Url))
                    {
                        items.Add(new OmniboxItem
                        {
                            Type = OmniboxItemType.Favorite,
                            Primary = string.IsNullOrWhiteSpace(fav.Title) ? fav.Url : fav.Title,
                            Secondary = fav.Url,
                            Url = fav.Url
                        });
                    }
                }

                if (!favoritesOnly)
                {
                    // L'historique est chronologique : parcours à rebours, sans tri ni copie.
                    for (int i = _history.Count - 1; i >= 0 && items.Count < MaxOmniboxSuggestions; i--)
                    {
                        HistoryEntry h = _history[i];

                        bool matches = text.Length == 0 ||
                                       h.Title.Contains(text, StringComparison.OrdinalIgnoreCase) ||
                                       h.Url.Contains(text, StringComparison.OrdinalIgnoreCase);

                        if (!matches || !seen.Add(h.Url))
                            continue;

                        items.Add(new OmniboxItem
                        {
                            Type = OmniboxItemType.History,
                            Primary = string.IsNullOrWhiteSpace(h.Title) ? h.Url : h.Title,
                            Secondary = h.Url,
                            Url = h.Url
                        });
                    }

                    if (text.Length > 0)
                    {
                        items.Add(new OmniboxItem
                        {
                            Type = OmniboxItemType.Search,
                            Primary = text,
                            Secondary = "Rechercher sur " + UrlResolver.GetSearchEngineName(_settings.Settings.Search),
                            Url = UrlResolver.BuildSearchUrl(text, _settings.Settings.Search)
                        });
                    }
                }
            }

            CommandList.ItemsSource = items;
            CommandList.SelectedIndex = -1;
            CommandSuggestionsPopup.IsOpen = items.Count > 0 && AddressBar.IsKeyboardFocusWithin;
        }

        void ActivateOmniboxItem(OmniboxItem item)
        {
            HideCommandSuggestions();

            switch (item.Type)
            {
                case OmniboxItemType.Command when item.Command != null:
                    _addressBarEditing = false;
                    ExecuteCommand(item.Command.Key);
                    UpdateAddressBarFromTab();
                    break;

                case OmniboxItemType.Tab when item.Tab != null:
                    _addressBarEditing = false;
                    Tabs.SelectedItem = item.Tab;
                    FocusCurrentPage();
                    break;

                default:
                    if (!string.IsNullOrWhiteSpace(item.Url))
                        Navigate(item.Url);
                    break;
            }
        }

        void HandleOmnibox(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return;

            input = input.Trim();

            if (input.StartsWith(':'))
            {
                _addressBarEditing = false;
                ExecuteCommand(input[1..]);
                UpdateAddressBarFromTab();
                return;
            }

            if (input.StartsWith('@'))
            {
                _addressBarEditing = false;
                FocusTab(input[1..]);
                return;
            }

            Navigate(UrlResolver.ResolveOrSearch(input.TrimStart('*').Trim(), _settings.Settings.Search));
        }

        /// <summary>
        /// Navigue dans l'onglet courant. Une page interne (accueil) est remplacée par
        /// un onglet web ; sans onglet sélectionné, un nouvel onglet est ouvert.
        /// </summary>
        void Navigate(string url)
        {
            url = UrlResolver.ResolveOrSearch(url, _settings.Settings.Search);

            _addressBarEditing = false;
            HideCommandSuggestions();

            if (Tabs.SelectedItem is TabItem tab && tab.Tag is WebTabContent state)
            {
                if (state.IsCustomView)
                {
                    ReplaceTabWithWeb(tab, url);
                    return;
                }

                if (state.Web != null && !state.IsLegacyExternal)
                {
                    state.FlashOverlay?.Hide();
                    state.FlashRequired = false;
                    state.FlashChecked = false;
                    state.FlashMode = classes.Flash.FlashMode.None;
                    ApplyLegacyRuleToMode(state);

                    if (state.IsSuspended)
                        ResumeSuspendedTab(tab, state);

                    NavigateWebView(state, url);
                    state.Web.Focus();
                    return;
                }
            }

            CreateTab(url);
        }

        // ---------------------------
        // Commandes (:new, :close…)
        // ---------------------------
        IEnumerable<CommandSetting> GetEnabledCommands()
        {
            if (!_settings.Settings.EnableCommands)
                return Enumerable.Empty<CommandSetting>();

            return _settings.Settings.Commands
                .Where(c => c.Enabled)
                .OrderBy(c => c.Key, StringComparer.OrdinalIgnoreCase);
        }

        bool IsCommandEnabled(string key)
        {
            var settings = _settings.Settings;

            if (!settings.EnableCommands)
                return false;

            var cmd = settings.Commands.FirstOrDefault(c =>
                string.Equals(c.Key, key, StringComparison.OrdinalIgnoreCase));
            return cmd?.Enabled == true;
        }

        void ExecuteCommand(string cmd)
        {
            cmd = cmd.Trim().ToLowerInvariant();

            if (!IsCommandEnabled(cmd))
            {
                ShowToast("Commande inconnue", ":" + cmd, ToastKind.Warning);
                return;
            }

            switch (cmd)
            {
                case "new":
                    NewTab_Click(this, new RoutedEventArgs());
                    break;

                case "close":
                    if (Tabs.SelectedItem is TabItem tab)
                        CloseTab(tab);
                    break;

                case "close others":
                    CloseOtherTabs();
                    break;

                case "reload":
                    ReloadCurrentTab(ignoreCache: false);
                    break;

                case "suspend":
                    if (Tabs.SelectedItem is TabItem y && y.Tag is WebTabContent k && !k.IsCustomView)
                        SuspendTab(y, k);
                    break;

                case "resume":
                    if (Tabs.SelectedItem is TabItem rr && rr.Tag is WebTabContent st && st.IsSuspended)
                        ResumeSuspendedTab(rr, st);
                    break;

                case "history":
                    OpenHistory();
                    break;

                case "suspend inactive":
                    AutoSuspendTabs(force: true);
                    break;
            }
        }

        void CloseOtherTabs()
        {
            if (Tabs.SelectedItem is not TabItem current)
                return;

            // Les onglets épinglés sont conservés, comme dans les autres navigateurs.
            var toClose = Tabs.Items.Cast<TabItem>()
                .Where(t => t != current && !(t.Tag is WebTabContent { IsPinned: true }))
                .ToList();

            foreach (var tab in toClose)
                CloseTab(tab);
        }

        void FocusTab(string query)
        {
            query = query.Trim();
            if (query.Length == 0)
                return;

            foreach (TabItem tab in Tabs.Items)
            {
                if (tab.Header is BrowserTabHeader header &&
                    header.TabTitle.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    Tabs.SelectedItem = tab;
                    return;
                }
            }
        }

        // ---------------------------
        // Boutons de navigation
        // ---------------------------
        private void UpdateNavButtonsFast() => UpdateNavButtons();

        private void UpdateNavButtons()
        {
            if (BackBtn == null || RefreshBtn == null || ForwardBtn == null)
                return;

            var core = Tabs.SelectedItem is TabItem { Tag: WebTabContent { IsCustomView: false } content }
                ? content.Web?.CoreWebView2
                : null;

            BackBtn.IsEnabled = core?.CanGoBack == true;
            ForwardBtn.IsEnabled = core?.CanGoForward == true;
            RefreshBtn.IsEnabled = core != null;

            bool loading = Tabs.SelectedItem is TabItem { Tag: WebTabContent { IsLoading: true } };
            RefreshIcon.Visibility = loading ? Visibility.Collapsed : Visibility.Visible;
            StopIcon.Visibility = loading ? Visibility.Visible : Visibility.Collapsed;
            RefreshBtn.ToolTip = loading ? "Arrêter le chargement (Échap)" : "Actualiser (F5)";
        }

        void ReloadCurrentTab(bool ignoreCache)
        {
            if (Tabs.SelectedItem is not TabItem { Tag: WebTabContent content } tab || content.IsCustomView)
                return;

            if (content.IsSuspended)
            {
                ResumeSuspendedTab(tab, content);
                return;
            }

            var core = content.Web?.CoreWebView2;
            if (core == null)
                return;

            if (ignoreCache)
                _ = core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}");
            else
                core.Reload();
        }

        private void ForwardBtn_Click(object sender, RoutedEventArgs e)
        {
            var core = Tabs.SelectedItem is TabItem { Tag: WebTabContent content } ? content.Web?.CoreWebView2 : null;
            if (core?.CanGoForward == true)
                core.GoForward();

            UpdateNavButtons();
        }
    }
}
