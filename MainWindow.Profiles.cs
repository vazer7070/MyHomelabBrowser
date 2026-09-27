using Microsoft.Web.WebView2.Core;
using MyHomelabBrowser.classes.Profiles;
using MyHomelabBrowser.controles;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // ---------------------------
        // Données WebView2 des profils
        // ---------------------------

        /// <summary>
        /// Les cookies et sessions d'un profil vivent dans un dossier WebView2 nommé d'après
        /// lui : sans ce suivi, un profil renommé perdait toutes ses connexions.
        /// </summary>
        void OnProfileRenamed(string oldName, string newName)
        {
            string oldId = WebViewProfileData.NormalizeId(oldName);
            string newId = WebViewProfileData.NormalizeId(newName);
            if (oldId == newId)
                return;

            // L'environnement déjà ouvert continue de servir le profil sous son nouveau nom ;
            // son dossier, verrouillé, sera déplacé au prochain démarrage.
            bool inUse = _envByProfile.Remove(oldId, out CoreWebView2Environment? environment);
            if (environment != null)
                _envByProfile[newId] = environment;

            WebViewProfileData.MoveOrSchedule(oldId, newId, inUse);
        }

        /// <summary>
        /// Supprimer un profil laissait ses cookies et son cache sur le disque.
        /// </summary>
        async void OnProfileDeleted(string name)
        {
            string id = WebViewProfileData.NormalizeId(name);
            bool inUse = _envByProfile.Remove(id, out CoreWebView2Environment? environment);

            if (environment != null)
                await ClearBrowsingDataForEnvironmentAsync(environment);

            WebViewProfileData.DeleteOrSchedule(id, inUse);
        }

        async Task ClearBrowsingDataForEnvironmentAsync(CoreWebView2Environment environment)
        {
            // Effacement immédiat via un onglet encore ouvert : le dossier, lui, n'est
            // supprimé qu'une fois libéré par le navigateur.
            foreach (TabItem tab in Tabs.Items.OfType<TabItem>().ToList())
            {
                if (tab.Tag is WebTabContent { Web.CoreWebView2: CoreWebView2 core } &&
                    ReferenceEquals(core.Environment, environment))
                {
                    try
                    {
                        await core.Profile.ClearBrowsingDataAsync();
                    }
                    catch
                    {
                        // Le dossier sera de toute façon supprimé au prochain démarrage.
                    }
                    return;
                }
            }
        }

        public IEnumerable<UserProfile> AllProfiles
            => _profileService.GetAllProfiles();

        void SwitchProfile_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not MenuItem mi ||
                mi.DataContext is not UserProfile profile)
                return;

            var dlg = new LoginDialog(profile.Username)
            {
                Owner = this,
                Title = Tr("Changer de profil"),
                ValidateLogin = (_, p) => _profileService.VerifyPassword(profile, p),
                FailureMessageProvider = _ => BuildLoginFailureMessage(profile)
            };

            if (dlg.ShowDialog() != true)
                return;

            _profileService.LoginSilent(profile);
            RefreshProfileUI();
        }

        static string? BuildLoginFailureMessage(UserProfile? profile)
        {
            if (profile?.LoginLockUntilUtc is DateTime until && until > DateTime.UtcNow)
                return Tr("Trop de tentatives. Réessayez après {0:HH:mm:ss}.", until.ToLocalTime());

            return null;
        }

        static string GetProfileInitial(string? username)
        {
            string value = (username ?? string.Empty).Trim();
            return value.Length == 0 ? "?" : value[..1].ToUpperInvariant();
        }

        void PopulateSwitchProfileMenu(MenuItem parent)
        {
            parent.Items.Clear();

            foreach (var profile in _profileService.GetAllProfiles())
            {
                if (_profileService.Current != null &&
                    profile.Username.Equals(_profileService.Current.Username,
                                             StringComparison.OrdinalIgnoreCase))
                    continue;

                var username = profile.Username;

                var item = new MenuItem
                {
                    DataContext = profile,
                    Header = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Children =
                        {
                            CreateAvatar(username, 24, 11),
                            new TextBlock
                            {
                                Text = username,
                                Margin = new Thickness(10, 0, 0, 0),
                                VerticalAlignment = VerticalAlignment.Center
                            }
                        }
                    }
                };

                item.Click += SwitchProfile_Click;
                parent.Items.Add(item);
            }

            if (parent.Items.Count == 0)
            {
                parent.Items.Add(new MenuItem
                {
                    Header = Tr("Aucun autre profil"),
                    IsEnabled = false
                });
            }
        }

        Border CreateAvatar(string username, double size, double fontSize)
        {
            return new Border
            {
                Width = size,
                Height = size,
                CornerRadius = new CornerRadius(size / 2),
                Background = GetAvatarBrush(username),
                Child = new TextBlock
                {
                    Text = GetProfileInitial(username),
                    Foreground = Brushes.White,
                    FontWeight = FontWeights.SemiBold,
                    FontSize = fontSize,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
        }

        void RefreshProfileUI()
        {
            var current = _profileService.Current;
            bool loggedIn = current != null && !string.IsNullOrWhiteSpace(current.Username);

            if (!loggedIn)
            {
                ProfileButton.Content = "👤";
                ProfileButton.ClearValue(BackgroundProperty);
                ProfileButton.ToolTip = Tr("Profils : se connecter ou créer un profil");
                return;
            }

            ProfileButton.Content = GetProfileInitial(current!.Username);
            ProfileButton.Background = GetAvatarBrush(current.Username);
            ProfileButton.ToolTip = Tr("Profil : {0}", current.Username);
        }

        void Profile_Login_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new LoginDialog
            {
                Owner = this,
                Title = Tr("Connexion"),
                ValidateLogin = (u, p) =>
                {
                    var profile = _profileService.FindProfile(u);
                    return profile != null && _profileService.VerifyPassword(profile, p);
                },
                FailureMessageProvider = u => BuildLoginFailureMessage(_profileService.FindProfile(u))
            };

            if (dlg.ShowDialog() != true)
                return;

            var prof = _profileService.FindProfile(dlg.Username);
            if (prof == null)
                return;

            _profileService.LoginSilent(prof);
            RefreshProfileUI();
        }

        void Profile_Create_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new CreateProfileDialog
            {
                Owner = this,
                Title = Tr("Créer un profil"),
                UsernameExists = _profileService.ProfileExists
            };

            if (dlg.ShowDialog() != true)
                return;

            try
            {
                _profileService.CreateProfile(dlg.Username, dlg.Password);
                _profileService.Login(dlg.Username, dlg.Password);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                MessageBox.Show(this, ex.Message, Tr("Créer un profil"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            RefreshProfileUI();
        }

        static readonly Brush[] AvatarBrushes = CreateAvatarBrushes();

        static Brush[] CreateAvatarBrushes()
        {
            string[] colors = { "#FF3A6EA5", "#FF8E44AD", "#FF1F9D55", "#FFD9731A", "#FFD64545", "#FF16A085", "#FF5B6CE0" };
            var brushes = new Brush[colors.Length];
            for (int i = 0; i < colors.Length; i++)
            {
                var brush = (Brush)new BrushConverter().ConvertFromString(colors[i])!;
                brush.Freeze();
                brushes[i] = brush;
            }
            return brushes;
        }

        static Brush GetAvatarBrush(string username)
        {
            // string.GetHashCode est aléatoire à chaque lancement en .NET :
            // la couleur de l'avatar changeait à chaque démarrage.
            uint hash = 2166136261;
            foreach (char c in (username ?? string.Empty).Trim().ToLowerInvariant())
                hash = (hash ^ c) * 16777619;

            return AvatarBrushes[hash % (uint)AvatarBrushes.Length];
        }

        void Profile_Settings_Click(object sender, RoutedEventArgs e)
        {
            if (_profileService.Current == null)
                return;

            var dlg = new ProfileSettingsDialog(_profileService, _vault)
            {
                Owner = this
            };

            dlg.ShowDialog();
            RefreshProfileUI();

            // Le dialogue peut avoir déverrouillé ou modifié le coffre.
            UpdateFillCredentialButtonState();
        }

        void Profile_Logout_Click(object sender, RoutedEventArgs e)
        {
            if (_profileService.Current == null)
                return;

            _profileService.Logout();

        }
    }
}
