using System;
using System.Collections.Generic;
using System.Linq;
using MyHomelabBrowser.classes.Profiles.Credentials;
using MyHomelabBrowser.classes.Security;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Ui
{
    /// <summary>
    /// Mots de passe du coffre : recherche, affichage, copie (effacée du presse-papiers après 30 s),
    /// codes de double authentification en direct, suppression.
    /// </summary>
    sealed class PasswordsView : PageView
    {
        readonly Action _vaultChanged;
        readonly Gtk.SearchEntry _search;
        readonly List<(Adw.ActionRow Row, TotpParameters Parameters)> _codes = new();
        readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
        uint _timer;

        public PasswordsView(BrowserApplication app, BrowserWindow window) : base(app, window)
        {
            _vaultChanged = ScheduleRefresh;
            _search = Gtk.SearchEntry.New();
            _search.SetPlaceholderText(Tr("Rechercher un site ou un identifiant"));
            _search.SetHexpand(true);
            _search.OnSearchChanged += (_, _) => ScheduleRefresh();
        }

        CredentialVaultService Service => App.Vault.Service;

        protected override void Subscribe()
        {
            App.Vault.Changed += _vaultChanged;
            // Codes 2FA : mis à jour chaque seconde tant que la page est affichée.
            _timer = GLib.Functions.TimeoutAddSeconds(0, 1, () =>
            {
                UpdateCodes();
                return true;
            });
        }

        protected override void Unsubscribe()
        {
            App.Vault.Changed -= _vaultChanged;
            if (_timer != 0)
            {
                GLib.Functions.SourceRemove(_timer);
                _timer = 0;
            }
        }

        protected override void Build(Gtk.Box content)
        {
            _codes.Clear();

            if (!Service.VaultExists)
            {
                content.Append(EmptyState("dialog-password-symbolic", Tr("Aucun coffre"),
                    Tr("Le coffre enregistre vos mots de passe et vos codes de double authentification, chiffrés avec un mot de passe que vous choisissez.")));
                content.Append(CenteredButton(Tr("Créer le coffre…"), () => App.Vault.EnsureUnlocked(Window.Window, ScheduleRefresh)));
                return;
            }

            if (!App.Vault.IsUnlocked)
            {
                content.Append(EmptyState("system-lock-screen-symbolic", Tr("Coffre verrouillé"),
                    Tr("Déverrouillez le coffre pour voir et utiliser vos identifiants.")));
                content.Append(CenteredButton(Tr("Déverrouiller…"), () => App.Vault.EnsureUnlocked(Window.Window, ScheduleRefresh)));
                return;
            }

            var bar = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            if (_search.GetParent() is Gtk.Box previous)
                previous.Remove(_search);
            bar.Append(_search);
            var changePassword = Gtk.Button.NewWithLabel(Tr("Changer le mot de passe…"));
            changePassword.OnClicked += (_, _) => App.Vault.ChangePassword(Window.Window, message => Window.ShowToast(message));
            bar.Append(changePassword);
            var lockButton = Gtk.Button.NewWithLabel(Tr("Verrouiller"));
            lockButton.OnClicked += (_, _) => App.Vault.Lock();
            bar.Append(lockButton);
            content.Append(bar);

            string[] tokens = _search.GetText().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            List<CredentialEntry> entries = Service.GetAll()
                .Where(e => tokens.All((e.DisplayHost + " " + e.Username).ToLowerInvariant().Contains))
                .OrderBy(e => e.DisplayHost, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.Username, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (entries.Count == 0)
            {
                content.Append(EmptyState("dialog-password-symbolic",
                    tokens.Length > 0 ? Tr("Aucun résultat") : Tr("Aucun identifiant enregistré"),
                    tokens.Length > 0
                        ? Tr("Aucun identifiant ne correspond à cette recherche.")
                        : Tr("Connectez-vous à un site : PommeBrowser vous proposera d'enregistrer le mot de passe.")));
                return;
            }

            content.Append(Heading(entries.Count == 1 ? Tr("1 identifiant") : Tr("{0} identifiants", entries.Count)));
            Gtk.ListBox list = BoxedList();
            foreach (CredentialEntry entry in entries)
                list.Append(EntryRow(entry));
            content.Append(list);
            UpdateCodes();
        }

        static Gtk.Widget CenteredButton(string label, Action onClick)
        {
            var button = Gtk.Button.NewWithLabel(label);
            button.AddCssClass("pill");
            button.AddCssClass("suggested-action");
            button.SetHalign(Gtk.Align.Center);
            button.OnClicked += (_, _) => onClick();
            return button;
        }

        Adw.ExpanderRow EntryRow(CredentialEntry entry)
        {
            var row = Adw.ExpanderRow.New();
            row.SetTitle(Escape(entry.DisplayHost));
            row.SetSubtitle(Escape(entry.Username.Length > 0 ? entry.Username : Tr("(sans identifiant)")));
            if (entry.HasTotp)
            {
                var badge = Gtk.Label.New("2FA");
                badge.AddCssClass("caption-heading");
                badge.AddCssClass("dim-label");
                badge.SetTooltipText(Tr("Code de double authentification enregistré"));
                row.AddSuffix(badge);
            }

            // Ligne dépliée gardée ouverte quand la page se met à jour (clé 2FA ajoutée…).
            string key = entry.Host + "\n" + entry.Username;
            row.SetExpanded(_expanded.Contains(key));
            row.OnNotify += (_, args) =>
            {
                if (args.Pspec.GetName() != "expanded")
                    return;
                if (row.GetExpanded())
                    _expanded.Add(key);
                else
                    _expanded.Remove(key);
            };

            // Identifiant
            var username = Adw.ActionRow.New();
            username.SetTitle(Tr("Identifiant"));
            username.SetSubtitle(Escape(entry.Username));
            username.AddCssClass("property");
            username.AddSuffix(CopyButton(Tr("Copier l'identifiant"), () => entry.Username, secret: false));
            row.AddRow(username);

            // Mot de passe, masqué par défaut
            const string Hidden = "••••••••••";
            var password = Adw.ActionRow.New();
            password.SetTitle(Tr("Mot de passe"));
            password.SetSubtitle(Hidden);
            password.AddCssClass("property");
            var reveal = Gtk.ToggleButton.New();
            reveal.SetIconName("view-reveal-symbolic");
            reveal.SetTooltipText(Tr("Afficher le mot de passe"));
            reveal.AddCssClass("flat");
            reveal.SetValign(Gtk.Align.Center);
            reveal.OnToggled += (_, _) =>
            {
                bool shown = reveal.GetActive();
                password.SetSubtitle(shown ? Escape(entry.Password) : Hidden);
                password.SetSubtitleSelectable(shown);
                reveal.SetIconName(shown ? "view-conceal-symbolic" : "view-reveal-symbolic");
            };
            password.AddSuffix(reveal);
            password.AddSuffix(CopyButton(Tr("Copier le mot de passe"), () => entry.Password, secret: true));
            row.AddRow(password);

            // Code de double authentification
            var code = Adw.ActionRow.New();
            code.SetTitle(Tr("Code 2FA"));
            code.AddCssClass("property");
            if (entry.HasTotp && Totp.TryParse(entry.TotpSecret, out TotpParameters parameters, out _))
            {
                _codes.Add((code, parameters));
                code.AddSuffix(CopyButton(Tr("Copier le code"), () => Totp.Generate(parameters, DateTimeOffset.UtcNow), secret: true));
                code.AddSuffix(SuffixButton("document-edit-symbolic", Tr("Modifier la clé 2FA"), () => EditTotp(entry)));
            }
            else
            {
                code.SetSubtitle(Tr("Aucune clé enregistrée"));
                var add = Gtk.Button.NewWithLabel(Tr("Ajouter…"));
                add.SetValign(Gtk.Align.Center);
                add.OnClicked += (_, _) => EditTotp(entry);
                code.AddSuffix(add);
            }
            row.AddRow(code);

            // Site
            var actions = Adw.ActionRow.New();
            actions.SetTitle(Tr("Site"));
            actions.SetSubtitle(Escape(entry.Host));
            actions.SetSubtitleLines(1);
            actions.AddCssClass("property");
            var open = Gtk.Button.NewWithLabel(Tr("Ouvrir le site"));
            open.SetValign(Gtk.Align.Center);
            open.OnClicked += (_, _) => Window.OpenFromPage(entry.Host + "/", newTab: true);
            actions.AddSuffix(open);
            var delete = Gtk.Button.NewWithLabel(Tr("Supprimer"));
            delete.AddCssClass("destructive-action");
            delete.SetValign(Gtk.Align.Center);
            delete.OnClicked += (_, _) => Dialogs.Confirm(Window.Window,
                Tr("Supprimer cet identifiant ?"),
                Tr("{0} sur {1} sera retiré du coffre.", entry.Username, entry.DisplayHost),
                Tr("Supprimer"), destructive: true,
                () =>
                {
                    Service.Delete(entry.Host, entry.Username);
                    App.Vault.NotifyChanged();
                    Window.ShowToast(Tr("Identifiant supprimé du coffre."));
                });
            actions.AddSuffix(delete);
            row.AddRow(actions);
            return row;
        }

        Gtk.Button CopyButton(string tooltip, Func<string> value, bool secret)
            => SuffixButton("edit-copy-symbolic", tooltip, () =>
            {
                if (!SecureClipboard.Copy(value(), secret))
                    return;
                Window.ShowToast(secret
                    ? Tr("Copié : effacé du presse-papiers dans 30 secondes.")
                    : Tr("Copié dans le presse-papiers."));
            });

        static Gtk.Button SuffixButton(string icon, string tooltip, Action onClick)
        {
            Gtk.Button button = Panels.SuffixButton(icon, tooltip);
            button.OnClicked += (_, _) => onClick();
            return button;
        }

        void UpdateCodes()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach ((Adw.ActionRow row, TotpParameters parameters) in _codes)
            {
                string code = Totp.FormatForDisplay(Totp.Generate(parameters, now));
                row.SetSubtitle(Tr("{0} — encore {1} s", code, Totp.SecondsRemaining(parameters, now)));
            }
        }

        void EditTotp(CredentialEntry entry)
        {
            var form = new FormDialog(Tr("Clé de double authentification"), Tr("Enregistrer"));
            form.AddText(Tr("Collez la clé fournie par {0} lors de l'activation de la double authentification : le texte en base32, ou l'adresse otpauth:// du QR code.", entry.DisplayHost));
            Adw.PasswordEntryRow key = FormDialog.PasswordRow(Tr("Clé"));
            form.AddGroup(null, key);
            if (entry.HasTotp)
            {
                var remove = Gtk.Button.NewWithLabel(Tr("Retirer la clé"));
                remove.AddCssClass("destructive-action");
                remove.AddCssClass("flat");
                remove.SetHalign(Gtk.Align.Start);
                remove.OnClicked += (_, _) =>
                {
                    Service.SetTotpSecret(entry.Host, entry.Username, null);
                    form.Dialog.Close();
                    App.Vault.NotifyChanged();
                    Window.ShowToast(Tr("Code de double authentification retiré."));
                };
                form.Add(remove);
            }

            form.Submit = () =>
            {
                string secret = key.GetText().Trim();
                if (!Totp.TryParse(secret, out _, out string error))
                    return System.Threading.Tasks.Task.FromResult<string?>(error);
                try
                {
                    Service.SetTotpSecret(entry.Host, entry.Username, secret);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException or UnauthorizedAccessException)
                {
                    return System.Threading.Tasks.Task.FromResult<string?>(ex.Message);
                }
                App.Vault.NotifyChanged();
                Window.ShowToast(Tr("Code de double authentification enregistré."));
                return System.Threading.Tasks.Task.FromResult<string?>(null);
            };
            form.Present(Window.Window);
            key.GrabFocus();
        }
    }
}
