using MyHomelabBrowser.controles;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // ---------------------------
        // Vue côte à côte
        // ---------------------------
        // L'onglet sélectionné occupe le volet actif ; _splitPartner reste affiché dans
        // l'autre. Sélectionner le partenaire échange les rôles sans déplacer les volets.
        private TabItem? _splitPartner;
        private bool _splitActiveIsLeft = true;
        private Grid? _splitGrid;
        private readonly SplitPane[] _splitPanes = new SplitPane[2];

        private sealed class SplitPane
        {
            public required DockPanel Root { get; init; }
            public required Border Header { get; init; }
            public required TextBlock Title { get; init; }
            public required Border Host { get; init; }
            public TabItem? Tab { get; set; }
        }

        bool IsSplitViewActive => _splitPartner != null;

        bool IsSplitEligible(TabItem? tab)
            => tab != null &&
               tab.Tag is WebTabContent { IsLegacyExternal: false, IsLegacyLaunching: false, IsClosed: false } &&
               Tabs.Items.Contains(tab) &&
               !_tabsBeingClosed.Contains(tab);

        /// <summary>
        /// Affiche <paramref name="partner"/> à côté de l'onglet sélectionné.
        /// </summary>
        void ShowSideBySide(TabItem partner)
        {
            if (Tabs.SelectedItem is not TabItem selected ||
                ReferenceEquals(selected, partner) ||
                !IsSplitEligible(selected) ||
                !IsSplitEligible(partner))
            {
                return;
            }

            _splitPartner = partner;
            _splitActiveIsLeft = true;
            SyncWebHostWithSelection();
        }

        void ExitSplitView()
        {
            if (_splitPartner == null)
                return;

            _splitPartner = null;
            SyncWebHostWithSelection();
        }

        /// <summary>
        /// Menu « Vue côte à côte » : associe l'onglet le plus récemment utilisé, ou un
        /// nouvel onglet d'accueil s'il n'y en a pas d'autre.
        /// </summary>
        void ToggleSplitView()
        {
            if (IsSplitViewActive)
            {
                ExitSplitView();
                return;
            }

            if (Tabs.SelectedItem is not TabItem selected || !IsSplitEligible(selected))
                return;

            TabItem? partner = Tabs.Items.OfType<TabItem>()
                .Where(t => !ReferenceEquals(t, selected) && IsSplitEligible(t))
                .OrderByDescending(t => ((WebTabContent)t.Tag).LastActivated)
                .FirstOrDefault();

            if (partner == null)
            {
                CreateEmptyStartTab(Tabs.Items.IndexOf(selected) + 1);
                partner = Tabs.SelectedItem as TabItem;
                Tabs.SelectedItem = selected;
            }

            if (partner != null)
                ShowSideBySide(partner);
        }

        /// <summary>
        /// Appelé avant la mise à jour de l'affichage quand la sélection change.
        /// </summary>
        void UpdateSplitForSelectionChange(TabItem? previous, TabItem? current)
        {
            if (_splitPartner == null || current == null)
                return;

            if (ReferenceEquals(current, _splitPartner))
            {
                // Clic sur l'onglet de l'autre volet : les rôles s'échangent.
                if (IsSplitEligible(previous))
                {
                    _splitPartner = previous;
                    _splitActiveIsLeft = !_splitActiveIsLeft;
                }
                else
                {
                    _splitPartner = null;
                }
            }
        }

        /// <summary>
        /// Place le contenu de l'onglet sélectionné dans la zone principale, seul ou à côté
        /// du partenaire de la vue côte à côte.
        /// </summary>
        void ShowInWebHost(TabItem tab, WebTabContent webTab)
        {
            if (_splitPartner != null && (!IsSplitEligible(_splitPartner) || ReferenceEquals(_splitPartner, tab)))
                _splitPartner = null;

            if (_splitPartner == null || !IsSplitEligible(tab))
            {
                ReleaseSplitHosts();
                WebHost.Content = webTab.HostGrid;
                return;
            }

            var partnerContent = (WebTabContent)_splitPartner.Tag;
            PrepareSplitPartner(_splitPartner, partnerContent);

            Grid grid = EnsureSplitGrid();
            SplitPane active = _splitPanes[_splitActiveIsLeft ? 0 : 1];
            SplitPane other = _splitPanes[_splitActiveIsLeft ? 1 : 0];

            if (!ReferenceEquals(WebHost.Content, grid) ||
                !ReferenceEquals(active.Host.Child, webTab.HostGrid) ||
                !ReferenceEquals(other.Host.Child, partnerContent.HostGrid))
            {
                // Un élément n'a qu'un parent : on libère tout avant de recomposer.
                WebHost.Content = null;
                _splitPanes[0].Host.Child = null;
                _splitPanes[1].Host.Child = null;

                active.Host.Child = webTab.HostGrid;
                other.Host.Child = partnerContent.HostGrid;
                WebHost.Content = grid;
            }

            active.Tab = tab;
            other.Tab = _splitPartner;
            UpdateSplitHeaders();
        }

        /// <summary>
        /// Retire les hôtes d'onglets des volets (avant d'afficher un contenu seul).
        /// </summary>
        void ReleaseSplitHosts()
        {
            if (_splitGrid == null)
                return;

            if (ReferenceEquals(WebHost.Content, _splitGrid))
                WebHost.Content = null;

            foreach (SplitPane pane in _splitPanes)
            {
                pane.Host.Child = null;
                pane.Tab = null;
            }
        }

        void PrepareSplitPartner(TabItem tab, WebTabContent content)
        {
            content.LastActivated = DateTime.Now;

            if (content.IsSuspended)
            {
                content.IsSuspended = false;
                if (tab.Header is BrowserTabHeader header)
                    header.ShowSuspended(false);
            }

            if (content.IsCustomView)
            {
                if (content.HostGrid.Children.Count == 0 || content.HostGrid.Children[0] is not EmptyStartPage)
                {
                    content.HostGrid.Children.Clear();
                    content.HostGrid.Children.Add(CreateStartPageView(tab));
                }
                return;
            }

            if (content.Web == null)
                return;

            if (content.HostGrid.Children.Count != 1 || !ReferenceEquals(content.HostGrid.Children[0], content.Web))
            {
                content.HostGrid.Children.Clear();
                content.HostGrid.Children.Add(content.Web);
            }

            ResumeWebView(content);
            EnsurePendingNavigation(content);
        }

        void UpdateSplitHeaders()
        {
            if (_splitGrid == null || _splitPartner == null)
                return;

            for (int i = 0; i < 2; i++)
            {
                SplitPane pane = _splitPanes[i];
                bool isActive = (i == 0) == _splitActiveIsLeft;

                pane.Title.Text = pane.Tab?.Header is BrowserTabHeader header && !string.IsNullOrWhiteSpace(header.TabTitle)
                    ? header.TabTitle
                    : Tr("Nouvel onglet");

                pane.Header.SetResourceReference(Border.BorderBrushProperty, isActive ? "AccentBrush" : "BorderBrush");
                pane.Title.SetResourceReference(TextBlock.ForegroundProperty, isActive ? "TextPrimaryBrush" : "TextSecondaryBrush");
                pane.Header.ToolTip = isActive ? null : Tr("Cliquer pour activer ce volet");
            }
        }

        Grid EnsureSplitGrid()
        {
            if (_splitGrid != null)
                return _splitGrid;

            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 260 });

            _splitPanes[0] = CreateSplitPane(grid, 0);
            _splitPanes[1] = CreateSplitPane(grid, 2);

            // Pas d'aperçu : il serait dessiné sous les fenêtres natives du WebView2.
            var splitter = new GridSplitter
            {
                Width = 6,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch,
                ResizeDirection = GridResizeDirection.Columns,
                ResizeBehavior = GridResizeBehavior.PreviousAndNext,
                ShowsPreview = false,
                Focusable = false,
                Cursor = Cursors.SizeWE
            };
            splitter.SetResourceReference(Control.BackgroundProperty, "ChromeBrush");
            Grid.SetColumn(splitter, 1);
            grid.Children.Add(splitter);

            _splitGrid = grid;
            return grid;
        }

        SplitPane CreateSplitPane(Grid grid, int column)
        {
            var title = new TextBlock
            {
                FontSize = 12,
                Margin = new Thickness(12, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis
            };

            var closeIcon = new TextBlock { Text = "", FontSize = 10 };
            closeIcon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");

            var close = new Button
            {
                Content = closeIcon,
                ToolTip = Tr("Quitter la vue côte à côte"),
                Margin = new Thickness(0, 0, 4, 0),
                VerticalAlignment = VerticalAlignment.Center
            };
            close.SetResourceReference(StyleProperty, "InlineIconButtonStyle");
            close.SetResourceReference(Control.ForegroundProperty, "TextSecondaryBrush");
            close.Click += (_, _) => ExitSplitView();

            var headerContent = new DockPanel();
            DockPanel.SetDock(close, Dock.Right);
            headerContent.Children.Add(close);
            headerContent.Children.Add(title);

            var header = new Border
            {
                Height = 30,
                BorderThickness = new Thickness(0, 0, 0, 2),
                Cursor = Cursors.Hand,
                Child = headerContent
            };
            header.SetResourceReference(Border.BackgroundProperty, "ToolbarBrush");

            var host = new Border();
            host.SetResourceReference(Border.BackgroundProperty, "WindowBackgroundBrush");

            var root = new DockPanel();
            DockPanel.SetDock(header, Dock.Top);
            root.Children.Add(header);
            root.Children.Add(host);
            Grid.SetColumn(root, column);
            grid.Children.Add(root);

            var pane = new SplitPane { Root = root, Header = header, Title = title, Host = host };

            header.MouseLeftButtonUp += (_, e) =>
            {
                if (e.OriginalSource is DependencyObject source && FindParent<Button>(source) != null)
                    return;

                if (pane.Tab != null && !ReferenceEquals(Tabs.SelectedItem, pane.Tab))
                    Tabs.SelectedItem = pane.Tab;
            };

            return pane;
        }

        private void MainMenu_SplitView_Click(object sender, RoutedEventArgs e) => ToggleSplitView();
    }
}
