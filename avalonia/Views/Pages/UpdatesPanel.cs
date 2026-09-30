using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using PommeBrowser.Core;
using PommeBrowser.Updates;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Pages
{
    /// <summary>Section « Mises à jour » des paramètres : version, état, recherche et installation.</summary>
    public sealed class UpdatesPanel : UserControl
    {
        readonly MainWindow _window;
        readonly BrowserApp _app;
        readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
        readonly Button _check;
        readonly Button _restart;
        readonly Button _page;

        public UpdatesPanel(MainWindow window)
        {
            _window = window;
            _app = window.App;

            _check = new Button { Content = Tr("Vérifier les mises à jour") };
            _check.Classes.Add("primary");
            _check.Click += async (_, _) => await _app.CheckForUpdatesAsync(manual: true);
            _restart = new Button { Content = Tr("Redémarrer pour installer") };
            _restart.Click += (_, _) => _app.RestartToUpdate();
            _page = new Button { Content = Tr("Page des versions") };
            _page.Click += (_, _) =>
            {
                if (_app.Updater.ReleasePage is { } page)
                    _window.NewTab(page, select: true);
            };

            var auto = new CheckBox { Content = Tr("Rechercher automatiquement au démarrage"), IsChecked = _app.Settings.AutoUpdate };
            auto.IsCheckedChanged += (_, _) =>
            {
                _app.Settings.AutoUpdate = auto.IsChecked == true;
                _app.SettingsService.Save();
            };

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            buttons.Children.Add(_check);
            buttons.Children.Add(_restart);
            buttons.Children.Add(_page);

            var box = new StackPanel { Spacing = 10 };
            box.Children.Add(new TextBlock { Text = Tr("Version actuelle : {0}", AppInfo.DisplayVersion), FontWeight = FontWeight.SemiBold });
            box.Children.Add(_status);
            box.Children.Add(auto);
            box.Children.Add(buttons);
            var card = new Border { Child = box };
            card.Classes.Add("card");
            Content = card;

            _app.Updater.Changed += Refresh;
            DetachedFromVisualTree += (_, _) => _app.Updater.Changed -= Refresh;
            Refresh();
        }

        void Refresh()
        {
            IUpdater updater = _app.Updater;
            _status.Text = updater.Status.Length > 0 ? updater.Status : updater.CanUpdate
                ? Tr("Prêt à vérifier les mises à jour.")
                : Tr("Les mises à jour automatiques ne concernent que la version installée (AppImage sous Linux, installateur sous Windows).");
            _check.IsEnabled = !updater.IsBusy && updater.Installed == null;
            _restart.IsVisible = updater.Installed != null;
            _page.IsVisible = updater.ReleasePage != null;
        }
    }
}
