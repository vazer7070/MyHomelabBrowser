using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using MyHomelabBrowser.classes;
using PommeBrowser.Core;
using PommeBrowser.Views.Dialogs;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>Barre de favoris : favoris sans dossier, puis un menu par dossier. Masquée s'il n'y en a aucun.</summary>
    public sealed partial class MainWindow
    {
        void InitializeFavoritesBar()
        {
            App.Favorites.Changed += RefreshFavoritesBar;
            FaviconStore.FaviconUpdated += _ => RefreshFavoritesBar();
            Closed += (_, _) => App.Favorites.Changed -= RefreshFavoritesBar;
            RefreshFavoritesBar();
        }

        void RefreshFavoritesBar()
        {
            FavoritesBar.Children.Clear();
            IReadOnlyList<FavoriteItem> all = App.Favorites.All;
            FavoritesBarHost.IsVisible = all.Count > 0;

            foreach (FavoriteItem favorite in all.Where(f => string.IsNullOrWhiteSpace(f.Folder)))
                FavoritesBar.Children.Add(FavoriteButtonFor(favorite));

            foreach (string folder in App.Favorites.Folders)
            {
                var button = new Button { Content = BarLabel("IconFolder", null, folder) };
                button.Classes.Add("favorite");
                var items = all.Where(f => string.Equals(f.Folder, folder, StringComparison.CurrentCultureIgnoreCase))
                    .Select(f => (Control)FavoriteMenuItem(f))
                    .ToList();
                button.Flyout = new MenuFlyout { ItemsSource = items, Placement = PlacementMode.BottomEdgeAlignedLeft };
                FavoritesBar.Children.Add(button);
            }
            UpdateFavoriteButton(_selected);
        }

        Control BarLabel(string icon, Avalonia.Media.Imaging.Bitmap? favicon, string text)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            if (favicon != null)
            {
                row.Children.Add(new Image { Source = favicon, Width = 14, Height = 14 });
            }
            else
            {
                var glyph = new PathIcon { Width = 13, Height = 13 };
                glyph.Bind(PathIcon.DataProperty, this.GetResourceObservable(icon));
                row.Children.Add(glyph);
            }
            row.Children.Add(new TextBlock { Text = text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center });
            return row;
        }

        Button FavoriteButtonFor(FavoriteItem favorite)
        {
            string title = string.IsNullOrWhiteSpace(favorite.Title) ? UrlDisplay.Short(favorite.Url) : favorite.Title;
            var button = new Button { Content = BarLabel("IconGlobe", FaviconStore.TryGet(favorite.Url), title) };
            button.Classes.Add("favorite");
            ToolTip.SetTip(button, title + "\n" + UrlDisplay.ForDisplay(favorite.Url));
            button.Click += (_, _) => Navigate(favorite.Url);
            button.PointerReleased += (_, e) =>
            {
                if (e.InitialPressMouseButton == MouseButton.Middle)
                    NewTab(favorite.Url, select: false);
            };
            button.ContextMenu = new ContextMenu { ItemsSource = FavoriteActions(favorite) };
            return button;
        }

        MenuItem FavoriteMenuItem(FavoriteItem favorite)
        {
            var item = new MenuItem { Header = string.IsNullOrWhiteSpace(favorite.Title) ? favorite.Url : favorite.Title };
            if (FaviconStore.TryGet(favorite.Url) is { } favicon)
                item.Icon = new Image { Source = favicon, Width = 14, Height = 14 };
            item.Click += (_, _) => Navigate(favorite.Url);
            return item;
        }

        List<Control> FavoriteActions(FavoriteItem favorite) => new()
        {
            Item(Tr("Ouvrir dans un nouvel onglet"), () => NewTab(favorite.Url, select: true)),
            Item(Tr("Ouvrir dans un onglet privé"), () => NewTab(favorite.Url, select: true, isPrivate: true)),
            new Separator(),
            Item(Tr("Modifier…"), () => _ = FavoriteDialog.EditAsync(this, favorite)),
            Item(Tr("Supprimer"), () => App.Favorites.Remove(favorite.Id))
        };
    }
}
