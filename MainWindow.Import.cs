using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Import;
using MyHomelabBrowser.controles;
using static MyHomelabBrowser.classes.Localization.Loc;

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
                0 => Tr("Tous ces favoris étaient déjà présents."),
                1 => Tr("1 favori ajouté"),
                _ => Tr("{0} favoris ajoutés", added.Count)
            };

            if (added.Count > 0 && skipped > 0)
                message += Tr(" ({0} déjà présent{1})", skipped, (skipped > 1 ? "s" : ""));

            ShowToast(Tr("Import depuis {0}", dialog.SourceName), message, added.Count > 0 ? ToastKind.Success : ToastKind.Info);
        }
    }
}
