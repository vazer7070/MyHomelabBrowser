using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.controles;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
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

        public MainWindow()
        {

            InitializeComponent();

            _suspendTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _suspendTimer.Tick += (_, _) => AutoSuspendTabs();
            _suspendTimer.Start();
            _settings = new SettingsService();
            _settings.SettingsChanged += ApplySettings;



            CreateTab(_settings.Settings.StartPage);
        }

        bool _addressBarEditing;

        readonly SettingsService _settings = new();

        void SyncAddressBarWithTab(TabItem tab)
        {
            if (tab?.Tag is WebTabContent webTab &&
                webTab.Web?.Source != null)
            {
                AddressBar.Text = webTab.Web.Source.AbsoluteUri;
            }
            else
            {
                AddressBar.Text = string.Empty;
            }
        }


        // ---------------------------
        // Dock indicator + docking mode
        // ---------------------------
        public bool IsDockIndicatorVisible => DockIndicator.Visibility == Visibility.Visible;

        bool _isDocking;

        public void BeginDockingMode() => _isDocking = true;

        public void EndDockingMode()
        {
            _isDocking = false;
            HideDockIndicator();
        }

        public void ShowDockIndicator() => DockIndicator.Visibility = Visibility.Visible;
        public void HideDockIndicator() => DockIndicator.Visibility = Visibility.Collapsed;

        // ---------------------------
        // Suspension
        // ---------------------------
        DispatcherTimer _suspendTimer;
        TimeSpan _SuspendDelay = TimeSpan.FromMinutes(5);

        // ---------------------------
        // Tabs / WebHost
        // ---------------------------
        void ApplySettings(BrowserSettings s)
        {
            // suspension
            _suspendTimer.IsEnabled = s.EnableSuspension;
            _SuspendDelay = TimeSpan.FromMinutes(s.SuspendDelayMinutes);

        }



        void OpenSettings()
        {
            // Si déjà ouvert -> sélectionner
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
        }

        void CreateTab(string url)
        {
            
            var web = new WebView2
            {
                Source = new Uri(url)
            };
            var header = new BrowserTabHeader();
            header.SetTitle("Nouvel onglet");

            var content = new WebTabContent
            {
                Web = web,
                IsPinned = false,
                IsSuspended = false,
                LastActivated = DateTime.Now
            };
            web.SourceChanged += (_, _) =>
            {
                Dispatcher.Invoke(UpdateAddressBarFromTab);
            };
            var tab = new TabItem
            {
                Header = header,
                Tag = content
            };
            web.NavigationCompleted += (_, _) =>
            {
                Dispatcher.Invoke(UpdateAddressBarFromTab);
                if (Tabs.SelectedItem == tab && web.Source != null)
                {
                    AddressBar.Text = web.Source.AbsoluteUri;
                }
            };

            

            

            header.CloseRequested += () => CloseTab(tab);

            header.DetachRequested += () =>
            {
                if (_isDocking) return;
                DetachTab(tab);
            };

            header.PinRequested += () =>
            {
                if (tab.Tag is not WebTabContent c) return;
                c.IsPinned = !c.IsPinned;
                ApplyPinState(tab, header, c.IsPinned);
            };

            header.ReorderRequested += dir => ReorderTab(tab, dir);

            web.NavigationCompleted += (_, _) =>
            {
                if (web.CoreWebView2 != null)
                {
                    header.SetTitle(web.CoreWebView2.DocumentTitle);

                    if (!string.IsNullOrEmpty(web.CoreWebView2.FaviconUri))
                        header.SetIcon(new BitmapImage(new Uri(web.CoreWebView2.FaviconUri)));
                }

                AttachPreview(tab, web);
            };

            Tabs.Items.Add(tab);
            Tabs.SelectedItem = tab;
            SyncWebHostWithSelection();
        }

        void ReorderTab(TabItem tab, int direction)
        {
            int index = Tabs.Items.IndexOf(tab);
            if (index < 0) return;

            int newIndex = index + direction;
            if (newIndex < 0 || newIndex >= Tabs.Items.Count)
                return;

            // bloquer si on essaie de passer "devant" un pinned
            if (Tabs.Items[newIndex] is TabItem other &&
                other.Tag is WebTabContent s && s.IsPinned)
                return;

            // animer (optionnel)
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
        void SuspendTab(TabItem tab, WebTabContent state)
        {
            if (state.IsSuspended)
                return;

            bool wasSelected = Equals(Tabs.SelectedItem, tab);

            // 🔁 si onglet actif → basculer AVANT suspension
            if (wasSelected)
                SelectFallbackTab(tab);

            state.IsSuspended = true;

            if (tab.Header is BrowserTabHeader header)
                header.ShowSuspended(true);

            // 🔑 resync seulement si la sélection a changé
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
            bool wasSelected = Equals(Tabs.SelectedItem, tab);
            Tabs.Items.Remove(tab);

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

        private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (Tabs.SelectedItem is TabItem tab)
            {
                SyncWebHostWithSelection();   
                SyncAddressBarWithTab(tab);   
            }
        }


        void SyncWebHostWithSelection()
        {
            if (Tabs.SelectedItem is not TabItem tab)
            {
                WebHost.Content = null;
                return;
            }

            switch (tab.Tag)
            {
                case WebTabContent webTab:
                    webTab.LastActivated = DateTime.Now;

                    if (webTab.IsSuspended)
                        WebHost.Content = CreateSuspendedPlaceholder(tab, webTab);
                    else
                        WebHost.Content = webTab.Web;

                    break;

                case ViewTabContent viewTab:
                    WebHost.Content = viewTab.View;
                    UpdateAddressBarFromTab();

                    break;

                default:
                    WebHost.Content = null;
                    break;
            }
        }

        // ---------------------------
        // Navigation / Omnibox
        // ---------------------------

        private void NewTab_Click(object sender, RoutedEventArgs e)
            => CreateTab(GetNewTabUrl());


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
                Navigate($"https://google.fr/?q={Uri.EscapeDataString(input)}");
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

            CommandList.Visibility = Visibility.Visible;

            // plus tard : filtrage commandes / historique ici
        }

        private void AddressBar_GotFocus(object sender, RoutedEventArgs e)
        {
            CommandList.ItemsSource =
                _settings.Settings.Commands
                    .Where(c => c.Enabled)
                    .ToList();

            CommandList.Visibility = Visibility.Visible;
        }

        private void CommandSuggestions_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (sender is not ListBox list || list.SelectedItem is not string cmd)
                return;

            AddressBar.Text = ":" + cmd;
            AddressBar.CaretIndex = AddressBar.Text.Length;
            list.SelectedItem = null;
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

                case "suspend inactive":
                    AutoSuspendTabs();
                    break;
            }
        }
        bool IsCommandEnabled(string key)
        {
            var settings = _settings.Settings;

            // commandes globalement désactivées
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

        void Navigate(string url)
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;

            if (Tabs.SelectedItem is TabItem tab && tab.Tag is WebTabContent state)
                state.Web.Source = new Uri(url);
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
            if (!CommandList.IsKeyboardFocusWithin)
                CommandList.Visibility = Visibility.Collapsed;
        }
        private void CommandSuggestions_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter && CommandList.SelectedItem != null)
            {
                if (CommandList.SelectedItem is CommandSetting cmd)
                {
                    ExecuteCommand(cmd.Key);
                    CommandList.Visibility = Visibility.Collapsed;
                    AddressBar.Focus();
                    e.Handled = true;
                }
            }

            if (e.Key == Key.Escape)
            {
                CommandList.Visibility = Visibility.Collapsed;
                AddressBar.Focus();
                e.Handled = true;
            }
        }

        void HideCommandSuggestions()
        {
            if (CommandList == null)
                return;

            CommandList.Visibility = Visibility.Collapsed;
        }

        private bool _addressBarSelectAllPending;

        private void AddressBar_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not TextBox tb || tb.IsKeyboardFocusWithin)
                return;

            _addressBarSelectAllPending = true;
            tb.Focus();
            e.Handled = true;
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
        // Preview (stable)
        // ---------------------------
        static void AttachPreview(TabItem tab, WebView2 web)
        {
            if (tab.ToolTip != null) return;

            var preview = new Image
            {
                Width = 320,
                Height = 200,
                Stretch = Stretch.UniformToFill
            };

            tab.ToolTip = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(32, 32, 32)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6),
                Child = preview
            };

            tab.MouseEnter += async (_, _) =>
            {
                if (web.CoreWebView2 == null) return;

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

                preview.Source = bmp;
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
        string GetNewTabUrl()
        {
            return _settings.Settings.NewTabPage?.Trim() is string url && url.Length > 0
                ? url
                : "about:blank";
        }

        void RedockWebView(WebView2 web)
        {
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
            public DateTime LastActivated { get; set; } = DateTime.Now;
        }

        class ViewTabContent : TabContent
        {
            public UserControl View { get; init; } = null!;
        }
    }
}
