using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MyHomelabBrowser.classes.Profiles;
using PommeBrowser.Core;
using PommeBrowser.Linux.Core;
using PommeBrowser.Views.Dialogs;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>
    /// Profils protégés par mot de passe (même fichier profiles.json que les autres éditions) :
    /// chaque profil a ses réglages, favoris, historique, cookies et coffre. Ouvrir un profil
    /// relance PommeBrowser, qui reprend les onglets de ce profil.
    /// </summary>
    public sealed partial class MainWindow
    {
        static readonly string[] AvatarColors = { "#3B7BF6", "#16A34A", "#D97706", "#DB2777", "#7C3AED", "#0891B2", "#DC2626", "#4F46E5" };

        void InitializeProfileButton()
        {
            UserProfile? current = App.Profiles.Current;
            ProfileButton.Content = current == null ? DefaultAvatarIcon(15) : current.Username[..1].ToUpperInvariant();
            if (current != null)
            {
                ProfileButton.Background = AvatarBrush(current.Username);
                ProfileButton.Foreground = Brushes.White;
            }
            ToolTip.SetTip(ProfileButton, current == null ? Tr("Profil par défaut") : Tr("Profil : {0}", current.Username));
        }

        static IBrush AvatarBrush(string name)
        {
            int hash = 0;
            foreach (char c in name.ToLowerInvariant())
                hash = unchecked(hash * 31 + c);
            return SolidColorBrush.Parse(AvatarColors[Math.Abs(hash % AvatarColors.Length)]);
        }

        Control DefaultAvatarIcon(double size)
        {
            var icon = new PathIcon { Width = size, Height = size };
            icon.Bind(PathIcon.DataProperty, this.GetResourceObservable("IconPerson"));
            return icon;
        }

        Control Avatar(string? username, double size)
        {
            var circle = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2) };
            if (username == null)
            {
                circle.Bind(Border.BackgroundProperty, this.GetResourceObservable("SurfaceRaisedBrush"));
                circle.Child = new Panel { Children = { DefaultAvatarIcon(size * 0.5) }, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            }
            else
            {
                circle.Background = AvatarBrush(username);
                circle.Child = new TextBlock
                {
                    Text = username[..1].ToUpperInvariant(),
                    Foreground = Brushes.White,
                    FontWeight = FontWeight.SemiBold,
                    FontSize = size * 0.42,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };
            }
            return circle;
        }

        void Profile_Click(object? sender, RoutedEventArgs e)
        {
            UserProfile? current = App.Profiles.Current;
            var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };
            var box = new StackPanel { Width = 280, Spacing = 2 };

            var identity = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(6, 6, 6, 10) };
            identity.Children.Add(Avatar(current?.Username, 40));
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            names.Children.Add(new TextBlock { Text = current?.Username ?? Tr("Profil par défaut"), FontWeight = FontWeight.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
            var detail = new TextBlock { Text = current == null ? Tr("Sans mot de passe") : Tr("Protégé par mot de passe") };
            detail.Classes.Add("hint");
            names.Children.Add(detail);
            identity.Children.Add(names);
            box.Children.Add(identity);

            Button Row(Control icon, string label, Action action)
            {
                var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
                content.Children.Add(icon);
                content.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
                var button = new Button { Content = content, Padding = new Thickness(8, 6) };
                button.Classes.Add("flat");
                button.Click += (_, _) =>
                {
                    flyout.Hide();
                    action();
                };
                return button;
            }

            Control Glyph(string key)
            {
                var icon = new PathIcon { Width = 16, Height = 16 };
                icon.Bind(PathIcon.DataProperty, this.GetResourceObservable(key));
                return new Panel { Width = 24, Children = { icon } };
            }

            var others = App.Profiles.GetAllProfiles()
                .Where(p => current == null || !p.Username.Equals(current.Username, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (others.Count > 0 || current != null)
            {
                box.Children.Add(new Separator());
                var title = new TextBlock { Text = Tr("Changer de profil"), Margin = new Thickness(8, 4, 8, 2) };
                title.Classes.Add("hint");
                box.Children.Add(title);
                foreach (UserProfile profile in others)
                    box.Children.Add(Row(Avatar(profile.Username, 24), profile.Username, () => _ = SwitchProfileAsync(profile)));
                if (current != null)
                    box.Children.Add(Row(Avatar(null, 24), Tr("Profil par défaut"), () => _ = SwitchToDefaultAsync()));
            }

            box.Children.Add(new Separator());
            box.Children.Add(Row(Glyph("IconVault"), Tr("Coffre des mots de passe"), OpenPasswords));
            box.Children.Add(Row(Glyph("IconAdd"), Tr("Créer un profil…"), () => _ = CreateProfileAsync()));
            if (current != null)
            {
                box.Children.Add(Row(Glyph("IconEdit"), Tr("Modifier le profil…"), () => _ = EditProfileAsync(current)));
                box.Children.Add(Row(Glyph("IconSignOut"), Tr("Se déconnecter"), () => _ = SwitchToDefaultAsync()));
            }

            flyout.Content = box;
            flyout.ShowAt(ProfileButton);
        }

        static string? LockMessage(UserProfile profile)
            => profile.LoginLockUntilUtc is DateTime until && until > DateTime.UtcNow
                ? Tr("Trop de tentatives. Réessayez après {0:HH:mm:ss}.", until.ToLocalTime())
                : null;

        /// <summary>Le calcul de l'empreinte du mot de passe prend près d'une seconde : hors du fil de l'interface.</summary>
        async Task<string?> VerifyAsync(UserProfile profile, string password)
        {
            if (LockMessage(profile) is { } locked)
                return locked;
            bool valid = await Task.Run(() => App.Profiles.VerifyPassword(profile, password));
            return valid ? null : LockMessage(profile) ?? Tr("Mot de passe incorrect");
        }

        /// <summary>
        /// Changement de profil, puis ouverture du profil courant (sans relance si possible, voir
        /// BrowserApp.ChangeProfile) ; une erreur (disque, nom déjà pris) reste dans la boîte.
        /// </summary>
        string? ApplyProfileChange(Action change, bool relaunch = false)
        {
            try
            {
                App.ChangeProfile(change, relaunch);
                return null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }

        async Task SwitchProfileAsync(UserProfile profile)
        {
            var form = new FormDialog(Tr("Changer de profil"), Tr("Ouvrir"));
            form.AddText(BrowserApp.SwitchesProfileInPlace
                ? Tr("Mot de passe du profil « {0} ». Ses onglets remplacent ceux du profil actuel, qui les retrouvera à son retour.", profile.Username)
                : Tr("Mot de passe du profil « {0} ». PommeBrowser redémarre avec ce profil et rouvre ses onglets.", profile.Username));
            TextBox password = form.AddEntry(Tr("Mot de passe"), password: true);
            form.Submit = async () =>
                await VerifyAsync(profile, password.Text ?? string.Empty) ?? ApplyProfileChange(() => App.Profiles.LoginSilent(profile));
            await form.ShowAsync(this);
        }

        async Task SwitchToDefaultAsync()
        {
            bool inPlace = BrowserApp.SwitchesProfileInPlace;
            if (!await Dialogs.Dialogs.ConfirmAsync(this, Tr("Revenir au profil par défaut ?"),
                    inPlace
                        ? Tr("Les onglets du profil par défaut remplacent ceux du profil actuel, qui les retrouvera à son retour.")
                        : Tr("PommeBrowser redémarre avec le profil par défaut et rouvre ses onglets."),
                    inPlace ? Tr("Changer de profil") : Tr("Redémarrer")))
                return;
            if (ApplyProfileChange(App.Profiles.Logout) is { } error)
                ShowToast(error, warning: true);
        }

        async Task CreateProfileAsync()
        {
            var form = new FormDialog(Tr("Créer un profil"), Tr("Créer"));
            form.AddText(Tr("Le nouveau profil a ses propres réglages, favoris, historique, cookies et mots de passe."));
            TextBox name = form.AddEntry(Tr("Nom du profil"));
            TextBox password = form.AddEntry(Tr("Mot de passe (6 caractères minimum)"), password: true);
            TextBox confirm = form.AddEntry(Tr("Confirmer le mot de passe"), password: true);

            void Validate() => form.SetConfirmEnabled((name.Text ?? string.Empty).Trim().Length > 0 && !string.IsNullOrEmpty(password.Text) && !string.IsNullOrEmpty(confirm.Text));
            name.TextChanged += (_, _) => Validate();
            password.TextChanged += (_, _) => Validate();
            confirm.TextChanged += (_, _) => Validate();
            Validate();

            UserProfile? created = null;
            form.Submit = async () =>
            {
                string username = (name.Text ?? string.Empty).Trim();
                string secret = password.Text ?? string.Empty;
                if (!ProfileService.TryValidateUsername(username, out string error))
                    return error;
                if (App.Profiles.ProfileExists(username))
                    return Tr("Un profil porte déjà ce nom.");
                if (secret.Length < 6)
                    return Tr("Le mot de passe doit faire au moins 6 caractères.");
                if (secret != confirm.Text)
                    return Tr("Les mots de passe ne correspondent pas.");

                try
                {
                    await Task.Run(() => App.Profiles.CreateProfile(username, secret));
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    return ex.Message;
                }

                created = App.Profiles.FindProfile(username);
                return created == null ? Tr("Impossible de créer le profil.") : null;
            };
            await form.ShowAsync(this);

            // Le profil est créé sans quitter celui-ci : l'ouvrir relance PommeBrowser (ses cookies
            // et ses données de sites ne peuvent pas remplacer ceux des pages ouvertes), seulement si
            // on le demande.
            if (created == null)
                return;
            int choice = await Dialogs.Dialogs.ChoiceAsync(this, Tr("Profil « {0} » créé", created.Username),
                BrowserApp.SwitchesProfileInPlace
                    ? Tr("Ouvrir ce profil maintenant ? Les onglets ouverts restent dans le profil actuel et reviennent quand vous le rouvrez.")
                    : Tr("Ouvrir ce profil maintenant ? PommeBrowser redémarre avec lui. Les onglets ouverts restent dans le profil actuel et reviennent quand vous le rouvrez."),
                (Tr("Ouvrir maintenant"), true, false), (Tr("Plus tard"), false, false));
            if (choice == 0 && ApplyProfileChange(() => App.Profiles.LoginSilent(created)) is { } error)
                ShowToast(error, warning: true);
        }

        /// <summary>Nom, mot de passe ou suppression du profil ouvert : son mot de passe actuel est demandé.</summary>
        async Task EditProfileAsync(UserProfile profile)
        {
            var form = new FormDialog(Tr("Modifier le profil"), Tr("Enregistrer"));
            TextBox current = form.AddEntry(Tr("Mot de passe actuel"), password: true);
            TextBox name = form.AddEntry(Tr("Nom du profil"), profile.Username);
            TextBox password = form.AddEntry(Tr("Nouveau mot de passe (facultatif)"), password: true);
            TextBox confirm = form.AddEntry(Tr("Confirmer le nouveau mot de passe"), password: true);

            void Validate() => form.SetConfirmEnabled(!string.IsNullOrEmpty(current.Text) && (name.Text ?? string.Empty).Trim().Length > 0);
            current.TextChanged += (_, _) => Validate();
            name.TextChanged += (_, _) => Validate();
            Validate();

            bool deleteRequested = false;
            form.AddExtraButton(Tr("Supprimer le profil…"), destructive: true, async () =>
            {
                if (string.IsNullOrEmpty(current.Text))
                {
                    form.ShowError(Tr("Saisissez le mot de passe actuel du profil pour le supprimer."));
                    current.Focus();
                    return;
                }
                if (await VerifyAsync(profile, current.Text) is { } refused)
                {
                    form.ShowError(refused);
                    return;
                }
                deleteRequested = true;
                form.Close();
            });

            form.Submit = async () =>
            {
                string newName = (name.Text ?? string.Empty).Trim();
                string newPassword = password.Text ?? string.Empty;
                bool renamed = !newName.Equals(profile.Username, StringComparison.Ordinal);

                if (renamed && !newName.Equals(profile.Username, StringComparison.OrdinalIgnoreCase))
                {
                    if (!ProfileService.TryValidateUsername(newName, out string error))
                        return error;
                    if (App.Profiles.ProfileExists(newName))
                        return Tr("Un profil porte déjà ce nom.");
                }
                if (newPassword.Length > 0 && newPassword.Length < 6)
                    return Tr("Le mot de passe doit faire au moins 6 caractères.");
                if (newPassword != (confirm.Text ?? string.Empty))
                    return Tr("Les mots de passe ne correspondent pas.");
                if (!renamed && newPassword.Length == 0)
                    return null;

                if (await VerifyAsync(profile, current.Text ?? string.Empty) is { } refused)
                    return refused;

                string oldName = profile.Username;
                if (!renamed)
                {
                    try
                    {
                        await Task.Run(() => App.Profiles.UpdateProfile(oldName, newPassword));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                    {
                        return ex.Message;
                    }
                    ShowToast(Tr("Mot de passe du profil modifié"));
                    return null;
                }

                // Nouveau nom : les dossiers du profil ne se déplacent qu'au démarrage, PommeBrowser redémarre.
                return ApplyProfileChange(() =>
                {
                    App.Profiles.UpdateProfile(newName, newPassword.Length > 0 ? newPassword : null);
                    ProfileStorage.ScheduleRename(oldName, newName);
                }, relaunch: true);
            };

            await form.ShowAsync(this);
            if (!deleteRequested)
                return;

            if (!await Dialogs.Dialogs.ConfirmAsync(this, Tr("Supprimer ce profil définitivement ?"),
                    Tr("Ses réglages, favoris, historique, cookies et mots de passe enregistrés sont effacés. PommeBrowser redémarre avec le profil par défaut."),
                    Tr("Supprimer"), destructive: true))
                return;

            string deleted = profile.Username;
            // Données du profil effacées au démarrage, moteur arrêté : PommeBrowser redémarre.
            if (ApplyProfileChange(() =>
                {
                    App.Profiles.DeleteCurrentProfile();
                    ProfileStorage.ScheduleDelete(deleted);
                }, relaunch: true) is { } failure)
            {
                ShowToast(failure, warning: true);
            }
        }
    }
}
