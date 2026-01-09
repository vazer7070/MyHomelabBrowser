using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.controles;
using System;
using System.IO;
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
            CreateTab("https://google.com");
            _suspendTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(30)
            };
            _suspendTimer.Tick += (_, _) => AutoSuspendTabs();
            _suspendTimer.Start();

        }

        public bool IsDockIndicatorVisible =>
            DockIndicator.Visibility == Visibility.Visible;
        DispatcherTimer _suspendTimer;
        readonly TimeSpan SuspendDelay = TimeSpan.FromMinutes(5);

        // ---------------------------
        // Tabs / WebHost
        // ---------------------------

        void CreateTab(string url)
        {
            var web = new WebView2
            {
                Source = new Uri(url)
            };

            var header = new BrowserTabHeader();
            header.SetTitle("Nouvel onglet");

            var tab = new TabItem
            {
                Header = header,
                Tag = new TabState { Web = web }
            };

            header.CloseRequested += () => CloseTab(tab);

            
            header.DetachRequested += () =>
            {
                if (_isDocking) return;
                DetachTab(tab);
            };

            header.PinRequested += () =>
            {
                if (tab.Tag is not TabState state)
                    return;

                state.IsPinned = !state.IsPinned;
                ApplyPinState(tab, header, state.IsPinned);
            };

            web.NavigationCompleted += (_, _) =>
            {
                if (web.CoreWebView2 != null)
                {
                    header.SetTitle(web.CoreWebView2.DocumentTitle);

                    if (!string.IsNullOrEmpty(web.CoreWebView2.FaviconUri))
                        header.SetIcon(new BitmapImage(new Uri(web.CoreWebView2.FaviconUri)));
                }

                // preview (optionnel mais stable)
                AttachPreview(tab, web);
            };

            Tabs.Items.Add(tab);
            Tabs.SelectedItem = tab;
            SyncWebHostWithSelection();

            header.ReorderRequested += dir =>
            {
                ReorderTab(tab, dir);
            };

        }
        void ReorderTab(TabItem tab, int direction)
        {
            int index = Tabs.Items.IndexOf(tab);
            if (index < 0) return;

            int newIndex = index + direction;
            if (newIndex < 0 || newIndex >= Tabs.Items.Count)
                return;

            // bloquer devant les pinned
            if (Tabs.Items[newIndex] is TabItem other &&
                other.Tag is TabState s && s.IsPinned)
                return;

            // largeur approximative pour l'animation
            double offset = direction * 160;

            // animer l'onglet déplacé
            if (tab.Header is BrowserTabHeader moving)
                moving.AnimateReorder(-offset);

            // animer l'onglet croisé
            if (Tabs.Items[newIndex] is TabItem crossed &&
                crossed.Header is BrowserTabHeader crossedHeader)
                crossedHeader.AnimateReorder(offset);

            // reorder logique
            Tabs.Items.RemoveAt(index);
            Tabs.Items.Insert(newIndex, tab);
            Tabs.SelectedItem = tab;
        }


        void MovePinnedTabsToFront()
        {
            var pinned = Tabs.Items.Cast<TabItem>()
                .Where(t => t.Tag is TabState s && s.IsPinned)
                .ToList();

            var others = Tabs.Items.Cast<TabItem>()
                .Where(t => t.Tag is TabState s && !s.IsPinned)
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



        void SuspendTab(TabItem tab, TabState state)
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

            // 🔄 synchroniser la vue
            SyncWebHostWithSelection();
        }

        UIElement CreateSuspendedPlaceholder(TabItem tab, TabState state)
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
                if (tab.Tag is not TabState state)
                    continue;

                if (state.IsPinned || state.IsSuspended)
                    continue;

                if (Equals(tab, Tabs.SelectedItem))
                    continue;

                if (now - state.LastActivated > SuspendDelay)
                {
                    SuspendTab(tab, state);
                }
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
            {
                Tabs.SelectedIndex = Math.Max(0, Tabs.SelectedIndex);
            }

            SyncWebHostWithSelection();
        }

        void SelectFallbackTab(TabItem from)
        {
            int index = Tabs.Items.IndexOf(from);

            // priorité : onglet précédent
            if (index > 0)
            {
                Tabs.SelectedIndex = index - 1;
                return;
            }

            // sinon suivant
            if (index < Tabs.Items.Count - 1)
            {
                Tabs.SelectedIndex = index + 1;
                return;
            }

            // sinon rien
            Tabs.SelectedItem = null;
        }


        private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
            => SyncWebHostWithSelection();

        void SyncWebHostWithSelection()
        {
            if (Tabs.SelectedItem is not TabItem tab ||
                tab.Tag is not TabState state)
            {
                WebHost.Content = null;
                return;
            }

            state.LastActivated = DateTime.Now;

            if (state.IsSuspended)
            {
                WebHost.Content = CreateSuspendedPlaceholder(tab, state);
                return;
            }

            WebHost.Content = state.Web;
        }




        // ---------------------------
        // Navigation
        // ---------------------------

        private void NewTab_Click(object sender, RoutedEventArgs e)
            => CreateTab("https://duckduckgo.com");

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
                // recherche web
                Navigate($"https://google.fr/?q={Uri.EscapeDataString(input)}");
                return;
            }

            Navigate(input);
        }
        void ExecuteCommand(string cmd)
        {
            cmd = cmd.Trim().ToLowerInvariant();

            switch (cmd)
            {
                case "new":
                    CreateTab("https://duckduckgo.com");
                    break;

                case "close":
                    if (Tabs.SelectedItem is TabItem tab)
                        CloseTab(tab);
                    break;

                case "close others":
                    CloseOtherTabs();
                    break;

                case "reload":
                    if (Tabs.SelectedItem is TabItem t &&
                        t.Tag is TabState s)
                        s.Web.Reload();
                    break;

                case "suspend":
                    if (Tabs.SelectedItem is TabItem y &&
                        y.Tag is TabState k)
                        SuspendTab(y, k);
                    break;

                case "resume":
                    if (Tabs.SelectedItem is TabItem tadb &&
                        tadb.Tag is TabState state &&
                        state.IsSuspended)
                    {
                        state.IsSuspended = false;
                        state.LastActivated = DateTime.Now;

                        if (tadb.Header is BrowserTabHeader h)
                            h.ShowSuspended(false);

                        SyncWebHostWithSelection();
                    }
                    break;


                case "suspend inactive":
                    AutoSuspendTabs();
                    break;

            }
        }
        

        void CloseOtherTabs()
        {
            if (Tabs.SelectedItem is not TabItem current)
                return;

            var toClose = Tabs.Items
                .Cast<TabItem>()
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

            // --------------------
            // COMMANDES INTERNES
            // --------------------
            if (input.StartsWith(":"))
            {
                ExecuteCommand(input[1..]);
                return;
            }

            // --------------------
            // RECHERCHE ONGLET
            // --------------------
            if (input.StartsWith("@"))
            {
                FocusTab(input[1..]);
                return;
            }

            // --------------------
            // URL / RECHERCHE
            // --------------------
            NavigateOrSearch(input);
        }

        void Navigate(string url)
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                url = "https://" + url;

            if (Tabs.SelectedItem is TabItem tab &&
                tab.Tag is TabState state && state.Web is WebView2 web)
            {
                web.Source = new Uri(url);
            }
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

        private void Tabs_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (sender is not TabControl tabs)
                return;

            if (VisualTreeHelper.GetChild(tabs, 0) is not Grid root)
                return;

            var scroll = FindVisualChild<ScrollViewer>(root);
            if (scroll == null)
                return;

            scroll.ScrollToHorizontalOffset(
                scroll.HorizontalOffset - e.Delta
            );

            e.Handled = true;
        }

        static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T typed)
                    return typed;

                var found = FindVisualChild<T>(child);
                if (found != null)
                    return found;
            }
            return null;
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
        // Dock indicator + docking mode
        // ---------------------------

        public void ShowDockIndicator()
        {
            DockIndicator.Visibility = Visibility.Visible;
        }

        public void HideDockIndicator()
        {
            DockIndicator.Visibility = Visibility.Collapsed;
        }

        bool _isDocking;

        public void BeginDockingMode()
        {
            _isDocking = true;
        }

        public void EndDockingMode()
        {
            _isDocking = false;
            HideDockIndicator();
        }

        // ---------------------------
        // Detach / Redock
        // ---------------------------

        void DetachTab(TabItem tab)
        {
            if (tab.Tag is not TabState state)
                return;

            var web = state.Web;
            bool wasSelected = Equals(Tabs.SelectedItem, tab);


            Tabs.Items.Remove(tab);

            // ✅ IMPORTANT : on passe en mode docking dès qu'on a détaché
            BeginDockingMode();

            var win = new DetachedWindow(this, web);

            // PAS de Owner


            // ✅ sécurité : si on ferme la fenêtre détachée sans redock -> reset
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

        void RedockWebView(WebView2 web)
        {
            Dispatcher.Invoke(() =>
            {
                // ✅ on sort du mode docking dès qu'on redock
                EndDockingMode();

                var header = new BrowserTabHeader();
                header.SetTitle(web.CoreWebView2?.DocumentTitle ?? "Onglet");

                if (!string.IsNullOrEmpty(web.CoreWebView2?.FaviconUri))
                    header.SetIcon(new BitmapImage(new Uri(web.CoreWebView2.FaviconUri)));

                var tab = new TabItem
                {
                    Header = header,
                    Tag = new TabState { Web = web }
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
                    if (tab.Tag is not TabState s)
                        return;

                    s.IsPinned = !s.IsPinned;
                    ApplyPinState(tab, header, s.IsPinned);
                };

                Tabs.Items.Add(tab);
                Tabs.SelectedItem = tab;
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


        class TabState
        {
            public WebView2 Web { get; init; } = null!;
            public bool IsPinned { get; set; }

            public DateTime LastActivated { get; set; } = DateTime.Now;
            public bool IsSuspended { get; set; }
        }

    }
}
