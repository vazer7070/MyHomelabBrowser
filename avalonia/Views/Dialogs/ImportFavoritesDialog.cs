using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MyHomelabBrowser.classes.Import;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Dialogs
{
    /// <summary>Import des favoris d'un autre navigateur (Chrome, Edge, Brave, Vivaldi, Opera, Firefox) ou d'un fichier HTML.</summary>
    public static class ImportFavoritesDialog
    {
        public static async Task ShowAsync(MainWindow window)
        {
            IReadOnlyList<BookmarkSource> sources = BookmarkImporter.DetectSources();
            var dialog = new FormDialog(Tr("Importer des favoris"), Tr("Importer"));
            dialog.AddText(sources.Count > 0
                ? Tr("Les favoris importés sont rangés dans le dossier « {0} ». Ceux que vous avez déjà ne sont pas dupliqués.", BookmarkImporter.DefaultFolder)
                : Tr("Aucun autre navigateur n'a été trouvé sur cet ordinateur. Vous pouvez importer un fichier de favoris exporté au format HTML."));

            var choices = sources.Select(s => s.Name).Append(Tr("Fichier HTML de favoris…")).ToList();
            ComboBox source = dialog.AddChoice(Tr("Depuis"), choices, 0);

            if (!await dialog.ShowAsync(window))
                return;

            int index = source.SelectedIndex;
            if (index >= 0 && index < sources.Count)
            {
                BookmarkSource chosen = sources[index];
                await ImportAsync(window, () => BookmarkImporter.Read(chosen), chosen.Name);
                return;
            }

            IReadOnlyList<IStorageFile> files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Tr("Importer des favoris"),
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(Tr("Favoris (HTML)")) { Patterns = new[] { "*.html", "*.htm" } } }
            });
            if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
                await ImportAsync(window, () => BookmarkImporter.ParseNetscapeHtml(File.ReadAllText(path)), Path.GetFileName(path));
        }

        static async Task ImportAsync(MainWindow window, Func<IReadOnlyList<ImportedBookmark>> read, string sourceName)
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
