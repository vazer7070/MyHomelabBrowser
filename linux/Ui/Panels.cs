using System;
using System.Linq;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Homelab;
using PommeBrowser.Linux.Web;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Ui
{
    /// <summary>Contenu des panneaux de la barre d'en-tête : téléchargements, services, bloqueur.</summary>
    static class Panels
    {
        public static Gtk.Widget Downloads(BrowserApplication app, BrowserWindow window)
        {
            var box = Panel(Tr("Téléchargements"));
            var list = Gtk.ListBox.New();
            list.AddCssClass("boxed-list");
            list.SetSelectionMode(Gtk.SelectionMode.None);

            var scroller = Gtk.ScrolledWindow.New();
            scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
            scroller.SetPropagateNaturalHeight(true);
            scroller.SetMaxContentHeight(420);
            scroller.SetChild(list);
            box.Append(scroller);

            void Refresh()
            {
                list.RemoveAll();
                foreach (DownloadEntry entry in app.Engine.Downloads.Entries)
                    list.Append(DownloadRow(window, entry));
            }

            Action changed = Refresh;
            app.Engine.Downloads.Changed += changed;
            box.OnUnmap += (_, _) => app.Engine.Downloads.Changed -= changed;
            Refresh();

            var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            buttons.SetHomogeneous(true);
            var folder = Gtk.Button.NewWithLabel(Tr("Ouvrir le dossier"));
            folder.OnClicked += (_, _) => OpenFile(window.Window, app.Engine.Downloads.Directory);
            var clear = Gtk.Button.NewWithLabel(Tr("Effacer la liste"));
            clear.OnClicked += (_, _) => app.Engine.Downloads.Clear();
            buttons.Append(folder);
            buttons.Append(clear);
            box.Append(buttons);
            return box;
        }

        static Gtk.Widget DownloadRow(BrowserWindow window, DownloadEntry entry)
        {
            var row = Adw.ActionRow.New();
            row.SetTitle(GLib.Functions.MarkupEscapeText(entry.FileName.Length > 0 ? entry.FileName : Tr("Préparation…"), -1));
            row.SetSubtitle(entry.State switch
            {
                DownloadState.Running => Tr("{0:0} %", entry.Progress * 100),
                DownloadState.Finished => Tr("Terminé"),
                DownloadState.Cancelled => Tr("Annulé"),
                _ => Tr("Échec : {0}", entry.Error ?? string.Empty)
            });

            if (entry.State == DownloadState.Running)
            {
                var cancel = SuffixButton("process-stop-symbolic", Tr("Annuler"));
                cancel.OnClicked += (_, _) => entry.Download.Cancel();
                row.AddSuffix(cancel);
            }
            else if (entry.State == DownloadState.Finished && entry.Destination != null)
            {
                var open = SuffixButton("document-open-symbolic", Tr("Ouvrir"));
                open.OnClicked += (_, _) => OpenFile(window.Window, entry.Destination);
                row.AddSuffix(open);
            }
            return row;
        }

        public static Gtk.Widget Services(BrowserApplication app, BrowserWindow window, Gtk.Popover popover)
        {
            var box = Panel(Tr("Services du homelab"));
            var services = app.Services.GetAll();

            if (services.Count == 0)
            {
                var empty = Gtk.Label.New(Tr("Ajoutez vos services (NAS, Proxmox, routeur…) pour les ouvrir d'un clic et être prévenu s'ils ne répondent plus."));
                empty.SetWrap(true);
                empty.SetMaxWidthChars(40);
                empty.AddCssClass("dim-label");
                box.Append(empty);
            }
            else
            {
                var list = Gtk.ListBox.New();
                list.AddCssClass("boxed-list");
                list.SetSelectionMode(Gtk.SelectionMode.None);
                foreach (HomelabService service in services.Take(20))
                {
                    var tile = new ServiceTile(service) { Result = app.Monitor.GetResult(service.Id) };
                    var row = Adw.ActionRow.New();
                    row.SetTitle(GLib.Functions.MarkupEscapeText(tile.Name, -1));
                    row.SetSubtitle(GLib.Functions.MarkupEscapeText(tile.StatusText, -1));
                    row.AddPrefix(StatusDot(tile.State));
                    row.SetActivatable(true);
                    string url = service.Url;
                    row.OnActivated += (_, _) =>
                    {
                        popover.Popdown();
                        window.OpenFromPage(url);
                    };
                    list.Append(row);
                }
                var scroller = Gtk.ScrolledWindow.New();
                scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
                scroller.SetPropagateNaturalHeight(true);
                scroller.SetMaxContentHeight(420);
                scroller.SetChild(list);
                box.Append(scroller);
            }

            var manage = Gtk.Button.NewWithLabel(Tr("Gérer les services"));
            manage.OnClicked += (_, _) =>
            {
                popover.Popdown();
                window.OpenPage(TabContent.Services);
            };
            box.Append(manage);
            return box;
        }

        public static Gtk.Widget AdBlock(BrowserApplication app, BrowserWindow window, Gtk.Popover popover)
        {
            AdBlocker blocker = app.Engine.AdBlocker;
            var box = Panel(Tr("Bloqueur de publicités"));

            var status = Gtk.Label.New(blocker.Status);
            status.SetXalign(0);
            status.SetWrap(true);
            status.AddCssClass("dim-label");
            box.Append(status);

            if (Uri.TryCreate(window.Current?.Uri, UriKind.Absolute, out Uri? uri) && uri.Host.Length > 0 && blocker.Settings.Enabled)
            {
                var row = Adw.SwitchRow.New();
                row.SetTitle(GLib.Functions.MarkupEscapeText(Tr("Actif sur {0}", uri.Host), -1));
                bool local = blocker.Settings.BypassPrivateNetworks && (UrlResolver.IsLocalHost(uri.Host) || MyHomelabBrowser.classes.AdBlock.Models.AdBlockDomain.IsPrivateOrLocalHost(uri.Host));
                row.SetActive(!local && !blocker.IsSiteAllowed(uri.Host));
                row.SetSensitive(!local);
                if (local)
                    row.SetSubtitle(Tr("Réseau local : jamais filtré"));
                string host = uri.Host;
                row.OnNotify += (_, args) =>
                {
                    if (args.Pspec.GetName() != "active")
                        return;
                    blocker.SetSiteAllowed(host, !row.GetActive());
                    window.Current?.Reload();
                };

                var list = Gtk.ListBox.New();
                list.AddCssClass("boxed-list");
                list.SetSelectionMode(Gtk.SelectionMode.None);
                list.Append(row);
                box.Append(list);
            }

            var update = Gtk.Button.NewWithLabel(Tr("Mettre à jour les listes"));
            update.OnClicked += async (_, _) =>
            {
                update.SetSensitive(false);
                var result = await blocker.UpdateListsAsync(force: true);
                window.ShowToast(result.Message);
                update.SetSensitive(true);
            };
            var settings = Gtk.Button.NewWithLabel(Tr("Préférences"));
            settings.SetActionName("app.preferences");
            settings.OnClicked += (_, _) => popover.Popdown();
            var buttons = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            buttons.SetHomogeneous(true);
            buttons.Append(update);
            buttons.Append(settings);
            box.Append(buttons);
            return box;
        }

        public static Gtk.Widget StatusDot(ServiceState state)
        {
            var dot = Gtk.Box.New(Gtk.Orientation.Horizontal, 0);
            dot.AddCssClass("status-dot");
            dot.SetValign(Gtk.Align.Center);
            string? css = state switch
            {
                ServiceState.Online => "online",
                ServiceState.Degraded => "degraded",
                ServiceState.Offline => "offline",
                _ => null
            };
            if (css != null)
                dot.AddCssClass(css);
            return dot;
        }

        public static Gtk.Button SuffixButton(string icon, string tooltip)
        {
            var button = Gtk.Button.NewFromIconName(icon);
            button.SetTooltipText(tooltip);
            button.SetValign(Gtk.Align.Center);
            button.AddCssClass("flat");
            return button;
        }

        /// <summary>Ouvre un fichier ou un dossier avec l'application par défaut du système.</summary>
        public static async void OpenFile(Gtk.Window parent, string? path)
        {
            if (string.IsNullOrEmpty(path))
                return;

            try
            {
                var launcher = Gtk.FileLauncher.New(Gio.FileHelper.NewForPath(path));
                await launcher.LaunchAsync(parent);
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[Ouverture] " + ex.Message);
            }
        }

        static Gtk.Box Panel(string title)
        {
            var box = Gtk.Box.New(Gtk.Orientation.Vertical, 10);
            box.SetMarginTop(6);
            box.SetMarginBottom(6);
            box.SetMarginStart(6);
            box.SetMarginEnd(6);
            box.SetSizeRequest(340, -1);

            var heading = Gtk.Label.New(title);
            heading.AddCssClass("heading");
            heading.SetXalign(0);
            box.Append(heading);
            return box;
        }
    }
}
