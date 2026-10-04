using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Session;
using PommeBrowser.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>
    /// Fenêtre du navigateur : barre d'onglets, barre de navigation, favoris et contenu des
    /// onglets. Chaque onglet a sa zone dans WebHost ; seule celle de l'onglet actif est visible
    /// (les vues natives des autres onglets restent en place, cachées, pour garder leur page).
    /// </summary>
    public sealed partial class MainWindow : Window
    {
        readonly ObservableCollection<BrowserTab> _tabs = new();
        BrowserTab? _selected;

        public MainWindow() : this(BrowserApp.Current)
        {
        }

        public MainWindow(BrowserApp app)
        {
            App = app;
            InitializeComponent();
            TabStrip.ItemsSource = _tabs;

            InitializeAddressBar();
            InitializeFind();
            InitializeShortcuts();
            InitializeFavoritesBar();
            InitializeDownloads();
            InitializeProfileButton();
            InitializeAdBlock();
            InitializeSuspension();

            ActualThemeVariantChanged += (_, _) => ApplyPrivateLook();
            // Le clavier des vues natives suit le focus d'Avalonia (voir IEngineTab.SyncKeyboard).
            AddHandler(GotFocusEvent, (_, e) =>
            {
                // Vue côte à côte : un clic dans la page de l'autre volet en fait l'onglet actif.
                if (IsSplitViewActive && _splitPartner is { } partner && e.Source is Visual source &&
                    (source == partner.Content || partner.Content.IsVisualAncestorOf(source)))
                    SelectTab(partner);
                Dispatcher.UIThread.Post(() => SyncKeyboard(force: false));
            }, handledEventsToo: true);
            Activated += (_, _) => SyncKeyboard(force: true);
            Closing += OnClosing;
            Closed += (_, _) => OnClosed();
            UpdateChrome();
        }

        public BrowserApp App { get; }

        void SyncKeyboard(bool force)
        {
            foreach (BrowserTab tab in _tabs)
            {
                tab.Engine?.SyncKeyboard(force && tab.Content.IsEffectivelyVisible);
                tab.SyncLegacyKeyboard(force && tab.Content.IsEffectivelyVisible);
            }
        }

        /// <summary>Après un raccourci tapé dans Basilisk : le clavier suit le nouveau focus.</summary>
        public void SyncAllKeyboards() => SyncKeyboard(force: false);

        public IReadOnlyList<BrowserTab> Tabs => _tabs;

        public BrowserTab? SelectedTab => _selected;

        // ---------------------------------------------------------------
        // Onglets
        // ---------------------------------------------------------------

        /// <summary>Nouvel onglet : adresse donnée, ou page d'accueil.</summary>
        public BrowserTab NewTab(string? url, bool select, bool isPrivate = false, int? index = null, bool httpsFallback = false)
        {
            var tab = new BrowserTab(this, isPrivate);
            AddTab(tab, index ?? _tabs.Count);
            MyHomelabBrowser.classes.RuntimeLogBuffer.Append($"[Onglet] Nouvel onglet ({_tabs.Count} ouverts).");
            if (url != null)
                tab.Navigate(url, httpsFallback);
            else
                tab.ShowHome();
            if (select || _selected == null)
                SelectTab(tab);
            return tab;
        }

        /// <summary>Onglet de la session précédente : chargé seulement à sa première sélection.</summary>
        public BrowserTab RestoreTab(TabState state, bool select)
        {
            var tab = new BrowserTab(this, isPrivate: false) { IsPinned = state.IsPinned };
            AddTab(tab, _tabs.Count);
            string url = state.IsLegacy && !string.IsNullOrWhiteSpace(state.LegacyUrl) ? state.LegacyUrl : state.Url;
            tab.SetPending(url, state.Title);
            if (select || _selected == null)
                SelectTab(tab);
            return tab;
        }

        /// <summary>Lien ouvert depuis une page : juste après l'onglet d'origine (même mode privé).</summary>
        public BrowserTab OpenTab(string url, bool background, BrowserTab? opener = null)
        {
            int index = opener != null ? _tabs.IndexOf(opener) + 1 : _tabs.Count;
            // Les onglets ouverts à la suite depuis une même page gardent leur ordre.
            while (opener != null && index < _tabs.Count && _tabs[index].Opener == opener)
                index++;
            BrowserTab tab = NewTab(url, select: !background, isPrivate: opener?.IsPrivate ?? false, index: index);
            tab.Opener = opener;
            return tab;
        }

        void AddTab(BrowserTab tab, int index)
        {
            // Les onglets épinglés restent en tête.
            int pinned = _tabs.Count(t => t.IsPinned);
            if (!tab.IsPinned)
                index = Math.Max(index, pinned);
            _tabs.Insert(Math.Clamp(index, 0, _tabs.Count), tab);
            tab.Content.IsVisible = false;
            Grid.SetRow(tab.Content, 1);
            WebHost.Children.Add(tab.Content);
            tab.Changed += OnTabChanged;
        }

        public void SelectTab(BrowserTab tab)
        {
            if (_selected == tab)
                return;

            if (_selected != null)
            {
                _selected.IsSelected = false;
                // Inactif à partir de maintenant (mise en veille) : pas depuis qu'il a été choisi.
                _selected.LastActivated = DateTime.Now;
                CloseFind();
            }

            // Vue côte à côte : choisir l'onglet de l'autre volet échange les rôles, sans déplacer les volets.
            if (IsSplitViewActive && tab == _splitPartner)
            {
                _splitPartner = _selected;
                _splitActiveIsLeft = !_splitActiveIsLeft;
            }

            _selected = tab;
            tab.IsSelected = true;
            tab.LastActivated = DateTime.Now;
            LayoutContents();
            tab.LoadPendingIfNeeded();
            HideSuggestions();
            LinkStatusPopup.IsOpen = false;
            ApplyPrivateLook();
            UpdateChrome();

            if (tab.Page == TabPage.Home)
                FocusAddressBar();
            else
                tab.FocusPage();
        }

        public void CloseTab(BrowserTab tab)
        {
            int index = _tabs.IndexOf(tab);
            if (index < 0)
                return;

            App.RememberClosedTab(new ClosedTab(tab.WebUrl, tab.Title, tab.IsPrivate, this, index));

            if (tab == _selected)
            {
                // Retour à l'onglet d'origine s'il existe encore, sinon l'onglet voisin.
                BrowserTab? next = tab.Opener != null && _tabs.Contains(tab.Opener)
                    ? tab.Opener
                    : index + 1 < _tabs.Count ? _tabs[index + 1] : index > 0 ? _tabs[index - 1] : null;
                if (next != null)
                    SelectTab(next);
                else
                    _selected = null;
            }

            if (tab == _splitPartner)
                _splitPartner = null;
            _tabs.Remove(tab);
            tab.Changed -= OnTabChanged;
            WebHost.Children.Remove(tab.Content);
            LayoutContents();
            tab.Close();
            foreach (BrowserTab other in _tabs.Where(t => t.Opener == tab))
                other.Opener = null;

            if (_tabs.Count == 0)
                Close();
            else
                UpdateChrome();
        }

        public void CloseOtherTabs(BrowserTab keep)
        {
            foreach (BrowserTab tab in _tabs.Where(t => t != keep && !t.IsPinned).ToList())
                CloseTab(tab);
        }

        public void ReopenClosedTab()
        {
            if (App.TakeClosedTab(this) is not { } closed)
                return;
            BrowserTab tab = NewTab(closed.Url, select: true, index: Math.Min(closed.Index, _tabs.Count));
            _ = tab;
        }

        public void TogglePin(BrowserTab tab)
        {
            tab.IsPinned = !tab.IsPinned;
            // Épinglé : en tête ; désépinglé : juste après les onglets épinglés.
            _tabs.Remove(tab);
            int pinned = _tabs.Count(t => t.IsPinned);
            _tabs.Insert(tab.IsPinned ? pinned : Math.Min(pinned, _tabs.Count), tab);
            TabStrip.InvalidateMeasure();
        }

        public void SelectRelative(int delta)
        {
            if (_selected == null || _tabs.Count < 2)
                return;
            int index = (_tabs.IndexOf(_selected) + delta + _tabs.Count) % _tabs.Count;
            SelectTab(_tabs[index]);
        }

        void OnTabChanged(BrowserTab tab)
        {
            if (tab == _selected)
                UpdateChrome();
            if (IsSplitViewActive && (tab == _selected || tab == _splitPartner))
                UpdateSplitHeaders();
        }

        /// <summary>Barre de navigation à jour pour l'onglet actif.</summary>
        void UpdateChrome()
        {
            BrowserTab? tab = _selected;
            BackButton.IsEnabled = tab?.CanGoBack ?? false;
            ForwardButton.IsEnabled = tab?.CanGoForward ?? false;
            RefreshIcon.IsVisible = !(tab?.IsLoading ?? false);
            StopIcon.IsVisible = tab?.IsLoading ?? false;
            ToolTip.SetTip(RefreshButton, tab?.IsLoading == true ? Tr("Arrêter (Échap)") : Tr("Actualiser (F5)"));

            LoadProgress.IsVisible = tab?.IsLoading ?? false;
            LoadProgress.Value = Math.Max(0.08, tab?.Progress ?? 0);

            if (!AddressBar.IsFocused || tab?.Page != TabPage.Web)
                ShowAddress(tab);

            UpdateSecurityIcon(tab);
            UpdateFavoriteButton(tab);
            UpdateZoomButton(tab);
            UpdateCredentialButton(tab);
            UpdateLegacyButton(tab);
            UpdateAdBlockButton();
            Title = tab == null ? "PommeBrowser" : tab.Title + " — PommeBrowser";
        }

        /// <summary>La barre devient violette quand l'onglet actif est privé.</summary>
        void ApplyPrivateLook()
        {
            bool isPrivate = _selected?.IsPrivate ?? false;
            PrivatePill.IsVisible = isPrivate;
            if (isPrivate)
            {
                Resources["SelectedTabBrush"] = FindThemeBrush("PrivateToolbarBrush");
                Resources["AddressFieldBrush"] = FindThemeBrush("PrivateAddressBrush");
            }
            else
            {
                Resources.Remove("SelectedTabBrush");
                Resources.Remove("AddressFieldBrush");
            }
        }

        IBrush FindThemeBrush(string key)
            => Application.Current!.TryGetResource(key, ActualThemeVariant, out object? value) && value is IBrush brush ? brush : Brushes.Transparent;

        // ---------------------------------------------------------------
        // Appels des onglets
        // ---------------------------------------------------------------

        public void ShowLinkStatus(BrowserTab tab, string? link)
        {
            if (tab != _selected)
                return;
            LinkStatusText.Text = link == null ? string.Empty : UrlDisplay.ForDisplay(link);
            LinkStatusPopup.IsOpen = !string.IsNullOrEmpty(link) && IsActive;
        }

        bool _webFullscreen;
        WindowState _stateBeforeFullscreen;

        /// <summary>Vidéo ou jeu en plein écran : la barre du navigateur disparaît.</summary>
        public void SetWebFullscreen(BrowserTab tab, bool full)
        {
            if (tab != _selected || full == _webFullscreen)
                return;
            _webFullscreen = full;
            SetFullscreen(full);
        }

        public void ToggleFullscreen() => SetFullscreen(WindowState != WindowState.FullScreen);

        void SetFullscreen(bool full)
        {
            if (full)
            {
                if (WindowState != WindowState.FullScreen)
                    _stateBeforeFullscreen = WindowState;
                WindowState = WindowState.FullScreen;
            }
            else if (WindowState == WindowState.FullScreen)
            {
                WindowState = _stateBeforeFullscreen == WindowState.FullScreen ? WindowState.Normal : _stateBeforeFullscreen;
            }
            BrowserChrome.IsVisible = !full;
        }

        /// <summary>Nouvelle page affichée dans l'onglet.</summary>
        public void OnTabCommitted(BrowserTab tab)
        {
            if (tab == _selected)
                CloseFind();
        }

        // ---------------------------------------------------------------
        // Boutons de la barre
        // ---------------------------------------------------------------

        void NewTab_Click(object? sender, RoutedEventArgs e) => NewTab(App.NewTabUrl, select: true);

        void NewPrivateTab_Click(object? sender, RoutedEventArgs e) => NewTab(null, select: true, isPrivate: true);

        void Back_Click(object? sender, RoutedEventArgs e) => _selected?.GoBack();

        void Forward_Click(object? sender, RoutedEventArgs e) => _selected?.GoForward();

        void Refresh_Click(object? sender, RoutedEventArgs e)
        {
            if (_selected?.IsLoading == true)
                _selected.Stop();
            else
                _selected?.Reload();
        }

        void TabClose_Click(object? sender, RoutedEventArgs e)
        {
            if ((sender as Control)?.DataContext is BrowserTab tab)
                CloseTab(tab);
            e.Handled = true;
        }

        void Tab_PointerPressed(object? sender, PointerPressedEventArgs e)
        {
            if ((sender as Control)?.DataContext is not BrowserTab tab)
                return;
            PointerPointProperties point = e.GetCurrentPoint(this).Properties;
            if (point.IsLeftButtonPressed)
                SelectTab(tab);
        }

        void Tab_PointerReleased(object? sender, PointerReleasedEventArgs e)
        {
            // Clic du milieu : fermeture de l'onglet.
            if (e.InitialPressMouseButton == MouseButton.Middle && (sender as Control)?.DataContext is BrowserTab tab)
                CloseTab(tab);
        }

        void OnClosing(object? sender, WindowClosingEventArgs e)
        {
            // Dernière fenêtre : la session est enregistrée avant la fermeture des onglets.
            if (App.Windows.Count == 1 && !App.IsQuitting)
                App.SaveSession();
        }

        void OnClosed()
        {
            foreach (BrowserTab tab in _tabs.ToList())
            {
                tab.Changed -= OnTabChanged;
                tab.Close();
            }
            _tabs.Clear();
            DisposeDownloads();
        }
    }
}
