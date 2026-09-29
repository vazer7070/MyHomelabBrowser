using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using PommeBrowser.Core;
using PommeBrowser.Engine;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>Raccourcis clavier, menu principal et menu des onglets.</summary>
    public sealed partial class MainWindow
    {
        void InitializeShortcuts()
        {
            AddHandler(KeyDownEvent, (_, e) =>
            {
                KeyModifiers modifiers = BrowserShortcuts.Normalize(e.KeyModifiers);
                if (!e.Handled && BrowserShortcuts.IsShortcut(e.Key, modifiers))
                    e.Handled = HandleShortcut(e.Key, modifiers);
                else if (!e.Handled && e.Key == Key.Escape && WindowState == WindowState.FullScreen && !_webFullscreen)
                {
                    SetFullscreen(false);
                    e.Handled = true;
                }
            }, RoutingStrategies.Tunnel);
        }

        /// <summary>Raccourci du navigateur (depuis la fenêtre ou depuis la page). Vrai s'il a été traité.</summary>
        public bool HandleShortcut(Key key, KeyModifiers modifiers)
        {
            bool ctrl = modifiers.HasFlag(KeyModifiers.Control);
            bool shift = modifiers.HasFlag(KeyModifiers.Shift);
            bool alt = modifiers.HasFlag(KeyModifiers.Alt);
            BrowserTab? tab = _selected;

            if (ctrl)
            {
                switch (key)
                {
                    case Key.T when shift: ReopenClosedTab(); return true;
                    case Key.T: NewTab(App.NewTabUrl, select: true); return true;
                    case Key.N when shift: NewTab(null, select: true, isPrivate: true); return true;
                    case Key.N: App.NewWindow(); return true;
                    case Key.W:
                    case Key.F4:
                        if (tab != null)
                            CloseTab(tab);
                        return true;
                    case Key.Tab: SelectRelative(shift ? -1 : 1); return true;
                    case Key.PageDown: SelectRelative(1); return true;
                    case Key.PageUp: SelectRelative(-1); return true;
                    case >= Key.D1 and <= Key.D8:
                        int index = key - Key.D1;
                        if (index < _tabs.Count)
                            SelectTab(_tabs[index]);
                        return true;
                    case Key.D9:
                        if (_tabs.Count > 0)
                            SelectTab(_tabs[^1]);
                        return true;
                    case Key.L: FocusAddressBar(); return true;
                    case Key.R: tab?.Reload(bypassCache: shift); return true;
                    case Key.F5: tab?.Reload(bypassCache: true); return true;
                    case Key.H: OpenHistory(); return true;
                    case Key.J: ShowDownloads(); return true;
                    case Key.D: ToggleFavorite(); return true;
                    case Key.OemComma: OpenSettings(); return true;
                    case Key.F: OpenFind(); return true;
                    case Key.G:
                        tab?.Engine?.FindNext(backward: shift);
                        return true;
                    case Key.P: tab?.Engine?.Print(); return true;
                    case Key.Delete when shift: _ = Dialogs.ClearDataDialog.ShowAsync(this); return true;
                    case Key.I when shift: tab?.Engine?.ShowDevTools(); return true;
                    case Key.OemPlus:
                    case Key.Add: ZoomStep(1); return true;
                    case Key.OemMinus:
                    case Key.Subtract: ZoomStep(-1); return true;
                    case Key.D0:
                    case Key.NumPad0: SetZoom(1); return true;
                }
                return false;
            }

            if (alt)
            {
                switch (key)
                {
                    case Key.Left: tab?.GoBack(); return true;
                    case Key.Right: tab?.GoForward(); return true;
                    case Key.Home:
                        tab?.ShowHome();
                        return true;
                    case Key.D: FocusAddressBar(); return true;
                }
                return false;
            }

            switch (key)
            {
                case Key.F5:
                case Key.BrowserRefresh:
                    tab?.Reload();
                    return true;
                case Key.F6: FocusAddressBar(); return true;
                case Key.F11: ToggleFullscreen(); return true;
                case Key.F12: tab?.Engine?.ShowDevTools(); return true;
                case Key.F3:
                    if (FindPopup.IsOpen)
                        tab?.Engine?.FindNext(backward: shift);
                    else
                        OpenFind();
                    return true;
                case Key.BrowserBack: tab?.GoBack(); return true;
                case Key.BrowserForward: tab?.GoForward(); return true;
            }
            return false;
        }

        // ---------------------------------------------------------------
        // Menus
        // ---------------------------------------------------------------

        MenuItem Item(string header, Action action, string? gesture = null, string? icon = null, bool enabled = true)
        {
            var item = new MenuItem { Header = header, IsEnabled = enabled, InputGesture = gesture != null ? KeyGesture.Parse(gesture) : null };
            if (icon != null)
            {
                var path = new PathIcon();
                path.Bind(PathIcon.DataProperty, this.GetResourceObservable(icon));
                item.Icon = path;
            }
            item.Click += (_, _) => action();
            return item;
        }

        void Menu_Click(object? sender, RoutedEventArgs e) => BuildMainMenu().ShowAt(MenuButton);

        MenuFlyout BuildMainMenu()
        {
            BrowserTab? tab = _selected;
            bool web = tab is { Page: TabPage.Web, Engine: not null };

            var zoom = Item(Tr("Zoom"), () => { }, icon: "IconZoomIn", enabled: web);
            zoom.ItemsSource = new List<Control>
            {
                Item(Tr("Zoom avant"), () => ZoomStep(1), "Ctrl+OemPlus"),
                Item(Tr("Zoom arrière"), () => ZoomStep(-1), "Ctrl+OemMinus"),
                Item(Tr("Taille réelle (100 %)"), () => SetZoom(1), "Ctrl+D0")
            };

            var menu = new MenuFlyout { Placement = PlacementMode.BottomEdgeAlignedRight };
            menu.ItemsSource = new List<Control>
            {
                Item(Tr("Nouvel onglet"), () => NewTab(App.NewTabUrl, select: true), "Ctrl+T", "IconAdd"),
                Item(Tr("Nouvel onglet privé"), () => NewTab(null, select: true, isPrivate: true), "Ctrl+Shift+N", "IconPrivate"),
                Item(Tr("Nouvelle fenêtre"), () => App.NewWindow(), "Ctrl+N", "IconWindow"),
                Item(Tr("Rouvrir l’onglet fermé"), ReopenClosedTab, "Ctrl+Shift+T", "IconUndo", App.ClosedTabs.Count > 0),
                new Separator(),
                Item(Tr("Rechercher dans la page…"), OpenFind, "Ctrl+F", "IconSearch", web),
                zoom,
                Item(IsSplitViewActive ? Tr("Quitter la vue côte à côte") : Tr("Vue côte à côte"), ToggleSplitView, icon: "IconSplitView", enabled: IsSplitViewActive || _tabs.Count > 1),
                BuildWorkspacesMenu(),
                Item(Tr("Imprimer…"), () => tab?.Engine?.Print(), "Ctrl+P", "IconPrint", web),
                new Separator(),
                Item(Tr("Historique"), OpenHistory, "Ctrl+H", "IconHistory"),
                Item(Tr("Téléchargements"), ShowDownloads, "Ctrl+J", "IconDownload"),
                Item(Tr("Favoris"), OpenFavorites, icon: "IconStar"),
                Item(Tr("Coffre des mots de passe"), OpenPasswords, icon: "IconVault"),
                Item(Tr("Ajouter la page aux services…"), AddCurrentPageAsService, icon: "IconAddService", enabled: web),
                Item(Tr("Importer des favoris…"), () => _ = Dialogs.ImportFavoritesDialog.ShowAsync(this), icon: "IconImport"),
                Item(Tr("Effacer les données de navigation…"), () => _ = Dialogs.ClearDataDialog.ShowAsync(this), "Ctrl+Shift+Delete", "IconDelete"),
                new Separator(),
                Item(WindowState == WindowState.FullScreen ? Tr("Quitter le plein écran") : Tr("Plein écran"), ToggleFullscreen, "F11", "IconFullScreen"),
                Item(Tr("Outils de développement"), () => tab?.Engine?.ShowDevTools(), "F12", "IconBug", web),
                Item(Tr("Paramètres"), () => OpenSettings(), "Ctrl+OemComma", "IconSettings"),
                Item(Tr("Signaler un problème"), OpenReport, icon: "IconBug"),
                Item(Tr("Diagnostic"), OpenDiagnostics, icon: "IconPulse"),
                new Separator(),
                new MenuItem { Header = "PommeBrowser " + AppInfo.DisplayVersion, IsEnabled = false }
            };
            return menu;
        }

        void Tab_ContextRequested(object? sender, ContextRequestedEventArgs e)
        {
            if ((sender as Control)?.DataContext is not BrowserTab tab)
                return;
            e.Handled = true;

            int index = _tabs.IndexOf(tab);
            bool isSelected = tab == _selected;
            bool isWeb = tab.WebUrl.Length > 0;
            bool hasTabsToRight = _tabs.Skip(index + 1).Any(t => !t.IsPinned);

            var items = new List<Control>
            {
                Item(Tr("Nouvel onglet à droite"), () => NewTab(null, select: true, index: index + 1)),
                Item(Tr("Recharger"), () => tab.Reload(), isSelected ? "Ctrl+R" : null, enabled: isWeb),
                Item(Tr("Dupliquer"), () => NewTab(tab.WebUrl, select: true, isPrivate: tab.IsPrivate, index: index + 1), enabled: isWeb),
                Item(tab.IsPinned ? Tr("Détacher de la barre (désépingler)") : Tr("Épingler"), () => TogglePin(tab)),
                Item(Tr("Déplacer dans une nouvelle fenêtre"), () => MoveToNewWindow(tab), enabled: _tabs.Count > 1),
                new Separator()
            };

            if (tab == _splitPartner || (isSelected && IsSplitViewActive))
                items.Add(Item(Tr("Quitter la vue côte à côte"), ExitSplitView));
            else if (!isSelected)
                items.Add(Item(Tr("Afficher à côté de l’onglet actif"), () => ShowSideBySide(tab)));

            items.Add(new Separator());
            items.Add(Item(Tr("Suspendre l’onglet"), () => SuspendTab(tab), enabled: !isSelected && tab.Engine != null));
            items.Add(Item(Tr("Fermer"), () => CloseTab(tab), isSelected ? "Ctrl+W" : null));
            items.Add(Item(Tr("Fermer les autres onglets"), () => CloseOtherTabs(tab), enabled: _tabs.Count > 1));
            items.Add(Item(Tr("Fermer les onglets à droite"), () =>
            {
                foreach (BrowserTab right in _tabs.Skip(index + 1).Where(t => !t.IsPinned).ToList())
                    CloseTab(right);
            }, enabled: hasTabsToRight));
            items.Add(new Separator());
            items.Add(Item(Tr("Rouvrir l’onglet fermé"), ReopenClosedTab, "Ctrl+Shift+T", enabled: App.ClosedTabs.Count > 0));

            new MenuFlyout { ItemsSource = items }.ShowAt((Control)sender!, showAtPointer: true);
        }
    }
}
