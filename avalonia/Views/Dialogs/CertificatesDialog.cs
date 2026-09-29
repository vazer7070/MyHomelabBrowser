using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Platform.Storage;
using MyHomelabBrowser.classes.Security;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Dialogs
{
    /// <summary>Autorités de certification propres au profil, et certificats approuvés pour les services locaux.</summary>
    public static class CertificatesDialog
    {
        public static async Task ShowAuthoritiesAsync(MainWindow window)
        {
            var store = new CertificateStoreService();
            var dialog = new ListDialog(Tr("Autorités du profil"),
                Tr("Les sites dont le certificat est émis par l'une de ces autorités (celle de votre homelab, par exemple) sont acceptés par PommeBrowser, sans modifier la confiance du système."),
                Tr("Aucune autorité ajoutée."),
                d => store.GetAuthorities(includeSystem: false)
                    .Select(entry => d.Row(entry.DisplayName,
                        entry.StoreDisplay + " · " + Tr("expire le {0}", entry.NotAfter.ToString("d", Culture)),
                        Tr("Retirer"),
                        () =>
                        {
                            CertificateOperationResult result = store.RemoveBrowserAuthority(entry);
                            if (!result.Success)
                                window.ShowToast(result.Message, warning: true);
                        })));

            dialog.AddExtraButton(Tr("Importer une autorité…"), destructive: false, async () =>
            {
                IReadOnlyList<IStorageFile> files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = Tr("Importer une autorité"),
                    AllowMultiple = false,
                    FileTypeFilter = new[] { new FilePickerFileType(Tr("Certificats (.crt, .cer, .pem)")) { Patterns = new[] { "*.crt", "*.cer", "*.pem", "*.der" } } }
                });
                if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path)
                    return;
                CertificateImportResult result = store.ImportAuthorityForBrowser(path);
                dialog.ShowError(result.Success ? null : result.Message);
                if (result.Success)
                    window.ShowToast(result.Message);
                dialog.Refresh();
            });
            await dialog.ShowAsync(window);
        }

        public static Task ShowPinnedAsync(MainWindow window)
        {
            BrowserApp app = window.App;
            var dialog = new ListDialog(Tr("Certificats acceptés"),
                Tr("Certificats que vous avez approuvés pour des services qui n'ont pas de certificat reconnu (homelab…). Un certificat différent déclenche un avertissement."),
                Tr("Aucun certificat accepté."),
                d => app.CertificatePins.GetAll()
                    .OrderBy(p => p.Authority, StringComparer.OrdinalIgnoreCase)
                    .Select(pin => d.Row(pin.Authority,
                        Tr("Émis pour : {0}", pin.Subject) + " · " + Tr("accepté le {0}", pin.PinnedAt.ToString("d", Culture)) + "\n" + CertificatePinStore.FormatFingerprint(pin.Sha256),
                        Tr("Retirer"),
                        () =>
                        {
                            app.CertificatePins.Remove(pin.Authority);
                            app.SessionTrustedHosts.Remove(pin.Authority);
                        })));
            return dialog.ShowAsync(window);
        }
    }
}
