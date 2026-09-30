using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Profiles.Credentials;
using MyHomelabBrowser.classes.Security;
using PommeBrowser.Views;
using PommeBrowser.Views.Dialogs;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Core
{
    /// <summary>
    /// Coffre des mots de passe du profil (même fichier chiffré que les autres éditions : AES-GCM,
    /// clé dérivée du mot de passe du coffre). Propose d'enregistrer les identifiants après une
    /// connexion, remplit les formulaires et les codes de double authentification.
    /// </summary>
    public sealed class Vault : IDisposable
    {
        static readonly TimeSpan PromptCooldown = TimeSpan.FromSeconds(15);

        readonly Dictionary<string, DateTime> _recentPrompts = new(StringComparer.OrdinalIgnoreCase);
        readonly Func<int> _autoLockMinutes;
        readonly DispatcherTimer _autoLock;
        DateTime _lastUse = DateTime.UtcNow;
        bool _prompting;
        bool _busy;

        /// <param name="autoLockMinutes">Délai sans utilisation avant le verrouillage (0 : jamais), lu à chaque vérification.</param>
        public Vault(Func<int> autoLockMinutes)
        {
            Service = new CredentialVaultService(() => AppPaths.Profile("vault.json.enc"));
            _autoLockMinutes = autoLockMinutes;
            _autoLock = new DispatcherTimer(TimeSpan.FromSeconds(30), DispatcherPriority.Background, (_, _) => CheckAutoLock(DateTime.UtcNow));
            _autoLock.Start();
        }

        public CredentialVaultService Service { get; }

        /// <summary>Coffre verrouillé, déverrouillé ou modifié.</summary>
        public event Action? Changed;

        public bool IsUnlocked => !_busy && Service.IsUnlocked;

        public void NotifyChanged() => Changed?.Invoke();

        /// <summary>Comptes enregistrés pour une origine (coffre déverrouillé), le plus récent d'abord.</summary>
        public IReadOnlyList<CredentialEntry> ForOrigin(string origin)
        {
            if (!IsUnlocked)
                return Array.Empty<CredentialEntry>();
            Touch();
            return Service.GetAll().Where(x => string.Equals(x.Host, origin, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        public void Lock()
        {
            Service.Lock();
            Changed?.Invoke();
        }

        /// <summary>Coffre utilisé (déverrouillage, remplissage, page du coffre) : le délai de verrouillage repart.</summary>
        public void Touch() => _lastUse = DateTime.UtcNow;

        /// <summary>Verrouille le coffre resté inutilisé plus longtemps que le délai choisi (vérifié toutes les 30 s).</summary>
        internal void CheckAutoLock(DateTime nowUtc)
        {
            int minutes = _autoLockMinutes();
            if (minutes > 0 && IsUnlocked && !_prompting && nowUtc - _lastUse >= TimeSpan.FromMinutes(minutes))
                Lock();
        }

        /// <summary>Profil quitté : plus de vérification, coffre verrouillé.</summary>
        public void Dispose()
        {
            _autoLock.Stop();
            Service.Lock();
        }

        // ---------------------------------------------------------------
        // Déverrouillage
        // ---------------------------------------------------------------

        /// <summary>Déverrouille le coffre (ou le crée) ; vrai s'il est prêt.</summary>
        public Task<bool> EnsureUnlockedAsync(Window owner)
            => IsUnlocked ? Task.FromResult(true) : Service.VaultExists ? UnlockAsync(owner) : CreateAsync(owner);

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

        async Task<bool> UnlockAsync(Window owner)
        {
            var form = new FormDialog(Tr("Déverrouiller le coffre"), Tr("Déverrouiller"));
            form.AddText(Tr("Saisissez le mot de passe du coffre pour utiliser vos identifiants enregistrés."));
            TextBox password = form.AddEntry(Tr("Mot de passe du coffre"), password: true);
            bool forgotten = false;
            form.AddExtraButton(Tr("Mot de passe oublié…"), destructive: false, () =>
            {
                forgotten = true;
                form.Close();
            });
            form.Submit = async () =>
            {
                if (LockMessage() is { } locked)
                    return locked;
                string text = password.Text ?? string.Empty;
                (bool unlocked, VaultUnlockFailure failure) = await RunAsync(() => (Service.TryUnlock(text, out VaultUnlockFailure reason), reason));
                if (unlocked)
                {
                    Touch();
                    Changed?.Invoke();
                    return null;
                }
                if (failure == VaultUnlockFailure.Unreadable)
                {
                    ErrorLog.Write("Coffre", new System.IO.InvalidDataException(Service.LastUnlockError ?? "Coffre illisible"));
                    return Tr("Le fichier du coffre est illisible : il est abîmé ou d'un format inconnu (détails dans le journal des erreurs). Le mot de passe n'est pas en cause.");
                }
                return LockMessage() ?? Tr("Mot de passe du coffre incorrect. Vérifiez la saisie avec l'œil du champ (majuscules, accents, caractères spéciaux).");
            };
            if (await form.ShowAsync(owner))
                return true;
            return forgotten && await ResetAsync(owner);
        }

        /// <summary>
        /// Mot de passe perdu : l'ancien coffre est mis de côté (jamais effacé, il reste lisible si
        /// le mot de passe revient) et un nouveau coffre vide est créé.
        /// </summary>
        public async Task<bool> ResetAsync(Window owner)
        {
            if (!await Dialogs.ConfirmAsync(owner, Tr("Réinitialiser le coffre ?"),
                    Tr("Sans son mot de passe, le coffre ne peut pas être ouvert : personne ne peut le récupérer, pas même PommeBrowser. Un nouveau coffre vide va être créé. L'ancien n'est pas effacé : il est gardé de côté et pourra encore être ouvert si le mot de passe vous revient."),
                    Tr("Réinitialiser"), destructive: true))
                return false;

            string? archived;
            try
            {
                archived = Service.ResetVault();
            }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
            {
                ErrorLog.Write("Coffre", ex);
                await Dialogs.AlertAsync(owner, Tr("Réinitialiser le coffre ?"), Tr("Impossible de mettre l'ancien coffre de côté : {0}", ex.Message));
                return false;
            }
            if (archived != null)
                RuntimeLogBuffer.Append("[Coffre] Ancien coffre mis de côté : " + archived);
            Changed?.Invoke();

            bool created = await CreateAsync(owner);
            if (created && archived != null && owner is MainWindow window)
                window.ShowToast(Tr("Nouveau coffre créé. L'ancien est gardé dans {0}.", archived));
            return created;
        }

        string? LockMessage()
            => Service.UnlockAvailableAtUtc is DateTime until && until > DateTime.UtcNow
                ? Tr("Le coffre est temporairement verrouillé jusqu’à {0:HH:mm:ss}.", until.ToLocalTime())
                : null;

        async Task<bool> CreateAsync(Window owner)
        {
            var form = new FormDialog(Tr("Créer le coffre"), Tr("Créer"));
            form.AddText(Tr("Le coffre chiffre vos identifiants avec un mot de passe que vous choisissez. Il ne quitte jamais cet ordinateur et ne peut pas être récupéré s'il est oublié."));
            TextBox password = form.AddEntry(Tr("Mot de passe du coffre"), password: true);
            TextBox confirm = form.AddEntry(Tr("Confirmer le mot de passe"), password: true);
            form.Submit = async () =>
            {
                string text = password.Text ?? string.Empty;
                if (text.Length < 6)
                    return Tr("Le mot de passe doit faire au moins 6 caractères.");
                if (text != confirm.Text)
                    return Tr("Les mots de passe ne correspondent pas.");
                if (!await RunAsync(() => Service.TryInitializeNewVault(text)))
                    return Tr("Impossible de créer le coffre.");
                Touch();
                Changed?.Invoke();
                return null;
            };
            return await form.ShowAsync(owner);
        }

        /// <summary>Nouveau mot de passe du coffre ; message de confirmation, ou null si annulé.</summary>
        public async Task<string?> ChangePasswordAsync(Window owner)
        {
            var form = new FormDialog(Tr("Changer le mot de passe du coffre"), Tr("Changer"));
            TextBox current = form.AddEntry(Tr("Mot de passe actuel du coffre"), password: true);
            TextBox next = form.AddEntry(Tr("Nouveau mot de passe du coffre"), password: true);
            TextBox confirm = form.AddEntry(Tr("Confirmer le nouveau mot de passe"), password: true);
            form.Submit = async () =>
            {
                string oldText = current.Text ?? string.Empty;
                string newText = next.Text ?? string.Empty;
                if (newText.Length < 6)
                    return Tr("Le mot de passe doit faire au moins 6 caractères.");
                if (newText != confirm.Text)
                    return Tr("Les mots de passe ne correspondent pas.");
                if (!await RunAsync(() => Service.TryChangeVaultPassword(oldText, newText)))
                    return LockMessage() ?? Tr("Impossible de changer le mot de passe du coffre (ancien mot de passe incorrect ?).");
                Changed?.Invoke();
                return null;
            };
            return await form.ShowAsync(owner) ? Tr("Mot de passe du coffre mis à jour.") : null;
        }

        // ---------------------------------------------------------------
        // Enregistrement après une connexion
        // ---------------------------------------------------------------

        /// <summary>Identifiants envoyés par un formulaire de connexion (onglet non privé).</summary>
        public void OnSubmitted(MainWindow window, CredentialCandidate candidate)
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
                _ = AskAsync(window, candidate, existing: null);
                return;
            }

            // Les préférences par site sont chiffrées avec le reste : sans le coffre, on ne peut pas les lire.
            if (!IsUnlocked)
            {
                window.ShowToast(Tr("Enregistrer le mot de passe pour {0} ?", site), Tr("Déverrouiller"), async () =>
                {
                    if (await EnsureUnlockedAsync(window))
                        Offer(window, candidate);
                });
                return;
            }

            Offer(window, candidate);
        }

        void Offer(MainWindow window, CredentialCandidate candidate)
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

            _ = AskAsync(window, candidate, existing);
        }

        async Task AskAsync(MainWindow window, CredentialCandidate candidate, CredentialEntry? existing)
        {
            string site = CredentialOrigin.DisplayName(candidate.Origin);
            var dialog = new FormDialog(existing == null ? Tr("Enregistrer le mot de passe ?") : Tr("Mettre à jour le mot de passe ?"),
                existing == null ? Tr("Enregistrer") : Tr("Mettre à jour"), cancelLabel: Tr("Plus tard"));
            dialog.AddText(Tr("{0} sur {1}", candidate.Username, site));
            CheckBox always = dialog.AddCheck(Tr("Toujours enregistrer pour ce site, sans demander"), false);

            bool never = false;
            // « Jamais » est mémorisé dans le coffre : proposé seulement s'il existe déjà.
            if (Service.VaultExists)
            {
                dialog.AddExtraButton(Tr("Jamais pour ce site"), false, () =>
                {
                    never = true;
                    dialog.Close();
                });
            }

            _prompting = true;
            bool save;
            try
            {
                save = await dialog.ShowAsync(window);
            }
            finally
            {
                _prompting = false;
            }

            if (save)
            {
                bool alwaysSave = always.IsChecked == true;
                if (await EnsureUnlockedAsync(window))
                    Save(window, candidate, alwaysSave, existing != null);
            }
            else if (never && await EnsureUnlockedAsync(window))
            {
                Service.SetPolicy(candidate.Origin, CredentialSavePolicy.NeverSave);
                window.ShowToast(Tr("Enregistrement désactivé pour {0}", site));
            }
        }

        void Save(MainWindow window, CredentialCandidate candidate, bool alwaysSave, bool update)
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
            if (!IsUnlocked || !CredentialOrigin.TryCreateTrusted(tab.WebUrl, out string origin))
                return;

            CredentialEntry? credential = Service.FindForOrigin(origin);
            if (credential != null)
                _ = FillAsync(tab, credential, origin, otp: null);
        }

        /// <summary>Bouton « clé » de la barre d'adresse : choix du compte s'il y en a plusieurs.</summary>
        public async Task FillAsync(MainWindow window, BrowserTab tab)
        {
            if (!CredentialOrigin.TryCreateTrusted(tab.WebUrl, out string origin) || !await EnsureUnlockedAsync(window))
                return;

            IReadOnlyList<CredentialEntry> accounts = ForOrigin(origin);
            if (accounts.Count == 0)
            {
                window.ShowToast(Tr("Aucun identifiant enregistré pour {0}", CredentialOrigin.DisplayName(origin)));
                return;
            }

            CredentialEntry? chosen = accounts[0];
            if (accounts.Count > 1)
            {
                int index = await Dialogs.ChoiceAsync(window, Tr("Quel compte utiliser ?"), CredentialOrigin.DisplayName(origin),
                    accounts.Take(6).Select(a => (a.Username.Length > 0 ? a.Username : Tr("(sans identifiant)"), false, false)).ToArray());
                chosen = index >= 0 ? accounts[index] : null;
            }

            if (chosen != null)
                await FillWithCodeAsync(window, tab, chosen, origin);
        }

        async Task FillWithCodeAsync(MainWindow window, BrowserTab tab, CredentialEntry credential, string origin)
        {
            string? code = CurrentCode(credential);
            string? result = await FillAsync(tab, credential, origin, code);
            if (code == null)
                return;

            if (result == "otp")
                window.ShowToast(Tr("Code 2FA rempli"));
            else if (await SecureClipboard.CopyAsync(window, code, secret: true))
                window.ShowToast(Tr("Code 2FA copié : collez-le à l'étape de double authentification (effacé du presse-papiers dans 30 s)."));
        }

        static async Task<string?> FillAsync(BrowserTab tab, CredentialEntry credential, string origin, string? otp)
        {
            try
            {
                return await tab.FillCredentialAsync(credential, origin, otp);
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

    /// <summary>
    /// Presse-papiers pour les secrets (mot de passe, code 2FA) : effacé au bout de 30 secondes,
    /// sauf si autre chose a été copié entre-temps.
    /// </summary>
    public static class SecureClipboard
    {
        public const int ClearAfterSeconds = 30;

        public static async Task<bool> CopyAsync(TopLevel owner, string text, bool secret)
        {
            if (owner.Clipboard is not { } clipboard)
                return false;

            await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
            if (!secret)
                return true;

            DispatcherTimer.RunOnce(async () =>
            {
                try
                {
                    // Toujours notre contenu : personne n'a copié autre chose depuis.
                    if (await Avalonia.Input.Platform.ClipboardExtensions.TryGetTextAsync(clipboard) == text)
                        await clipboard.ClearAsync();
                }
                catch (Exception)
                {
                    // Presse-papiers indisponible : rien à effacer.
                }
            }, TimeSpan.FromSeconds(ClearAfterSeconds));
            return true;
        }
    }
}
