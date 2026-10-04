using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Import;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Dialogs
{
    /// <summary>
    /// Mots de passe d'un autre navigateur ou gestionnaire : ils ne se lisent pas directement
    /// (chiffrés par leur navigateur), on les exporte en CSV puis on importe ce fichier dans le
    /// coffre. Le fichier contient les mots de passe en clair : sa suppression est proposée aussitôt.
    /// </summary>
    public static class ImportPasswordsDialog
    {
        public static async Task ShowAsync(MainWindow window)
        {
            var intro = new FormDialog(Tr("Importer des mots de passe"), Tr("Choisir le fichier…"), width: 520);
            intro.AddText(Tr("Exportez d'abord vos mots de passe depuis l'autre navigateur, au format CSV :"));
            intro.AddText(Tr("Chrome, Edge, Brave : Paramètres › Mots de passe (gestionnaire de mots de passe) › Paramètres › Exporter les mots de passe."), hint: true);
            intro.AddText(Tr("Firefox : about:logins › menu ⋯ › Exporter les identifiants."), hint: true);
            intro.AddText(Tr("Safari, Bitwarden et la plupart des gestionnaires de mots de passe exportent aussi en CSV."), hint: true);
            intro.AddText(Tr("Les identifiants rejoignent le coffre chiffré de PommeBrowser. Ceux que vous avez déjà ne sont pas remplacés."), hint: true);
            if (!await intro.ShowAsync(window))
                return;

            IReadOnlyList<IStorageFile> files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Tr("Importer des mots de passe"),
                AllowMultiple = false,
                FileTypeFilter = new[] { new FilePickerFileType(Tr("Mots de passe (CSV)")) { Patterns = new[] { "*.csv" } } }
            });
            if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
                return;

            (List<ImportedCredential> Credentials, int Skipped)? parsed;
            try
            {
                parsed = await Task.Run(() => new FileInfo(path).Length > 20 * 1024 * 1024 ? null : PasswordCsvImporter.Parse(File.ReadAllText(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Dialogs.AlertAsync(window, Tr("Importer des mots de passe"), ex.Message);
                return;
            }
            if (parsed is not { } result)
            {
                await Dialogs.AlertAsync(window, Tr("Importer des mots de passe"),
                    Tr("Ce fichier n'est pas un export de mots de passe reconnu (il faut au moins les colonnes de l'adresse et du mot de passe)."));
                return;
            }

            if (!await window.App.Vault.EnsureUnlockedAsync(window))
                return;
            (int added, int existing, int invalid) = window.App.Vault.Service.Import(
                result.Credentials.Select(c => (c.Url, c.Username, c.Password, c.Totp)));
            window.App.Vault.NotifyChanged();
            RuntimeLogBuffer.Append($"[Import] Mots de passe : {added} ajoutés, {existing} déjà présents, {invalid + result.Skipped} ignorés.");

            var done = new FormDialog(Tr("Mots de passe importés"), Tr("Supprimer le fichier"), destructive: true, cancelLabel: Tr("Garder le fichier"));
            done.AddText(Tr("{0} identifiants ajoutés au coffre ({1} déjà présents, {2} ignorés : applications, adresses ou mots de passe vides).",
                added, existing, invalid + result.Skipped));
            done.AddText(Tr("Le fichier exporté contient vos mots de passe en clair : supprimez-le maintenant."), hint: true);
            if (!await done.ShowAsync(window))
                return;
            try
            {
                File.Delete(path);
                window.ShowToast(Tr("Fichier des mots de passe supprimé."));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                window.ShowToast(Tr("Le fichier n'a pas pu être supprimé : {0}", ex.Message), warning: true);
            }
        }
    }
}
