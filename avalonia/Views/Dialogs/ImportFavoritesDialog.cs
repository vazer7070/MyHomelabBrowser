using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Import;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Dialogs
{
    /// <summary>
    /// Venir d'un autre navigateur (Chrome, Edge, Brave, Vivaldi, Opera, Firefox) : ses favoris et
    /// son historique, ou un fichier de favoris HTML. Les mots de passe ont leur propre import
    /// (fichier CSV exporté, voir <see cref="ImportPasswordsDialog"/>).
    /// </summary>
    public static class ImportFavoritesDialog
    {
        public static async Task ShowAsync(MainWindow window)
        {
            IReadOnlyList<BookmarkSource> sources = await Task.Run(BookmarkImporter.DetectSources);
            var dialog = new FormDialog(Tr("Importer depuis un autre navigateur"), Tr("Importer"));
            dialog.AddText(sources.Count > 0
                ? Tr("Les favoris importés sont rangés dans le dossier « {0} ». Ceux que vous avez déjà ne sont pas dupliqués.", BookmarkImporter.DefaultFolder)
                : Tr("Aucun autre navigateur n'a été trouvé sur cet ordinateur. Vous pouvez importer un fichier de favoris exporté au format HTML."));

            var choices = sources.Select(s => s.Name).Append(Tr("Fichier HTML de favoris…")).ToList();
            ComboBox source = dialog.AddChoice(Tr("Depuis"), choices, 0);
            CheckBox favorites = dialog.AddCheck(Tr("Favoris"), true);
            CheckBox history = dialog.AddCheck(Tr("Historique (pages visitées, pour la barre d'adresse)"), true);
            void UpdateHistory()
            {
                bool available = source.SelectedIndex >= 0 && source.SelectedIndex < sources.Count &&
                                 HistoryImporter.HistoryPath(sources[source.SelectedIndex]) != null;
                history.IsEnabled = available;
                if (!available)
                    history.IsChecked = false;
            }
            source.SelectionChanged += (_, _) => UpdateHistory();
            UpdateHistory();
            dialog.AddText(Tr("Fermez l'autre navigateur avant l'import : ses données récentes sont parfois encore en mémoire."), hint: true);

            if (!await dialog.ShowAsync(window))
                return;

            int index = source.SelectedIndex;
            if (index >= 0 && index < sources.Count)
            {
                BookmarkSource chosen = sources[index];
                await ImportAsync(window, chosen, favorites.IsChecked == true, history.IsChecked == true);
                return;
            }

            IReadOnlyList<IStorageFile> files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Tr("Importer des favoris"),
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(Tr("Favoris (HTML)")) { Patterns = new[] { "*.html", "*.htm" } } }
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
                await ImportFavoritesAsync(window, () => BookmarkImporter.ParseNetscapeHtml(File.ReadAllText(path)), Path.GetFileName(path));
        }

        /// <summary>Favoris et historique d'un navigateur détecté, puis un seul message.</summary>
        public static async Task ImportAsync(MainWindow window, BookmarkSource source, bool favorites, bool history)
        {
            try
            {
                int addedFavorites = 0, addedPages = 0;
                if (favorites)
                {
                    IReadOnlyList<ImportedBookmark> bookmarks = await Task.Run(() => BookmarkImporter.Read(source));
                    addedFavorites = window.App.Favorites.Import(bookmarks);
                }
                if (history)
                {
                    List<HistoryEntry> pages = await Task.Run(() => HistoryImporter.Read(source));
                    addedPages = await window.App.History.ImportAsync(pages);
                }
                window.ShowToast(addedFavorites == 0 && addedPages == 0
                    ? Tr("Rien de nouveau à importer depuis {0}.", source.Name)
                    : Tr("Importés depuis {0} : {1} favoris, {2} pages de l'historique.", source.Name, addedFavorites, addedPages));
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[Import] " + ex.Message);
                window.ShowToast(Tr("Import impossible : {0}", ex.Message), warning: true);
            }
        }

        static async Task ImportFavoritesAsync(MainWindow window, Func<IReadOnlyList<ImportedBookmark>> read, string sourceName)
        {
            try
            {
                IReadOnlyList<ImportedBookmark> bookmarks = await Task.Run(read);
                int added = window.App.Favorites.Import(bookmarks);
                window.ShowToast(added == 0
                    ? Tr("Aucun nouveau favori dans {0}.", sourceName)
                    : Tr("{0} favoris importés depuis {1}.", added, sourceName));
            }
            catch (Exception ex)
            {
                window.ShowToast(Tr("Import impossible : {0}", ex.Message), warning: true);
            }
        }
    }
}
