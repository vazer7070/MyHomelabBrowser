using MyHomelabBrowser.controles;
using System.Windows;
using System.Windows.Controls;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // ---------------------------
        // Menu contextuel des onglets (clic droit sur l'en-tête)
        // ---------------------------
        void AttachTabContextMenu(TabItem tab, BrowserTabHeader header, WebTabContent content)
        {
            header.ContextMenu = new ContextMenu();
            header.ContextMenuOpening += (_, _) => BuildTabContextMenu(header.ContextMenu, tab, header, content);
        }

        void BuildTabContextMenu(ContextMenu menu, TabItem tab, BrowserTabHeader header, WebTabContent content)
        {
            menu.Items.Clear();

            MenuItem Add(string text, Action action, string? gesture = null, bool enabled = true)
            {
                var item = new MenuItem { Header = text, InputGestureText = gesture ?? string.Empty, IsEnabled = enabled };
                item.Click += (_, _) => action();
                menu.Items.Add(item);
                return item;
            }

            bool isWeb = !content.IsCustomView && content.Web != null;
            bool isSelected = ReferenceEquals(Tabs.SelectedItem, tab);

            Add(Tr("Nouvel onglet à droite"), () => CreateEmptyStartTab(Tabs.Items.IndexOf(tab) + 1));
            Add(Tr("Recharger"), () => ReloadTab(tab, content), isSelected ? "Ctrl+R" : null, isWeb);
            Add(Tr("Dupliquer"), () => DuplicateTab(tab, content), enabled: isWeb || content.IsCustomView);
            Add(content.IsPinned ? Tr("Détacher de la barre (désépingler)") : Tr("Épingler"), () =>
            {
                content.IsPinned = !content.IsPinned;
                ApplyPinState(tab, header, content.IsPinned);
            });

            menu.Items.Add(new Separator());

            if (ReferenceEquals(tab, _splitPartner) || (isSelected && IsSplitViewActive))
                Add(Tr("Quitter la vue côte à côte"), ExitSplitView);
            else if (!isSelected)
                Add(Tr("Afficher à côté de l’onglet actif"), () => ShowSideBySide(tab), enabled: IsSplitEligible(tab) && IsSplitEligible(Tabs.SelectedItem as TabItem));

            menu.Items.Add(new Separator());

            int index = Tabs.Items.IndexOf(tab);
            bool hasTabsToRight = Tabs.Items.OfType<TabItem>().Skip(index + 1).Any(t => t.Tag is not WebTabContent { IsPinned: true });

            Add(Tr("Fermer"), () => CloseTab(tab), isSelected ? "Ctrl+W" : null);
            Add(Tr("Fermer les autres onglets"), () => CloseTabsExcept(tab), enabled: Tabs.Items.Count > 1);
            Add(Tr("Fermer les onglets à droite"), () => CloseTabsToRight(tab), enabled: hasTabsToRight);

            menu.Items.Add(new Separator());
            Add(Tr("Rouvrir l’onglet fermé"), ReopenClosedTab, "Ctrl+Maj+T", HasRecentlyClosedTabs);
        }

        void ReloadTab(TabItem tab, WebTabContent content)
        {
            if (ReferenceEquals(Tabs.SelectedItem, tab))
            {
                ReloadCurrentTab(ignoreCache: false);
                return;
            }

            try
            {
                content.Web?.CoreWebView2?.Reload();
            }
            catch
            {
            }
        }

        void DuplicateTab(TabItem tab, WebTabContent content)
        {
            int index = Tabs.Items.IndexOf(tab) + 1;

            if (content.IsCustomView)
            {
                CreateEmptyStartTab(index);
                return;
            }

            string? url = content.PendingUrl ?? content.Web?.Source?.AbsoluteUri;
            if (!string.IsNullOrWhiteSpace(url))
                _ = CreateWebTabAsync(url, content.IsPrivate, insertIndex: index);
        }

        void CloseTabsExcept(TabItem keep)
        {
            foreach (TabItem tab in Tabs.Items.OfType<TabItem>()
                         .Where(t => !ReferenceEquals(t, keep) && t.Tag is not WebTabContent { IsPinned: true })
                         .ToList())
            {
                CloseTab(tab);
            }

            Tabs.SelectedItem = keep;
        }

        void CloseTabsToRight(TabItem from)
        {
            int index = Tabs.Items.IndexOf(from);
            foreach (TabItem tab in Tabs.Items.OfType<TabItem>()
                         .Skip(index + 1)
                         .Where(t => t.Tag is not WebTabContent { IsPinned: true })
                         .ToList())
            {
                CloseTab(tab);
            }
        }
    }
}
