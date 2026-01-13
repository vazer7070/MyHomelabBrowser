using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.controles;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MyHomelabBrowser.classes.Flash;
using System.Diagnostics;



namespace MyHomelabBrowser
{
    public partial class MainWindow : Window
    {
        // ---------------------------
        // Settings
        // ---------------------------
        readonly SettingsService _settings;

        // ---------------------------
        // Omnibox
        // ---------------------------
        bool _addressBarEditing;
        private bool _addressBarSelectAllPending;
        bool _navigatingFromHistory;

        // ---------------------------
        // Dock indicator + docking mode
        // ---------------------------
        public bool IsDockIndicatorVisible => DockIndicator.Visibility == Visibility.Visible;
        bool _isDocking;

        // ---------------------------
        // Suspension
        // ---------------------------
        DispatcherTimer _suspendTimer;
        TimeSpan _SuspendDelay = TimeSpan.FromMinutes(5);

        // ---------------------------
        // History / Favorites (persisted)
        // ---------------------------
        readonly List<HistoryEntry> _history = new();
        readonly List<FavoriteItem> _favorites = new();
        CoreWebView2Environment? _privateEnvironment;
        CoreWebView2Environment? _normalEnvironment;

        LegacyLauncher _legacyLauncher;

        readonly ObservableCollection<ToastItem> _toasts = new();

        public FlashDecisionService? FlashService { get; set; }
        public FlashMode FlashMode { get; set; } = FlashMode.None;

        string _lastHistoryUrl = "";
        DateTime _lastHistoryAt = DateTime.MinValue;

        string AppDataDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MyHomelabBrowser");

        string HistoryPath => Path.Combine(AppDataDir, "history.json");
        string FavoritesPath => Path.Combine(AppDataDir, "favorites.json");


        static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public MainWindow()
        {
            InitializeComponent();

            // timers + settings
            _suspendTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
            _suspendTimer.Tick += (_, _) => AutoSuspendTabs();
            _suspendTimer.Start();
            ToastHost.ItemsSource = _toasts;


            _settings = new SettingsService();
            _legacyLauncher = new LegacyLauncher(_settings);
            _settings.SettingsChanged += ApplySettings;
            PreviewMouseDown += OnGlobalMouseDown;

            // load persisted data (safe)
            LoadHistory();
            LoadFavorites();

           

            // initial UI refresh (safe even if XAML not ready yet)
            RefreshFavoritesBar();
            UpdateFavoriteButton();
            UpdateDownloadsBadge();


            // start page
            CreateTab(_settings.Settings.StartPage);
        }
        void UpdateFlashModeButton(WebTabContent content)
        {
            if (content?.Web?.Source == null)
            {
                FlashModeBtn.Visibility = Visibility.Collapsed;
                return;
            }

            if (!content.FlashRequired)
            {
                FlashModeBtn.Visibility = Visibility.Collapsed;
                return;
            }

            var uri = content.Web.Source;
            var rule = FlashDomainRules.GetRule(uri);

            FlashModeBtn.Visibility = Visibility.Visible;

            FlashModeBtn.Content =
                content.FlashMode == FlashMode.Legacy
                    ? "⚡ Legacy"
                    : rule == FlashRuleMode.Legacy
                        ? "⚡ Legacy"
                        : "⚡ Auto";

            bool legacyAvailable = _legacyLauncher.CanLaunch();

            FlashModeBtn.IsEnabled =
                content.FlashMode != FlashMode.Legacy || legacyAvailable;

            FlashModeBtn.ToolTip =
                !legacyAvailable && content.FlashMode == FlashMode.Legacy
                    ? "Basilisk n’est pas configuré"
                    : "Changer le mode Flash pour ce site";

           Dispatcher.BeginInvoke(
    DispatcherPriority.Background,
    () => FlashModeBtn.InvalidateVisual()
);

        }






        private void FlashModeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (Tabs.SelectedItem is not TabItem tab ||
                tab.Tag is not WebTabContent content ||
                content.Web.Source == null)
                return;

            var uri = content.Web.Source;

            // overlay UNIQUE pour l’onglet courant
            var overlay = content.FlashOverlay;
            if (overlay == null)
                return;


            // ===============================
            // CAS 1 : ON ÉTAIT EN LEGACY → RETOUR AUTO
            // ===============================
            if (FlashDomainRules.GetRule(uri) == FlashRuleMode.Legacy)
            {
                FlashDomainRules.RemoveRule(uri);

                content.FlashMode = FlashMode.None;
                content.IsLegacyExternal = false;

                overlay.TryShow("Retour au mode automatique…");

                // 🔁 recharge → HandleFlashAsync décidera quoi faire
                Navigate(uri.AbsoluteUri);
                return;
            }

            // ===============================
            // CAS 2 : DEMANDE LEGACY
            // ===============================
            if (!_legacyLauncher.CanLaunch())
            {
                overlay.ShowBlocked(
                    "Impossible d’activer Flash Legacy : Basilisk n’est pas configuré.");
                overlay.OpenSettingsRequested += OpenSettings;
                return;
            }

            // ⚡ règle utilisateur uniquement
            FlashDomainRules.SetRule(uri, FlashRuleMode.Legacy);

            overlay.TryShow("Mode Flash Legacy forcé. Rechargement…");

            // 🔁 recharge → HandleFlashAsync lancera Legacy proprement
            Navigate(uri.AbsoluteUri);
        }




        // ---------------------------
        // Settings apply
        // ---------------------------
        void ApplySettings(BrowserSettings s)
        {
            _suspendTimer.IsEnabled = s.EnableSuspension;
            _SuspendDelay = TimeSpan.FromMinutes(s.SuspendDelayMinutes);

            RefreshCommandSuggestions();
        }
        async Task InitPrivateWebViewAsync(
    WebView2 web,
    BrowserTabHeader header,
    TabItem tab,
    string url,
    FlashUxOverlay overlay,
    FlashDecisionService flashService,
    WebTabContent content)
        {
            try
            {
                await InitWebViewEnvironmentsAsync();
                await web.EnsureCoreWebView2Async(_privateEnvironment);
                AttachNewWindowHandling(web);

                web.SourceChanged += (_, _) =>
                    Dispatcher.Invoke(UpdateAddressBarFromTab);

                // ===============================
                // NAVIGATION COMPLETED (PRIVÉ)
                // ===============================
                web.NavigationCompleted += async (_, _) =>
                {
                    Dispatcher.Invoke(UpdateAddressBarFromTab);
                    UpdateFavoriteButton();

                    if (web.CoreWebView2 != null)
                    {
                        header.SetTitle(web.CoreWebView2.DocumentTitle);
                        if (!string.IsNullOrEmpty(web.CoreWebView2.FaviconUri))
                            header.SetIcon(new BitmapImage(new Uri(web.CoreWebView2.FaviconUri)));
                    }

                    AttachPreview(tab, web);

                    // 🔥 FLASH — LOGIQUE UNIQUE, CENTRALISÉE
                    await HandleFlashAsync(web, content, header, overlay);
                };

                web.Source = new Uri(url);
            }
            catch (Exception ex)
            {
                MessageBox.Show(
                    ex.ToString(),
                    "Erreur onglet privé",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);

                Dispatcher.Invoke(() =>
                {
                    Tabs.Items.Remove(tab);
                    if (Tabs.Items.Count == 0)
                        Close();
                });
            }
        }






        async Task InitWebViewEnvironmentsAsync()
        {
            if (_privateEnvironment != null && _normalEnvironment != null)
                return;

            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyHomelabBrowser"
            );

            _normalEnvironment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(baseDir, "Default")
            );

            _privateEnvironment = await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(baseDir, "Private")
            );
        }


        // ---------------------------
        // Address bar sync
        // ---------------------------
        void SyncAddressBarWithTab(TabItem tab)
        {
            if (tab?.Tag is WebTabContent webTab && webTab.Web?.Source != null)
                AddressBar.Text = webTab.Web.Source.AbsoluteUri;
            else
                AddressBar.Text = string.Empty;

            UpdateFavoriteButton();
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

        void OnGlobalMouseDown(object sender, MouseButtonEventArgs e)
        {
            // NE PAS intercepter les clics sur les boutons
            if (e.OriginalSource is DependencyObject d &&
                FindParent<Button>(d) != null)
                return;

            if (!AddressBar.IsKeyboardFocusWithin &&
                !CommandSuggestionsPopup.IsMouseOver)
            {
                HideCommandSuggestions();
            }
        }

        DownloadItem? _lastToastItem;
        private void Toast_OpenFile(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.Tag is ToastItem t)
                DownloadManager.Instance.OpenFile(t.Download);
        }

        void AnimateToastOut(FrameworkElement el, Action onDone)
        {
            var sb = new Storyboard();

            var fade = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(200)
            };

            fade.Completed += (_, _) => onDone();

            Storyboard.SetTarget(fade, el);
            Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));

            sb.Children.Add(fade);
            sb.Begin();
        }


        void AnimateToastIn(FrameworkElement el)
        {
            var sb = new Storyboard();

            // Fade in
            var fade = new DoubleAnimation
            {
                From = 0,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(220)
            };
            Storyboard.SetTarget(fade, el);
            Storyboard.SetTargetProperty(fade, new PropertyPath("Opacity"));

            // Slide in
            var slide = new DoubleAnimation
            {
                From = 40,
                To = 0,
                Duration = TimeSpan.FromMilliseconds(220),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(slide, el);
            Storyboard.SetTargetProperty(
                slide,
                new PropertyPath("(UIElement.RenderTransform).(TranslateTransform.X)")
            );

            sb.Children.Add(fade);
            sb.Children.Add(slide);

            sb.Begin();
        }


        public void ShowToast(string title, string message, DownloadItem item)
        {
            var toast = new ToastItem
            {
                Title = title,
                Message = message,
                Download = item
            };

            _toasts.Insert(0, toast);

            Dispatcher.BeginInvoke(() =>
            {
                var container = (FrameworkElement)ToastHost.ItemContainerGenerator
                    .ContainerFromItem(toast);

                if (container == null) return;

                AnimateToastIn(container);

                var timer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(4)
                };

                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    AnimateToastOut(container, () => _toasts.Remove(toast));
                };

                timer.Start();
            }, DispatcherPriority.Loaded);
        }

        

        void UpdateAddressBarFromTab()
        {
            if (_addressBarEditing)
                return;

            if (Tabs.SelectedItem is not TabItem tab)
                return;

            if (tab.Tag is WebTabContent webTab)
            {
                var uri = webTab.Web.Source;
                AddressBar.Text = uri?.ToString() ?? "";
            }

            UpdateFavoriteButton();
        }

        // ---------------------------
        // Dock indicator
        // ---------------------------
        public void BeginDockingMode() => _isDocking = true;

        public void EndDockingMode()
        {
            _isDocking = false;
            HideDockIndicator();
        }

        public void ShowDockIndicator() => DockIndicator.Visibility = Visibility.Visible;
        public void HideDockIndicator() => DockIndicator.Visibility = Visibility.Collapsed;

        // ---------------------------
        // Tabs / WebHost
        // ---------------------------
        void OpenSettings()
        {
            foreach (TabItem t in Tabs.Items)
            {
                if (t.Tag is ViewTabContent)
                {
                    Tabs.SelectedItem = t;
                    SyncWebHostWithSelection();
                    return;
                }
            }

            var view = new SettingsView(_settings);
            view.OpenHistoryRequested += OpenHistory;

            var header = new BrowserTabHeader();
            header.SetTitle("Paramètres");

            var tab = new TabItem
            {
                Header = header,
                Tag = new ViewTabContent { View = view }
            };

            header.CloseRequested += () => CloseTab(tab);

            Tabs.Items.Add(tab);
            Tabs.SelectedItem = tab;
            SyncWebHostWithSelection();
        }

        void CreateTab(string url)
        {
            var web = new WebView2 { Source = new Uri(url) };
            DownloadHook.Attach(web, isPrivate: false);

            var header = new BrowserTabHeader();
            header.SetTitle("Nouvel onglet");

            var overlay = new FlashUxOverlay(this);
            var flashService = new FlashDecisionService(web, _settings, _legacyLauncher);

            var content = new WebTabContent
            {
                Web = web,
                IsPinned = false,
                IsSuspended = false,
                LastActivated = DateTime.Now,
                FlashService = flashService,
                FlashMode = FlashMode.None,
                FlashOverlay = overlay
            };

            web.SourceChanged += (_, _) =>
                Dispatcher.Invoke(UpdateAddressBarFromTab);

            var tab = new TabItem
            {
                Header = header,
                Tag = content
            };

            // ===============================
            // PRÉ-DÉTECTION RAPIDE (AVANT DOM)
            // ===============================
            web.CoreWebView2InitializationCompleted += (_, e) =>
            {
                if (!e.IsSuccess || web.CoreWebView2 == null)
                    return;

                web.CoreWebView2.NavigationStarting += (_, nav) =>
                {
                    try
                    {
                        var uri = new Uri(nav.Uri);
                        AttachNewWindowHandling(web);

                        Dispatcher.Invoke(() =>
                        {
                            // intention Flash (pas décision)
                            content.FlashRequired = true;
                            UpdateFlashModeButton(content);
                            overlay.TryShow("Vérification Flash en cours…");
                        });
                    }
                    catch { }
                };
            };

            // ---------------------------
            // Address bar + history + fav
            // ---------------------------
            web.NavigationCompleted += (_, _) =>
            {
                Dispatcher.Invoke(UpdateAddressBarFromTab);

                if (Tabs.SelectedItem == tab && web.Source != null)
                    AddressBar.Text = web.Source.AbsoluteUri;

                if (web.Source != null)
                    AddHistoryEntry(web);

                UpdateFavoriteButton();
            };

            // ---------------------------
            // Title / icon / preview + FLASH
            // ---------------------------
            web.NavigationCompleted += async (_, _) =>
            {
                if (web.CoreWebView2 != null)
                {
                    header.SetTitle(web.CoreWebView2.DocumentTitle);
                    if (!string.IsNullOrEmpty(web.CoreWebView2.FaviconUri))
                        header.SetIcon(new BitmapImage(new Uri(web.CoreWebView2.FaviconUri)));
                }

                AttachPreview(tab, web);

                // 🔥 LOGIQUE FLASH UNIQUE
                await HandleFlashAsync(web, content, header, overlay);

                var hideTimer = new DispatcherTimer
                {
                    Interval = TimeSpan.FromSeconds(2)
                };
                hideTimer.Tick += (_, _) =>
                {
                    hideTimer.Stop();
                    overlay.Hide();
                };
                hideTimer.Start();
            };

            // ---------------------------
            // Header actions
            // ---------------------------
            header.CloseRequested += () => CloseTab(tab);
            header.DetachRequested += () =>
            {
                if (_isDocking) return;
                DetachTab(tab);
            };
            header.PinRequested += () =>
            {
                content.IsPinned = !content.IsPinned;
                ApplyPinState(tab, header, content.IsPinned);
            };
            header.ReorderRequested += dir => ReorderTab(tab, dir);

            Tabs.Items.Add(tab);
            Tabs.SelectedItem = tab;
            SyncWebHostWithSelection();
        }





        private void NewPrivateTab_Click(object sender, RoutedEventArgs e)
        {
            // on ne bloque jamais l'UI sur EnsureCoreWebView2Async
            CreatePrivateTab(GetNewTabUrl());
        }


        void CreatePrivateTab(string url)
        {
            var web = new WebView2(); // pas de Source ici
            DownloadHook.Attach(web, isPrivate: true);

            var header = new BrowserTabHeader();
            header.SetTitle("Privé");
            header.SetPrivate(true);

            var overlay = new FlashUxOverlay(this);
            var flashService = new FlashDecisionService(web, _settings, _legacyLauncher);


            var content = new WebTabContent
            {
                Web = web,
                IsPinned = false,
                IsSuspended = false,
                LastActivated = DateTime.Now,
                FlashService = flashService,
                FlashMode = FlashMode.None,
                FlashOverlay = overlay
            };

            var tab = new TabItem
            {
                Header = header,
                Tag = content
            };

            header.CloseRequested += () => CloseTab(tab);
            header.DetachRequested += () => { if (_isDocking) return; DetachTab(tab); };

            Tabs.Items.Add(tab);
            Tabs.SelectedItem = tab;
            SyncWebHostWithSelection();

            _ = InitPrivateWebViewAsync(web, header, tab, url, overlay, flashService, content);
        }

        public bool CanUseFlashOrLegacy(Uri uri, bool flashDetected, out string reason)
        {
            reason = "";

            // Pas de Flash → aucune contrainte
            if (!flashDetected)
                return true;

            var s = _settings.Settings;

            if (!s.EnableFlashSupport)
            {
                reason = "Le support Flash est désactivé dans les paramètres.";
                return false;
            }

            var rule = FlashDomainRules.GetRule(uri);

            bool legacyRequired =
                rule == FlashRuleMode.Legacy ||
                (rule == FlashRuleMode.Auto && !s.PreferRuffle);

            if (legacyRequired && !_legacyLauncher.CanLaunch())
            {
                reason = "Ce site nécessite Flash réel, mais Basilisk n’est pas configuré.";
                return false;
            }

            return true;
        }




        UIElement CreateLegacyPlaceholder(TabItem tab, WebTabContent state)
        {
            var panel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(40),
                Padding = new Thickness(30)
            };

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            stack.Children.Add(new TextBlock
            {
                Text = "🧩 Mode Legacy (Flash réel)",
                FontSize = 24,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            stack.Children.Add(new TextBlock
            {
                Text = state.LegacyUrl ?? "",
                FontSize = 13,
                Margin = new Thickness(0, 8, 0, 18),
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                HorizontalAlignment = HorizontalAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 900
            });

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };

            var reopen = new Button
            {
                Content = "Ré-ouvrir Basilisk",
                Margin = new Thickness(0, 0, 10, 0),
                Padding = new Thickness(12, 8, 12, 8)
            };

            reopen.Click += (_, _) =>
            {
                if (string.IsNullOrWhiteSpace(state.LegacyUrl))
                    return;

                var uri = new Uri(state.LegacyUrl);
                var profile = LegacyProfileManager.GetProfileForDomain(uri.Host);
                var p = _legacyLauncher.Launch(state.LegacyUrl, profile);
                state.LegacyPid = p?.Id;

                if (tab.Header is BrowserTabHeader h)
                    h.SetTitle("Legacy Flash");
            };

            var tryRuffle = new Button
            {
                Content = "Tenter Ruffle ici",
                Padding = new Thickness(12, 8, 12, 8)
            };

            tryRuffle.Click += async (_, _) =>
            {
                state.IsLegacyExternal = false;
                state.FlashMode = FlashMode.None;

                // recharge l’URL dans WebView2
                if (!string.IsNullOrWhiteSpace(state.LegacyUrl))
                    state.Web.Source = new Uri(state.LegacyUrl);

                // resync affichage
                SyncWebHostWithSelection();

                // si Flash, inject Ruffle
                if (state.FlashService != null)
                {
                    var hasFlash = await state.FlashService.DetectFlashDomAsync();
                    if (hasFlash)
                    {
                        state.FlashMode = FlashMode.Ruffle;
                        await RuffleInjector.InjectAsync(state.Web);
                    }
                }
            };

            btnRow.Children.Add(reopen);
            btnRow.Children.Add(tryRuffle);

            stack.Children.Add(btnRow);
            panel.Child = stack;

            return panel;
        }


        void AnimatePrivateTransition()
        {
            var anim = new DoubleAnimation
            {
                From = 0.85,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };

            this.BeginAnimation(OpacityProperty, anim);
        }

        void SyncWebHostWithSelection()
        {
            if (Tabs.SelectedItem is not TabItem tab)
            {
                WebHost.Content = null;
                PrivateIndicator.Visibility = Visibility.Collapsed;
                FlashModeBtn.Visibility = Visibility.Collapsed;
                return;
            }

            if (tab.Tag is WebTabContent webTab)
            {
                // ---------------------------
                // Indicateurs / thème
                // ---------------------------
                PrivateIndicator.Visibility =
                    webTab.IsPrivate ? Visibility.Visible : Visibility.Collapsed;

                ApplyPrivateTheme(webTab.IsPrivate);
                AnimatePrivateTransition();

                AddressBar.Background = webTab.IsPrivate
                    ? new SolidColorBrush(Color.FromRgb(70, 40, 90))
                    : new SolidColorBrush(Color.FromRgb(72, 68, 68));

                webTab.LastActivated = DateTime.Now;

                // ---------------------------
                // Contenu principal
                // ---------------------------
                WebHost.Content =
                    webTab.IsLegacyExternal ? CreateLegacyPlaceholder(tab, webTab) :
                    webTab.IsSuspended ? CreateSuspendedPlaceholder(tab, webTab) :
                    webTab.Web;

                // ---------------------------
                // 🔥 FLASH UI
                // ---------------------------
                UpdateFlashModeButton(webTab);

                // ⚠️ UTILISER L’OVERLAY EXISTANT
                if (webTab.FlashRequired && !webTab.IsLegacyExternal)
                {
                    var overlay = webTab.FlashOverlay;
                    if (overlay != null)
                    {
                        overlay.InvalidateLayout();
                        overlay.TryShow("Ce site nécessite Flash. Choisissez un mode.");
                    }
                }
            }
            else if (tab.Tag is ViewTabContent viewTab)
            {
                PrivateIndicator.Visibility = Visibility.Collapsed;
                ApplyPrivateTheme(false);
                AnimatePrivateTransition();

                AddressBar.Background = new SolidColorBrush(Color.FromRgb(72, 68, 68));
                WebHost.Content = viewTab.View;

                UpdateAddressBarFromTab();

                FlashModeBtn.Visibility = Visibility.Collapsed;
            }
            else
            {
                WebHost.Content = null;
                PrivateIndicator.Visibility = Visibility.Collapsed;
                FlashModeBtn.Visibility = Visibility.Collapsed;
            }

            UpdateFavoriteButton();
        }





        private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Tabs.SelectedItem is TabItem tab)
            {
                SyncWebHostWithSelection();
                SyncAddressBarWithTab(tab);
            }
        }

        private void NewTab_Click(object sender, RoutedEventArgs e)
            => CreateTab(GetNewTabUrl());

        string GetNewTabUrl()
        {
            return _settings.Settings.NewTabPage?.Trim() is string url && url.Length > 0
                ? url
                : "about:blank";
        }

        // ---------------------------
        // Favorites bar + star button (safe until XAML exists)
        // ---------------------------
        void RefreshFavoritesBar()
        {
            if (FavoritesBar == null) return;

            FavoritesBar.Children.Clear();

            // =====================
            // Favoris racine
            // =====================
            foreach (var fav in _favorites.Where(f => f.Folder == null))
            {
                FavoritesBar.Children.Add(CreateFavoriteButton(fav));
            }

            // =====================
            // Dossiers
            // =====================
            var folders = _favorites
                .Where(f => !string.IsNullOrWhiteSpace(f.Folder))
                .GroupBy(f => f.Folder!);

            foreach (var folder in folders)
            {
                var menu = new Menu
                {
                    Background = Brushes.Transparent,
                    Margin = new Thickness(4, 0, 4, 0)
                };

                // 📁 Header du dossier
                var headerPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center
                };

                headerPanel.Children.Add(new TextBlock
                {
                    Text = "📁",
                    Margin = new Thickness(0, 0, 6, 0)
                });

                headerPanel.Children.Add(new TextBlock
                {
                    Text = folder.Key,
                    Foreground = Brushes.White
                });

                var root = new MenuItem
                {
                    Header = headerPanel,
                    Padding = new Thickness(10, 4, 10, 4)
                };

                foreach (var fav in folder)
                {
                    // item avec favicon
                    var itemPanel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal
                    };

                    itemPanel.Children.Add(new Image
                    {
                        Width = 16,
                        Height = 16,
                        Margin = new Thickness(0, 0, 6, 0),
                        Source = GetFavicon(fav.Url)
                    });

                    itemPanel.Children.Add(new TextBlock
                    {
                        Text = fav.Title
                    });

                    var item = new MenuItem
                    {
                        Header = itemPanel,
                        Tag = fav
                    };

                    item.Click += (_, _) => Navigate(fav.Url);

                    item.MouseRightButtonUp += (_, _) =>
                    {
                        var dlg = new EditFavoriteDialog(fav)
                        {
                            Owner = this
                        };

                        if (dlg.ShowDialog() == true)
                        {
                            if (dlg.Deleted)
                                RemoveFavorite(fav);
                            else
                                SaveFavorites();

                            RefreshFavoritesBar();
                            UpdateFavoriteButton();
                        }
                    };


                    root.Items.Add(item);
                }

                menu.Items.Add(root);
                FavoritesBar.Children.Add(menu);
            }
        }

        BitmapImage GetFavicon(string url)
        {
            try
            {
                var uri = new Uri(url);
                var faviconUrl = $"https://www.google.com/s2/favicons?domain={uri.Host}&sz=32";

                return new BitmapImage(new Uri(faviconUrl));
            }
            catch
            {
                return null!;
            }
        }

        Button CreateFavoriteButton(FavoriteItem fav)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var icon = new Image
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 6, 0),
                Source = GetFavicon(fav.Url)
            };

            var text = new TextBlock
            {
                Text = fav.Title,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = Brushes.White
            };

            panel.Children.Add(icon);
            panel.Children.Add(text);

            var btn = new Button
            {
                Content = panel,
                Tag = fav,
                Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(4, 0, 4, 0),
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };

            btn.Click += (_, _) => Navigate(fav.Url);

            btn.MouseRightButtonUp += (_, _) =>
            {
                var dlg = new EditFavoriteDialog(fav)
                {
                    Owner = this
                };

                if (dlg.ShowDialog() == true)
                {
                    if (dlg.Deleted)
                        RemoveFavorite(fav);
                    else
                        SaveFavorites();

                    RefreshFavoritesBar();
                    UpdateFavoriteButton();
                }
            };


            return btn;
        }



        void UpdateFavoriteButton()
        {
            if (FavoriteButton == null) return;

            if (Tabs.SelectedItem is not TabItem tab ||
                tab.Tag is not WebTabContent web ||
                web.Web.Source == null)
            {
                FavoriteButton.Foreground = Brushes.Gray;
                return;
            }

            bool isFav = _favorites.Any(f => string.Equals(f.Url, web.Web.Source.AbsoluteUri, StringComparison.OrdinalIgnoreCase));
            FavoriteButton.Foreground = isFav ? Brushes.Gold : Brushes.Gray;
        }

        private void ToggleFavorite_Click(object sender, RoutedEventArgs e)
        {
            if (Tabs.SelectedItem is not TabItem tab ||
                tab.Tag is not WebTabContent web ||
                web.Web.Source == null)
                return;

            var url = web.Web.Source.AbsoluteUri;
            var existing = _favorites.FirstOrDefault(f => string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                RemoveFavorite(existing);
                return;
            }

            AddFavorite(new FavoriteItem
            {
                Url = url,
                Title = web.Web.CoreWebView2?.DocumentTitle ?? url,
                // Folder = null par défaut (racine)
            });
        }

        void AddFavorite(FavoriteItem fav)
        {
            // éviter doublons
            if (_favorites.Any(f => string.Equals(f.Url, fav.Url, StringComparison.OrdinalIgnoreCase)))
                return;

            _favorites.Add(fav);
            SaveFavorites();

            RefreshFavoritesBar();
            UpdateFavoriteButton();
        }

        void RemoveFavorite(FavoriteItem fav)
        {
            _favorites.Remove(fav);
            SaveFavorites();

            RefreshFavoritesBar();
            UpdateFavoriteButton();
        }

        // ---------------------------
        // History
        // ---------------------------
        void AddHistoryEntry(WebView2 web)
        {
            if (Tabs.SelectedItem is TabItem tab &&
        tab.Tag is WebTabContent state &&
        state.IsPrivate)
                return;

            // 🚫 navigation issue de l’historique → ne pas réenregistrer
            if (_navigatingFromHistory)
            {
                _navigatingFromHistory = false;
                return;
            }

            if (web.Source == null) return;

            string url = web.Source.AbsoluteUri;
            var now = DateTime.Now;

            // anti-spam: même URL, très proche dans le temps
            if (string.Equals(url, _lastHistoryUrl, StringComparison.OrdinalIgnoreCase) &&
                (now - _lastHistoryAt) < TimeSpan.FromSeconds(3))
                return;

            _lastHistoryUrl = url;
            _lastHistoryAt = now;

            _history.Add(new HistoryEntry
            {
                Title = web.CoreWebView2?.DocumentTitle ?? web.Source.Host,
                Url = url,
                VisitedAt = now
            });

            // limiter taille (évite gonflement infini)
            const int max = 5000;
            if (_history.Count > max)
                _history.RemoveRange(0, _history.Count - max);

            SaveHistory();
        }


        // ---------------------------
        // Persist (JSON)
        // ---------------------------
        void EnsureAppDataDir()
        {
            if (!Directory.Exists(AppDataDir))
                Directory.CreateDirectory(AppDataDir);
        }

        void LoadHistory()
        {
            try
            {
                EnsureAppDataDir();
                if (!File.Exists(HistoryPath)) return;

                var json = File.ReadAllText(HistoryPath);
                var items = JsonSerializer.Deserialize<List<HistoryEntry>>(json, JsonOpts);
                _history.Clear();
                if (items != null) _history.AddRange(items);
            }
            catch
            {
                // volontairement silencieux: pas de crash au démarrage
                _history.Clear();
            }
        }

        void SaveHistory()
        {
            try
            {
                EnsureAppDataDir();
                var json = JsonSerializer.Serialize(_history, JsonOpts);
                File.WriteAllText(HistoryPath, json);
            }
            catch
            {
                // silencieux: pas de crash pendant navigation
            }
        }

        void LoadFavorites()
        {
            try
            {
                EnsureAppDataDir();
                if (!File.Exists(FavoritesPath)) return;

                var json = File.ReadAllText(FavoritesPath);
                var items = JsonSerializer.Deserialize<List<FavoriteItem>>(json, JsonOpts);
                _favorites.Clear();
                if (items != null) _favorites.AddRange(items);
            }
            catch
            {
                _favorites.Clear();
            }
        }

        void SaveFavorites()
        {
            try
            {
                EnsureAppDataDir();
                var json = JsonSerializer.Serialize(_favorites, JsonOpts);
                File.WriteAllText(FavoritesPath, json);
            }
            catch
            {
            }
        }

        // ---------------------------
        // Reorder / pin
        // ---------------------------
        void ReorderTab(TabItem tab, int direction)
        {
            int index = Tabs.Items.IndexOf(tab);
            if (index < 0) return;

            int newIndex = index + direction;
            if (newIndex < 0 || newIndex >= Tabs.Items.Count)
                return;

            if (Tabs.Items[newIndex] is TabItem other &&
                other.Tag is WebTabContent s && s.IsPinned)
                return;

            double offset = direction * 160;

            if (tab.Header is BrowserTabHeader moving)
                moving.AnimateReorder(-offset);

            if (Tabs.Items[newIndex] is TabItem crossed &&
                crossed.Header is BrowserTabHeader crossedHeader)
                crossedHeader.AnimateReorder(offset);

            Tabs.Items.RemoveAt(index);
            Tabs.Items.Insert(newIndex, tab);
            Tabs.SelectedItem = tab;
        }

        void MovePinnedTabsToFront()
        {
            var pinned = Tabs.Items.Cast<TabItem>()
                .Where(t => t.Tag is WebTabContent s && s.IsPinned)
                .ToList();

            var others = Tabs.Items.Cast<TabItem>()
                .Where(t => t.Tag is not WebTabContent s || !s.IsPinned)
                .ToList();

            Tabs.Items.Clear();

            foreach (var t in pinned.Concat(others))
                Tabs.Items.Add(t);
        }

        void ApplyPinState(TabItem tab, BrowserTabHeader header, bool pinned)
        {
            if (pinned)
            {
                header.SetPinned(true);
                tab.MinWidth = 56;
                tab.MaxWidth = 56;
            }
            else
            {
                header.SetPinned(false);
                tab.MinWidth = 160;
                tab.MaxWidth = 260;
            }

            MovePinnedTabsToFront();
        }

        // ---------------------------
        // Suspension
        // ---------------------------
        void SuspendTab(TabItem tab, WebTabContent state)
        {
            if (state.IsSuspended)
                return;

            bool wasSelected = Equals(Tabs.SelectedItem, tab);

            if (wasSelected)
                SelectFallbackTab(tab);

            state.IsSuspended = true;

            if (tab.Header is BrowserTabHeader header)
                header.ShowSuspended(true);

            if (!wasSelected || !Equals(Tabs.SelectedItem, tab))
                SyncWebHostWithSelection();
        }

        UIElement CreateSuspendedPlaceholder(TabItem tab, WebTabContent state)
        {
            var panel = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(30, 30, 30)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(60, 60, 60)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(40),
                Padding = new Thickness(30),
                Cursor = Cursors.Hand
            };

            var stack = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            stack.Children.Add(new TextBlock
            {
                Text = "⏸ Onglet suspendu",
                FontSize = 24,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center
            });

            stack.Children.Add(new TextBlock
            {
                Text = "Cliquez pour réactiver",
                FontSize = 14,
                Margin = new Thickness(0, 12, 0, 0),
                Foreground = new SolidColorBrush(Color.FromRgb(180, 180, 180)),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            panel.Child = stack;

            panel.MouseLeftButtonUp += (_, _) =>
            {
                state.IsSuspended = false;
                state.LastActivated = DateTime.Now;

                if (tab.Header is BrowserTabHeader h)
                    h.ShowSuspended(false);

                SyncWebHostWithSelection();
            };

            return panel;
        }

        void AutoSuspendTabs()
        {
            var now = DateTime.Now;

            foreach (TabItem tab in Tabs.Items)
            {
                if (tab.Tag is not WebTabContent state)
                    continue;

                if (state.IsPinned || state.IsSuspended)
                    continue;

                if (Equals(tab, Tabs.SelectedItem))
                    continue;

                if (now - state.LastActivated > _SuspendDelay)
                    SuspendTab(tab, state);
            }
        }

        void CloseTab(TabItem tab)
        {
            bool wasPrivate =
                tab.Tag is WebTabContent w && w.IsPrivate;

            bool wasSelected = Equals(Tabs.SelectedItem, tab);

            Tabs.Items.Remove(tab);

            
            if (wasPrivate && !HasAnyPrivateTab())
            {
               
                DownloadManager.Instance.ClearPrivateDownloads();

                
                UpdateDownloadsBadge();
            }

            if (Tabs.Items.Count == 0)
            {
                Close();
                return;
            }

            if (wasSelected)
                Tabs.SelectedIndex = Math.Max(0, Tabs.SelectedIndex);

            SyncWebHostWithSelection();
        }


        void SelectFallbackTab(TabItem from)
        {
            int index = Tabs.Items.IndexOf(from);

            if (index > 0)
            {
                Tabs.SelectedIndex = index - 1;
                return;
            }

            if (index < Tabs.Items.Count - 1)
            {
                Tabs.SelectedIndex = index + 1;
                return;
            }

            Tabs.SelectedItem = null;
        }

        // ---------------------------
        // Navigation / Omnibox
        // ---------------------------
        private void AddressBar_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            HandleOmnibox(AddressBar.Text);
        }

        void NavigateOrSearch(string input)
        {
            
            if (!input.StartsWith("http", StringComparison.OrdinalIgnoreCase) &&
                !input.Contains("."))
            {
                Navigate($"https://www.google.com/search?q={Uri.EscapeDataString(input)}");
                return;
            }

            Navigate(input);
        }


        IEnumerable<CommandSetting> GetEnabledCommands()
        {
            if (!_settings.Settings.EnableCommands)
                return Enumerable.Empty<CommandSetting>();

            return _settings.Settings.Commands
                .Where(c => c.Enabled)
                .OrderBy(c => c.Key);
        }

        void ShowCommandSuggestions()
        {
            if (CommandList == null)
                return;

            CommandSuggestionsPopup.IsOpen = true;

            var items = new List<OmniboxItem>();

            // =========================
            // COMMANDES
            // =========================
            if (_addressBarEditing)
            {
                var query = AddressBar.Text?.Trim() ?? "";

                if (query.StartsWith(":"))
                {
                    var cmd = query[1..];

                    items.AddRange(
                        _settings.Settings.Commands
                            .Where(c => c.Enabled &&
                                        c.Key.StartsWith(cmd, StringComparison.OrdinalIgnoreCase))
                            .Select(c => new OmniboxItem
                            {
                                Type = OmniboxItemType.Command,
                                Primary = ":" + c.Key,
                                Secondary = c.Description,
                                Command = c
                            })
                    );
                }
            }

            // =========================
            // HISTORIQUE GLOBAL (🔥)
            // =========================
            IEnumerable<HistoryEntry> history = _history
                .OrderByDescending(h => h.VisitedAt);

            // ❗ filtrer UNIQUEMENT si l'utilisateur tape
            if (_addressBarEditing)
            {
                var q = AddressBar.Text?.Trim();

                if (!string.IsNullOrWhiteSpace(q) &&
                    !q.StartsWith(":") &&
                    !q.StartsWith("@") &&
                    !q.StartsWith("*"))
                {
                    history = history.Where(h =>
                        h.Title.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        h.Url.Contains(q, StringComparison.OrdinalIgnoreCase));
                }
            }

            items.AddRange(
                history.Take(10).Select(h => new OmniboxItem
                {
                    Type = OmniboxItemType.History,
                    Primary = h.Title,
                    Secondary = h.Url,
                    History = h
                })
            );

            CommandList.ItemsSource = items;
        }





        void HideCommandSuggestions()
        {
            if (CommandSuggestionsPopup != null)
                CommandSuggestionsPopup.IsOpen = false;
        }

        private void AddressBar_GotFocus(object sender, RoutedEventArgs e)
        {
            RefreshCommandSuggestions();
            ShowCommandSuggestions();
        }
        private void AddressBar_TextChanged(object sender, TextChangedEventArgs e)
        {
            RefreshOmniboxSuggestions(AddressBar.Text);
            ShowCommandSuggestions();
        }

        private void CommandSuggestions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox list || list.SelectedItem is not OmniboxItem item)
                return;

            list.SelectedItem = null;
            HideCommandSuggestions();

            switch (item.Type)
            {
                case OmniboxItemType.Command:
                    if (item.Command != null)
                    {
                        AddressBar.Text = ":" + item.Command.Key;
                        AddressBar.CaretIndex = AddressBar.Text.Length;
                        AddressBar.Focus();
                    }
                    break;

                case OmniboxItemType.History:
                    if (item.History != null)
                        Navigate(item.History.Url, fromHistory: true);
                    break;
            }
        }



        void ExecuteCommand(string cmd)
        {
            cmd = cmd.Trim().ToLowerInvariant();

            if (!IsCommandEnabled(cmd))
                return;

            switch (cmd)
            {
                case "new":
                    CreateTab(GetNewTabUrl());
                    break;

                case "close":
                    if (Tabs.SelectedItem is TabItem tab)
                        CloseTab(tab);
                    break;

                case "close others":
                    CloseOtherTabs();
                    break;

                case "reload":
                    if (Tabs.SelectedItem is TabItem t && t.Tag is WebTabContent w)
                        w.Web.Reload();
                    break;

                case "suspend":
                    if (Tabs.SelectedItem is TabItem y && y.Tag is WebTabContent k)
                        SuspendTab(y, k);
                    break;

                case "resume":
                    if (Tabs.SelectedItem is TabItem rr && rr.Tag is WebTabContent st && st.IsSuspended)
                    {
                        st.IsSuspended = false;
                        st.LastActivated = DateTime.Now;

                        if (rr.Header is BrowserTabHeader h)
                            h.ShowSuspended(false);

                        SyncWebHostWithSelection();
                    }
                    break;

                case "history":
                    OpenHistory();
                    break;

                case "suspend inactive":
                    AutoSuspendTabs();
                    break;
            }
        }
        void RefreshOmniboxSuggestions(string input)
        {
            if (CommandList == null) return;

            input = input.Trim();

            if (input.StartsWith(":"))
            {
                CommandList.ItemsSource = GetEnabledCommands();
                return;
            }

            if (input.StartsWith("*"))
            {
                CommandList.ItemsSource = _favorites;
                return;
            }

            if (input.StartsWith("@"))
            {
                CommandList.ItemsSource = Tabs.Items
                    .OfType<TabItem>()
                    .Select(t => t.Header)
                    .OfType<BrowserTabHeader>()
                    .Select(h => h.Title.Text)
                    .ToList();
                return;
            }

            // historique par défaut
            CommandList.ItemsSource = _history
                .OrderByDescending(h => h.VisitedAt)
                .Take(50)
                .ToList();
        }

        void RemoveHistoryEntry(HistoryEntry entry)
        {
            _history.Remove(entry);
            SaveHistory();
        }

        private void OpenHistory_Click(object sender, RoutedEventArgs e)
        {
            OpenHistory();
        }

        void OpenHistory()
        {
            // éviter doublon
            foreach (TabItem t in Tabs.Items)
            {
                if (t.Tag is ViewTabContent v &&
                    v.View is HistoryView)
                {
                    Tabs.SelectedItem = t;
                    SyncWebHostWithSelection();
                    return;
                }
            }

            var view = new HistoryView(_history, Navigate, RemoveHistoryEntry);


            var header = new BrowserTabHeader();
            header.SetTitle("Historique");

            var tab = new TabItem
            {
                Header = header,
                Tag = new ViewTabContent { View = view }
            };

            header.CloseRequested += () => CloseTab(tab);

            Tabs.Items.Add(tab);
            Tabs.SelectedItem = tab;
            SyncWebHostWithSelection();
        }


        bool IsCommandEnabled(string key)
        {
            var settings = _settings.Settings;

            if (!settings.EnableCommands)
                return false;

            var cmd = settings.Commands.FirstOrDefault(c => c.Key == key);
            return cmd?.Enabled == true;
        }

        void CloseOtherTabs()
        {
            if (Tabs.SelectedItem is not TabItem current)
                return;

            var toClose = Tabs.Items.Cast<TabItem>()
                .Where(t => t != current)
                .ToList();

            foreach (var tab in toClose)
                CloseTab(tab);
        }

        void FocusTab(string query)
        {
            query = query.ToLowerInvariant();

            foreach (TabItem tab in Tabs.Items)
            {
                if (tab.Header is BrowserTabHeader header &&
                    header.Title.Text.ToLowerInvariant().Contains(query))
                {
                    Tabs.SelectedItem = tab;
                    SyncWebHostWithSelection();
                    return;
                }
            }
        }

        void HandleOmnibox(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return;

            input = input.Trim();

            if (input.StartsWith(":"))
            {
                ExecuteCommand(input[1..]);
                return;
            }

            if (input.StartsWith("@"))
            {
                FocusTab(input[1..]);
                return;
            }

            NavigateOrSearch(input);
        }

        void Navigate(string url, bool fromHistory = false)
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;

            _navigatingFromHistory = fromHistory;

            // 🔹 Si l’onglet courant est un WebView → naviguer dedans
            if (Tabs.SelectedItem is TabItem tab &&
                tab.Tag is WebTabContent state)
            {
                state.Web.Source = new Uri(url);
                return;
            }

            // 🔹 Sinon (History, Settings, etc.) → nouvel onglet
            CreateTab(url);
        }



        // ---------------------------
        // Sidebar
        // ---------------------------
        bool sidebarOpen = true;

        private void ToggleSidebar_Click(object sender, RoutedEventArgs e)
        {
            double targetWidth = sidebarOpen ? 0 : 56;

            var anim = new DoubleAnimation
            {
                To = targetWidth,
                Duration = TimeSpan.FromMilliseconds(180),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
            };

            Sidebar.BeginAnimation(FrameworkElement.WidthProperty, anim);
            ToggleSidebarBtn.Content = sidebarOpen ? "❮" : "❯";
            sidebarOpen = !sidebarOpen;
        }

        private void AddressBar_LostFocus(object sender, RoutedEventArgs e)
        {
            if (CommandList != null && CommandList.IsKeyboardFocusWithin)
                return;

            HideCommandSuggestions();
        }

        void RefreshCommandSuggestions()
        {
            if (CommandList == null)
                return;

            CommandList.ItemsSource =
                _settings.Settings.Commands
                    .Where(c => c.Enabled)
                    .ToList();
        }

        private void CommandSuggestions_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && CommandList.SelectedItem != null)
            {
                if (CommandList.SelectedItem is CommandSetting cmd)
                {
                    ExecuteCommand(cmd.Key);
                    HideCommandSuggestions();
                    AddressBar.Focus();
                    e.Handled = true;
                }
            }

            if (e.Key == Key.Escape)
            {
                HideCommandSuggestions();
                AddressBar.Focus();
                e.Handled = true;
            }
        }

        private void AddressBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TextBox tb)
                return;

            // si le popup est ouvert et qu'on reclique dans la barre → fermer
            if (CommandSuggestionsPopup?.IsOpen == true &&
                !CommandSuggestionsPopup.IsMouseOver)
            {
                HideCommandSuggestions();
                e.Handled = false;
                return;
            }

            if (!tb.IsKeyboardFocusWithin)
            {
                tb.Focus();
                e.Handled = true;
            }
        }


        private void AddressBar_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            if (sender is not TextBox tb)
                return;

            tb.SelectAll();
            _addressBarSelectAllPending = false;

            ShowCommandSuggestions();
        }

        // ---------------------------
        // Preview (stable, SANS CRASH)
        // ---------------------------
        static void AttachPreview(TabItem tab, WebView2 web)
        {
            if (tab.ToolTip != null)
                return;

            var border = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6),
                Width = 320,
                Height = 200
            };

            tab.ToolTip = border;

            tab.MouseEnter += async (_, _) =>
            {
                if (web.CoreWebView2 == null)
                    return;

                // ⚠️ Image TOUJOURS recréée (clé du fix)
                var preview = new Image
                {
                    Width = 320,
                    Height = 200,
                    Stretch = Stretch.UniformToFill
                };

                using var stream = new MemoryStream();
                await web.CoreWebView2.CapturePreviewAsync(
                    Microsoft.Web.WebView2.Core.CoreWebView2CapturePreviewImageFormat.Png,
                    stream
                );

                stream.Position = 0;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.StreamSource = stream;
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.EndInit();
                bmp.Freeze(); // 🔒 thread-safe + perf

                preview.Source = bmp;

                // 🔁 remplacement SAFE (pas de parent fantôme)
                border.Child = preview;
            };
        }


        // ---------------------------
        // Settings button handler
        // ---------------------------
        private void OpenSettings_Click(object sender, RoutedEventArgs e)
            => OpenSettings();

        // ---------------------------
        // Detach / Redock
        // ---------------------------
        void DetachTab(TabItem tab)
        {
            if (tab.Tag is not WebTabContent state)
                return;

            var web = state.Web;
            bool wasSelected = Equals(Tabs.SelectedItem, tab);

            Tabs.Items.Remove(tab);

            BeginDockingMode();

            var win = new DetachedWindow(this, web);

            win.Closed += (_, _) => EndDockingMode();
            win.RequestRedock += RedockWebView;

            POINT p;
            GetCursorPos(out p);

            win.Left = p.X - 100;
            win.Top = p.Y - 10;

            win.Show();

            if (Tabs.Items.Count == 0)
                return;

            if (wasSelected)
            {
                Tabs.SelectedIndex = 0;
                SyncWebHostWithSelection();
            }
        }


        async Task HandleFlashAsync(
     WebView2 web,
     WebTabContent content,
     BrowserTabHeader header,
     FlashUxOverlay overlay)
        {
            if (web.Source == null || content.FlashService == null)
                return;

            var uri = web.Source;

            // ===============================
            // 1️⃣ HEURISTIQUE RAPIDE (SILENCIEUSE)
            // ===============================
            bool flashSuspected =
                await content.FlashService.DetectFlashRequirementAsync();

            content.FlashRequired = flashSuspected;

            // 👉 bouton OK, PAS d’overlay
            Dispatcher.Invoke(() => UpdateFlashModeButton(content));

            // ===============================
            // 2️⃣ DÉTECTION DOM RÉELLE (SEULE SOURCE DE VÉRITÉ)
            // ===============================
            bool hasFlashDom =
                await content.FlashService.DetectFlashDomAsync();

            if (!hasFlashDom)
                return; // ⛔ PAS DE FLASH → RIEN

            content.FlashRequired = true;

            // ===============================
            // 3️⃣ VÉRIFICATION CAPACITÉ
            // ===============================
            if (!CanUseFlashOrLegacy(uri, hasFlashDom, out var reason))
            {
                Dispatcher.Invoke(() =>
                {
                    overlay.ShowBlocked(reason);
                });

                overlay.OpenSettingsRequested += OpenSettings;
                return;
            }

            // ===============================
            // 4️⃣ DÉCISION
            // ===============================
            var mode = content.FlashService.DecideInitialMode(uri);
            content.FlashMode = mode;

            // -------------------------------
            // LEGACY
            // -------------------------------
            if (mode == FlashMode.Legacy)
            {
                Dispatcher.Invoke(() =>
                {
                    overlay.TryShow("Flash réel requis → ouverture en mode Legacy…");
                });

                var profile = LegacyProfileManager.GetProfileForDomain(uri.Host);
                var p = _legacyLauncher.Launch(uri.AbsoluteUri, profile);

                content.IsLegacyExternal = true;
                content.LegacyUrl = uri.AbsoluteUri;
                content.LegacyPid = p?.Id;

                header.SetTitle("Legacy Flash");
                SyncWebHostWithSelection();
                return;
            }

            // -------------------------------
            // RUFFLE
            // -------------------------------
            Dispatcher.Invoke(() =>
            {
                overlay.TryShow("Flash détecté → émulation Ruffle…");
            });

            content.FlashMode = FlashMode.Ruffle;

            await RuffleInjector.InjectAsync(web);

            content.RuffleMonitor?.Stop();
            content.RuffleMonitor = new RuffleMonitor(web);

            content.RuffleMonitor.FailureDetected += _ =>
            {
                Dispatcher.Invoke(() =>
                {
                    overlay.TryShow("Ruffle a échoué → bascule en mode Legacy…");

                    var profile = LegacyProfileManager.GetProfileForDomain(uri.Host);
                    var p = _legacyLauncher.Launch(uri.AbsoluteUri, profile);

                    content.IsLegacyExternal = true;
                    content.LegacyUrl = uri.AbsoluteUri;
                    content.LegacyPid = p?.Id;
                    content.FlashMode = FlashMode.Legacy;

                    header.SetTitle("Legacy Flash");
                    SyncWebHostWithSelection();
                });
            };

            content.RuffleMonitor.Start();
        }


        public void UpdateDownloadsBadge()
        {
            var count = DownloadManager.Instance.Items
                .Count(d => d.IsInProgress);

            if (count > 0)
            {
                DownloadsBadge.Visibility = Visibility.Visible;
                DownloadsBadgeText.Text = count.ToString();
            }
            else
            {
                DownloadsBadge.Visibility = Visibility.Collapsed;
            }
        }

        private void ClearCompletedDownloads(object sender, RoutedEventArgs e)
        {
            var toRemove = DownloadManager.Instance.Items
                .Where(d => d.IsCompleted)
                .ToList();

            foreach (var d in toRemove)
                DownloadManager.Instance.Remove(d);

            UpdateDownloadsBadge();
        }


        private void DownloadsBtn_Click(object sender, RoutedEventArgs e)
    {
        DownloadsPopup.IsOpen = !DownloadsPopup.IsOpen;
    }

    private void DownloadItem_Open(object sender, MouseButtonEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItem it)
            DownloadManager.Instance.OpenFile(it);
    }
        private void DownloadItem_Remove(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is DownloadItem it)
                DownloadManager.Instance.Remove(it);
            UpdateDownloadsBadge();
        }


        private void DownloadItem_OpenFolder(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItem it)
            DownloadManager.Instance.OpenContainingFolder(it);
    }

    private void DownloadItem_Pause(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItem it)
            DownloadManager.Instance.Pause(it);
    }

    private void DownloadItem_ResumeOrRetry(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItem it)
            DownloadManager.Instance.Retry(it);
    }

    private void DownloadItem_Cancel(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is DownloadItem it)
            DownloadManager.Instance.Cancel(it);
    }


    void ApplyPrivateTheme(bool isPrivate)
        {
            Resources["FluentSurface"] =
                isPrivate
                    ? Resources["PrivateSurfaceBrush"]
                    : Resources["NormalSurfaceBrush"];

            AddressBar.Background =
                isPrivate
                    ? (Brush)Resources["PrivateAddressBrush"]
                    : new SolidColorBrush(Color.FromRgb(72, 68, 68));
        }

        bool HasAnyPrivateTab()
        {
            return Tabs.Items
                .OfType<TabItem>()
                .Any(t => t.Tag is WebTabContent w && w.IsPrivate);
        }
        void AttachNewWindowHandling(WebView2 web)
        {
            web.CoreWebView2.NewWindowRequested += (s, e) =>
            {
                e.Handled = true;

                Dispatcher.Invoke(() =>
                {
                    // ouverture DANS UN NOUVEL ONGLET
                    CreateTab(e.Uri);
                });
            };
        }

        void RedockWebView(WebView2 web)
        {
            DownloadHook.Attach(web, isPrivate: false);

            Dispatcher.Invoke(() =>
            {
                EndDockingMode();

                var header = new BrowserTabHeader();
                header.SetTitle(web.CoreWebView2?.DocumentTitle ?? "Onglet");

                if (!string.IsNullOrEmpty(web.CoreWebView2?.FaviconUri))
                    header.SetIcon(new BitmapImage(new Uri(web.CoreWebView2.FaviconUri)));

                var state = new WebTabContent
                {
                    Web = web,
                    IsPinned = false,
                    IsSuspended = false,
                    LastActivated = DateTime.Now
                };

                var tab = new TabItem
                {
                    Header = header,
                    Tag = state
                };

                header.CloseRequested += () => CloseTab(tab);

                header.DetachRequested += () =>
                {
                    if (_isDocking)
                    {
                        header.ResetVisualState();
                        return;
                    }
                    DetachTab(tab);
                };

                header.PinRequested += () =>
                {
                    state.IsPinned = !state.IsPinned;
                    ApplyPinState(tab, header, state.IsPinned);
                };

                header.ReorderRequested += dir => ReorderTab(tab, dir);

                Tabs.Items.Add(tab);
                Tabs.SelectedItem = tab;
                SyncWebHostWithSelection();
            });
        }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT lpPoint);

        // ===============================
        // CONTENU D’ONGLET (BASE PROPRE)
        // ===============================
        abstract class TabContent { }

        class WebTabContent : TabContent
        {
            public WebView2 Web { get; init; } = null!;
            public bool IsPinned { get; set; }
            public bool IsSuspended { get; set; }
            public bool IsPrivate { get; set; }
            public DateTime LastActivated { get; set; } = DateTime.Now;

            // FLASH
            public FlashDecisionService? FlashService { get; set; }
            public FlashMode FlashMode { get; set; } = FlashMode.None;
            public bool FlashRequired { get; set; }


            // Legacy proxy
            public bool IsLegacyExternal { get; set; }
            public string? LegacyUrl { get; set; }
            public int? LegacyPid { get; set; }

            // Ruffle monitor (PLUS DE ref)
            public RuffleMonitor? RuffleMonitor { get; set; }

            public FlashUxOverlay? FlashOverlay { get; set; }
        }




        class ViewTabContent : TabContent
        {
            public UserControl View { get; init; } = null!;
        }
    }
}
