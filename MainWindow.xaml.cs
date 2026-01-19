using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.controles;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
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
        private IntPtr _legacyDockHwnd = IntPtr.Zero;
        private FrameworkElement? _legacyDockTarget = null;

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
        FlashUxOverlay? _activeOverlay;
        private DispatcherTimer? _dockTimer;
        private int _dockTicksLeft;
        private int _dockPid;
        private FrameworkElement? _dockTarget;
        private WebTabContent? _retryContent;
        private Uri? _retryUri;
        private BrowserTabHeader? _retryHeader;
        private FlashUxOverlay? _retryOverlay;



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
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
        [DllImport("user32.dll")] static extern bool MoveWindow(IntPtr hWnd, int X, int Y, int nWidth, int nHeight, bool bRepaint);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
        [DllImport("user32.dll")] static extern int GetWindowLong(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll")] static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        private int _legacyDockPid = 0;
        private DispatcherTimer? _legacyDockTimer;
        private int _legacyDockTicksLeft = 0;

        const int SW_HIDE = 0;
        const int SW_SHOW = 5;

        const int GWL_STYLE = -16;
        const int WS_CAPTION = 0x00C00000;
        const int WS_THICKFRAME = 0x00040000;
        const int WS_SYSMENU = 0x00080000;
        const int WS_MINIMIZEBOX = 0x00020000;
        const int WS_MAXIMIZEBOX = 0x00010000;
        const int GWL_EXSTYLE = -20;
        const int WS_EX_APPWINDOW = 0x00040000;
        const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
            int X, int Y, int cx, int cy, uint uFlags);

        const uint SWP_NOZORDER = 0x0004;
        const uint SWP_FRAMECHANGED = 0x0020;
        const uint SWP_SHOWWINDOW = 0x0040;


        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }


        [DllImport("user32.dll")]
        static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);




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

        async Task<bool> OpenLegacyBasiliskTabAsync(Uri url)
        {
            Debug.WriteLine("OpenLegacyBasiliskTabAsync ENTER");

            var view = new controles.BasiliskHostView();
            view.SetUrl(url.AbsoluteUri);

            var header = new BrowserTabHeader();
            header.SetTitle(url.Host);

            Process? proc = null;

            var tab = new TabItem
            {
                Header = header,
                Tag = new ViewTabContent { View = view }
            };

            header.CloseRequested += () =>
            {
                try { view.DetachExternalWindow(); } catch { }

                if (proc != null)
                {
                    try { if (!proc.HasExited) proc.Kill(); }
                    catch { }
                }

                CloseTab(tab);
            };

            view.SettingsRequested += OpenSettings;

            view.RetryRequested += async () =>
            {
               Debug.WriteLine("About to call TryAttachBasiliskToViewAsync");

                view.SetStatus("⏳ Nouvelle tentative…");
                var r = await TryAttachBasiliskToViewAsync(url, view, header);
                proc = r.proc;

                if (!r.ok)
                    view.ShowOverlay(true);
            };

            Tabs.Items.Add(tab);
            Tabs.SelectedItem = tab;
            await Dispatcher.InvokeAsync(() =>
            {
                Tabs.SelectedItem = tab;
                SyncWebHostWithSelection();
            }, DispatcherPriority.Loaded);


            var result = await TryAttachBasiliskToViewAsync(url, view, header);
            proc = result.proc;

            return result.ok;
        }


        

        async Task<(bool ok, Process? proc)> TryAttachBasiliskToViewAsync(
    Uri url,
    controles.BasiliskHostView view,
    BrowserTabHeader header)
        {
            Debug.WriteLine("[LEGACY] TryAttachBasiliskToViewAsync ENTER " + url);

            if (!_legacyLauncher.CanLaunch())
            {
                view.SetStatus("❌ Basilisk non configuré (chemin invalide).");
                view.ShowOverlay(true);
                return (false, null);
            }

            view.ShowOverlay(true);
            view.SetStatus("⏳ Lancement de Basilisk…");

            try
            {
                var profile = LegacyProfileManager.GetProfileForDomain(url.Host);
                var proc = _legacyLauncher.Launch(url.AbsoluteUri, profile);
                Debug.WriteLine("[LEGACY] Launch() done, proc=" + (proc?.Id.ToString() ?? "null"));

                if (proc == null)
                {
                    view.SetStatus("❌ Échec lancement (Launch() a retourné null).");
                    return (false, null);
                }

                // ✅ s'assurer que la vue est affichée (sinon AttachExternalWindow trop tôt)
                await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);

                // ✅ attendre top-level + choisir le meilleur child à embed si possible
                var (top, embedCandidate) = await WaitForBasiliskWindowsAsync(proc, timeoutMs: 12000);
                if (top == IntPtr.Zero)
                {
                    view.SetStatus("❌ Fenêtre Basilisk introuvable (timeout).");
                    view.ShowOverlay(true);
                    return (false, proc);
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    view.AttachExternalWindow(top);
                }, DispatcherPriority.Loaded);

                await Dispatcher.InvokeAsync(() =>
                {
                    view.ShowOverlay(true);
                    view.SetStatus("⏳ Dock en cours…");
                }, DispatcherPriority.Loaded);

                for (int i = 0; i < 30; i++) // ~3s
                {
                    await Task.Delay(100);

                    await Dispatcher.InvokeAsync(() =>
                    {
                        var host = view.GetHost();
                        host?.RefreshLayout();
                    }, DispatcherPriority.Loaded);

                    // ✅ si le host a bien un handle + un hwnd docké, on considère OK
                    var ok = false;
                    await Dispatcher.InvokeAsync(() =>
                    {
                        var host = view.GetHost();
                        ok = host != null && host.HostHandle != IntPtr.Zero && host.DockedHwnd != IntPtr.Zero;
                    });

                    if (ok)
                        break;
                }

                await Dispatcher.InvokeAsync(() =>
                {
                    view.ShowOverlay(false);
                }, DispatcherPriority.Loaded);



                header.SetTitle(url.Host);
                view.SetStatus($"✅ Basilisk docké (PID {proc.Id})");
                view.ShowOverlay(false);

                return (true, proc);
            }
            catch (Exception ex)
            {
                view.SetStatus("❌ Échec Basilisk : " + ex.Message);
                view.ShowOverlay(true);
                return (false, null);
            }
        }





        [DllImport("user32.dll", SetLastError = true)]
        static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);


        [DllImport("user32.dll", SetLastError = true)]
        static extern bool IsWindowVisible(IntPtr hWnd);

        private static IntPtr FindTopLevelWindowForPid(int pid)
        {
            IntPtr found = IntPtr.Zero;

            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var winPid);
                if (winPid != (uint)pid) return true;
                if (!IsWindowVisible(h)) return true;

                found = h;
                return false;
            }, IntPtr.Zero);

            return found;
        }

        

        private void MainWindow_DockTick(object? sender, EventArgs e)
        {
            RefreshLegacyDock();
        }

        

        void DockBasiliskWindowToTarget(IntPtr hwnd, FrameworkElement target)
        {
            if (hwnd == IntPtr.Zero) return;

            const int GWL_STYLE = -16;

            const int WS_CAPTION = 0x00C00000;
            const int WS_THICKFRAME = 0x00040000;
            const int WS_BORDER = 0x00800000;

            const int WS_POPUP = unchecked((int)0x80000000);
            const int WS_CHILD = 0x40000000;

            int style = GetWindowLong(hwnd, GWL_STYLE);

            // Enlever décorations + mode popup
            style &= ~WS_CAPTION;
            style &= ~WS_THICKFRAME;
            style &= ~WS_BORDER;
            style &= ~WS_POPUP;

            // Forcer child (embed)
            style |= WS_CHILD;

            SetWindowLong(hwnd, GWL_STYLE, style);

            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
        }




        private void StartLegacyDock(int pid, FrameworkElement target)
        {
            _legacyDockPid = pid;
            _legacyDockTarget = target;
            _legacyDockHwnd = IntPtr.Zero;

            // Hook global (une fois, idempotent)
            LocationChanged -= MainWindow_LegacyDockTick;
            SizeChanged -= MainWindow_LegacyDockTick;
            StateChanged -= MainWindow_LegacyDockTick;

            LocationChanged += MainWindow_LegacyDockTick;
            SizeChanged += MainWindow_LegacyDockTick;
            StateChanged += MainWindow_LegacyDockTick;

            // Timer court : Gecko rebouge la fenêtre au démarrage
            _legacyDockTicksLeft = 40; // ~2s @50ms
            _legacyDockTimer?.Stop();
            _legacyDockTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
            _legacyDockTimer.Tick += (_, _) =>
            {
                if (_legacyDockTicksLeft-- <= 0)
                {
                    _legacyDockTimer?.Stop();
                    return;
                }
                RefreshLegacyDock();
            };
            _legacyDockTimer.Start();

            // Premier refresh immédiat
            RefreshLegacyDock();
        }



        private void RefreshLegacyDock()
        {
            if (_legacyDockTarget == null)
                return;

            if (WindowState == WindowState.Minimized || !_legacyDockTarget.IsVisible)
            {
                if (_legacyDockHwnd != IntPtr.Zero)
                    ShowWindow(_legacyDockHwnd, SW_HIDE);
                return;
            }

            // ✅ on garde le handle stable
            if (_legacyDockHwnd == IntPtr.Zero)
            {
                // dernier recours uniquement
                if (_legacyDockPid > 0)
                    _legacyDockHwnd = FindTopLevelWindowForPid(_legacyDockPid);
            }

            if (_legacyDockHwnd == IntPtr.Zero)
                return;

            DockBasiliskWindowToTarget(_legacyDockHwnd, _legacyDockTarget);
            ShowWindow(_legacyDockHwnd, SW_SHOW);
        }



        private void MainWindow_LegacyDockTick(object? sender, EventArgs e)
        {
            RefreshLegacyDock();
        }





        void UpdateFlashModeButton(WebTabContent content)
        {
            // 🔁 Forcer le recalcul quand on revient sur l’onglet
            content.LastFlashButtonVisible = null;
            content.LastFlashMode = null;

            // ⛔ Sécurité : bouton valable UNIQUEMENT pour l’onglet actif
            if (Tabs.SelectedItem is not TabItem sel ||
                !ReferenceEquals(sel.Tag, content))
            {
                SetFlashButton(false);
                return;
            }
            if (content?.Web?.Source == null)
            {
                SetFlashButton(false);
                return;
            }

            var uri = content.Web.Source;
            var rule = FlashDomainRules.GetRule(uri);

            bool flashRequired = content.FlashRequired;
            bool shouldBeVisible = flashRequired;

            var mode =
                content.FlashMode == FlashMode.Legacy
                    ? FlashMode.Legacy
                    : rule == FlashRuleMode.Legacy
                        ? FlashMode.Legacy
                        : FlashMode.None;

            // ⛔ NE RIEN FAIRE si l'état n'a pas changé
            if (content.LastFlashButtonVisible == shouldBeVisible &&
                content.LastFlashMode == mode)
                return;

            content.LastFlashButtonVisible = shouldBeVisible;
            content.LastFlashMode = mode;

            SetFlashButton(shouldBeVisible, mode, content);
        }



        void SetFlashButton(bool visible, FlashMode mode = FlashMode.Ruffle, WebTabContent content = null)
        {
            FlashModeBtn.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

            if (!visible || content == null)
                return;

            FlashModeBtn.Content = mode == FlashMode.Legacy ? "⚡ Legacy" : "⚡ Auto";

            bool legacyAvailable = _legacyLauncher.CanLaunch();

            FlashModeBtn.IsEnabled =
                content.FlashMode != FlashMode.Legacy || legacyAvailable;

            FlashModeBtn.ToolTip =
                !legacyAvailable && content.FlashMode == FlashMode.Legacy
                    ? "Basilisk n’est pas configuré"
                    : "Changer le mode Flash pour ce site";

        }




        private void FlashModeBtn_Click(object sender, RoutedEventArgs e)
        {
            if (Tabs.SelectedItem is not TabItem tab ||
                tab.Tag is not WebTabContent content ||
                content.Web?.Source == null)
                return;

            var uri = content.Web.Source;
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

            overlay.TryShow("Mode Flash Legacy forcé.");

            // ✅ PAS DE Navigate() !!!!!
            // ✅ On bascule directement l’onglet en legacy interne
            content.FlashMode = FlashMode.Legacy;
            content.IsLegacyExternal = true;
            content.IsLegacyLaunching = true;

            // ✅ si tu as déjà un ancien Basilisk encore docké, cache-le avant
            if (content.LegacyHwnd != IntPtr.Zero)
                ShowWindow(content.LegacyHwnd, SW_HIDE);

            // ✅ lance le legacy interne (celui qui marche déjà chez toi)
            _ = LaunchLegacyIntoInternalTabAsync(content, uri, (BrowserTabHeader)tab.Header);

            // refresh UI immédiat
            SyncWebHostWithSelection();


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
            // 🔒 Initialisation STABLE du host
            content.HostGrid.Children.Add(web);

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
                            // on remet l'état à "inconnu / pas encore décidé"
                            content.FlashRequired = false;

                            // cache immédiatement le bouton (pas de flicker)
                            UpdateFlashModeButton(content);

                            if (Tabs.SelectedItem == tab)
                            {
                                overlay.Hide(); // reset visuel
                            }


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
            // 🔒 Initialisation STABLE du host
            content.HostGrid.Children.Add(web);

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
        private void HideLegacyWindowsExcept(WebTabContent? keep)
        {
            foreach (var item in Tabs.Items)
            {
                if (item is not TabItem ti) continue;
                if (ti.Tag is not WebTabContent wt) continue;

                // ✅ Ne pas toucher à l'onglet actif legacy
                if (keep != null && ReferenceEquals(wt, keep))
                    continue;

                // ✅ On cache uniquement les fenêtres legacy des autres onglets
                if (wt.LegacyHwnd != IntPtr.Zero)
                    ShowWindow(wt.LegacyHwnd, SW_HIDE);

                if (wt.LegacyTopHwnd != IntPtr.Zero && wt.LegacyTopHwnd == wt.LegacyHwnd)
                    ShowWindow(wt.LegacyTopHwnd, SW_HIDE);
            }
        }


        void SyncWebHostWithSelection()
        {
            _activeOverlay?.DetachVisualOnly();
            _activeOverlay = null;

            FlashModeBtn.Visibility = Visibility.Collapsed;
            FlashModeBtn.Tag = null;

            if (Tabs.SelectedItem is not TabItem tab)
            {
                WebHost.Content = null;
                PrivateIndicator.Visibility = Visibility.Collapsed;
                FlashModeBtn.Visibility = Visibility.Collapsed;
                return;
            }

            // =========================================================
            // ✅ CAS 1 : ONGLET WEBVIEW (WebTabContent)
            // =========================================================
            if (tab.Tag is WebTabContent webTab)
            {
                PrivateIndicator.Visibility = webTab.IsPrivate ? Visibility.Visible : Visibility.Collapsed;

                ApplyPrivateTheme(webTab.IsPrivate);
                AnimatePrivateTransition();

                AddressBar.Background = webTab.IsPrivate
                    ? new SolidColorBrush(Color.FromRgb(70, 40, 90))
                    : new SolidColorBrush(Color.FromRgb(72, 68, 68));

                webTab.LastActivated = DateTime.Now;

                WebHost.Content = webTab.HostGrid;

                // ✅ Cache les autres fenêtres Basilisk (mais ne reparent plus jamais)
                HideLegacyWindowsExcept(webTab);

                // ✅ overlay toujours attaché au host de l'onglet
                webTab.FlashOverlay?.BindHost(webTab.HostGrid);

                // ---------------------------
                // LEGACY interne
                // ---------------------------
                if (webTab.IsLegacyExternal || webTab.IsLegacyLaunching)
                {
                    if (webTab.LegacyView == null)
                    {
                        var view = new LegacyFlashView();

                        view.SettingsRequested += OpenSettings;
                        view.RetryRequested += () =>
                        {
                            if (webTab.Web.Source == null) return;
                            _ = LaunchLegacyIntoInternalTabAsync(
                                webTab,
                                webTab.Web.Source,
                                (BrowserTabHeader)tab.Header
                            );
                        };

                        webTab.LegacyView = view;
                    }

                    var legacyView = webTab.LegacyView;

                    legacyView.SetUrl(webTab.LegacyUrl ?? webTab.Web.Source?.AbsoluteUri ?? "");

                    if (webTab.IsLegacyLaunching)
                        legacyView.SetLaunching();
                    else if (webTab.LegacyPid.HasValue)
                        legacyView.SetLaunched(webTab.LegacyPid.Value);

                    if (!string.IsNullOrWhiteSpace(webTab.LegacyLastError))
                        legacyView.SetError(webTab.LegacyLastError);

                    // ✅ Si on a un handle Basilisk -> DockOverlay (pas embed)
                    if (webTab.LegacyHwnd != IntPtr.Zero)
                    {
                        webTab.LegacyHost ??= new MyHomelabBrowser.controles.ExternalWindowDock();

                        // Le DockControl doit être présent dans le HostGrid
                        if (webTab.HostGrid.Children.Count != 1 ||
                            !ReferenceEquals(webTab.HostGrid.Children[0], webTab.LegacyHost))
                        {
                            webTab.HostGrid.Children.Clear();
                            webTab.HostGrid.Children.Add(webTab.LegacyHost);
                        }

                        // ✅ On bind la fenêtre Basilisk et on la positionne sur la zone
                        webTab.LegacyHost.Bind(webTab.LegacyHwnd);
                        webTab.LegacyHost.ShowDock();

                        // ✅ Forcer une mise à jour après layout WPF
                        Dispatcher.BeginInvoke(new Action(() =>
                        {
                            webTab.LegacyHost.UpdateDockPosition();
                        }), DispatcherPriority.Loaded);
                    }
                    else
                    {
                        // ✅ Ici on montre juste la vue "Launching/Retry"
                        if (webTab.HostGrid.Children.Count != 1 || !ReferenceEquals(webTab.HostGrid.Children[0], legacyView))
                        {
                            webTab.HostGrid.Children.Clear();
                            webTab.HostGrid.Children.Add(legacyView);
                        }
                    }

                    webTab.FlashOverlay?.Hide();
                }
                else if (webTab.IsSuspended)
                {
                    webTab.HostGrid.Children.Clear();
                    webTab.HostGrid.Children.Add(CreateSuspendedPlaceholder(tab, webTab));
                    webTab.FlashOverlay?.Hide();
                }
                else
                {
                    // Web normal
                    webTab.HostGrid.Children.Clear();
                    webTab.HostGrid.Children.Add(webTab.Web);
                }

                // ---------------------------
                // FLASH UI
                // ---------------------------
                webTab.LastFlashButtonVisible = null;
                webTab.LastFlashMode = null;

                UpdateFlashModeButton(webTab);

                bool shouldShowFlashPopup =
                    webTab.FlashRequired &&
                    !webTab.IsLegacyExternal &&
                    !webTab.IsLegacyLaunching;

                if (shouldShowFlashPopup)
                {
                    var overlay = webTab.FlashOverlay;
                    if (overlay != null)
                    {
                        overlay.InvalidateLayout();
                        overlay.TryShow("Ce site nécessite Flash. Choisissez un mode.");
                    }
                }
                else
                {
                    webTab.FlashOverlay?.Hide();
                }

                UpdateFavoriteButton();
                return;
            }

            // =========================================================
            // ✅ CAS 2 : ONGLET "VIEW" (Settings / History / BasiliskHostView)
            // =========================================================
            if (tab.Tag is ViewTabContent viewTab)
            {
                PrivateIndicator.Visibility = Visibility.Collapsed;

                ApplyPrivateTheme(false);
                AnimatePrivateTransition();

                AddressBar.Background = new SolidColorBrush(Color.FromRgb(72, 68, 68));

                WebHost.Content = viewTab.View;

                UpdateAddressBarFromTab();
                FlashModeBtn.Visibility = Visibility.Collapsed;

                UpdateFavoriteButton();
                return;
            }

            WebHost.Content = null;
            PrivateIndicator.Visibility = Visibility.Collapsed;
            FlashModeBtn.Visibility = Visibility.Collapsed;
            UpdateFavoriteButton();
        }



        private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // ✅ 1) Onglet qu’on quitte
            foreach (var removed in e.RemovedItems)
            {
                if (removed is not TabItem oldTab)
                    continue;

                // WebView2 overlay
                if (oldTab.Tag is WebTabContent oldState)
                {
                    oldState.FlashOverlay?.DeactivateTabVisuals();

                    // ✅ Legacy DockOverlay : cacher Basilisk quand on quitte l’onglet
                    if (oldState.LegacyHost != null)
                        oldState.LegacyHost.HideDock();
                    else if (oldState.LegacyHwnd != IntPtr.Zero)
                        ShowWindow(oldState.LegacyHwnd, SW_HIDE);
                }
            }

            // ✅ 2) Mettre à jour la vue sélectionnée
            if (Tabs.SelectedItem is TabItem tab)
            {
                SyncWebHostWithSelection();
                SyncAddressBarWithTab(tab);

                // ✅ Si onglet legacy : show + reposition + bring to front
                if (tab.Tag is WebTabContent wt &&
                    wt.LegacyHwnd != IntPtr.Zero &&
                    wt.LegacyHost != null &&
                    (wt.IsLegacyExternal || wt.IsLegacyLaunching))
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        wt.LegacyHost.ShowDock();
                        wt.LegacyHost.UpdateDockPosition(); // doit contenir SetWindowPos(HWND_TOP,...)
                    }), DispatcherPriority.Loaded);
                }
            }

            // ✅ 3) Dock externe (ancienne feature) -> tu peux laisser, mais ce n'est pas lié au legacy interne
            if (Tabs.SelectedItem is TabItem ti &&
                ti.Tag is ViewTabContent vtc &&
                vtc.View is MyHomelabBrowser.controles.BasiliskHostView)
            {
                RefreshLegacyDock();
            }
            else
            {
                if (_legacyDockHwnd != IntPtr.Zero)
                    ShowWindow(_legacyDockHwnd, SW_HIDE);
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
        private IntPtr FindBestEmbedChild(IntPtr top)
        {
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;

            void Scan(IntPtr parent)
            {
                EnumChildWindows(parent, (h, _) =>
                {
                    if (!IsWindowVisible(h)) return true;

                    if (GetWindowRect(h, out var rc))
                    {
                        int w = rc.Right - rc.Left;
                        int hgt = rc.Bottom - rc.Top;

                        if (w > 10 && hgt > 10)
                        {
                            long area = (long)w * hgt;

                            if (area > bestArea)
                            {
                                bestArea = area;
                                best = h;
                            }
                        }
                    }

                    // ✅ descend dans tous les enfants
                    Scan(h);

                    return true;
                }, IntPtr.Zero);
            }

            Scan(top);
            return best;
        }



        void CloseTab(TabItem tab)
        {
            bool wasPrivate =
                tab.Tag is WebTabContent w && w.IsPrivate;

            bool wasSelected = Equals(Tabs.SelectedItem, tab);
            if (tab.Tag is WebTabContent wr && wr.IsLegacyExternal)
            {
                try
                {
                    try { LegacyLauncher.KillAllBasiliskProcesses(); } catch { }
                    

                }
                catch { }
            }

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
                // ✅ FIX CRITIQUE : cacher l’overlay de l’ancienne page
                state.FlashOverlay?.Hide();

                // 🔥 Reset état Flash pour la nouvelle page
                state.FlashRequired = false;
                state.FlashMode = FlashMode.None;

                UpdateFlashModeButton(state);

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

            if (ReferenceEquals(WebHost.Content, web))
                WebHost.Content = null;

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
        bool IsActiveTab(WebTabContent c) =>
    Tabs.SelectedItem is TabItem t && ReferenceEquals(t.Tag, c);

        private async Task<bool> TryLaunchLegacy(
     WebTabContent content,
     Uri uri,
     BrowserTabHeader header)
        {
            try
            {
                if (!_legacyLauncher.CanLaunch())
                {
                    content.LegacyLastError = "Basilisk n’est pas configuré ou chemin invalide.";
                    return false;
                }

                return await LaunchLegacyIntoInternalTabAsync(content, uri, header);
            }
            catch (Exception ex)
            {
                content.LegacyLastError = ex.ToString();
                return false;
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
            
            // ✅ règle user
            var rule = FlashDomainRules.GetRule(uri);
            bool forcedLegacy = (rule == FlashRuleMode.Legacy);

            // -------------------------------------------------------
            // ✅ helper : UN SEUL pipeline Legacy (réutilisé partout)
            // -------------------------------------------------------
            void LaunchLegacySameWayAsFallback()
            {
                // Basilisk pas configuré
                if (!_legacyLauncher.CanLaunch())
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (!IsActiveTab(content)) return;

                        overlay.BindHost(content.HostGrid);
                        overlay.ShowBlocked("Flash réel requis, mais Basilisk n’est pas configuré.");
                        overlay.SetActions("Configurer", OpenSettings);
                    });
                    return;
                }

                // garder les infos pour retry
                _retryContent = content;
                _retryUri = uri;
                _retryHeader = header;
                _retryOverlay = overlay;

                Dispatcher.Invoke(() =>
                {
                    if (!IsActiveTab(content)) return;
                    overlay.BindHost(content.HostGrid);
                    overlay.TryShow("Mode Flash Legacy → lancement Basilisk…");
                    UpdateFlashModeButton(content);
                });

                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    bool ok;
                    try
                    {

                        ok = await TryLaunchLegacy(content, uri, header);
                    }
                    catch (Exception ex)
                    {
                        Dispatcher.Invoke(() =>
                        {
                            if (!IsActiveTab(content)) return;
                            overlay.ShowBlocked("Échec Basilisk :\n" + ex);
                            overlay.SetActions("Paramètres", OpenSettings);
                        });
                        return;
                    }

                    Dispatcher.Invoke(() =>
                    {
                        if (!IsActiveTab(content)) return;

                        if (ok)
                        {
                            overlay.Hide();
                            SyncWebHostWithSelection();
                        }
                        else
                        {
                            overlay.ShowBlocked(
                                "Impossible de lancer Basilisk.\n\n" +
                                (content.LegacyLastError ?? "Erreur inconnue.")
                            );
                            overlay.SetActions("Réessayer", RetryLegacyFromOverlay, "Paramètres", OpenSettings);
                        }
                    });
                });
            }

            // =======================================================
            // ✅ CAS 0 : LEGACY FORCÉ -> BYPASS TOTAL + même pipeline
            // =======================================================
            if (forcedLegacy)
            {
                // Basilisk pas configuré
                if (!_legacyLauncher.CanLaunch())
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (!IsActiveTab(content)) return;
                        overlay.BindHost(content.HostGrid);
                        overlay.ShowBlocked("Flash réel requis, mais Basilisk n’est pas configuré.");
                        overlay.SetActions("Configurer", OpenSettings);
                    });
                    return;
                }

                // ✅ on ne fait PAS de dock overlay : on utilise le tab BasiliskHostView (dock réel)
                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsActiveTab(content)) return;
                    overlay.Hide();
                });

                // ✅ ouvre l’onglet Basilisk interne (embed)
                bool ok = await OpenLegacyBasiliskTabAsync(uri);

                await Dispatcher.InvokeAsync(() =>
                {
                    if (!IsActiveTab(content)) return;

                    if (ok)
                    {
                        // ✅ ferme l'onglet flash source pour éviter 2 onglets
                        if (Tabs.SelectedItem is TabItem currentTab && ReferenceEquals(currentTab.Tag, content))
                            CloseTab(currentTab);
                    }
                    else
                    {
                        overlay.BindHost(content.HostGrid);
                        overlay.ShowBlocked("Impossible de lancer Basilisk.");
                        overlay.SetActions("Réessayer", () => Navigate(uri.AbsoluteUri), "Paramètres", OpenSettings);
                    }
                });

                return;
            }


            // ===============================
            // 1️⃣ HEURISTIQUE RAPIDE
            // ===============================
            bool flashSuspected =
                await content.FlashService.DetectFlashRequirementAsync();

            if (Tabs.SelectedItem is not TabItem t1 ||
                !ReferenceEquals(t1.Tag, content))
                return;

            content.FlashRequired = flashSuspected;

            Dispatcher.Invoke(() => UpdateFlashModeButton(content));
            Dispatcher.Invoke(() =>
            {
                if (!IsActiveTab(content)) return;

                if (flashSuspected)
                    overlay.TryShow("Flash suspecté… analyse…");
                else
                    overlay.Hide();
            });

            // ===============================
            // 2️⃣ DÉTECTION DOM RÉELLE
            // ===============================
            bool hasFlashDom =
                await content.FlashService.DetectFlashDomAsync();

            if (!hasFlashDom)
            {
                Dispatcher.Invoke(() =>
                {
                    if (!IsActiveTab(content)) return;
                    overlay.Hide();
                });
                return;
            }

            if (Tabs.SelectedItem is not TabItem t2 ||
                !ReferenceEquals(t2.Tag, content))
                return;

            Dispatcher.Invoke(() =>
            {
                if (IsActiveTab(content))
                    overlay.BindHost(content.HostGrid);
            });

            content.FlashRequired = true;

            // ===============================
            // 3️⃣ CAPACITÉ
            // ===============================
            if (!CanUseFlashOrLegacy(uri, hasFlashDom, out var reason))
            {
                Dispatcher.Invoke(() =>
                {
                    if (Tabs.SelectedItem is TabItem t &&
                        ReferenceEquals(t.Tag, content))
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
                bool ok = await OpenLegacyBasiliskTabAsync(uri);
                return;
            }


            // -------------------------------
            // RUFFLE
            // -------------------------------
            Dispatcher.Invoke(() =>
            {
                if (Tabs.SelectedItem is TabItem t &&
                    ReferenceEquals(t.Tag, content))
                    overlay.TryShow("Flash détecté → émulation Ruffle…");
            });

            content.FlashMode = FlashMode.Ruffle;

            await RuffleInjector.InjectAsync(web);

            if (Tabs.SelectedItem is not TabItem t3 ||
                !ReferenceEquals(t3.Tag, content))
                return;

            content.RuffleMonitor?.Stop();
            content.RuffleMonitor = new RuffleMonitor(web);

            content.RuffleMonitor.FailureDetected += _ =>
            {
                _retryContent = content;
                _retryUri = uri;
                _retryHeader = header;
                _retryOverlay = overlay;

                Dispatcher.Invoke(() =>
                {
                    if (Tabs.SelectedItem is not TabItem t ||
                        !ReferenceEquals(t.Tag, content))
                        return;

                    if (!_legacyLauncher.CanLaunch())
                    {
                        overlay.ShowBlocked("Ruffle a échoué.\n\n⚠ Basilisk n’est pas configuré (ou chemin invalide).");
                        overlay.SetActions("Configurer Basilisk", OpenSettings);
                        return;
                    }

                    overlay.ShowBlocked("Ruffle a échoué.\n\nTentative de lancement Basilisk…");

                    // ✅ même pipeline legacy
                    LaunchLegacySameWayAsFallback();
                });
            };

            content.RuffleMonitor.Start();
        }





        private void RetryLegacyFromOverlay()
        {
            var content = _retryContent;
            var uri = _retryUri;
            var header = _retryHeader;
            var overlay = _retryOverlay;

            if (content == null || uri == null || header == null || overlay == null)
                return;

            // ✅ launch hors UI thread
            System.Threading.Tasks.Task.Run(async () =>
            {
                bool ok;

                try
                {
                    ok = await TryLaunchLegacy(content, uri, header); // ✅ Task<bool>
                }
                catch (Exception ex)
                {
                    Dispatcher.Invoke(() =>
                    {
                        if (!IsActiveTab(content)) return;

                        overlay.ShowBlocked("Échec Basilisk :\n" + ex);
                        overlay.SetActions("Paramètres", OpenSettings);
                    });
                    return;
                }

                Dispatcher.Invoke(() =>
                {
                    if (!IsActiveTab(content)) return;

                    if (ok)
                    {
                        overlay.Hide();
                        SyncWebHostWithSelection();
                    }
                    else
                    {
                        overlay.ShowBlocked("Toujours impossible de lancer Basilisk.");
                        overlay.SetActions("Paramètres", OpenSettings);
                    }
                });
            });
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

        private const int GW_OWNER = 4;


        [DllImport("user32.dll")]
        private static extern int GetWindowThreadProcessId(IntPtr hWnd, out int lpdwProcessId);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

        protected override void OnClosing(CancelEventArgs e)
        {
            try
            {
                LegacyLauncher.KillAllBasiliskProcesses();
            }
            catch { }

            base.OnClosing(e);
        }

        async Task<bool> LaunchLegacyIntoInternalTabAsync(WebTabContent content, Uri uri, BrowserTabHeader header)
        {
            if (content.IsLegacyLaunching)
                return false;

            content.IsLegacyLaunching = true;
            content.IsLegacyExternal = true;
            content.LegacyLastError = null;
            content.LegacyUrl = uri.AbsoluteUri;

            await Dispatcher.InvokeAsync(() =>
            {
                header.SetTitle("Legacy Flash");
                content.FlashMode = FlashMode.Legacy;
                SyncWebHostWithSelection();
            });

            await Task.Delay(50);

            if (!_legacyLauncher.CanLaunch())
            {
                content.IsLegacyLaunching = false;
                content.IsLegacyExternal = true;
                content.LegacyLastError = "Basilisk n’est pas configuré ou chemin invalide.";

                await Dispatcher.InvokeAsync(SyncWebHostWithSelection);
                return false;
            }

            try
            {
                var profile = LegacyProfileManager.GetProfileForDomain(uri.Host)
                              + "_" + Guid.NewGuid().ToString("N");
                var p = _legacyLauncher.Launch(uri.AbsoluteUri, profile);
                content.LegacyProc = p;

                if (p == null)
                {
                    content.IsLegacyLaunching = false;
                    content.IsLegacyExternal = true;
                    content.LegacyLastError = "Process Basilisk non lancé (Launch() a retourné null).";

                    await Dispatcher.InvokeAsync(SyncWebHostWithSelection);
                    return false;
                }
                // ✅ attendre fenêtre top-level Basilisk
                var (top, realPid) = await WaitForAnyTopWindowFromProcessFamilyAsync(p, timeoutMs: 15000);
                if (top == IntPtr.Zero)
                {
                    content.IsLegacyLaunching = false;
                    content.IsLegacyExternal = true;
                    content.LegacyLastError = "Fenêtre Basilisk introuvable (timeout).";

                    await Dispatcher.InvokeAsync(SyncWebHostWithSelection);
                    return false;
                }
                // ✅ set handles
                content.LegacyPid = realPid;
                content.LegacyTopHwnd = top;

                // ✅ ton dock overlay MoveWindow(PointToScreen) doit viser la TOP-LEVEL
                content.LegacyHwnd = top;

                // ✅ dock host
                content.LegacyHost ??= new MyHomelabBrowser.controles.ExternalWindowDock();

                // ✅ applique toolwindow/owner sur la TOP window
                content.LegacyHost.SetTopLevel(top);

                // ✅ on bind la TOP (coords écran OK)
                var embed = FindBestEmbedChild(top);
                content.LegacyHost.Bind(embed != IntPtr.Zero ? embed : top);


                content.IsLegacyLaunching = false;
                content.IsLegacyExternal = true;
                content.LegacyLastError = null;
                await Dispatcher.InvokeAsync(() =>
                {
                    
                    SyncWebHostWithSelection();


                    content.LegacyHost?.ShowDock();
                    content.LegacyHost?.UpdateDockPosition();
                }, DispatcherPriority.Loaded);

                return true;
            }
            catch (Exception ex)
            {
                content.IsLegacyLaunching = false;
                content.IsLegacyExternal = true;
                content.LegacyLastError = "Erreur au lancement de Basilisk :\n" + ex;

                await Dispatcher.InvokeAsync(SyncWebHostWithSelection);
                return false;
            }
        }












        async Task<(IntPtr top, IntPtr embed)> WaitForBasiliskWindowsAsync(
    Process proc,
    int timeoutMs = 15000,
    int pollMs = 50)
        {
            var sw = Stopwatch.StartNew();
            int tick = 0;

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                tick++;

                if (proc.HasExited)
                {
                    Debug.WriteLine($"[BASILISK] EXITED after {sw.ElapsedMilliseconds}ms");
                    return (IntPtr.Zero, IntPtr.Zero);
                }

                IntPtr top = FindTopLevelWindowByPid(proc.Id);

                if (tick % 20 == 0) // ~1s
                    Debug.WriteLine($"[BASILISK] waiting... {sw.ElapsedMilliseconds}ms top=0x{top.ToInt64():X}");

                if (top != IntPtr.Zero)
                {
                    Debug.WriteLine($"[BASILISK] FOUND top=0x{top.ToInt64():X} after {sw.ElapsedMilliseconds}ms");
                    return (top, top);
                }

                await Task.Delay(pollMs);
            }

            Debug.WriteLine($"[BASILISK] TIMEOUT after {sw.ElapsedMilliseconds}ms");
            return (IntPtr.Zero, IntPtr.Zero);
        }



        private IntPtr FindTopLevelWindowByPid(int pid)
        {
            IntPtr found = IntPtr.Zero;

            EnumWindows((hWnd, lParam) =>
            {
                if (!IsWindowVisible(hWnd)) return true;

                GetWindowThreadProcessId(hWnd, out int winPid);
                if (winPid != pid) return true;

                // ignore owned windows
                if (GetWindow(hWnd, GW_OWNER) != IntPtr.Zero) return true;

                found = hWnd;
                return false;
            }, IntPtr.Zero);

            return found;
        }
        private HashSet<int> GetProcessFamilyPids(Process root)
        {
            var set = new HashSet<int>();

            int rootPid;
            try
            {
                rootPid = root.Id;
                set.Add(rootPid);
            }
            catch
            {
                return set;
            }

            // ✅ collecte des enfants via Win32 snapshot
            foreach (var pid in EnumerateDescendantPids(rootPid))
                set.Add(pid);

            return set;
        }

        private static HashSet<int> EnumerateDescendantPids(int rootPid)
        {
            var result = new HashSet<int>();
            var queue = new Queue<int>();
            queue.Enqueue(rootPid);

            while (queue.Count > 0)
            {
                int parent = queue.Dequeue();

                foreach (var child in EnumerateChildPids(parent))
                {
                    if (result.Add(child))
                        queue.Enqueue(child);
                }
            }

            return result;
        }

        private static HashSet<int> EnumerateChildPids(int parentPid)
        {
            var result = new HashSet<int>();

            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == (IntPtr)(-1))
                return result;

            try
            {
                var pe = new PROCESSENTRY32();
                pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));

                if (!Process32First(snapshot, ref pe))
                    return result;

                do
                {
                    if ((int)pe.th32ParentProcessID == parentPid)
                        result.Add((int)pe.th32ProcessID);

                    pe.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                }
                while (Process32Next(snapshot, ref pe));
            }
            finally
            {
                CloseHandle(snapshot);
            }

            return result;
        }

        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
        private struct PROCESSENTRY32
        {
            public uint dwSize;
            public uint cntUsage;
            public uint th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID;
            public uint cntThreads;
            public uint th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);




        private async Task<(IntPtr top, int realPid)> WaitForAnyTopWindowFromProcessFamilyAsync(
     Process rootProc,
     int timeoutMs = 15000)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();

            while (sw.ElapsedMilliseconds < timeoutMs)
            {
                var pids = GetProcessFamilyPids(rootProc);

                foreach (var pid in pids)
                {
                    var hwnd = FindAnyTopLevelWindowForPid(pid);
                    if (hwnd != IntPtr.Zero)
                        return (hwnd, pid);
                }

                await Task.Delay(100);
            }

            return (IntPtr.Zero, 0);
        }



        private static IntPtr FindAnyTopLevelWindowForPid(int pid)
        {
            IntPtr found = IntPtr.Zero;

            EnumWindows((hWnd, lParam) =>
            {
                GetWindowThreadProcessId(hWnd, out int winPid);
                if (winPid != pid)
                    return true;

                if (!IsWindowVisible(hWnd))
                    return true;

                found = hWnd;
                return false;
            }, IntPtr.Zero);

            return found;
        }


        [DllImport("user32.dll")]
        private static extern int GetWindowTextLength(IntPtr hWnd);






        [StructLayout(LayoutKind.Sequential)]
        struct POINT
        {
            public int X;
            public int Y;
        }

        [DllImport("user32.dll")]
        static extern bool GetCursorPos(out POINT lpPoint);
        

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        static extern int GetClassName(IntPtr hWnd, System.Text.StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", SetLastError = true)]
        static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        static extern IntPtr GetParent(IntPtr hWnd);

        Rect GetWebHostScreenRect()
        {
            // WebHost est ton ContentControl qui affiche l’onglet
            var p = WebHost.PointToScreen(new Point(0, 0));
            return new Rect(p.X, p.Y, WebHost.ActualWidth, WebHost.ActualHeight);
        }
        

        const uint SWP_NOMOVE = 0x0002;
        const uint SWP_NOSIZE = 0x0001;

        void HideBasiliskWindow(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return;
            ShowWindow(hwnd, SW_HIDE);
        }


        static string GetWndClass(IntPtr h)
        {
            var sb = new System.Text.StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            return sb.ToString();
        }

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
            public bool? LastFlashButtonVisible { get; set; }
            public FlashMode? LastFlashMode { get; set; }
            public Grid HostGrid { get; } = new Grid();
            public bool IsLegacyLaunching { get; set; }
            public string? LegacyLastError { get; set; }
            public LegacyFlashView? LegacyView { get; set; }
            public bool IsLegacyEmbedded { get; set; }
            public IntPtr LegacyHwnd { get; set; } = IntPtr.Zero;
            public ExternalWindowDock? LegacyHost;
            public Process? LegacyProc { get; set; }
            public IntPtr LegacyTopHwnd { get; set; } = IntPtr.Zero;
            public bool LegacyDocked { get; set; }
            public bool LegacyEmbeddedReady { get; set; }





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
