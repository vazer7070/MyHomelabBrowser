using System;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Homelab;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Ui
{
    /// <summary>Boîtes de dialogue (libadwaita) : favoris, services, confirmations.</summary>
    static class Dialogs
    {
        public static void Confirm(Gtk.Widget parent, string heading, string body, string confirmLabel, bool destructive, Action onConfirm)
        {
            var dialog = Adw.AlertDialog.New(heading, body);
            dialog.AddResponse("cancel", Tr("Annuler"));
            dialog.AddResponse("confirm", confirmLabel);
            dialog.SetResponseAppearance("confirm", destructive ? Adw.ResponseAppearance.Destructive : Adw.ResponseAppearance.Suggested);
            dialog.SetDefaultResponse("cancel");
            dialog.SetCloseResponse("cancel");
            dialog.OnResponse += (_, args) =>
            {
                if (args.Response == "confirm")
                    onConfirm();
            };
            dialog.Present(parent);
        }

        public static void EditFavorite(BrowserApplication app, Gtk.Widget parent, FavoriteItem favorite)
        {
            var title = Adw.EntryRow.New();
            title.SetTitle(Tr("Titre"));
            title.SetText(favorite.Title);
            var url = Adw.EntryRow.New();
            url.SetTitle(Tr("Adresse"));
            url.SetText(favorite.Url);
            var folder = Adw.EntryRow.New();
            folder.SetTitle(Tr("Dossier (facultatif)"));
            folder.SetText(favorite.Folder ?? string.Empty);

            var dialog = Adw.AlertDialog.New(Tr("Modifier le favori"), null);
            dialog.SetExtraChild(BoxedList(title, url, folder));
            dialog.AddResponse("remove", Tr("Supprimer"));
            dialog.AddResponse("cancel", Tr("Annuler"));
            dialog.AddResponse("save", Tr("Enregistrer"));
            dialog.SetResponseAppearance("remove", Adw.ResponseAppearance.Destructive);
            dialog.SetResponseAppearance("save", Adw.ResponseAppearance.Suggested);
            dialog.SetDefaultResponse("save");
            dialog.SetCloseResponse("cancel");

            void Validate() => dialog.SetResponseEnabled("save", IsWebAddress(url.GetText()));
            url.OnChanged += (_, _) => Validate();
            Validate();

            dialog.OnResponse += (_, args) =>
            {
                if (args.Response == "save")
                    app.Favorites.Update(favorite.Id, title.GetText(), url.GetText(), folder.GetText());
                else if (args.Response == "remove")
                    app.Favorites.Remove(favorite.Id);
            };
            dialog.Present(parent);
        }

        public static void EditService(BrowserApplication app, Gtk.Widget parent, HomelabService? existing, string? suggestedUrl = null)
        {
            HomelabService service = existing ?? new HomelabService { Url = suggestedUrl ?? "http://" };

            var name = Adw.EntryRow.New();
            name.SetTitle(Tr("Nom"));
            name.SetText(service.Name);
            var url = Adw.EntryRow.New();
            url.SetTitle(Tr("Adresse"));
            url.SetText(service.Url);
            var group = Adw.EntryRow.New();
            group.SetTitle(Tr("Groupe (facultatif)"));
            group.SetText(service.Group ?? string.Empty);
            var monitor = Adw.SwitchRow.New();
            monitor.SetTitle(Tr("Surveiller la disponibilité"));
            monitor.SetActive(service.Monitor);

            var dialog = Adw.AlertDialog.New(existing == null ? Tr("Ajouter un service") : Tr("Modifier le service"), null);
            dialog.SetExtraChild(BoxedList(name, url, group, monitor));
            dialog.AddResponse("cancel", Tr("Annuler"));
            dialog.AddResponse("save", existing == null ? Tr("Ajouter") : Tr("Enregistrer"));
            dialog.SetResponseAppearance("save", Adw.ResponseAppearance.Suggested);
            dialog.SetDefaultResponse("save");
            dialog.SetCloseResponse("cancel");

            void Validate()
            {
                string? error = HomelabServiceStore.Validate(new HomelabService { Url = url.GetText() });
                dialog.SetResponseEnabled("save", error == null);
                if (error == null)
                    url.RemoveCssClass("error");
                else
                    url.AddCssClass("error");
            }
            url.OnChanged += (_, _) => Validate();
            Validate();

            dialog.OnResponse += (_, args) =>
            {
                if (args.Response != "save")
                    return;

                string address = url.GetText().Trim();
                app.Services.AddOrUpdate(new HomelabService
                {
                    Id = service.Id,
                    Name = string.IsNullOrWhiteSpace(name.GetText()) && Uri.TryCreate(address, UriKind.Absolute, out Uri? uri) ? uri.Host : name.GetText(),
                    Url = address,
                    Group = group.GetText(),
                    Monitor = monitor.GetActive(),
                    CreatedAt = service.CreatedAt
                });
            };
            dialog.Present(parent);
        }

        public static bool IsWebAddress(string? text)
            => Uri.TryCreate(text?.Trim(), UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile);

        static Gtk.Widget BoxedList(params Gtk.Widget[] rows)
        {
            var list = Gtk.ListBox.New();
            list.AddCssClass("boxed-list");
            list.SetSelectionMode(Gtk.SelectionMode.None);
            foreach (Gtk.Widget row in rows)
                list.Append(row);
            return list;
        }
    }
}
