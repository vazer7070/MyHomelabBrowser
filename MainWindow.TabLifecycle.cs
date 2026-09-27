using Microsoft.Web.WebView2.Core;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.controles;
using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        private const double PinnedTabWidth = 44;
        private const double TabMinWidth = 0;
        private const double TabMaxWidth = 240;

        // ---------------------------
        // Nouvel onglet
        // ---------------------------
        string GetNewTabUrl()
        {
            string url = _settings.Settings.NewTabPage;
            return string.IsNullOrWhiteSpace(url) ? "about:blank" : url.Trim();
        }

        private void NewTab_Click(object sender, RoutedEventArgs e)
        {
            var url = GetNewTabUrl();

            if (url.Equals("about:blank", StringComparison.OrdinalIgnoreCase))
                CreateEmptyStartTab();
            else
                _ = CreateTabInternal(UrlResolver.ResolveOrSearch(url, _settings.Settings.Search));
        }

        // ---------------------------
        // Réorganisation / épinglage
        // ---------------------------
        void ReorderTab(TabItem tab, int direction)
        {
            int index = Tabs.Items.IndexOf(tab);
            if (index < 0) return;

            int newIndex = index + direction;
            if (newIndex < 0 || newIndex >= Tabs.Items.Count)
                return;

            bool movingPinned = tab.Tag is WebTabContent { IsPinned: true };
            bool targetPinned = Tabs.Items[newIndex] is TabItem { Tag: WebTabContent { IsPinned: true } };

            // Les onglets épinglés restent groupés en tête.
            if (movingPinned != targetPinned)
                return;

            double offset = direction * Math.Max(40, tab.ActualWidth);

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
            var selected = Tabs.SelectedItem;
            var ordered = Tabs.Items.Cast<TabItem>()
                .OrderBy(t => t.Tag is WebTabContent { IsPinned: true } ? 0 : 1)
                .ToList();

            if (ordered.SequenceEqual(Tabs.Items.Cast<TabItem>()))
                return;

            Tabs.Items.Clear();
            foreach (var t in ordered)
                Tabs.Items.Add(t);

            Tabs.SelectedItem = selected;
        }

        void ApplyPinState(TabItem tab, BrowserTabHeader header, bool pinned)
        {
            header.SetPinned(pinned);
            tab.MinWidth = pinned ? PinnedTabWidth : TabMinWidth;
            tab.MaxWidth = pinned ? PinnedTabWidth : TabMaxWidth;

            MovePinnedTabsToFront();
        }

        // ---------------------------
        // Suspension
        // ---------------------------

        /// <summary>
        /// Met un onglet en veille. Auparavant seul un panneau était affiché : le moteur
        /// continuait de tourner. WebView2 libère maintenant réellement le processus de
        /// rendu (TrySuspendAsync), la page reprenant à l'identique au réveil.
        /// </summary>
        void SuspendTab(TabItem tab, WebTabContent state)
        {
            if (state.IsSuspended || state.IsCustomView || state.IsLegacyExternal)
                return;

            bool wasSelected = Equals(Tabs.SelectedItem, tab);

            if (wasSelected)
                SelectFallbackTab(tab);

            state.IsSuspended = true;

            if (tab.Header is BrowserTabHeader header)
                header.ShowSuspended(true);

            if (!Equals(Tabs.SelectedItem, tab))
                _ = TrySuspendWebViewAsync(state);
            else
                SyncWebHostWithSelection();
        }

        async Task TrySuspendWebViewAsync(WebTabContent state)
        {
            // Laisser WPF retirer le contrôle de l'arbre visuel (IsVisible = false),
            // condition exigée par WebView2 pour la mise en veille.
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Background);

            var core = state.Web?.CoreWebView2;
            if (core == null || state.IsClosed || !state.IsSuspended || state.Web!.IsVisible)
                return;

            try
            {
                await core.TrySuspendAsync();
            }
            catch
            {
                // Page qui refuse la veille (audio en cours, WebRTC…) : pas grave.
            }
        }

        void ResumeWebView(WebTabContent state)
        {
            var core = state.Web?.CoreWebView2;
            if (core == null)
                return;

            try
            {
                if (core.IsSuspended)
                    core.Resume();
            }
            catch
            {
            }
        }

        void ResumeSuspendedTab(TabItem tab, WebTabContent state)
        {
            state.IsSuspended = false;
            state.LastActivated = DateTime.Now;

            if (tab.Header is BrowserTabHeader h)
                h.ShowSuspended(false);

            ResumeWebView(state);

            if (Equals(Tabs.SelectedItem, tab))
                SyncWebHostWithSelection();
        }

        UIElement CreateSuspendedPlaceholder(TabItem tab, WebTabContent state)
        {
            var panel = new Border
            {
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(36, 28, 36, 28),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Cursor = Cursors.Hand
            };
            panel.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            panel.SetResourceReference(Border.BorderBrushProperty, "BorderBrush");

            var stack = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };

            var icon = new TextBlock
            {
                Text = "",
                FontSize = 30,
                HorizontalAlignment = HorizontalAlignment.Center
            };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            icon.SetResourceReference(TextBlock.ForegroundProperty, "AccentBrush");

            var title = new TextBlock
            {
                Text = "Onglet en veille",
                FontSize = 20,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 14, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            title.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

            var hint = new TextBlock
            {
                Text = "Mis en pause pour libérer de la mémoire. Cliquez pour le réactiver.",
                FontSize = 13,
                Margin = new Thickness(0, 8, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

            stack.Children.Add(icon);
            stack.Children.Add(title);
            stack.Children.Add(hint);
            panel.Child = stack;

            panel.MouseLeftButtonUp += (_, _) => ResumeSuspendedTab(tab, state);

            var host = new Grid();
            host.SetResourceReference(Panel.BackgroundProperty, "WindowBackgroundBrush");
            host.Children.Add(panel);
            return host;
        }

        void AutoSuspendTabs(bool force = false)
        {
            var now = DateTime.Now;

            foreach (TabItem tab in Tabs.Items.OfType<TabItem>().ToList())
            {
                if (tab.Tag is not WebTabContent state)
                    continue;

                if (state.IsPinned || state.IsSuspended || state.IsCustomView || state.IsLegacyExternal)
                    continue;

                if (Equals(tab, Tabs.SelectedItem))
                    continue;

                // Onglet jamais affiché (session restaurée) : rien à libérer.
                if (!string.IsNullOrEmpty(state.PendingUrl))
                    continue;

                // Une page qui joue du son n'est pas mise en veille automatiquement.
                if (!force && state.Web?.CoreWebView2?.IsDocumentPlayingAudio == true)
                    continue;

                if (force || now - state.LastActivated > _SuspendDelay)
                    SuspendTab(tab, state);
            }
        }

        // ---------------------------
        // Fermeture
        // ---------------------------
        async void CloseTab(TabItem tab)
        {
            if (!_tabsBeingClosed.Add(tab))
                return;

            try
            {
                bool wasPrivate = tab.Tag is WebTabContent { IsPrivate: true };
                bool wasSelected = Equals(Tabs.SelectedItem, tab);
                int index = Tabs.Items.IndexOf(tab);

                if (tab.Tag is WebTabContent closingTab)
                {
                    RememberClosedTab(closingTab);
                    closingTab.IsClosed = true;
                }

                // L'onglet disparaît immédiatement ; l'arrêt du moteur suit en arrière-plan.
                if (wasSelected)
                    SelectFallbackTab(tab);

                Tabs.Items.Remove(tab);

                if (Tabs.Items.Count == 0)
                    CreateEmptyStartTab();

                if (tab.Tag is WebTabContent closingWebTab)
                    await ShutdownWebTabAsync(closingWebTab);

                if (wasPrivate && !HasAnyPrivateTab())
                {
                    DownloadManager.Instance.ClearPrivateDownloads();
                    UpdateDownloadsBadge();
                }

                if (index >= 0)
                    SyncWebHostWithSelection();
            }
            finally
            {
                _tabsBeingClosed.Remove(tab);
            }
        }

        /// <summary>
        /// Sélectionne l'onglet voisin (à droite, sinon à gauche) avant une fermeture.
        /// </summary>
        void SelectFallbackTab(TabItem from)
        {
            int index = Tabs.Items.IndexOf(from);

            if (index >= 0 && index < Tabs.Items.Count - 1)
            {
                Tabs.SelectedIndex = index + 1;
                return;
            }

            if (index > 0)
            {
                Tabs.SelectedIndex = index - 1;
                return;
            }

            Tabs.SelectedItem = null;
        }

        void RedockWebTab(WebTabContent state)
        {
            EndDockingMode();

            // On réutilise le même TabItem : ses gestionnaires (titre, favicon,
            // fermeture…) sont liés à lui depuis la création de l'onglet.
            TabItem tab = state.OwnerTab ?? new TabItem
            {
                Header = new BrowserTabHeader(),
                Tag = state
            };

            if (state.OwnerTab == null)
            {
                state.OwnerTab = tab;
                var header = (BrowserTabHeader)tab.Header;
                header.SetTitle(state.Web?.CoreWebView2?.DocumentTitle ?? "Onglet");
                header.SetPrivate(state.IsPrivate);
                WireTabHeader(tab, header, state);
            }

            if (tab.Header is BrowserTabHeader redockedHeader)
                redockedHeader.ResetVisualState();

            if (!Tabs.Items.Contains(tab))
                Tabs.Items.Add(tab);

            Tabs.SelectedItem = tab;
            SyncWebHostWithSelection();
        }

        // ---------------------------
        // Précédent / Suivant / Actualiser
        // ---------------------------
        private void BackBtn_Click(object sender, RoutedEventArgs e)
        {
            var core = Tabs.SelectedItem is TabItem { Tag: WebTabContent content } ? content.Web?.CoreWebView2 : null;
            if (core?.CanGoBack == true)
                core.GoBack();

            UpdateNavButtons();
        }

        private void RefreshBtn_Click(object sender, RoutedEventArgs e)
        {
            if (Tabs.SelectedItem is TabItem { Tag: WebTabContent { IsLoading: true } content })
            {
                content.Web?.CoreWebView2?.Stop();
                return;
            }

            bool ignoreCache = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ||
                               Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
            ReloadCurrentTab(ignoreCache);
        }

        private void BackBtn_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _backHoldTriggered = false;

            _backHoldTimer?.Stop();
            _backHoldTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(380)
            };

            _backHoldTimer.Tick += (_, _) =>
            {
                _backHoldTimer?.Stop();
                _backHoldTriggered = true;
                _ = ShowBackHistoryMenuAsync();
            };

            _backHoldTimer.Start();
        }

        private void BackBtn_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _backHoldTimer?.Stop();

            // Appui long : le menu remplace le clic.
            if (_backHoldTriggered)
                e.Handled = true;
        }

        private void BackBtn_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            e.Handled = true;
            _ = ShowBackHistoryMenuAsync();
        }

        /// <summary>
        /// Appui long sur « Précédent » : pages précédentes de CET onglet (auparavant
        /// le menu listait l'historique global, sans rapport avec l'onglet).
        /// </summary>
        private async Task ShowBackHistoryMenuAsync()
        {
            if (Tabs.SelectedItem is not TabItem { Tag: WebTabContent content } ||
                content.Web?.CoreWebView2 is not CoreWebView2 core)
                return;

            string json;
            try
            {
                json = await core.CallDevToolsProtocolMethodAsync("Page.getNavigationHistory", "{}");
            }
            catch
            {
                return;
            }

            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            int currentIndex = root.GetProperty("currentIndex").GetInt32();
            JsonElement entries = root.GetProperty("entries");

            var menu = new ContextMenu();

            for (int i = currentIndex - 1; i >= 0 && menu.Items.Count < 15; i--)
            {
                JsonElement entry = entries[i];
                int entryId = entry.GetProperty("id").GetInt32();
                string url = entry.GetProperty("url").GetString() ?? string.Empty;
                string title = entry.TryGetProperty("title", out var t) ? t.GetString() ?? string.Empty : string.Empty;

                var item = new MenuItem
                {
                    Header = string.IsNullOrWhiteSpace(title) ? url : title,
                    ToolTip = url
                };

                item.Click += async (_, _) =>
                {
                    try
                    {
                        await core.CallDevToolsProtocolMethodAsync(
                            "Page.navigateToHistoryEntry",
                            JsonSerializer.Serialize(new { entryId }));
                    }
                    catch
                    {
                    }
                };

                menu.Items.Add(item);
            }

            if (menu.Items.Count == 0)
                return;

            menu.PlacementTarget = BackBtn;
            menu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
            menu.IsOpen = true;
        }

        // ---------------------------
        // Fermeture de la fenêtre
        // ---------------------------
        protected override void OnClosing(CancelEventArgs e)
        {
            SaveSessionOnExit();
            FlushPersistentState();

            foreach (TabItem tab in Tabs.Items.OfType<TabItem>())
            {
                if (tab.Tag is not WebTabContent content)
                    continue;

                try { content.FlashNavigationCts?.Cancel(); } catch { }
                try { content.RuffleMonitor?.Dispose(); } catch { }
                try
                {
                    if (content.LegacyProc is { HasExited: false })
                        content.LegacyProc.Kill(entireProcessTree: true);
                }
                catch { }
                try { content.LegacyProfileLease?.Dispose(); } catch { }
            }

            try { _oauthPopup?.Close(); } catch { }

            base.OnClosing(e);
        }
    }
}
