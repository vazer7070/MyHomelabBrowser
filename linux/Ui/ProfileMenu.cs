using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MyHomelabBrowser.classes.Profiles;
using PommeBrowser.Linux.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Ui
{
    /// <summary>
    /// Profils protégés par mot de passe (même fichier profiles.json que l'édition Windows) :
    /// chaque profil a ses réglages, favoris, historique, cookies et coffre. Ouvrir un profil
    /// relance PommeBrowser, qui reprend les onglets de ce profil.
    /// </summary>
    static class ProfileMenu
    {
        public static Gtk.MenuButton CreateButton(BrowserApplication app, BrowserWindow window)
        {
            UserProfile? current = app.Profiles.Current;
            var button = Gtk.MenuButton.New();
            button.SetChild(Avatar(current?.Username, 24));
            button.SetTooltipText(current == null ? Tr("Profil par défaut") : Tr("Profil : {0}", current.Username));
            button.AddCssClass("flat");
            var popover = Gtk.Popover.New();
            popover.OnShow += (_, _) => popover.SetChild(Build(app, window, popover));
            button.SetPopover(popover);
            return button;
        }

        /// <summary>Initiale sur une couleur tirée du nom (toujours la même), ou silhouette pour le profil par défaut.</summary>
        static Adw.Avatar Avatar(string? username, int size)
            => Adw.Avatar.New(size, username ?? "PommeBrowser", username != null);

        static Gtk.Widget Build(BrowserApplication app, BrowserWindow window, Gtk.Popover popover)
        {
            UserProfile? current = app.Profiles.Current;

            var box = Gtk.Box.New(Gtk.Orientation.Vertical, 4);
            box.SetSizeRequest(270, -1);

            var identity = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
            identity.SetMarginTop(6);
            identity.SetMarginBottom(6);
            identity.SetMarginStart(6);
            identity.SetMarginEnd(6);
            identity.Append(Avatar(current?.Username, 40));
            var names = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
            names.SetValign(Gtk.Align.Center);
            var name = Gtk.Label.New(current?.Username ?? Tr("Profil par défaut"));
            name.AddCssClass("heading");
            name.SetXalign(0);
            name.SetEllipsize(Pango.EllipsizeMode.End);
            var detail = Gtk.Label.New(current == null ? Tr("Sans mot de passe") : Tr("Protégé par mot de passe"));
            detail.AddCssClass("dim-label");
            detail.AddCssClass("caption");
            detail.SetXalign(0);
            names.Append(name);
            names.Append(detail);
            identity.Append(names);
            box.Append(identity);

            var others = app.Profiles.GetAllProfiles()
                .Where(p => current == null || !p.Username.Equals(current.Username, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (others.Count > 0 || current != null)
            {
                box.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
                var title = Gtk.Label.New(Tr("Changer de profil"));
                title.AddCssClass("dim-label");
                title.AddCssClass("caption-heading");
                title.SetXalign(0);
                title.SetMarginStart(8);
                title.SetMarginTop(4);
                box.Append(title);

                foreach (UserProfile profile in others)
                    box.Append(Row(Avatar(profile.Username, 24), profile.Username, () => { popover.Popdown(); SwitchTo(app, window, profile); }));
                if (current != null)
                    box.Append(Row(Avatar(null, 24), Tr("Profil par défaut"), () => { popover.Popdown(); SwitchToDefault(app, window); }));
            }

            box.Append(Gtk.Separator.New(Gtk.Orientation.Horizontal));
            box.Append(Row(Gtk.Image.NewFromIconName("list-add-symbolic"), Tr("Créer un profil…"), () => { popover.Popdown(); Create(app, window); }));
            if (current != null)
                box.Append(Row(Gtk.Image.NewFromIconName("document-edit-symbolic"), Tr("Modifier le profil…"), () => { popover.Popdown(); Edit(app, window, current); }));
            return box;
        }

        static Gtk.Button Row(Gtk.Widget icon, string label, Action onClick)
        {
            var content = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            content.Append(icon);
            var text = Gtk.Label.New(label);
            text.SetXalign(0);
            text.SetHexpand(true);
            text.SetEllipsize(Pango.EllipsizeMode.End);
            content.Append(text);

            var button = Gtk.Button.New();
            button.SetChild(content);
            button.AddCssClass("flat");
            button.OnClicked += (_, _) => onClick();
            return button;
        }

        static string? LockMessage(UserProfile profile)
            => profile.LoginLockUntilUtc is DateTime until && until > DateTime.UtcNow
                ? Tr("Trop de tentatives. Réessayez après {0:HH:mm:ss}.", until.ToLocalTime())
                : null;

        /// <summary>Le calcul de l'empreinte du mot de passe prend près d'une seconde : hors du fil de l'interface.</summary>
        static async Task<string?> VerifyAsync(BrowserApplication app, UserProfile profile, string password)
        {
            if (LockMessage(profile) is { } locked)
                return locked;
            bool valid = await Task.Run(() => app.Profiles.VerifyPassword(profile, password));
            return valid ? null : LockMessage(profile) ?? Tr("Mot de passe incorrect");
        }

        /// <summary>Changement de profil puis relance ; une erreur (disque, nom déjà pris) reste dans la boîte.</summary>
        static string? Apply(BrowserApplication app, Action change)
        {
            try
            {
                app.ChangeProfile(change);
                return null;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }

        static void SwitchTo(BrowserApplication app, BrowserWindow window, UserProfile profile)
        {
            var form = new FormDialog(Tr("Changer de profil"), Tr("Ouvrir"));
            form.AddText(Tr("Mot de passe du profil « {0} ». PommeBrowser redémarre avec ce profil et rouvre ses onglets.", profile.Username));
            Adw.PasswordEntryRow password = FormDialog.PasswordRow(Tr("Mot de passe"));
            form.AddGroup(null, password);
            form.Submit = async () =>
                await VerifyAsync(app, profile, password.GetText()) ?? Apply(app, () => app.Profiles.LoginSilent(profile));
            form.Present(window.Window);
            password.GrabFocus();
        }

        static void SwitchToDefault(BrowserApplication app, BrowserWindow window)
            => Dialogs.Confirm(window.Window, Tr("Revenir au profil par défaut ?"),
                Tr("PommeBrowser redémarre avec le profil par défaut et rouvre ses onglets."),
                Tr("Redémarrer"), destructive: false,
                () =>
                {
                    if (Apply(app, app.Profiles.Logout) is { } error)
                        window.ShowToast(error);
                });

        static void Create(BrowserApplication app, BrowserWindow window)
        {
            var form = new FormDialog(Tr("Créer un profil"), Tr("Créer"));
            form.AddText(Tr("Le nouveau profil a ses propres réglages, favoris, historique, cookies et mots de passe. PommeBrowser redémarre avec lui."));
            Adw.EntryRow name = FormDialog.EntryRow(Tr("Nom du profil"));
            Adw.PasswordEntryRow password = FormDialog.PasswordRow(Tr("Mot de passe (6 caractères minimum)"));
            Adw.PasswordEntryRow confirm = FormDialog.PasswordRow(Tr("Confirmer le mot de passe"));
            form.AddGroup(null, name, password, confirm);

            void Validate() => form.SetConfirmEnabled(name.GetText().Trim().Length > 0 && password.GetText().Length > 0 && confirm.GetText().Length > 0);
            name.OnChanged += (_, _) => Validate();
            password.OnChanged += (_, _) => Validate();
            confirm.OnChanged += (_, _) => Validate();
            Validate();

            form.Submit = async () =>
            {
                string username = name.GetText().Trim();
                string secret = password.GetText();
                if (!ProfileService.TryValidateUsername(username, out string error))
                    return error;
                if (app.Profiles.ProfileExists(username))
                    return Tr("Un profil porte déjà ce nom.");
                if (secret.Length < 6)
                    return Tr("Le mot de passe doit faire au moins 6 caractères.");
                if (secret != confirm.GetText())
                    return Tr("Les mots de passe ne correspondent pas.");

                try
                {
                    await Task.Run(() => app.Profiles.CreateProfile(username, secret));
                }
                catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                {
                    return ex.Message;
                }

                UserProfile? created = app.Profiles.FindProfile(username);
                return created == null ? Tr("Impossible de créer le profil.") : Apply(app, () => app.Profiles.LoginSilent(created));
            };
            form.Present(window.Window);
            name.GrabFocus();
        }

        /// <summary>Nom, mot de passe ou suppression du profil ouvert : son mot de passe actuel est demandé.</summary>
        static void Edit(BrowserApplication app, BrowserWindow window, UserProfile profile)
        {
            var form = new FormDialog(Tr("Modifier le profil"), Tr("Enregistrer"));
            Adw.PasswordEntryRow current = FormDialog.PasswordRow(Tr("Mot de passe actuel"));
            form.AddGroup(null, current);
            Adw.EntryRow name = FormDialog.EntryRow(Tr("Nom du profil"), profile.Username);
            Adw.PasswordEntryRow password = FormDialog.PasswordRow(Tr("Nouveau mot de passe (facultatif)"));
            Adw.PasswordEntryRow confirm = FormDialog.PasswordRow(Tr("Confirmer le nouveau mot de passe"));
            form.AddGroup(Tr("Modifications"), name, password, confirm);

            var delete = Gtk.Button.NewWithLabel(Tr("Supprimer le profil…"));
            delete.AddCssClass("destructive-action");
            delete.AddCssClass("flat");
            delete.SetHalign(Gtk.Align.Start);
            form.Add(delete);

            void Validate() => form.SetConfirmEnabled(current.GetText().Length > 0 && name.GetText().Trim().Length > 0);
            current.OnChanged += (_, _) => Validate();
            name.OnChanged += (_, _) => Validate();
            Validate();

            form.Submit = async () =>
            {
                string newName = name.GetText().Trim();
                string newPassword = password.GetText();
                bool renamed = !newName.Equals(profile.Username, StringComparison.Ordinal);

                if (renamed && !newName.Equals(profile.Username, StringComparison.OrdinalIgnoreCase))
                {
                    if (!ProfileService.TryValidateUsername(newName, out string error))
                        return error;
                    if (app.Profiles.ProfileExists(newName))
                        return Tr("Un profil porte déjà ce nom.");
                }
                if (newPassword.Length > 0 && newPassword.Length < 6)
                    return Tr("Le mot de passe doit faire au moins 6 caractères.");
                if (newPassword != confirm.GetText())
                    return Tr("Les mots de passe ne correspondent pas.");
                if (!renamed && newPassword.Length == 0)
                    return null;

                if (await VerifyAsync(app, profile, current.GetText()) is { } refused)
                    return refused;

                string oldName = profile.Username;
                if (!renamed)
                {
                    try
                    {
                        await Task.Run(() => app.Profiles.UpdateProfile(oldName, newPassword));
                    }
                    catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
                    {
                        return ex.Message;
                    }
                    window.ShowToast(Tr("Mot de passe du profil modifié"));
                    return null;
                }

                // Nouveau nom : les dossiers du profil changent, PommeBrowser redémarre.
                return Apply(app, () =>
                {
                    app.Profiles.UpdateProfile(newName, newPassword.Length > 0 ? newPassword : null);
                    ProfileData.ScheduleMove(oldName, newName);
                });
            };

            delete.OnClicked += async (_, _) =>
            {
                if (current.GetText().Length == 0)
                {
                    form.ShowError(Tr("Saisissez le mot de passe actuel du profil pour le supprimer."));
                    current.GrabFocus();
                    return;
                }
                delete.SetSensitive(false);
                string? refused = await VerifyAsync(app, profile, current.GetText());
                delete.SetSensitive(true);
                if (refused != null)
                {
                    form.ShowError(refused);
                    return;
                }

                form.Dialog.Close();
                Dialogs.Confirm(window.Window, Tr("Supprimer ce profil définitivement ?"),
                    Tr("Ses réglages, favoris, historique, cookies et mots de passe enregistrés sont effacés. PommeBrowser redémarre avec le profil par défaut."),
                    Tr("Supprimer"), destructive: true,
                    () =>
                    {
                        if (Apply(app, app.Profiles.DeleteCurrentProfile) is { } error)
                            window.ShowToast(error);
                    });
            };

            form.Present(window.Window);
            current.GrabFocus();
        }
    }
}
