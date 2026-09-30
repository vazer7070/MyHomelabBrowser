using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MyHomelabBrowser.classes.Profiles.Credentials;
using MyHomelabBrowser.classes.Security;
using PommeBrowser.Linux.Core;
using PommeBrowser.Linux.Web;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Ui
{
    /// <summary>
    /// Coffre des mots de passe du profil (même fichier chiffré que l'édition Windows : AES-GCM,
    /// clé dérivée du mot de passe du coffre). Propose d'enregistrer les identifiants après une
    /// connexion, remplit les formulaires et les codes de double authentification.
    /// </summary>
    sealed class Vault
    {
        static readonly TimeSpan PromptCooldown = TimeSpan.FromSeconds(15);

        readonly Dictionary<string, DateTime> _recentPrompts = new(StringComparer.OrdinalIgnoreCase);
        bool _prompting;
        bool _busy;

        public Vault()
        {
            Service = new CredentialVaultService(() => LinuxPaths.Profile("vault.json.enc"));
        }

        public CredentialVaultService Service { get; }

        /// <summary>Coffre verrouillé, déverrouillé ou modifié.</summary>
        public event Action? Changed;

        public bool IsUnlocked => !_busy && Service.IsUnlocked;

        public void NotifyChanged() => Changed?.Invoke();

        /// <summary>Comptes enregistrés pour une origine (coffre déverrouillé), le plus récent d'abord.</summary>
        public IReadOnlyList<CredentialEntry> ForOrigin(string origin)
            => IsUnlocked
                ? Service.GetAll().Where(x => string.Equals(x.Host, origin, StringComparison.OrdinalIgnoreCase)).ToList()
                : Array.Empty<CredentialEntry>();

        public void Lock()
        {
            Service.Lock();
            Changed?.Invoke();
        }

        // ---------------------------------------------------------------
        // Déverrouillage
        // ---------------------------------------------------------------

        /// <summary>Déverrouille le coffre (ou le crée), puis appelle <paramref name="onReady"/>.</summary>
        public void EnsureUnlocked(Gtk.Widget parent, Action onReady)
        {
            if (IsUnlocked)
                onReady();
            else if (Service.VaultExists)
                Unlock(parent, onReady);
            else
                Create(parent, onReady);
        }

        /// <summary>Calcul de la clé (PBKDF2) hors du fil de l'interface ; le coffre n'est pas lu pendant ce temps.</summary>
        async Task<T> RunAsync<T>(Func<T> work)
        {
            _busy = true;
            try
            {
                return await Task.Run(work);
            }
            finally
            {
                _busy = false;
            }
        }

        void Unlock(Gtk.Widget parent, Action onReady)
        {
            var form = new FormDialog(Tr("Déverrouiller le coffre"), Tr("Déverrouiller"));
            form.AddText(Tr("Saisissez le mot de passe du coffre pour utiliser vos identifiants enregistrés."));
            Adw.PasswordEntryRow password = FormDialog.PasswordRow(Tr("Mot de passe du coffre"));
            form.AddGroup(null, password);
            form.Submit = async () =>
            {
                if (LockMessage() is { } locked)
                    return locked;
                string text = password.GetText();
                if (!await RunAsync(() => Service.TryUnlock(text)))
                    return LockMessage() ?? Tr("Mot de passe du coffre incorrect.");
                Changed?.Invoke();
                MainThread.Post(onReady);
                return null;
            };
            form.Present(parent);
            password.GrabFocus();
        }

        string? LockMessage()
            => Service.UnlockAvailableAtUtc is DateTime until && until > DateTime.UtcNow
                ? Tr("Le coffre est temporairement verrouillé jusqu’à {0:HH:mm:ss}.", until.ToLocalTime())
                : null;

        void Create(Gtk.Widget parent, Action onReady)
        {
            var form = new FormDialog(Tr("Créer le coffre"), Tr("Créer"));
            form.AddText(Tr("Le coffre chiffre vos identifiants avec un mot de passe que vous choisissez. Il ne quitte jamais cet ordinateur et ne peut pas être récupéré s'il est oublié."));
            Adw.PasswordEntryRow password = FormDialog.PasswordRow(Tr("Mot de passe du coffre"));
            Adw.PasswordEntryRow confirm = FormDialog.PasswordRow(Tr("Confirmer le mot de passe"));
            form.AddGroup(null, password, confirm);
            form.Submit = async () =>
            {
                string text = password.GetText();
                if (text.Length < 6)
                    return Tr("Le mot de passe doit faire au moins 6 caractères.");
                if (text != confirm.GetText())
                    return Tr("Les mots de passe ne correspondent pas.");
                if (!await RunAsync(() => Service.TryInitializeNewVault(text)))
                    return Tr("Impossible de créer le coffre.");
                Changed?.Invoke();
                MainThread.Post(onReady);
                return null;
            };
            form.Present(parent);
            password.GrabFocus();
        }

        public void ChangePassword(Gtk.Widget parent, Action<string> done)
        {
            var form = new FormDialog(Tr("Changer le mot de passe du coffre"), Tr("Changer"));
            Adw.PasswordEntryRow current = FormDialog.PasswordRow(Tr("Mot de passe actuel du coffre"));
            Adw.PasswordEntryRow next = FormDialog.PasswordRow(Tr("Nouveau mot de passe du coffre"));
            Adw.PasswordEntryRow confirm = FormDialog.PasswordRow(Tr("Confirmer le nouveau mot de passe"));
            form.AddGroup(null, current, next, confirm);
            form.Submit = async () =>
            {
                string oldText = current.GetText();
                string newText = next.GetText();
                if (newText.Length < 6)
                    return Tr("Le mot de passe doit faire au moins 6 caractères.");
                if (newText != confirm.GetText())
                    return Tr("Les mots de passe ne correspondent pas.");
                if (!await RunAsync(() => Service.TryChangeVaultPassword(oldText, newText)))
                    return LockMessage() ?? Tr("Impossible de changer le mot de passe du coffre (ancien mot de passe incorrect ?).");
                Changed?.Invoke();
                done(Tr("Mot de passe du coffre mis à jour."));
                return null;
            };
            form.Present(parent);
            current.GrabFocus();
        }

        // ---------------------------------------------------------------
        // Enregistrement après une connexion
        // ---------------------------------------------------------------

        /// <summary>Identifiants envoyés par un formulaire de connexion (onglet non privé).</summary>
        public void OnSubmitted(BrowserWindow window, CredentialCandidate candidate)
        {
            string key = string.Join("|", candidate.Origin, candidate.Username, candidate.FormAction ?? string.Empty);
            DateTime now = DateTime.UtcNow;
            foreach (string expired in _recentPrompts.Where(x => now - x.Value > PromptCooldown).Select(x => x.Key).ToList())
                _recentPrompts.Remove(expired);
            if (_prompting || _recentPrompts.ContainsKey(key))
                return;
            _recentPrompts[key] = now;

            string site = CredentialOrigin.DisplayName(candidate.Origin);
            if (!Service.VaultExists)
            {
                Ask(window, candidate, existing: null);
                return;
            }

            // Les préférences par site sont chiffrées avec le reste : sans le coffre, on ne peut pas les lire.
            if (!IsUnlocked)
            {
                window.ShowToast(Tr("Enregistrer le mot de passe pour {0} ?", site), Tr("Déverrouiller"),
                    () => EnsureUnlocked(window.Window, () => Offer(window, candidate)));
                return;
            }

            Offer(window, candidate);
        }

        void Offer(BrowserWindow window, CredentialCandidate candidate)
        {
            if (!IsUnlocked || Service.GetPolicy(candidate.Origin) == CredentialSavePolicy.NeverSave)
                return;

            CredentialEntry? existing = Service.FindForOrigin(candidate.Origin, candidate.Username);
            if (existing != null && string.Equals(existing.Password, candidate.Password, StringComparison.Ordinal))
                return;

            if (Service.GetPolicy(candidate.Origin) == CredentialSavePolicy.AlwaysSave)
            {
                Save(window, candidate, alwaysSave: true, existing != null);
                return;
            }

            Ask(window, candidate, existing);
        }

        void Ask(BrowserWindow window, CredentialCandidate candidate, CredentialEntry? existing)
        {
            string site = CredentialOrigin.DisplayName(candidate.Origin);
            var dialog = Adw.AlertDialog.New(
                existing == null ? Tr("Enregistrer le mot de passe ?") : Tr("Mettre à jour le mot de passe ?"),
                Tr("{0} sur {1}", candidate.Username, site));
            var always = Gtk.CheckButton.NewWithLabel(Tr("Toujours enregistrer pour ce site, sans demander"));
            dialog.SetExtraChild(always);

            // « Jamais » est mémorisé dans le coffre : proposé seulement s'il existe déjà.
            if (Service.VaultExists)
                dialog.AddResponse("never", Tr("Jamais pour ce site"));
            dialog.AddResponse("later", Tr("Plus tard"));
            dialog.AddResponse("save", existing == null ? Tr("Enregistrer") : Tr("Mettre à jour"));
            dialog.SetResponseAppearance("save", Adw.ResponseAppearance.Suggested);
            dialog.SetDefaultResponse("save");
            dialog.SetCloseResponse("later");
            dialog.OnResponse += (_, args) =>
            {
                _prompting = false;
                switch (args.Response)
                {
                    case "save":
                        bool alwaysSave = always.GetActive();
                        EnsureUnlocked(window.Window, () => Save(window, candidate, alwaysSave, existing != null));
                        break;
                    case "never":
                        EnsureUnlocked(window.Window, () =>
                        {
                            Service.SetPolicy(candidate.Origin, CredentialSavePolicy.NeverSave);
                            window.ShowToast(Tr("Enregistrement désactivé pour {0}", site));
                        });
                        break;
                }
            };
            _prompting = true;
            dialog.Present(window.Window);
        }

        void Save(BrowserWindow window, CredentialCandidate candidate, bool alwaysSave, bool update)
        {
            try
            {
                Service.Upsert(candidate.Origin, candidate.Username, candidate.Password, candidate.FormAction, alwaysSave);
                if (!alwaysSave)
                    Service.SetPolicy(candidate.Origin, CredentialSavePolicy.Ask);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException or UnauthorizedAccessException)
            {
                window.ShowToast(ex.Message);
                return;
            }

            Changed?.Invoke();
            string site = CredentialOrigin.DisplayName(candidate.Origin);
            window.ShowToast(update ? Tr("Mot de passe mis à jour pour {0}", site) : Tr("Mot de passe enregistré pour {0}", site));
        }

        // ---------------------------------------------------------------
        // Remplissage
        // ---------------------------------------------------------------

        /// <summary>Remplissage automatique d'une page chargée : identifiant et mot de passe, jamais le code 2FA.</summary>
        public void AutoFill(BrowserTab tab)
        {
            if (!IsUnlocked || !CredentialOrigin.TryCreateTrusted(tab.Web.Url(), out string origin))
                return;

            CredentialEntry? credential = Service.FindForOrigin(origin);
            if (credential != null)
                _ = FillAsync(tab, credential, origin, otp: null);
        }

        /// <summary>Bouton « clé » de la barre d'adresse : choix du compte s'il y en a plusieurs.</summary>
        public void Fill(BrowserWindow window, BrowserTab tab)
        {
            if (!CredentialOrigin.TryCreateTrusted(tab.Web.Url(), out string origin))
                return;

            EnsureUnlocked(window.Window, () =>
            {
                IReadOnlyList<CredentialEntry> accounts = ForOrigin(origin);
                if (accounts.Count == 0)
                {
                    window.ShowToast(Tr("Aucun identifiant enregistré pour {0}", CredentialOrigin.DisplayName(origin)));
                    return;
                }

                if (accounts.Count == 1)
                {
                    _ = FillWithCodeAsync(window, tab, accounts[0], origin);
                    return;
                }

                var dialog = Adw.AlertDialog.New(Tr("Quel compte utiliser ?"), CredentialOrigin.DisplayName(origin));
                dialog.SetPreferWideLayout(false);
                for (int i = 0; i < accounts.Count && i < 8; i++)
                    dialog.AddResponse("account-" + i, accounts[i].Username.Length > 0 ? accounts[i].Username.Replace("_", "__") : Tr("(sans identifiant)"));
                dialog.AddResponse("cancel", Tr("Annuler"));
                dialog.SetCloseResponse("cancel");
                dialog.OnResponse += (sender, args) =>
                {
                    if (args.Response.StartsWith("account-", StringComparison.Ordinal) &&
                        int.TryParse(args.Response["account-".Length..], out int index) && index < accounts.Count)
                    {
                        _ = FillWithCodeAsync(window, tab, accounts[index], origin);
                    }
                };
                dialog.Present(window.Window);
            });
        }

        async Task FillWithCodeAsync(BrowserWindow window, BrowserTab tab, CredentialEntry credential, string origin)
        {
            string? code = CurrentCode(credential);
            string? result = await FillAsync(tab, credential, origin, code);
            if (code == null)
                return;

            if (result == "otp")
                window.ShowToast(Tr("Code 2FA rempli"));
            else if (SecureClipboard.Copy(code, secret: true))
                window.ShowToast(Tr("Code 2FA copié : collez-le à l'étape de double authentification (effacé du presse-papiers dans 30 s)."));
        }

        static async Task<string?> FillAsync(BrowserTab tab, CredentialEntry credential, string origin, string? otp)
        {
            try
            {
                return await CredentialCapture.FillAsync(tab.Web, credential, origin, otp);
            }
            catch (InvalidOperationException)
            {
                // Page en cours de fermeture ou de chargement : le remplissage est opportuniste.
                return null;
            }
        }

        /// <summary>Code de double authentification du moment, si une clé est enregistrée.</summary>
        public static string? CurrentCode(CredentialEntry credential)
            => credential.HasTotp && Totp.TryParse(credential.TotpSecret, out TotpParameters parameters, out _)
                ? Totp.Generate(parameters, DateTimeOffset.UtcNow)
                : null;
    }
}
