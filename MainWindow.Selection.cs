using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.controles;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        /// <summary>
        /// Affiche le contenu de l'onglet sélectionné dans la zone principale.
        /// </summary>
        void SyncWebHostWithSelection()
        {
            _activeOverlay?.DetachVisualOnly();
            _activeOverlay = null;

            if (Tabs.SelectedItem is not TabItem tab)
            {
                ReleaseSplitHosts();
                WebHost.Content = null;
                ApplyPrivateTheme(false);
                UpdateFavoriteButton();
                return;
            }

            if (tab.Tag is WebTabContent webTab)
            {
                ApplyPrivateTheme(webTab.IsPrivate);
                webTab.LastActivated = DateTime.Now;

                ShowInWebHost(tab, webTab);

                // Cache les autres fenêtres Basilisk (sans jamais les re-parenter).
                HideLegacyWindowsExcept(webTab);

                // L'overlay Flash reste attaché à l'hôte de l'onglet.
                webTab.FlashOverlay?.BindHost(webTab.HostGrid);

                if (webTab.IsLegacyExternal || webTab.IsLegacyLaunching)
                {
                    ShowLegacyContent(tab, webTab);
                }
                else if (webTab.IsSuspended)
                {
                    webTab.HostGrid.Children.Clear();
                    webTab.HostGrid.Children.Add(CreateSuspendedPlaceholder(tab, webTab));
                    webTab.FlashOverlay?.Hide();
                }
                else if (webTab.IsCustomView)
                {
                    // Page d'accueil native : elle n'est jamais remplacée par un WebView2.
                    if (webTab.HostGrid.Children.Count == 0 ||
                        webTab.HostGrid.Children[0] is not EmptyStartPage)
                    {
                        webTab.HostGrid.Children.Clear();
                        webTab.HostGrid.Children.Add(CreateStartPageView(tab));
                    }

                    webTab.FlashOverlay?.Hide();
                }
                else if (webTab.Web != null)
                {
                    if (webTab.HostGrid.Children.Count != 1 ||
                        !ReferenceEquals(webTab.HostGrid.Children[0], webTab.Web))
                    {
                        webTab.HostGrid.Children.Clear();
                        webTab.HostGrid.Children.Add(webTab.Web);
                    }

                    ResumeWebView(webTab);
                    EnsurePendingNavigation(webTab);
                }

                ApplyLegacyRuleToMode(webTab);

                if (!webTab.IsLegacyExternal && !webTab.IsLegacyLaunching)
                    RestoreFlashOverlayForSelectedTab(tab, webTab);
                else
                    webTab.FlashOverlay?.Hide();

                UpdateAddressBarFromTab();
                return;
            }

            if (tab.Tag is ViewTabContent viewTab)
            {
                ApplyPrivateTheme(false);

                // Une vue ne peut avoir qu'un parent visuel.
                ReleaseSplitHosts();
                DetachFromParent(viewTab.View);
                WebHost.Content = viewTab.View;

                UpdateAddressBarFromTab();
                return;
            }

            ReleaseSplitHosts();
            WebHost.Content = null;
            ApplyPrivateTheme(false);
            UpdateFavoriteButton();
        }

        void ShowLegacyContent(TabItem tab, WebTabContent webTab)
        {
            if (webTab.LegacyView == null)
            {
                var view = new LegacyFlashView();

                view.SettingsRequested += OpenSettings;
                view.RetryRequested += () =>
                {
                    string? retryUrl = webTab.LegacyUrl ?? webTab.Web?.Source?.AbsoluteUri;
                    if (!Uri.TryCreate(retryUrl, UriKind.Absolute, out Uri? retryUri) ||
                        tab.Header is not BrowserTabHeader retryHeader)
                        return;

                    _ = LaunchLegacyIntoInternalTabAsync(webTab, retryUri, retryHeader);
                };

                webTab.LegacyView = view;
            }

            var legacyView = webTab.LegacyView;
            legacyView.SetUrl(webTab.LegacyUrl ?? webTab.Web?.Source?.AbsoluteUri ?? "");

            if (webTab.IsLegacyLaunching)
                legacyView.SetLaunching();
            else if (webTab.LegacyPid.HasValue)
                legacyView.SetLaunched(webTab.LegacyPid.Value);

            if (!string.IsNullOrWhiteSpace(webTab.LegacyLastError))
                legacyView.SetError(webTab.LegacyLastError);

            if (webTab.LegacyHwnd != IntPtr.Zero)
            {
                webTab.LegacyHost ??= new ExternalWindowDock();

                if (webTab.HostGrid.Children.Count != 1 ||
                    !ReferenceEquals(webTab.HostGrid.Children[0], webTab.LegacyHost))
                {
                    webTab.HostGrid.Children.Clear();
                    webTab.HostGrid.Children.Add(webTab.LegacyHost);
                }

                webTab.LegacyHost.Bind(webTab.LegacyHwnd);
                webTab.LegacyHost.ShowDock();

                // Position recalculée après le passage de layout WPF.
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    webTab.LegacyHost?.UpdateDockPosition();
                }), DispatcherPriority.Loaded);
            }
            else if (webTab.HostGrid.Children.Count != 1 ||
                     !ReferenceEquals(webTab.HostGrid.Children[0], legacyView))
            {
                // Vue « lancement / réessayer » tant que Basilisk n'a pas de fenêtre.
                webTab.HostGrid.Children.Clear();
                webTab.HostGrid.Children.Add(legacyView);
            }

            webTab.FlashOverlay?.Hide();
        }

        /// <summary>
        /// Une règle de domaine « Legacy » impose le mode Legacy à l'onglet.
        /// </summary>
        static void ApplyLegacyRuleToMode(WebTabContent content)
        {
            if (content.Web?.Source == null)
                return;

            if (FlashDomainRules.GetRule(content.Web.Source) == FlashRuleMode.Legacy &&
                content.FlashMode != FlashMode.Legacy)
            {
                content.FlashMode = FlashMode.Legacy;
            }
        }

        bool? _appliedPrivateTheme;

        /// <summary>
        /// La barre de navigation et l'onglet actif prennent une teinte violette en
        /// navigation privée. Auparavant toute la fenêtre était ré-animée (opacité) à
        /// chaque changement d'onglet, même sans changement de mode.
        /// </summary>
        void ApplyPrivateTheme(bool isPrivate)
        {
            if (_appliedPrivateTheme == isPrivate)
                return;

            _appliedPrivateTheme = isPrivate;

            Resources["SelectedTabBrush"] = FindResource(isPrivate ? "PrivateToolbarBrush" : "ToolbarBrush");
            Resources["AddressFieldBrush"] = FindResource(isPrivate ? "PrivateAddressBrush" : "AddressBarBrush");
            PrivatePill.Visibility = isPrivate ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            // Les TabItem internes (en-têtes) remontent aussi SelectionChanged : on filtre.
            if (!ReferenceEquals(e.OriginalSource, Tabs))
                return;

            foreach (var removed in e.RemovedItems)
            {
                if (removed is not TabItem oldTab || oldTab.Tag is not WebTabContent oldState)
                    continue;

                oldState.FlashOverlay?.DeactivateTabVisuals();

                // Basilisk est masqué quand on quitte son onglet.
                if (oldState.LegacyHost != null)
                    oldState.LegacyHost.HideDock();
                else if (oldState.LegacyHwnd != IntPtr.Zero)
                    ShowWindow(oldState.LegacyHwnd, SW_HIDE);

                // Un plein écran vidéo ne survit pas à un changement d'onglet.
                ExitHtmlFullscreenIfNeeded();
            }

            _addressBarEditing = false;
            HideCommandSuggestions();

            UpdateSplitForSelectionChange(
                e.RemovedItems.Count > 0 ? e.RemovedItems[0] as TabItem : null,
                Tabs.SelectedItem as TabItem);

            if (Tabs.SelectedItem is TabItem tab)
            {
                SyncWebHostWithSelection();

                if (tab.Tag is WebTabContent wt &&
                    wt.LegacyHwnd != IntPtr.Zero &&
                    wt.LegacyHost != null &&
                    (wt.IsLegacyExternal || wt.IsLegacyLaunching))
                {
                    Dispatcher.BeginInvoke(new Action(() =>
                    {
                        wt.LegacyHost.ShowDock();
                        wt.LegacyHost.UpdateDockPosition();
                    }), DispatcherPriority.Loaded);
                }
            }

            UpdateManualLegacyButton();
            UpdateNavButtonsFast();

            Dispatcher.BeginInvoke(UpdateFillCredentialButtonState);
        }
    }
}
