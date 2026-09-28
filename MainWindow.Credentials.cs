using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Profiles.Credentials;
using MyHomelabBrowser.classes.Security;
using MyHomelabBrowser.controles;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // Identifiants : proposition d'enregistrement et remplissage depuis le coffre.

        private readonly SemaphoreSlim _credentialPromptGate = new(1, 1);

        private readonly Dictionary<string, DateTime> _recentCredentialPrompts =
            new(StringComparer.OrdinalIgnoreCase);

        private SaveCredentialDialog? _activeCredentialPrompt;

        private async Task HandleCredentialCandidateAsync(CredentialCandidate candidate)
        {
            if (candidate == null ||
                string.IsNullOrWhiteSpace(candidate.Origin) ||
                string.IsNullOrEmpty(candidate.Password))
            {
                return;
            }

            var promptKey = BuildCredentialPromptKey(candidate);

            if (IsCredentialPromptCoolingDown(promptKey))
                return;

            if (!await _credentialPromptGate.WaitAsync(0))
                return;

            try
            {
                if (Dispatcher.CheckAccess())
                {
                    await HandleCredentialCandidateOnUiAsync(candidate);
                }
                else
                {
                    await Dispatcher
                        .InvokeAsync(() => HandleCredentialCandidateOnUiAsync(candidate))
                        .Task
                        .Unwrap();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("[Vault] Erreur pendant la demande d'enregistrement : " + ex.Message);
            }
            finally
            {
                RememberCredentialPrompt(promptKey);
                _credentialPromptGate.Release();
            }
        }

        private async Task HandleCredentialCandidateOnUiAsync(CredentialCandidate candidate)
        {
            var profileAtCapture = _profileService.Current?.Username;
            if (string.IsNullOrWhiteSpace(profileAtCapture))
                return;

            if (!EnsureVaultAvailableAndUnlocked())
                return;

            var policy = _vault.GetPolicy(candidate.Origin);
            if (policy == CredentialSavePolicy.NeverSave)
                return;

            var existing = _vault.FindForOrigin(candidate.Origin, candidate.Username);
            if (existing != null &&
                string.Equals(existing.Password, candidate.Password, StringComparison.Ordinal))
            {
                return;
            }

            if (policy == CredentialSavePolicy.AlwaysSave)
            {
                _vault.Upsert(
                    candidate.Origin,
                    candidate.Username,
                    candidate.Password,
                    candidate.FormAction,
                    alwaysSave: true);

                ShowToast(
                    existing == null ? Tr("Identifiant enregistré") : Tr("Identifiant mis à jour"),
                    CredentialOrigin.DisplayName(candidate.Origin),
                    null);
                return;
            }

            var dialog = new SaveCredentialDialog(
                CredentialOrigin.DisplayName(candidate.Origin),
                candidate.Username);

            _activeCredentialPrompt = dialog;

            SaveCredentialDialogResult result;
            try
            {
                result = await dialog.ShowAsync(this);
            }
            finally
            {
                if (ReferenceEquals(_activeCredentialPrompt, dialog))
                    _activeCredentialPrompt = null;
            }

            if (!string.Equals(
                    _profileService.Current?.Username,
                    profileAtCapture,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            if (result.Decision == SaveCredentialDecision.NeverSave)
            {
                _vault.SetPolicy(candidate.Origin, CredentialSavePolicy.NeverSave);
                ShowToast(
                    Tr("Enregistrement désactivé"),
                    CredentialOrigin.DisplayName(candidate.Origin),
                    null);
                return;
            }

            if (result.Decision != SaveCredentialDecision.Save)
                return;

            _vault.Upsert(
                candidate.Origin,
                candidate.Username,
                candidate.Password,
                candidate.FormAction,
                alwaysSave: result.AlwaysSave);

            if (!result.AlwaysSave)
                _vault.SetPolicy(candidate.Origin, CredentialSavePolicy.Ask);

            ShowToast(
                existing == null ? Tr("Mot de passe enregistré") : Tr("Mot de passe mis à jour"),
                CredentialOrigin.DisplayName(candidate.Origin),
                null);
        }

        private static string BuildCredentialPromptKey(CredentialCandidate candidate)
        {
            return string.Join(
                "|",
                candidate.Origin.Trim().ToLowerInvariant(),
                candidate.Username.Trim().ToLowerInvariant(),
                (candidate.FormAction ?? string.Empty).Trim().ToLowerInvariant());
        }

        private bool IsCredentialPromptCoolingDown(string key)
        {
            var now = DateTime.UtcNow;

            lock (_recentCredentialPrompts)
            {
                foreach (var expired in _recentCredentialPrompts
                             .Where(x => now - x.Value > TimeSpan.FromSeconds(30))
                             .Select(x => x.Key)
                             .ToArray())
                {
                    _recentCredentialPrompts.Remove(expired);
                }

                return _recentCredentialPrompts.TryGetValue(key, out var lastShown) &&
                       now - lastShown < TimeSpan.FromSeconds(15);
            }
        }

        private void RememberCredentialPrompt(string key)
        {
            lock (_recentCredentialPrompts)
                _recentCredentialPrompts[key] = DateTime.UtcNow;
        }

        private void CloseActiveCredentialPrompt()
        {
            var dialog = _activeCredentialPrompt;
            _activeCredentialPrompt = null;

            try
            {
                dialog?.CancelAndClose();
            }
            catch
            {
                // Un changement de profil ne doit jamais casser l'interface.
            }
        }

        private bool EnsureVaultAvailableAndUnlocked()
        {
            if (_profileService.Current == null)
                return false;

            if (_vault.IsUnlocked)
                return true;

            if (!_vault.VaultExists)
            {
                var first = new SimplePasswordDialog(Tr("Créer le mot de passe du coffre"))
                {
                    Owner = this
                };

                if (first.ShowDialog() != true || string.IsNullOrWhiteSpace(first.Password))
                    return false;

                var confirmation = new SimplePasswordDialog(Tr("Confirmer le mot de passe du coffre"))
                {
                    Owner = this
                };

                if (confirmation.ShowDialog() != true)
                    return false;

                if (!string.Equals(first.Password, confirmation.Password, StringComparison.Ordinal))
                {
                    MessageDialog.Show(
                        this,
                        Tr("Les mots de passe ne correspondent pas."),
                        Tr("Coffre des mots de passe"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                    return false;
                }

                if (!_vault.TryInitializeNewVault(first.Password))
                {
                    MessageDialog.Show(
                        this,
                        Tr("Impossible de créer le coffre."),
                        Tr("Coffre des mots de passe"),
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return false;
                }

                return true;
            }

            var unlockDialog = new UnlockVaultDialog
            {
                Owner = this
            };

            if (unlockDialog.ShowDialog() != true)
                return false;

            if (_vault.TryUnlock(unlockDialog.EnteredPassword))
                return true;

            var lockedUntil = _vault.UnlockAvailableAtUtc;
            var message = lockedUntil.HasValue && lockedUntil.Value > DateTime.UtcNow
                ? Tr("Le coffre est temporairement verrouillé jusqu’à {0:HH:mm:ss}.", lockedUntil.Value.ToLocalTime())
                : Tr("Mot de passe du coffre incorrect.");

            MessageDialog.Show(
                this,
                message,
                Tr("Coffre des mots de passe"),
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return false;
        }

        async void FillCredential_Click(object sender, RoutedEventArgs e)
        {
            if (Tabs.SelectedItem is not TabItem tab ||
                tab.Tag is not WebTabContent content)
                return;

            var web = content.Web;
            if (web?.Source == null ||
                !CredentialOrigin.TryCreateTrusted(web.Source, out _))
                return;

            if (!EnsureVaultAvailableAndUnlocked())
                return;

            var credential = _vault.FindForOrigin(web.Source);
            if (credential == null)
            {
                ShowToast(
                    Tr("Aucun identifiant"),
                    Tr("Aucun compte n’est enregistré pour cette origine."),
                    null);
                UpdateFillCredentialButtonState();
                return;
            }

            // Code de double authentification, s'il est enregistré pour ce compte.
            string? otpCode = credential.HasTotp && Totp.TryParse(credential.TotpSecret, out TotpParameters totp, out _)
                ? Totp.Generate(totp, DateTimeOffset.UtcNow)
                : null;

            var script = CredentialScripts.Fill(credential.Username, credential.Password, otpCode);

            string result;
            try
            {
                result = await web.ExecuteScriptAsync(script);
            }
            catch
            {
                return;
            }

            if (otpCode == null)
                return;

            if (result == "\"otp\"")
            {
                ShowToast(Tr("Code 2FA rempli"), credential.DisplayHost, ToastKind.Success);
            }
            else if (ClipboardHelper.TryCopyWithAutoClear(otpCode))
            {
                // Le code sera demandé à l'étape suivante : il est prêt à être collé.
                ShowToast(Tr("Code 2FA copié"), Tr("Collez-le à l’étape de double authentification (effacé du presse-papiers dans 30 s)."), ToastKind.Info);
            }
        }

        void UpdateFillCredentialButtonState()
        {
            try
            {
                FillCredentialButton.IsEnabled = false;
                FillCredentialButton.Opacity = 0.35;
                FillCredentialButton.ToolTip = Tr("Aucun identifiant disponible");

                if (_profileService.Current == null)
                    return;

                if (Tabs.SelectedItem is not TabItem tab)
                    return;

                if (tab.Tag is not WebTabContent content)
                    return;

                var source = content.Web?.Source;
                if (source == null)
                    return;

                if (!CredentialOrigin.TryCreateTrusted(source, out _))
                    return;

                if (!_vault.IsUnlocked)
                {
                    if (_vault.VaultExists)
                    {
                        FillCredentialButton.IsEnabled = true;
                        FillCredentialButton.Opacity = 1.0;
                        FillCredentialButton.ToolTip =
                            Tr("Déverrouiller le coffre pour rechercher un identifiant");
                    }

                    return;
                }

                if (_vault.HasCredentialForOrigin(source))
                {
                    FillCredentialButton.IsEnabled = true;
                    FillCredentialButton.Opacity = 1.0;
                    FillCredentialButton.ToolTip = Tr("Remplir les identifiants");
                }
            }
            catch
            {
                // L'état du bouton ne doit jamais casser l'interface.
            }
        }
    }
}
