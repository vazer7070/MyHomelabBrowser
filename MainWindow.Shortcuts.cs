using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        private const int MaxRecentlyClosed = 25;
        private readonly LinkedList<string> _recentlyClosedUrls = new();
        private bool _windowFullscreen;

        /// <summary>
        /// Raccourcis du navigateur. Le contrôle WebView2 relaie aussi ses touches
        /// d'accélération sous forme d'événements WPF : ils fonctionnent donc même
        /// quand la page a le focus. Les touches non gérées ici (Ctrl+F, Ctrl+P, zoom…)
        /// restent traitées par le moteur web.
        /// </summary>
        private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            ModifierKeys mods = Keyboard.Modifiers;
            bool ctrl = mods.HasFlag(ModifierKeys.Control);
            bool shift = mods.HasFlag(ModifierKeys.Shift);
            bool alt = mods.HasFlag(ModifierKeys.Alt);

            bool handled = true;

            if (ctrl && !alt)
            {
                switch (key)
                {
                    case Key.T when shift:
                        ReopenClosedTab();
                        break;
                    case Key.T:
                        NewTab_Click(this, new RoutedEventArgs());
                        break;
                    case Key.N when shift:
                        NewPrivateTab_Click(this, new RoutedEventArgs());
                        break;
                    case Key.W:
                    case Key.F4:
                        if (Tabs.SelectedItem is TabItem tab)
                            CloseTab(tab);
                        break;
                    case Key.Tab:
                        SelectRelativeTab(shift ? -1 : 1);
                        break;
                    case Key.PageDown:
                        SelectRelativeTab(1);
                        break;
                    case Key.PageUp:
                        SelectRelativeTab(-1);
                        break;
                    case >= Key.D1 and <= Key.D8:
                        SelectTabAt(key - Key.D1);
                        break;
                    case Key.D9:
                        SelectTabAt(Tabs.Items.Count - 1);
                        break;
                    case Key.L:
                        FocusAddressBar();
                        break;
                    case Key.R:
                        ReloadCurrentTab(ignoreCache: shift);
                        break;
                    case Key.F5:
                        ReloadCurrentTab(ignoreCache: true);
                        break;
                    case Key.H:
                        OpenHistory();
                        break;
                    case Key.J:
                        DownloadsPopup.IsOpen = !DownloadsPopup.IsOpen;
                        break;
                    case Key.D:
                        ToggleFavoriteSafe_Click(this, new RoutedEventArgs());
                        break;
                    case Key.OemComma:
                        OpenSettings();
                        break;
                    default:
                        handled = false;
                        break;
                }
            }
            else if (alt && !ctrl)
            {
                switch (key)
                {
                    case Key.Left:
                        BackBtn_Click(this, new RoutedEventArgs());
                        break;
                    case Key.Right:
                        ForwardBtn_Click(this, new RoutedEventArgs());
                        break;
                    case Key.D:
                        FocusAddressBar();
                        break;
                    case Key.Home:
                        NewTab_Click(this, new RoutedEventArgs());
                        break;
                    default:
                        handled = false;
                        break;
                }
            }
            else if (!ctrl && !alt)
            {
                switch (key)
                {
                    case Key.F5:
                        ReloadCurrentTab(ignoreCache: shift);
                        break;
                    case Key.F6:
                        FocusAddressBar();
                        break;
                    case Key.F11:
                        ToggleWindowFullscreen();
                        break;
                    case Key.BrowserBack:
                        BackBtn_Click(this, new RoutedEventArgs());
                        break;
                    case Key.BrowserForward:
                        ForwardBtn_Click(this, new RoutedEventArgs());
                        break;
                    case Key.BrowserRefresh:
                        ReloadCurrentTab(ignoreCache: false);
                        break;
                    case Key.Escape when _windowFullscreen:
                        ToggleWindowFullscreen();
                        break;
                    default:
                        handled = false;
                        break;
                }
            }
            else
            {
                handled = false;
            }

            if (handled)
                e.Handled = true;
        }

        void SelectRelativeTab(int delta)
        {
            int count = Tabs.Items.Count;
            if (count == 0)
                return;

            int index = Tabs.SelectedIndex < 0 ? 0 : Tabs.SelectedIndex;
            Tabs.SelectedIndex = ((index + delta) % count + count) % count;
        }

        void SelectTabAt(int index)
        {
            if (index >= 0 && index < Tabs.Items.Count)
                Tabs.SelectedIndex = index;
        }

        /// <summary>
        /// Mémorise l'adresse d'un onglet normal fermé (jamais un onglet privé).
        /// </summary>
        void RememberClosedTab(WebTabContent content)
        {
            if (content.IsPrivate || content.IsCustomView)
                return;

            string? url = content.IsLegacyExternal ? content.LegacyUrl : content.PendingUrl ?? content.Web?.Source?.AbsoluteUri;
            if (string.IsNullOrWhiteSpace(url) || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                return;

            _recentlyClosedUrls.AddFirst(url);
            while (_recentlyClosedUrls.Count > MaxRecentlyClosed)
                _recentlyClosedUrls.RemoveLast();
        }

        void ReopenClosedTab()
        {
            if (_recentlyClosedUrls.First is not { } node)
                return;

            _recentlyClosedUrls.RemoveFirst();
            _ = CreateTabInternal(node.Value);
        }

        bool HasRecentlyClosedTabs => _recentlyClosedUrls.Count > 0;

        /// <summary>
        /// Plein écran de la fenêtre (F11) : masque la barre d'onglets et de navigation.
        /// </summary>
        void ToggleWindowFullscreen()
        {
            if (_htmlFullscreen)
            {
                ExitHtmlFullscreenIfNeeded();
                return;
            }

            _windowFullscreen = !_windowFullscreen;

            if (_windowFullscreen)
            {
                _stateBeforeFullscreen = WindowState;
                _styleBeforeFullscreen = WindowStyle;
                _resizeBeforeFullscreen = ResizeMode;

                BrowserChrome.Visibility = Visibility.Collapsed;
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Normal;
                WindowState = WindowState.Maximized;

                ShowToast("Plein écran", "Appuyez sur F11 ou Échap pour quitter.", duration: TimeSpan.FromSeconds(3));
            }
            else
            {
                BrowserChrome.Visibility = Visibility.Visible;
                WindowStyle = _styleBeforeFullscreen;
                ResizeMode = _resizeBeforeFullscreen;
                WindowState = _stateBeforeFullscreen;
            }
        }
    }
}
