using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.controles;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace MyHomelabBrowser
{
    /// <summary>
    /// Barre d'outils : menus, favoris, badges et boutons de modules.
    /// </summary>
    public partial class MainWindow
    {
        // ---------------------------
        // Menus
        // ---------------------------
        static MenuItem? FindMenuItem(ItemsControl menu, string name)
        {
            foreach (var item in menu.Items)
            {
                if (item is MenuItem mi && mi.Name == name)
                    return mi;
            }

            return null;
        }

        void OpenMenu(ContextMenu menu, UIElement target)
        {
            menu.PlacementTarget = target;
            menu.Placement = PlacementMode.Bottom;
            menu.HorizontalOffset = 0;
            menu.VerticalOffset = 4;
            menu.IsOpen = true;
        }

        void ProfileButton_Click(object sender, RoutedEventArgs e)
        {
            ContextMenu menu;
            var current = _profileService.Current;

            if (current == null)
            {
                menu = (ContextMenu)Resources["ProfileContextMenu_LoggedOut"];
            }
            else
            {
                menu = (ContextMenu)Resources["ProfileContextMenu_LoggedIn"];

                if (FindMenuItem(menu, "SwitchProfileMenu") is MenuItem switchMenu)
                    PopulateSwitchProfileMenu(switchMenu);

                if (FindMenuItem(menu, "ProfileHeaderItem") is MenuItem header)
                {
                    header.Header = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children =
                        {
                            CreateAvatar(current.Username, 30, 13),
                            new StackPanel
                            {
                                Margin = new Thickness(12, 0, 0, 0),
                                VerticalAlignment = VerticalAlignment.Center,
                                Children =
                                {
                                    new TextBlock
                                    {
                                        Text = current.Username,
                                        FontWeight = FontWeights.SemiBold,
                                        FontSize = 13.5,
                                        Foreground = (Brush)FindResource("TextPrimaryBrush")
                                    },
                                    new TextBlock
                                    {
                                        Text = _vault.IsUnlocked ? "Coffre déverrouillé" : "Profil actif",
                                        FontSize = 11.5,
                                        Foreground = (Brush)FindResource("TextTertiaryBrush")
                                    }
                                }
                            }
                        }
                    };
                }
            }

            OpenMenu(menu, ProfileButton);
        }

        private void MainMenuButton_Click(object sender, RoutedEventArgs e)
        {
            var menu = (ContextMenu)Resources["MainMenu"];

            if (FindMenuItem(menu, "ReopenClosedMenuItem") is MenuItem reopen)
                reopen.IsEnabled = HasRecentlyClosedTabs;

            if (FindMenuItem(menu, "VersionMenuItem") is MenuItem version)
                version.Header = $"PommeBrowser {AppVersion.Current}";

            OpenMenu(menu, MainMenuButton);
        }

        private void MainMenu_NewTab_Click(object sender, RoutedEventArgs e) => NewTab_Click(sender, e);

        private void MainMenu_ReopenClosed_Click(object sender, RoutedEventArgs e) => ReopenClosedTab();

        private void MainMenu_History_Click(object sender, RoutedEventArgs e) => OpenHistory();

        private void MainMenu_Downloads_Click(object sender, RoutedEventArgs e) => DownloadsPopup.IsOpen = true;

        private void MainMenu_Fullscreen_Click(object sender, RoutedEventArgs e) => ToggleWindowFullscreen();

        private void MainMenu_ReportIssue_Click(object sender, RoutedEventArgs e)
        {
            var s = _settings.Settings;
            OpenReportIssueView(new ReportIssueOptions
            {
                IncludeLogs = s.ReportIncludeLogs,
                IncludePcInfo = s.ReportIncludePcInfo,
                IncludeMode = s.ReportIncludeMode,
                Module = ReportModule.Browser
            });
        }

        // ---------------------------
        // Favoris
        // ---------------------------
        void RefreshFavoritesBar()
        {
            if (FavoritesBar == null)
                return;

            FavoritesBar.Children.Clear();

            foreach (var fav in _favorites.Where(f => string.IsNullOrWhiteSpace(f.Folder)))
                FavoritesBar.Children.Add(CreateFavoriteButton(fav));

            var folders = _favorites
                .Where(f => !string.IsNullOrWhiteSpace(f.Folder))
                .GroupBy(f => f.Folder!.Trim(), StringComparer.CurrentCultureIgnoreCase);

            foreach (var folder in folders)
            {
                var root = new MenuItem
                {
                    Header = CreateFavoriteLabel("", null, folder.Key),
                    Padding = new Thickness(8, 4, 8, 4)
                };

                foreach (var fav in folder)
                {
                    var item = new MenuItem
                    {
                        Header = CreateFavoriteLabel(null, fav.Url, fav.Title),
                        ToolTip = fav.Url,
                        Tag = fav
                    };

                    item.Click += (_, _) => Navigate(fav.Url);
                    item.MouseRightButtonUp += (_, args) =>
                    {
                        args.Handled = true;
                        EditFavorite(fav);
                    };

                    root.Items.Add(item);
                }

                var menu = new Menu
                {
                    Background = Brushes.Transparent,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(2, 0, 2, 0)
                };
                menu.Items.Add(root);
                FavoritesBar.Children.Add(menu);
            }

            // La barre ne prend de place que s'il y a des favoris à afficher.
            FavoritesBarHost.Visibility = _favorites.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            RefreshStartPageFavorites();
        }

        StackPanel CreateFavoriteLabel(string? glyph, string? url, string title)
        {
            var panel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            ImageSource? icon = url != null ? FaviconStore.TryGet(url) : null;

            if (icon != null)
            {
                var image = new Image
                {
                    Width = 16,
                    Height = 16,
                    Margin = new Thickness(0, 0, 7, 0),
                    Source = icon
                };
                RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
                panel.Children.Add(image);
            }
            else
            {
                var glyphBlock = new TextBlock
                {
                    Text = glyph ?? "",
                    FontFamily = (FontFamily)FindResource("IconFont"),
                    FontSize = 13,
                    Margin = new Thickness(0, 0, 7, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = (Brush)FindResource(glyph != null ? "WarningBrush" : "TextTertiaryBrush")
                };
                panel.Children.Add(glyphBlock);
            }

            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(title) ? url ?? string.Empty : title,
                MaxWidth = 180,
                FontSize = 12.5,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("TextSecondaryBrush")
            });

            return panel;
        }

        Button CreateFavoriteButton(FavoriteItem fav)
        {
            var btn = new Button
            {
                Content = CreateFavoriteLabel(null, fav.Url, fav.Title),
                Tag = fav,
                ToolTip = $"{fav.Title}\n{fav.Url}",
                Padding = new Thickness(8, 3, 10, 3),
                Margin = new Thickness(0, 0, 2, 0),
                MinHeight = 26,
                Height = 26,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0)
            };

            btn.Click += (_, _) => Navigate(fav.Url);

            // Clic milieu : ouverture dans un nouvel onglet.
            btn.PreviewMouseDown += (_, args) =>
            {
                if (args.ChangedButton != System.Windows.Input.MouseButton.Middle)
                    return;

                args.Handled = true;
                CreateTab(fav.Url);
            };

            btn.MouseRightButtonUp += (_, args) =>
            {
                args.Handled = true;
                EditFavorite(fav);
            };

            return btn;
        }

        void EditFavorite(FavoriteItem fav)
        {
            var dlg = new EditFavoriteDialog(fav)
            {
                Owner = this
            };

            if (dlg.ShowDialog() != true)
                return;

            if (dlg.Deleted)
                RemoveFavorite(fav);
            else
                SaveFavorites();

            RefreshFavoritesBar();
            UpdateFavoriteButton();
            UpdateManualLegacyButton();
        }

        void RefreshStartPageFavorites()
        {
            foreach (var item in Tabs.Items)
            {
                if (item is TabItem { Tag: WebTabContent { IsCustomView: true } content } &&
                    content.HostGrid.Children.Count > 0 &&
                    content.HostGrid.Children[0] is EmptyStartPage page)
                {
                    page.SetFavorites(_favorites);
                }
            }
        }

        string? GetCurrentPageUrl()
        {
            if (Tabs.SelectedItem is not TabItem { Tag: WebTabContent content } || content.IsCustomView)
                return null;

            if (content.IsLegacyExternal && !string.IsNullOrWhiteSpace(content.LegacyUrl))
                return content.LegacyUrl;

            return content.PendingUrl ?? content.Web?.Source?.AbsoluteUri;
        }

        void UpdateFavoriteButton()
        {
            if (FavoriteButton == null)
                return;

            string? url = GetCurrentPageUrl();
            bool canFavorite = !string.IsNullOrWhiteSpace(url) &&
                               !url.StartsWith("about:", StringComparison.OrdinalIgnoreCase);

            bool isFav = canFavorite && _favorites.Any(f =>
                string.Equals(f.Url, url, StringComparison.OrdinalIgnoreCase));

            FavoriteButton.IsEnabled = canFavorite;
            FavoriteIconEmpty.Visibility = isFav ? Visibility.Collapsed : Visibility.Visible;
            FavoriteIconFilled.Visibility = isFav ? Visibility.Visible : Visibility.Collapsed;
            FavoriteButton.ToolTip = isFav ? "Retirer des favoris (Ctrl+D)" : "Ajouter aux favoris (Ctrl+D)";
        }

        // ---------------------------
        // Téléchargements
        // ---------------------------
        public void UpdateDownloadsBadge()
        {
            int count = DownloadManager.Instance.Items.Count(d => d.IsRunning);

            DownloadsBadge.Visibility = count > 0 ? Visibility.Visible : Visibility.Collapsed;
            DownloadsBadgeText.Text = Math.Min(count, 99).ToString();
            DownloadsEmptyText.Visibility = DownloadManager.Instance.Items.Count == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        private void DownloadsBtn_Click(object sender, RoutedEventArgs e)
        {
            UpdateDownloadsBadge();
            DownloadsPopup.IsOpen = !DownloadsPopup.IsOpen;
        }

        private void ClearCompletedDownloads(object sender, RoutedEventArgs e)
        {
            DownloadManager.Instance.RemoveFinished();
            UpdateDownloadsBadge();
        }

        private void OpenDownloadsFolder_Click(object sender, RoutedEventArgs e)
        {
            string folder = DownloadManager.Instance.DownloadFolder;

            try
            {
                Directory.CreateDirectory(folder);
                Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowToast("Dossier introuvable", ex.Message, ToastKind.Warning);
            }
        }

        // ---------------------------
        // Flash Legacy
        // ---------------------------
        void UpdateManualLegacyButton()
        {
            if (ManualLegacyButton == null)
                return;

            if (Tabs.SelectedItem is not TabItem { Tag: WebTabContent content } ||
                content.IsCustomView ||
                content.Web?.Source == null)
            {
                ManualLegacyButton.IsEnabled = false;
                ManualLegacyButton.Visibility = _legacyLauncher.CanLaunch() ? Visibility.Visible : Visibility.Collapsed;
                return;
            }

            var uri = content.Web.Source;

            bool isLegacyNow =
                content.FlashMode == FlashMode.Legacy ||
                content.IsLegacyExternal ||
                FlashDomainRules.GetRule(uri) == FlashRuleMode.Legacy;

            bool canLaunch = _legacyLauncher.CanLaunch();

            // Sans Basilisk configuré, le bouton n'apporte rien : il disparaît.
            ManualLegacyButton.Visibility = canLaunch || isLegacyNow ? Visibility.Visible : Visibility.Collapsed;
            ManualLegacyButton.IsEnabled = !isLegacyNow && canLaunch;
            ManualLegacyButton.ToolTip = isLegacyNow
                ? "Ce site est ouvert avec Flash Legacy (Basilisk)"
                : "Ouvrir ce site avec Flash Legacy (Basilisk)";
        }
    }
}
