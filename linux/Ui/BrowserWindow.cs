using System;
using System.Collections.Generic;
using System.Linq;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.AdBlock.Models;
using MyHomelabBrowser.classes.Homelab;
using MyHomelabBrowser.classes.Profiles.Credentials;
using PommeBrowser.Linux.Core;
using PommeBrowser.Linux.Web;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Ui
{
    /// <summary>
    /// Fenêtre du navigateur : barre d'en-tête (navigation, adresse, favoris, bloqueur, services,
    /// téléchargements, menu), onglets et barre de recherche dans la page.
    /// </summary>
    sealed class BrowserWindow
    {
        static bool _acceleratorsRegistered;

        readonly BrowserApplication _app;
        readonly Adw.TabView _tabs;
        readonly Adw.ToolbarView _toolbar;
        readonly Adw.ToastOverlay _toasts;
        readonly Omnibox _omnibox;
        readonly Gtk.Button _back;
        readonly Gtk.Button _forward;
        readonly Gtk.Button _reload;
        readonly Gtk.Button _star;
        readonly Gtk.Button _fillCredentials;
        readonly Gtk.MenuButton _adblockButton;
        readonly Gtk.MenuButton _downloadsButton;
        readonly Gtk.MenuButton _favoritesButton;
        readonly Gtk.Label _zoomLabel;
        readonly Gtk.SearchBar _findBar;
        readonly Gtk.SearchEntry _findEntry;
        readonly Gtk.Label _findStatus;
        readonly Dictionary<Gtk.Widget, BrowserTab> _tabsByWidget = new();
        readonly HashSet<BrowserTab> _closing = new();
        readonly Stack<SessionTab> _closedTabs = new();
        readonly Dictionary<Adw.TabPage, string?> _pageIcons = new();
        BrowserTab? _menuTab;

        /// <summary>Tous les onglets, toutes fenêtres confondues (pour les transferts entre fenêtres).</summary>
        static readonly Dictionary<Gtk.Widget, BrowserTab> AllTabs = new();

        public BrowserWindow(BrowserApplication app, bool isPrivate)
        {
            _app = app;
            IsPrivate = isPrivate;
            Session = isPrivate ? app.Engine.CreatePrivateSession() : app.Engine.Session;

            Window = Adw.ApplicationWindow.New(app.App);
            Window.SetDefaultSize(1280, 820);
            Window.SetTitle(isPrivate ? Tr("Navigation privée — PommeBrowser") : "PommeBrowser");
            if (isPrivate)
                Window.AddCssClass("private");

            // --- Onglets ---
            _tabs = Adw.TabView.New();
            _tabs.OnPageAttached += (_, args) => OnPageAttached(args.Page);
            _tabs.OnPageDetached += (_, args) => OnPageDetached(args.Page);
            _tabs.OnClosePage += (_, args) =>
            {
                if (TabFor(args.Page) is { } tab)
                {
                    _closing.Add(tab);
                    if (tab.GetSessionTab() is { } closed && !IsPrivate)
                        _closedTabs.Push(closed);
                }
                // Les onglets épinglés se ferment aussi (Ctrl+W, menu de l'onglet).
                _tabs.ClosePageFinish(args.Page, true);
                return true;
            };
            _tabs.OnNotify += (_, args) =>
            {
                switch (args.Pspec.GetName())
                {
                    case "selected-page":
                        OnSelectionChanged();
                        break;
                    case "is-transferring-page" when !_tabs.GetIsTransferringPage() && _tabs.GetNPages() == 0:
                        // Dernier onglet glissé vers une autre fenêtre.
                        Window.Close();
                        break;
                }
            };
            _tabs.OnCreateWindow += (_, _) =>
            {
                BrowserWindow other = _app.CreateWindow(IsPrivate);
                other.Window.Present();
                return other._tabs;
            };
            // Page nulle : menu refermé. L'onglet visé est gardé, l'action peut arriver juste après.
            _tabs.OnSetupMenu += (_, args) =>
            {
                if (args.Page != null)
                    _menuTab = TabFor(args.Page);
            };
            _tabs.SetMenuModel(BuildTabMenu());
            _tabs.SetDefaultIcon(Gio.ThemedIcon.New("web-browser-symbolic"));

            var tabBar = Adw.TabBar.New();
            tabBar.SetView(_tabs);
            tabBar.SetAutohide(false);
            var newTabButton = Gtk.Button.NewFromIconName("tab-new-symbolic");
            newTabButton.SetTooltipText(Tr("Nouvel onglet (Ctrl+T)"));
            newTabButton.SetActionName("win.new-tab");
            newTabButton.AddCssClass("flat");
            tabBar.SetEndActionWidget(newTabButton);

            // --- Barre d'en-tête ---
            _back = IconButton("go-previous-symbolic", Tr("Page précédente (Alt+←)"), "win.back");
            _forward = IconButton("go-next-symbolic", Tr("Page suivante (Alt+→)"), "win.forward");
            _reload = IconButton("view-refresh-symbolic", Tr("Recharger (F5)"), null);
            _reload.OnClicked += (_, _) =>
            {
                if (Current?.IsLoading == true)
                    Current.Stop();
                else
                    Current?.Reload();
            };
            Gtk.Button home = IconButton("go-home-symbolic", Tr("Accueil"), "win.home");

            _omnibox = new Omnibox(app);
            _omnibox.Navigate += url => NavigateCurrent(url);
            _omnibox.SecurityIconClicked += ShowSecurityInfo;

            _star = IconButton("non-starred-symbolic", Tr("Ajouter aux favoris (Ctrl+D)"), "win.bookmark");
            _fillCredentials = IconButton("dialog-password-symbolic", Tr("Remplir les identifiants"), "win.fill-credentials");
            _fillCredentials.SetVisible(false);

            var addressBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 4);
            addressBox.Append(_omnibox.Widget);
            addressBox.Append(_fillCredentials);
            addressBox.Append(_star);
            var clamp = Adw.Clamp.New();
            clamp.SetMaximumSize(900);
            clamp.SetTighteningThreshold(600);
            clamp.SetChild(addressBox);
            clamp.SetHexpand(true);

            var header = Adw.HeaderBar.New();
            header.PackStart(_back);
            header.PackStart(_forward);
            header.PackStart(_reload);
            header.PackStart(home);
            if (isPrivate)
            {
                var badge = Gtk.Label.New(Tr("Privé"));
                badge.AddCssClass("private-badge");
                badge.SetTooltipText(Tr("Navigation privée : ni historique, ni cookies, ni cache conservés à la fermeture de la fenêtre."));
                header.PackStart(badge);
            }
            header.SetTitleWidget(clamp);

            _zoomLabel = Gtk.Label.New("100 %");
            var menuButton = Gtk.MenuButton.New();
            menuButton.SetIconName("open-menu-symbolic");
            menuButton.SetTooltipText(Tr("Menu principal"));
            menuButton.SetPrimary(true);
            menuButton.SetPopover(BuildMainMenu());
            header.PackEnd(menuButton);
            if (!isPrivate)
                header.PackEnd(ProfileMenu.CreateButton(app, this));

            _downloadsButton = Gtk.MenuButton.New();
            _downloadsButton.SetIconName("folder-download-symbolic");
            _downloadsButton.SetTooltipText(Tr("Téléchargements"));
            var downloadsPopover = Gtk.Popover.New();
            downloadsPopover.OnShow += (_, _) => downloadsPopover.SetChild(Panels.Downloads(_app, this));
            _downloadsButton.SetPopover(downloadsPopover);
            _downloadsButton.SetVisible(false);
            header.PackEnd(_downloadsButton);

            var servicesButton = Gtk.MenuButton.New();
            servicesButton.SetIconName("network-server-symbolic");
            servicesButton.SetTooltipText(Tr("Services du homelab"));
            var servicesPopover = Gtk.Popover.New();
            servicesPopover.OnShow += (_, _) => servicesPopover.SetChild(Panels.Services(_app, this, servicesPopover));
            servicesButton.SetPopover(servicesPopover);
            header.PackEnd(servicesButton);

            _adblockButton = Gtk.MenuButton.New();
            _adblockButton.SetIconName("security-high-symbolic");
            _adblockButton.SetTooltipText(Tr("Bloqueur de publicités"));
            var adblockPopover = Gtk.Popover.New();
            adblockPopover.OnShow += (_, _) => adblockPopover.SetChild(Panels.AdBlock(_app, this, adblockPopover));
            _adblockButton.SetPopover(adblockPopover);
            header.PackEnd(_adblockButton);

            _favoritesButton = Gtk.MenuButton.New();
            _favoritesButton.SetIconName("user-bookmarks-symbolic");
            _favoritesButton.SetTooltipText(Tr("Favoris"));
            header.PackEnd(_favoritesButton);
            RebuildFavoritesMenu();

            // --- Recherche dans la page ---
            _findEntry = Gtk.SearchEntry.New();
            _findEntry.SetHexpand(true);
            _findEntry.SetPlaceholderText(Tr("Rechercher dans la page"));
            _findEntry.OnSearchChanged += (_, _) => Find();
            _findEntry.OnActivate += (_, _) => FindNext(1);
            _findEntry.OnNextMatch += (_, _) => FindNext(1);
            _findEntry.OnPreviousMatch += (_, _) => FindNext(-1);
            _findEntry.OnStopSearch += (_, _) => CloseFind();
            _findStatus = Gtk.Label.New(null);
            _findStatus.AddCssClass("dim-label");
            var findPrevious = IconButton("go-up-symbolic", Tr("Précédent (Maj+Entrée)"), "win.find-previous");
            var findNext = IconButton("go-down-symbolic", Tr("Suivant (Entrée)"), "win.find-next");
            var findBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            findBox.Append(_findEntry);
            findBox.Append(_findStatus);
            findBox.Append(findPrevious);
            findBox.Append(findNext);
            var findClamp = Adw.Clamp.New();
            findClamp.SetMaximumSize(640);
            findClamp.SetChild(findBox);
            _findBar = Gtk.SearchBar.New();
            _findBar.SetChild(findClamp);
            _findBar.ConnectEntry(_findEntry);
            _findBar.SetShowCloseButton(true);
            _findBar.OnNotify += (_, args) =>
            {
                if (args.Pspec.GetName() == "search-mode-enabled" && !_findBar.GetSearchMode())
                    Current?.Web.GetFindController().SearchFinish();
            };

            // --- Assemblage ---
            _toasts = Adw.ToastOverlay.New();
            _toasts.SetChild(_tabs);

            _toolbar = Adw.ToolbarView.New();
            _toolbar.AddTopBar(header);
            _toolbar.AddTopBar(tabBar);
            _toolbar.AddBottomBar(_findBar);
            _toolbar.SetContent(_toasts);
            _toolbar.SetTopBarStyle(Adw.ToolbarStyle.Raised);
            Window.SetContent(_toolbar);

            AddActions();
            RegisterAccelerators(app.App);

            // Mises à jour venues du reste de l'application.
            Action favoritesChanged = () => { RebuildFavoritesMenu(); UpdateChrome(); };
            Action adblockChanged = () => { foreach (BrowserTab tab in Tabs) tab.RefreshFilter(); UpdateAdBlockButton(); };
            Action downloadsChanged = UpdateDownloadsButton;
            Action vaultChanged = UpdateChrome;
            app.Vault.Changed += vaultChanged;
            app.Favorites.Changed += favoritesChanged;
            app.Engine.AdBlocker.Changed += adblockChanged;
            app.Engine.Downloads.Changed += downloadsChanged;
            Window.OnDestroy += (_, _) =>
            {
                // Onglets encore ouverts à la fermeture de la fenêtre : plus aucune référence.
                foreach (Gtk.Widget widget in _tabsByWidget.Keys)
                    AllTabs.Remove(widget);
                _tabsByWidget.Clear();
                app.Vault.Changed -= vaultChanged;
                app.Favorites.Changed -= favoritesChanged;
                app.Engine.AdBlocker.Changed -= adblockChanged;
                app.Engine.Downloads.Changed -= downloadsChanged;
            };

            UpdateDownloadsButton();
            UpdateAdBlockButton();
        }

        public Adw.ApplicationWindow Window { get; }
        public bool IsPrivate { get; }
        public WebKit.NetworkSession Session { get; }

        /// <summary>Sites qui ont refusé HTTPS pendant cette session (évite les boucles http ↔ https).</summary>
        public HashSet<string> HttpOnlyHosts { get; } = new(StringComparer.OrdinalIgnoreCase);

        public int TabCount => _tabs.GetNPages();

        public int SelectedIndex => _tabs.GetSelectedPage() is { } page ? _tabs.GetPagePosition(page) : 0;

        public BrowserTab? Current => _tabs.GetSelectedPage() is { } page ? TabFor(page) : null;

        IEnumerable<BrowserTab> Tabs
        {
            get
            {
                for (int i = 0; i < _tabs.GetNPages(); i++)
                {
                    if (TabFor(_tabs.GetNthPage(i)) is { } tab)
                        yield return tab;
                }
            }
        }

        BrowserTab? TabFor(Adw.TabPage page)
            => page.GetChild() is { } child && _tabsByWidget.TryGetValue(child, out BrowserTab? tab) ? tab : null;

        // ---------------------------------------------------------------
        // Onglets
        // ---------------------------------------------------------------

        BrowserTab AddTab(BrowserTab tab, bool select, BrowserTab? after = null)
        {
            AllTabs[tab.Widget] = tab;
            Adw.TabPage page = after?.Page is { } parent
                ? _tabs.AddPage(tab.Widget, parent)
                : _tabs.Append(tab.Widget);
            tab.Page = page;
            if (select)
                _tabs.SetSelectedPage(page);
            return tab;
        }

        public void OpenHomeTab()
        {
            var tab = new BrowserTab(_app, this);
            tab.ShowHome();
            AddTab(tab, select: true);
            _omnibox.Focus();
        }

        public BrowserTab OpenInNewTab(string url, bool background, BrowserTab? opener = null)
        {
            var tab = new BrowserTab(_app, this);
            AddTab(tab, select: !background, after: opener);
            tab.Navigate(url);
            return tab;
        }

        /// <summary>Page ouverte par une autre (window.open, target=_blank) : même processus, même session.</summary>
        public BrowserTab CreateRelatedTab(BrowserTab opener, string? url)
        {
            var tab = new BrowserTab(_app, this, opener.Web);
            AddTab(tab, select: true, after: opener);
            return tab;
        }

        public void CloseTab(BrowserTab tab)
        {
            if (tab.Page != null && _tabsByWidget.ContainsKey(tab.Widget))
                _tabs.ClosePage(tab.Page);
        }

        public void RestoreSession(SessionState session)
        {
            foreach (SessionTab saved in session.Tabs)
            {
                var tab = new BrowserTab(_app, this);
                AddTab(tab, select: false);
                tab.SetPending(saved.Url, saved.Title);
            }

            if (_tabs.GetNPages() > 0)
            {
                _tabs.SetSelectedPage(_tabs.GetNthPage(Math.Clamp(session.Selected, 0, _tabs.GetNPages() - 1)));
                OnSelectionChanged();
            }
        }

        public IEnumerable<SessionTab> GetSessionTabs()
            => Tabs.Select(t => t.GetSessionTab()).Where(t => t != null).Select(t => t!);

        void OnPageAttached(Adw.TabPage page)
        {
            if (page.GetChild() is not { } child || !AllTabs.TryGetValue(child, out BrowserTab? tab))
                return;

            _tabsByWidget[child] = tab;
            tab.Window = this;
            tab.Page = page;
            tab.Changed += OnTabChanged;
            OnTabChanged(tab);
        }

        void OnPageDetached(Adw.TabPage page)
        {
            if (page.GetChild() is not { } child || !_tabsByWidget.Remove(child, out BrowserTab? tab))
                return;

            tab.Changed -= OnTabChanged;
            _pageIcons.Remove(page);
            if (_closing.Remove(tab))
            {
                AllTabs.Remove(child);
                tab.Page = null;
                tab.OnClosed();
            }

            // Dernier onglet fermé : la fenêtre se ferme aussi (après un transfert, voir is-transferring-page).
            if (_tabs.GetNPages() == 0 && !_tabs.GetIsTransferringPage())
                Window.Close();
        }

        void OnTabChanged(BrowserTab tab)
        {
            if (tab.Page is { } page)
            {
                page.SetTitle(tab.Title);
                page.SetTooltip(tab.Uri.Length > 0 ? tab.Title + "\n" + tab.Uri : tab.Title);
                page.SetLoading(tab.IsLoading);

                string? themed = tab.Content == TabContent.Web ? null : ContentIcon(tab.Content);
                if (themed != _pageIcons.GetValueOrDefault(page) || (themed == null && !ReferenceEquals(page.GetIcon(), tab.Favicon)))
                {
                    _pageIcons[page] = themed;
                    page.SetIcon(themed != null ? Gio.ThemedIcon.New(themed) : tab.Favicon);
                }
            }

            if (tab == Current)
                UpdateChrome();
        }

        static string ContentIcon(TabContent content) => content switch
        {
            TabContent.History => "document-open-recent-symbolic",
            TabContent.Favorites => "user-bookmarks-symbolic",
            TabContent.Services => "network-server-symbolic",
            TabContent.Passwords => "dialog-password-symbolic",
            TabContent.Error => "dialog-warning-symbolic",
            _ => "go-home-symbolic"
        };

        void OnSelectionChanged()
        {
            BrowserTab? tab = Current;
            if (tab == null)
                return;

            tab.LoadPendingIfNeeded();
            if (_findBar.GetSearchMode())
                CloseFind();
            UpdateChrome();
            if (tab.Content == TabContent.Home)
                _omnibox.Focus();
        }

        void UpdateChrome()
        {
            BrowserTab? tab = Current;
            if (tab == null)
                return;

            _omnibox.ShowUrl(tab.Uri);
            _omnibox.SetProgress(tab.Progress);
            _omnibox.SetSecurity(tab.Security);
            _back.SetSensitive(tab.CanGoBack);
            _forward.SetSensitive(tab.CanGoForward);
            _reload.SetIconName(tab.IsLoading ? "process-stop-symbolic" : "view-refresh-symbolic");
            _reload.SetTooltipText(tab.IsLoading ? Tr("Arrêter") : Tr("Recharger (F5)"));

            bool isFavorite = tab.Uri.Length > 0 && _app.Favorites.Find(tab.Uri) != null;
            _star.SetIconName(isFavorite ? "starred-symbolic" : "non-starred-symbolic");
            _star.SetTooltipText(isFavorite ? Tr("Modifier le favori (Ctrl+D)") : Tr("Ajouter aux favoris (Ctrl+D)"));
            _star.SetSensitive(tab.Uri.Length > 0);
            UpdateFillCredentialsButton(tab);

            _zoomLabel.SetLabel(SiteZoomStore.Format(tab.Web.GetZoomLevel()));
            Window.SetTitle(IsPrivate ? Tr("{0} — Navigation privée", tab.Title) : tab.Title + " — PommeBrowser");
            UpdateAdBlockButton();
        }

        /// <summary>Clé de la barre d'adresse : coffre verrouillé, ou identifiant enregistré pour ce site.</summary>
        void UpdateFillCredentialsButton(BrowserTab tab)
        {
            Vault vault = _app.Vault;
            bool available = tab.Content == TabContent.Web &&
                             vault.Service.VaultExists &&
                             CredentialOrigin.TryCreateTrusted(tab.Uri, out string origin) &&
                             (!vault.IsUnlocked || vault.Service.FindForOrigin(origin) != null);
            _fillCredentials.SetVisible(available);
            _fillCredentials.SetTooltipText(vault.IsUnlocked
                ? Tr("Remplir les identifiants")
                : Tr("Déverrouiller le coffre pour remplir les identifiants"));
        }

        void UpdateAdBlockButton()
        {
            BrowserTab? tab = Current;
            bool active = _app.Engine.AdBlocker.ShouldFilter(tab?.Uri is { Length: > 0 } uri ? uri : null);
            _adblockButton.SetIconName(active ? "security-high-symbolic" : "security-low-symbolic");
        }

        void UpdateDownloadsButton()
        {
            int total = _app.Engine.Downloads.Entries.Count;
            int active = _app.Engine.Downloads.ActiveCount;
            _downloadsButton.SetVisible(total > 0);
            _downloadsButton.SetTooltipText(active > 0 ? Tr("Téléchargements ({0} en cours)", active) : Tr("Téléchargements"));
        }

        void NavigateCurrent(string url)
        {
            BrowserTab tab = Current ?? OpenInNewTab(url, background: false);
            tab.Navigate(url);
            tab.Web.GrabFocus();
        }

        /// <summary>Page de PommeBrowser : réutilise l'onglet qui l'affiche déjà, ou un nouvel onglet vide.</summary>
        public void OpenPage(TabContent content)
        {
            BrowserTab? existing = Tabs.FirstOrDefault(t => t.Content == content);
            if (existing?.Page != null)
            {
                _tabs.SetSelectedPage(existing.Page);
                return;
            }

            BrowserTab tab = Current is { Content: TabContent.Home } home ? home : AddTab(new BrowserTab(_app, this), select: true, after: Current);
            Gtk.Widget page = content switch
            {
                TabContent.History => new HistoryView(_app, this).Widget,
                TabContent.Favorites => new FavoritesView(_app, this).Widget,
                TabContent.Services => new ServicesView(_app, this).Widget,
                TabContent.Passwords => new PasswordsView(_app, this).Widget,
                _ => new HomeView(_app, this).Widget
            };
            tab.ShowPage(content, page);
        }

        /// <summary>Ouvre une adresse depuis une page de PommeBrowser (clic : cet onglet, clic du milieu : nouvel onglet).</summary>
        public void OpenFromPage(string url, bool newTab = false)
        {
            if (newTab || Current == null)
                OpenInNewTab(url, background: newTab && Current != null, opener: Current);
            else
                NavigateCurrent(url);
        }

        // ---------------------------------------------------------------
        // Notifications
        // ---------------------------------------------------------------

        public void ShowToast(string text, string? buttonLabel = null, Action? action = null)
        {
            var toast = Adw.Toast.New(text);
            toast.SetTimeout(4);
            if (buttonLabel != null && action != null)
            {
                toast.SetButtonLabel(buttonLabel);
                toast.OnButtonClicked += (_, _) => action();
            }
            _toasts.AddToast(toast);
        }

        public void ShowDownloadFinished(DownloadEntry entry)
            => ShowToast(Tr("Téléchargement terminé : {0}", entry.FileName), Tr("Ouvrir"), () => Panels.OpenFile(Window, entry.Destination));

        public void SetWebFullscreen(bool fullscreen)
        {
            _toolbar.SetRevealTopBars(!fullscreen);
            if (fullscreen)
                Window.Fullscreen();
            else
                Window.Unfullscreen();
        }

        public void OnSettingsChanged()
        {
            foreach (BrowserTab tab in Tabs)
                tab.OnSettingsChanged();
            UpdateChrome();
        }

        void ShowSecurityInfo()
        {
            BrowserTab? tab = Current;
            if (tab == null || !Uri.TryCreate(tab.Uri, UriKind.Absolute, out Uri? uri))
                return;

            string text = tab.Security switch
            {
                SecurityLevel.Secure => Tr("La connexion à {0} est chiffrée et son certificat est valide.", uri.Host),
                SecurityLevel.Trusted => Tr("La connexion à {0} est chiffrée avec un certificat que vous avez approuvé vous-même.", uri.Host),
                SecurityLevel.Mixed => Tr("La page {0} est chiffrée, mais certains éléments sont chargés sans chiffrement.", uri.Host),
                SecurityLevel.Local => Tr("{0} est sur votre réseau local. La connexion n'est pas chiffrée.", uri.Host),
                _ => Tr("La connexion à {0} n'est pas chiffrée : évitez d'y saisir des mots de passe.", uri.Host)
            };
            ShowToast(text);
        }

        // ---------------------------------------------------------------
        // Recherche dans la page
        // ---------------------------------------------------------------

        void OpenFind()
        {
            if (Current is not { Content: TabContent.Web })
                return;
            _findBar.SetSearchMode(true);
            _findEntry.GrabFocus();
        }

        void CloseFind()
        {
            _findBar.SetSearchMode(false);
            _findStatus.SetLabel(string.Empty);
            Current?.Web.GetFindController().SearchFinish();
        }

        void Find()
        {
            if (Current is not { } tab)
                return;

            WebKit.FindController finder = tab.Web.GetFindController();
            string text = _findEntry.GetText();
            if (text.Length == 0)
            {
                finder.SearchFinish();
                _findStatus.SetLabel(string.Empty);
                return;
            }

            ConnectFinder(finder);
            uint options = (uint)(WebKit.FindOptions.CaseInsensitive | WebKit.FindOptions.WrapAround);
            finder.CountMatches(text, options, 1000);
            finder.Search(text, options, 1000);
        }

        readonly HashSet<WebKit.FindController> _connectedFinders = new();

        void ConnectFinder(WebKit.FindController finder)
        {
            if (!_connectedFinders.Add(finder))
                return;

            finder.OnCountedMatches += (_, args) => _findStatus.SetLabel(args.MatchCount switch
            {
                0 => Tr("Aucun résultat"),
                1 => Tr("1 résultat"),
                _ => Tr("{0} résultats", args.MatchCount)
            });
            finder.OnFailedToFindText += (_, _) => _findStatus.SetLabel(Tr("Aucun résultat"));
        }

        void FindNext(int direction)
        {
            if (Current is not { } tab || _findEntry.GetText().Length == 0)
                return;

            WebKit.FindController finder = tab.Web.GetFindController();
            if (direction > 0)
                finder.SearchNext();
            else
                finder.SearchPrevious();
        }

        // ---------------------------------------------------------------
        // Zoom, favoris
        // ---------------------------------------------------------------

        void Zoom(int direction)
        {
            if (Current is not { Content: TabContent.Web } tab)
                return;

            double zoom = direction == 0 ? 1.0 : SiteZoomStore.Step(tab.Web.GetZoomLevel(), direction);
            tab.Web.SetZoomLevel(zoom);
            if (!IsPrivate && Uri.TryCreate(tab.Uri, UriKind.Absolute, out Uri? uri))
                _app.Zoom.Set(uri, zoom);
            _zoomLabel.SetLabel(SiteZoomStore.Format(zoom));
        }

        void ToggleFavorite()
        {
            if (Current is not { } tab || tab.Uri.Length == 0)
                return;

            FavoriteItem? existing = _app.Favorites.Find(tab.Uri);
            if (existing != null)
            {
                Dialogs.EditFavorite(_app, Window, existing);
                return;
            }

            FavoriteItem added = _app.Favorites.Add(tab.Title, tab.Uri);
            ShowToast(Tr("Ajouté aux favoris"), Tr("Modifier"), () => Dialogs.EditFavorite(_app, Window, added));
        }

        void RebuildFavoritesMenu()
        {
            var menu = Gio.Menu.New();
            var root = Gio.Menu.New();
            foreach (FavoriteItem favorite in _app.Favorites.All.Where(f => f.Folder == null).Take(40))
                root.AppendItem(FavoriteMenuItem(favorite));
            menu.AppendSection(null, root);

            var folders = Gio.Menu.New();
            foreach (string folder in _app.Favorites.Folders)
            {
                var submenu = Gio.Menu.New();
                foreach (FavoriteItem favorite in _app.Favorites.All.Where(f => string.Equals(f.Folder, folder, StringComparison.CurrentCultureIgnoreCase)).Take(60))
                    submenu.AppendItem(FavoriteMenuItem(favorite));
                folders.AppendSubmenu(folder, submenu);
            }
            menu.AppendSection(null, folders);

            var actions = Gio.Menu.New();
            actions.Append(Tr("Ajouter cette page"), "win.bookmark");
            actions.Append(Tr("Gérer les favoris"), "win.favorites");
            menu.AppendSection(null, actions);

            _favoritesButton.SetMenuModel(menu);
        }

        static Gio.MenuItem FavoriteMenuItem(FavoriteItem favorite)
        {
            string label = string.IsNullOrWhiteSpace(favorite.Title) ? favorite.Url : favorite.Title;
            if (label.Length > 60)
                label = label[..57] + "…";
            // Les « _ » seraient pris pour des raccourcis clavier.
            var item = Gio.MenuItem.New(label.Replace("_", "__"), null);
            item.SetActionAndTargetValue("win.open-url", GLib.Variant.NewString(favorite.Url));
            return item;
        }

        // ---------------------------------------------------------------
        // Menus et actions
        // ---------------------------------------------------------------

        Gtk.PopoverMenu BuildMainMenu()
        {
            var menu = Gio.Menu.New();

            var windows = Gio.Menu.New();
            windows.Append(Tr("Nouvel onglet"), "win.new-tab");
            windows.Append(Tr("Nouvelle fenêtre"), "app.new-window");
            windows.Append(Tr("Nouvelle fenêtre privée"), "app.new-private-window");
            menu.AppendSection(null, windows);

            var zoom = Gio.Menu.New();
            var zoomItem = Gio.MenuItem.New(null, null);
            zoomItem.SetAttributeValue("custom", GLib.Variant.NewString("zoom"));
            zoom.AppendItem(zoomItem);
            menu.AppendSection(null, zoom);

            var pages = Gio.Menu.New();
            pages.Append(Tr("Historique"), "win.history");
            pages.Append(Tr("Favoris"), "win.favorites");
            pages.Append(Tr("Services du homelab"), "win.services");
            pages.Append(Tr("Mots de passe"), "win.passwords");
            menu.AppendSection(null, pages);

            var tools = Gio.Menu.New();
            tools.Append(Tr("Rechercher dans la page…"), "win.find");
            tools.Append(Tr("Imprimer…"), "win.print");
            tools.Append(Tr("Outils de développement"), "win.inspector");
            menu.AppendSection(null, tools);

            var app = Gio.Menu.New();
            app.Append(Tr("Préférences"), "app.preferences");
            app.Append(Tr("À propos de PommeBrowser"), "app.about");
            app.Append(Tr("Quitter"), "app.quit");
            menu.AppendSection(null, app);

            var popover = Gtk.PopoverMenu.NewFromModel(menu);

            var zoomBox = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
            zoomBox.AddCssClass("linked");
            zoomBox.SetHalign(Gtk.Align.Center);
            zoomBox.SetMarginTop(4);
            zoomBox.SetMarginBottom(4);
            Gtk.Button zoomOut = IconButton("zoom-out-symbolic", Tr("Zoom arrière (Ctrl+-)"), "win.zoom-out");
            var zoomReset = Gtk.Button.New();
            zoomReset.SetChild(_zoomLabel);
            zoomReset.SetActionName("win.zoom-reset");
            zoomReset.SetTooltipText(Tr("Taille normale (Ctrl+0)"));
            _zoomLabel.SetWidthChars(6);
            Gtk.Button zoomIn = IconButton("zoom-in-symbolic", Tr("Zoom avant (Ctrl++)"), "win.zoom-in");
            foreach (Gtk.Button button in new[] { zoomOut, zoomReset, zoomIn })
            {
                button.RemoveCssClass("flat");
                zoomBox.Append(button);
            }
            popover.AddChild(zoomBox, "zoom");
            return popover;
        }

        Gio.Menu BuildTabMenu()
        {
            var menu = Gio.Menu.New();
            var first = Gio.Menu.New();
            first.Append(Tr("Recharger"), "tab.reload");
            first.Append(Tr("Dupliquer"), "tab.duplicate");
            first.Append(Tr("Épingler / détacher"), "tab.pin");
            menu.AppendSection(null, first);
            var second = Gio.Menu.New();
            second.Append(Tr("Fermer les autres onglets"), "tab.close-others");
            second.Append(Tr("Fermer"), "tab.close");
            menu.AppendSection(null, second);
            return menu;
        }

        void AddActions()
        {
            Add("new-tab", OpenHomeTab);
            Add("close-tab", () => { if (Current != null) CloseTab(Current); });
            Add("reopen-tab", () =>
            {
                if (_closedTabs.Count > 0)
                    OpenInNewTab(_closedTabs.Pop().Url, background: false);
            });
            Add("location", () => _omnibox.Focus());
            Add("reload", () => Current?.Reload());
            Add("reload-hard", () => Current?.Reload(bypassCache: true));
            Add("stop", () => Current?.Stop());
            Add("back", () => Current?.GoBack());
            Add("forward", () => Current?.GoForward());
            Add("home", () => { Current?.ShowHome(); _omnibox.Focus(); });
            Add("find", OpenFind);
            Add("find-next", () => FindNext(1));
            Add("find-previous", () => FindNext(-1));
            Add("zoom-in", () => Zoom(1));
            Add("zoom-out", () => Zoom(-1));
            Add("zoom-reset", () => Zoom(0));
            Add("bookmark", ToggleFavorite);
            Add("history", () => OpenPage(TabContent.History));
            Add("favorites", () => OpenPage(TabContent.Favorites));
            Add("services", () => OpenPage(TabContent.Services));
            Add("passwords", () => OpenPage(TabContent.Passwords));
            Add("fill-credentials", () =>
            {
                if (Current is { Content: TabContent.Web } tab)
                    _app.Vault.Fill(this, tab);
            });
            Add("print", () =>
            {
                if (Current is { Content: TabContent.Web } tab)
                    WebKit.PrintOperation.New(tab.Web).RunDialog(Window);
            });
            Add("inspector", () =>
            {
                if (Current is { Content: TabContent.Web } tab)
                    tab.Web.GetInspector().Show();
            });
            Add("fullscreen", () =>
            {
                if (Window.IsFullscreen())
                    Window.Unfullscreen();
                else
                    Window.Fullscreen();
            });

            var openUrl = Gio.SimpleAction.New("open-url", GLib.VariantType.New("s"));
            openUrl.OnActivate += (_, args) =>
            {
                if (args.Parameter?.GetString(out nuint _) is { Length: > 0 } url)
                    NavigateCurrent(url);
            };
            Window.AddAction(openUrl);

            // Menu contextuel des onglets.
            var tabActions = Gio.SimpleActionGroup.New();
            void AddTabAction(string name, Action<BrowserTab> handler)
            {
                var action = Gio.SimpleAction.New(name, null);
                action.OnActivate += (_, _) =>
                {
                    if ((_menuTab ?? Current) is { } tab)
                        handler(tab);
                };
                tabActions.AddAction(action);
            }
            AddTabAction("reload", tab => tab.Reload());
            AddTabAction("duplicate", tab =>
            {
                if (tab.WebUri.Length > 0)
                    OpenInNewTab(tab.WebUri, background: false, opener: tab);
            });
            AddTabAction("pin", tab =>
            {
                if (tab.Page != null)
                    _tabs.SetPagePinned(tab.Page, !tab.Page.GetPinned());
            });
            AddTabAction("close-others", tab =>
            {
                if (tab.Page != null)
                    _tabs.CloseOtherPages(tab.Page);
            });
            AddTabAction("close", CloseTab);
            Window.InsertActionGroup("tab", tabActions);
        }

        void Add(string name, Action handler)
        {
            var action = Gio.SimpleAction.New(name, null);
            action.OnActivate += (_, _) => handler();
            Window.AddAction(action);
        }

        static void RegisterAccelerators(Adw.Application app)
        {
            if (_acceleratorsRegistered)
                return;
            _acceleratorsRegistered = true;

            void Set(string action, params string[] keys) => app.SetAccelsForAction("win." + action, keys);
            Set("new-tab", "<Control>t");
            Set("close-tab", "<Control>w", "<Control>F4");
            Set("reopen-tab", "<Control><Shift>t");
            Set("location", "<Control>l", "<Alt>d", "F6");
            Set("reload", "<Control>r", "F5");
            Set("reload-hard", "<Control><Shift>r", "<Control>F5");
            Set("back", "<Alt>Left", "Back");
            Set("forward", "<Alt>Right", "Forward");
            Set("home", "<Alt>Home");
            Set("find", "<Control>f");
            Set("find-next", "<Control>g", "F3");
            Set("find-previous", "<Control><Shift>g", "<Shift>F3");
            Set("zoom-in", "<Control>plus", "<Control>equal", "<Control>KP_Add");
            Set("zoom-out", "<Control>minus", "<Control>KP_Subtract");
            Set("zoom-reset", "<Control>0", "<Control>KP_0");
            Set("bookmark", "<Control>d");
            Set("history", "<Control>h");
            Set("favorites", "<Control><Shift>o");
            Set("services", "<Control><Shift>e");
            Set("print", "<Control>p");
            Set("inspector", "<Control><Shift>i", "F12");
            Set("fullscreen", "F11");
        }

        static Gtk.Button IconButton(string icon, string tooltip, string? action)
        {
            var button = Gtk.Button.NewFromIconName(icon);
            button.SetTooltipText(tooltip);
            button.AddCssClass("flat");
            if (action != null)
                button.SetActionName(action);
            return button;
        }
    }
}
