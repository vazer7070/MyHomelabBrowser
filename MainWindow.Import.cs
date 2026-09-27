using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Import;
using MyHomelabBrowser.controles;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // ---------------------------
        // Import des favoris d'un autre navigateur
        // ---------------------------
        void OpenImportFavorites()
        {
            var dialog = new ImportFavoritesDialog();
            if (!dialog.ShowFor(this))
                return;

            List<FavoriteItem> added = BookmarkImporter.SelectNew(_favorites, dialog.Result);
            int skipped = dialog.Result.Count - added.Count;

            if (added.Count > 0)
            {
                _favorites.AddRange(added);
                SaveFavorites();
                RefreshFavoritesBar();
                RefreshStartPageFavorites();
                UpdateFavoriteButton();
            }

            string message = added.Count switch
            {
                0 => "Tous ces favoris étaient déjà présents.",
                1 => "1 favori ajouté",
                _ => $"{added.Count} favoris ajoutés"
            };

            if (added.Count > 0 && skipped > 0)
                message += $" ({skipped} déjà présent{(skipped > 1 ? "s" : "")})";

            ShowToast("Import depuis " + dialog.SourceName, message, added.Count > 0 ? ToastKind.Success : ToastKind.Info);
        }
    }
}
