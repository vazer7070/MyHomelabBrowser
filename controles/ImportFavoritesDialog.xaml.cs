using Microsoft.Win32;
using MyHomelabBrowser.classes.Import;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace MyHomelabBrowser.controles
{
    public partial class ImportFavoritesDialog : DialogWindow
    {
        public ImportFavoritesDialog()
        {
            InitializeComponent();

            List<SourceItem> sources = BookmarkImporter.DetectSources().Select(s => new SourceItem(s)).ToList();
            SourcesList.ItemsSource = sources;
            SourcesList.Visibility = sources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            NoSourceText.Visibility = sources.Count > 0 ? Visibility.Collapsed : Visibility.Visible;

            if (sources.Count > 0)
                SourcesList.SelectedIndex = 0;
        }

        /// <summary>
        /// Favoris lus, disponibles quand la boîte a été validée.
        /// </summary>
        public IReadOnlyList<ImportedBookmark> Result { get; private set; } = Array.Empty<ImportedBookmark>();

        public string SourceName { get; private set; } = string.Empty;

        private void SourcesList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            ImportButton.IsEnabled = SourcesList.SelectedItem is SourceItem;
        }

        private async void Import_Click(object sender, RoutedEventArgs e)
        {
            if (SourcesList.SelectedItem is SourceItem item)
                await ImportAsync(item.Source);
        }

        private async void ChooseFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Fichier de favoris exporté",
                Filter = "Favoris HTML (*.html;*.htm)|*.html;*.htm|Tous les fichiers (*.*)|*.*"
            };

            if (dialog.ShowDialog(this) == true)
                await ImportAsync(new BookmarkSource(Path.GetFileName(dialog.FileName), BookmarkSourceKind.NetscapeHtml, dialog.FileName));
        }

        private async Task ImportAsync(BookmarkSource source)
        {
            ImportButton.IsEnabled = false;
            ShowStatus($"Lecture des favoris de {source.Name}…");

            try
            {
                IReadOnlyList<ImportedBookmark> bookmarks = await Task.Run(() => BookmarkImporter.Read(source));
                if (bookmarks.Count == 0)
                {
                    ShowStatus($"Aucun favori trouvé dans {source.Name}.");
                    ImportButton.IsEnabled = SourcesList.SelectedItem is SourceItem;
                    return;
                }

                Result = bookmarks;
                SourceName = source.Name;
                DialogResult = true;
            }
            catch (Exception ex)
            {
                ShowStatus("Lecture impossible : " + ex.Message +
                           (source.Kind == BookmarkSourceKind.FirefoxPlaces ? " Fermez Firefox puis réessayez." : string.Empty));
                ImportButton.IsEnabled = SourcesList.SelectedItem is SourceItem;
            }
        }

        private void ShowStatus(string message)
        {
            StatusText.Text = message;
            StatusText.Visibility = Visibility.Visible;
        }

        private sealed class SourceItem
        {
            public SourceItem(BookmarkSource source)
            {
                Source = source;
            }

            public BookmarkSource Source { get; }
            public string Name => Source.Name;
            public string Detail => Source.Path;
            public string Icon => "\uE774";
        }
    }
}
