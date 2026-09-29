using System;
using System.Linq;
using Avalonia.Controls;
using MyHomelabBrowser.classes;
using PommeBrowser.Core;
using PommeBrowser.Views.Dialogs;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Pages
{
    /// <summary>Favoris : par dossier, ouverture, modification, import depuis les autres navigateurs.</summary>
    public sealed class FavoritesPage : PageBase
    {
        public FavoritesPage(MainWindow window) : base(window, Tr("Favoris"))
        {
            AddToolbar(TextButton(Tr("Importer…"), () => _ = ImportFavoritesDialog.ShowAsync(Window)));
            App.Favorites.Changed += ScheduleRefresh;
        }

        protected override void OnDisposed() => App.Favorites.Changed -= ScheduleRefresh;

        protected override void Build(StackPanel content)
        {
            if (App.Favorites.All.Count == 0)
            {
                content.Children.Add(EmptyState("IconStar", Tr("Aucun favori"),
                    Tr("Ajoutez une page avec l'étoile de la barre d'adresse (Ctrl+D), ou importez les favoris d'un autre navigateur.")));
                return;
            }

            var groups = App.Favorites.All
                .GroupBy(f => f.Folder ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(g => g.Key.Length == 0 ? 0 : 1)
                .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

            foreach (IGrouping<string, FavoriteItem> group in groups)
            {
                content.Children.Add(Heading(group.Key.Length == 0 ? Tr("Barre de favoris") : group.Key));
                content.Children.Add(Card(group.Select(favorite =>
                {
                    FavoriteItem target = favorite;
                    return Row(string.IsNullOrWhiteSpace(favorite.Title) ? UrlDisplay.ForDisplay(favorite.Url) : favorite.Title,
                        UrlDisplay.ForDisplay(favorite.Url),
                        SiteIcon(favorite.Url),
                        () => Window.Navigate(target.Url),
                        IconButton("IconOpen", Tr("Ouvrir dans un nouvel onglet"), () => Window.NewTab(target.Url, select: true)),
                        IconButton("IconEdit", Tr("Modifier"), () => _ = FavoriteDialog.EditAsync(Window, target)),
                        IconButton("IconDelete", Tr("Supprimer"), () => App.Favorites.Remove(target.Id)));
                }).ToArray()));
            }
        }
    }
}
