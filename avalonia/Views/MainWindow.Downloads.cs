using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using PommeBrowser.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>Bouton et panneau des téléchargements (comme l'édition Windows).</summary>
    public sealed partial class MainWindow
    {
        Flyout? _downloadsFlyout;
        StackPanel? _downloadsList;
        TextBlock? _downloadsEmpty;

        void InitializeDownloads()
        {
            App.Downloads.Changed += OnDownloadsChanged;
            App.Downloads.Started += OnDownloadStarted;
            App.Downloads.Completed += OnDownloadCompleted;
            UpdateDownloadsBadge();
        }

        void DisposeDownloads()
        {
            App.Downloads.Changed -= OnDownloadsChanged;
            App.Downloads.Started -= OnDownloadStarted;
            App.Downloads.Completed -= OnDownloadCompleted;
        }

        void OnDownloadsChanged()
        {
            UpdateDownloadsBadge();
            if (_downloadsFlyout?.IsOpen == true)
                RefreshDownloadsList();
        }

        void OnDownloadStarted(DownloadEntry entry)
        {
            if (IsActive)
                ShowDownloads();
        }

        void OnDownloadCompleted(DownloadEntry entry)
        {
            if (IsActive)
                ShowToast(Tr("Téléchargement terminé : {0}", entry.FileName), Tr("Ouvrir"), () => _ = OpenFileAsync(entry.Path));
        }

        void UpdateDownloadsBadge()
        {
            int active = App.Downloads.ActiveCount;
            DownloadsBadge.IsVisible = active > 0;
            DownloadsBadgeText.Text = active > 9 ? "9+" : active.ToString(Culture);
        }

        void Downloads_Click(object? sender, RoutedEventArgs e) => ShowDownloads();

        public void ShowDownloads()
        {
            if (_downloadsFlyout == null)
            {
                _downloadsList = new StackPanel { Spacing = 6 };
                _downloadsEmpty = new TextBlock { Text = Tr("Aucun téléchargement pour le moment."), Margin = new Thickness(0, 6, 0, 10) };
                _downloadsEmpty.Classes.Add("hint");

                var clear = new Button { Content = Tr("Effacer la liste"), FontSize = 12 };
                clear.Classes.Add("link");
                clear.Click += (_, _) => App.Downloads.ClearFinished();
                var folder = new Button { Content = Tr("Ouvrir le dossier"), FontSize = 12 };
                folder.Classes.Add("link");
                folder.Click += (_, _) => _ = OpenFolderAsync(App.DownloadDirectory);

                var header = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
                DockPanel.SetDock(clear, Dock.Right);
                DockPanel.SetDock(folder, Dock.Right);
                header.Children.Add(clear);
                header.Children.Add(folder);
                header.Children.Add(new TextBlock { Text = Tr("Téléchargements"), FontSize = 15, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });

                var body = new StackPanel { Width = 400 };
                body.Children.Add(header);
                body.Children.Add(_downloadsEmpty);
                body.Children.Add(new ScrollViewer { Content = _downloadsList, MaxHeight = 380 });

                _downloadsFlyout = new Flyout { Content = body, Placement = PlacementMode.BottomEdgeAlignedRight };
            }

            RefreshDownloadsList();
            _downloadsFlyout.ShowAt(DownloadsButton);
        }

        void RefreshDownloadsList()
        {
            if (_downloadsList == null || _downloadsEmpty == null)
                return;
            _downloadsList.Children.Clear();
            _downloadsEmpty.IsVisible = App.Downloads.Entries.Count == 0;
            foreach (DownloadEntry entry in App.Downloads.Entries)
                _downloadsList.Children.Add(DownloadRow(entry));
        }

        Control DownloadRow(DownloadEntry entry)
        {
            var icon = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 0, 10, 0) };
            icon.Bind(Border.BackgroundProperty, this.GetResourceObservable("AccentSoftBrush"));
            var glyph = new PathIcon { Width = 15, Height = 15 };
            glyph.Bind(PathIcon.DataProperty, this.GetResourceObservable("IconDocument"));
            glyph.Bind(PathIcon.ForegroundProperty, this.GetResourceObservable("AccentBrush"));
            icon.Child = glyph;

            var name = new TextBlock { Text = entry.FileName, FontWeight = FontWeight.SemiBold, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand) };
            ToolTip.SetTip(name, entry.Path);
            name.PointerReleased += (_, _) =>
            {
                if (entry.IsCompleted)
                    _ = OpenFileAsync(entry.Path);
            };

            var status = new TextBlock { Text = entry.SizeText + " · " + entry.StatusText, FontSize = 11.5, Margin = new Thickness(0, 3, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            status.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable(
                entry.IsCompleted ? "SuccessBrush" : entry.IsFailed ? "DangerBrush" : entry.IsRunning ? "AccentBrush" : "TextTertiaryBrush"));

            var texts = new StackPanel();
            texts.Children.Add(name);
            texts.Children.Add(status);
            if (entry.IsRunning)
            {
                texts.Children.Add(new ProgressBar
                {
                    Minimum = 0,
                    Maximum = 1,
                    Value = entry.Progress,
                    IsIndeterminate = !entry.HasProgress,
                    Margin = new Thickness(0, 7, 0, 0),
                    MinHeight = 4
                });
            }

            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(8, 0, 0, 0) };
            if (entry.IsRunning)
                actions.Children.Add(ActionButton("IconDismiss", Tr("Annuler"), () => entry.Download.Cancel()));
            if (entry.IsCompleted)
                actions.Children.Add(ActionButton("IconFolderOpen", Tr("Afficher dans le dossier"), () => _ = OpenFolderAsync(Path.GetDirectoryName(entry.Path))));
            if (entry.IsFinished)
                actions.Children.Add(ActionButton("IconDelete", Tr("Retirer de la liste"), () => App.Downloads.Remove(entry)));

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            grid.Children.Add(icon);
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);
            Grid.SetColumn(actions, 2);
            grid.Children.Add(actions);

            var card = new Border { Padding = new Thickness(12, 10), CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = grid };
            card.Bind(Border.BackgroundProperty, this.GetResourceObservable("SurfaceRaisedBrush"));
            card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BorderBrush"));
            return card;
        }

        Button ActionButton(string icon, string tip, Action action)
        {
            var glyph = new PathIcon { Width = 12, Height = 12 };
            glyph.Bind(PathIcon.DataProperty, this.GetResourceObservable(icon));
            var button = new Button { Content = glyph, Width = 28, Height = 28, Margin = new Thickness(2, 0, 0, 0) };
            button.Classes.Add("inline");
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => action();
            return button;
        }

        /// <summary>Ouvre un fichier avec l'application du système.</summary>
        public async Task OpenFileAsync(string? path)
        {
            if (path == null || !File.Exists(path))
            {
                ShowToast(Tr("Le fichier n'existe plus."), warning: true);
                return;
            }
            if (!await Launcher.LaunchFileInfoAsync(new FileInfo(path)))
                ShowToast(Tr("Aucune application ne sait ouvrir ce fichier."), warning: true);
        }

        public async Task OpenFolderAsync(string? directory)
        {
            if (directory == null || !Directory.Exists(directory))
            {
                ShowToast(Tr("Le dossier n'existe pas."), warning: true);
                return;
            }
            await Launcher.LaunchDirectoryInfoAsync(new DirectoryInfo(directory));
        }
    }
}
