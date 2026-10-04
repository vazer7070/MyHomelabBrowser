using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Import;
using PommeBrowser.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Dialogs
{
    /// <summary>
    /// Accueil du tout premier lancement, pour passer d'un autre navigateur sans effort : reprendre
    /// ses favoris et son historique (le navigateur le plus utilisé est proposé), choisir son moteur
    /// de recherche, faire de PommeBrowser le navigateur par défaut, puis ses mots de passe. Tout
    /// reste dans les réglages ; « Plus tard » ne le repropose pas.
    /// </summary>
    public static class WelcomeDialog
    {
        public static async Task ShowAsync(MainWindow window)
        {
            BrowserApp app = window.App;
            IReadOnlyList<BookmarkSource> sources = await Task.Run(BookmarkImporter.DetectSources);
            bool? isDefault = await DefaultBrowser.IsDefaultAsync();

            var dialog = new FormDialog(Tr("Bienvenue dans PommeBrowser"), Tr("Commencer"), width: 520, cancelLabel: Tr("Plus tard"));
            dialog.AddText(Tr("Retrouvez vos habitudes en un instant. Tout reste modifiable dans les réglages."));

            ComboBox? source = null;
            CheckBox? favorites = null, history = null;
            if (sources.Count > 0)
            {
                var choices = sources.Select(s => s.Name).Append(Tr("Ne rien importer")).ToList();
                source = dialog.AddChoice(Tr("Importer depuis"), choices, MostRecentlyUsed(sources));
                favorites = dialog.AddCheck(Tr("Favoris"), true);
                history = dialog.AddCheck(Tr("Historique (pages visitées, pour la barre d'adresse)"), true);
                void Update()
                {
                    bool chosen = source.SelectedIndex >= 0 && source.SelectedIndex < sources.Count;
                    favorites.IsEnabled = chosen;
                    history.IsEnabled = chosen && HistoryImporter.HistoryPath(sources[source.SelectedIndex]) != null;
                    if (!history.IsEnabled)
                        history.IsChecked = false;
                }
                source.SelectionChanged += (_, _) => Update();
                Update();
            }

            List<BrowserSettings.SearchEngine> engines = Enum.GetValues<BrowserSettings.SearchEngine>().ToList();
            ComboBox search = dialog.AddChoice(Tr("Moteur de recherche"), engines.Select(UrlResolver.GetSearchEngineName).ToList(),
                Math.Max(0, engines.IndexOf(app.Settings.Search)));
            CheckBox? makeDefault = isDefault == true
                ? null
                : dialog.AddCheck(Tr("Ouvrir dans PommeBrowser les liens des autres applications (navigateur par défaut)"), true);
            CheckBox passwords = dialog.AddCheck(Tr("Importer aussi mes mots de passe (fichier exporté par l'autre navigateur)"), false);

            if (!await dialog.ShowAsync(window))
                return;

            if (search.SelectedIndex >= 0 && engines[search.SelectedIndex] != app.Settings.Search)
            {
                app.Settings.Search = engines[search.SelectedIndex];
                app.SettingsService.Save();
            }

            if (source != null && source.SelectedIndex >= 0 && source.SelectedIndex < sources.Count &&
                (favorites?.IsChecked == true || history?.IsChecked == true))
            {
                await ImportFavoritesDialog.ImportAsync(window, sources[source.SelectedIndex], favorites?.IsChecked == true, history?.IsChecked == true);
            }

            if (makeDefault?.IsChecked == true)
            {
                string? error = await DefaultBrowser.MakeDefaultAsync();
                if (error != null)
                    window.ShowToast(error, warning: true);
                else if (OperatingSystem.IsWindows())
                    window.ShowToast(Tr("Dans la fenêtre de Windows qui s'est ouverte, choisissez PommeBrowser pour les liens (HTTP et HTTPS)."), timeout: 15);
            }

            if (passwords.IsChecked == true)
                await ImportPasswordsDialog.ShowAsync(window);
        }

        /// <summary>Navigateur proposé : celui dont les favoris ou l'historique ont changé le plus récemment.</summary>
        static int MostRecentlyUsed(IReadOnlyList<BookmarkSource> sources)
        {
            int best = 0;
            DateTime latest = DateTime.MinValue;
            for (int i = 0; i < sources.Count; i++)
            {
                foreach (string? path in new[] { sources[i].Path, HistoryImporter.HistoryPath(sources[i]) })
                {
                    try
                    {
                        if (path != null && File.GetLastWriteTimeUtc(path) is var written && written > latest)
                        {
                            latest = written;
                            best = i;
                        }
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                    }
                }
            }
            return best;
        }
    }
}
