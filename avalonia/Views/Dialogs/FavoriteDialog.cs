using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using MyHomelabBrowser.classes;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Dialogs
{
    /// <summary>Modification d'un favori : titre, adresse, dossier ; ou suppression.</summary>
    public static class FavoriteDialog
    {
        public static async Task EditAsync(MainWindow window, FavoriteItem favorite)
        {
            BrowserApp app = window.App;
            var dialog = new FormDialog(Tr("Modifier le favori"), Tr("Enregistrer"));
            TextBox title = dialog.AddEntry(Tr("Titre"), favorite.Title);
            TextBox url = dialog.AddEntry(Tr("Adresse"), favorite.Url);
            var folders = app.Favorites.Folders.ToList();
            var folder = new AutoCompleteBox
            {
                Text = favorite.Folder ?? string.Empty,
                ItemsSource = folders,
                FilterMode = AutoCompleteFilterMode.ContainsOrdinal,
                PlaceholderText = Tr("Aucun (barre de favoris)"),
                MinimumPrefixLength = 0
            };
            dialog.Add(FormDialog.Labeled(Tr("Dossier (facultatif)"), folder));

            bool removed = false;
            dialog.AddExtraButton(Tr("Supprimer"), destructive: true, () =>
            {
                removed = true;
                dialog.Close();
            });

            void Validate() => dialog.SetConfirmEnabled(Dialogs.IsWebAddress(url.Text));
            url.TextChanged += (_, _) => Validate();
            Validate();

            if (await dialog.ShowAsync(window))
                app.Favorites.Update(favorite.Id, title.Text ?? string.Empty, url.Text ?? string.Empty, folder.Text);
            else if (removed)
                app.Favorites.Remove(favorite.Id);
        }
    }
}
