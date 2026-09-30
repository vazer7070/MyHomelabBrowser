using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using MyHomelabBrowser.classes.Homelab;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Dialogs
{
    /// <summary>Ajout ou modification d'un service du homelab (page d'accueil).</summary>
    public static class ServiceDialog
    {
        public static async Task EditAsync(MainWindow window, HomelabService? existing, string? suggestedUrl = null, string? suggestedName = null)
        {
            BrowserApp app = window.App;
            HomelabService service = existing ?? new HomelabService { Url = suggestedUrl ?? "http://", Name = suggestedName ?? string.Empty };

            var dialog = new FormDialog(existing == null ? Tr("Ajouter un service") : Tr("Modifier le service"),
                existing == null ? Tr("Ajouter") : Tr("Enregistrer"));
            TextBox name = dialog.AddEntry(Tr("Nom"), service.Name);
            TextBox url = dialog.AddEntry(Tr("Adresse"), service.Url, watermark: "http://192.168.1.10:8006");
            TextBox group = dialog.AddEntry(Tr("Groupe (facultatif)"), service.Group);
            CheckBox monitor = dialog.AddCheck(Tr("Surveiller la disponibilité"), service.Monitor);

            void Validate()
            {
                string? error = HomelabServiceStore.Validate(new HomelabService { Url = url.Text ?? string.Empty });
                dialog.SetConfirmEnabled(error == null);
                dialog.ShowError(string.IsNullOrWhiteSpace(url.Text) || url.Text == "http://" ? null : error);
            }
            url.TextChanged += (_, _) => Validate();
            Validate();

            dialog.Submit = () =>
            {
                string address = (url.Text ?? string.Empty).Trim();
                string label = string.IsNullOrWhiteSpace(name.Text) && Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) ? uri.Host : name.Text ?? string.Empty;
                app.Services.AddOrUpdate(new HomelabService
                {
                    Id = service.Id,
                    Name = label.Trim(),
                    Url = address,
                    Group = string.IsNullOrWhiteSpace(group.Text) ? null : group.Text.Trim(),
                    Monitor = monitor.IsChecked == true,
                    CreatedAt = service.CreatedAt
                });
                return Task.FromResult<string?>(null);
            };
            await dialog.ShowAsync(window);
        }
    }
}
