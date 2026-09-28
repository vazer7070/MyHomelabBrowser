using System;
using System.Collections.Generic;
using System.Linq;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Homelab;
using MyHomelabBrowser.classes.Import;
using PommeBrowser.Linux.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Ui
{
    /// <summary>Base des pages de PommeBrowser : défilement, largeur limitée, mise à jour tant qu'elles sont visibles.</summary>
    abstract class PageView
    {
        protected readonly BrowserApplication App;
        protected readonly BrowserWindow Window;
        readonly Gtk.Box _content;
        uint _refreshSource;

        protected PageView(BrowserApplication app, BrowserWindow window)
        {
            App = app;
            Window = window;

            _content = Gtk.Box.New(Gtk.Orientation.Vertical, 18);
            _content.AddCssClass("home-page");

            var clamp = Adw.Clamp.New();
            clamp.SetMaximumSize(900);
            clamp.SetChild(_content);

            var scroller = Gtk.ScrolledWindow.New();
            scroller.SetPolicy(Gtk.PolicyType.Never, Gtk.PolicyType.Automatic);
            scroller.SetChild(clamp);
            scroller.SetVexpand(true);
            scroller.OnMap += (_, _) => { Subscribe(); Refresh(); };
            scroller.OnUnmap += (_, _) => Unsubscribe();
            Widget = scroller;
        }

        public Gtk.Widget Widget { get; }

        protected abstract void Build(Gtk.Box content);

        protected virtual void Subscribe()
        {
        }

        protected virtual void Unsubscribe()
        {
        }

        /// <summary>Reconstruit la page (regroupé : plusieurs changements rapprochés = une seule fois).</summary>
        protected void ScheduleRefresh()
        {
            if (_refreshSource != 0)
                return;
            _refreshSource = GLib.Functions.TimeoutAdd(0, 150, () =>
            {
                _refreshSource = 0;
                Refresh();
                return false;
            });
        }

        protected void Refresh()
        {
            for (Gtk.Widget? child = _content.GetFirstChild(); child != null; child = _content.GetFirstChild())
                _content.Remove(child);
            Build(_content);
        }

        protected static Gtk.Label Heading(string text)
        {
            var label = Gtk.Label.New(text);
            label.AddCssClass("title-4");
            label.SetXalign(0);
            return label;
        }

        protected static Gtk.ListBox BoxedList()
        {
            var list = Gtk.ListBox.New();
            list.AddCssClass("boxed-list");
            list.SetSelectionMode(Gtk.SelectionMode.None);
            return list;
        }

        protected static string Escape(string? text) => GLib.Functions.MarkupEscapeText(text ?? string.Empty, -1);

        /// <summary>Ligne cliquable ; le clic du milieu ouvre un nouvel onglet.</summary>
        protected Adw.ActionRow LinkRow(string title, string subtitle, string url)
        {
            var row = Adw.ActionRow.New();
            row.SetTitle(Escape(title.Length > 0 ? title : url));
            row.SetSubtitle(Escape(subtitle));
            row.SetTitleLines(1);
            row.SetSubtitleLines(1);
            row.SetActivatable(true);
            row.OnActivated += (_, _) => Window.OpenFromPage(url);

            var middle = Gtk.GestureClick.New();
            middle.SetButton(2);
            middle.OnReleased += (_, _) => Window.OpenFromPage(url, newTab: true);
            row.AddController(middle);
            return row;
        }

        protected static Gtk.Widget EmptyState(string icon, string title, string description)
        {
            var status = Adw.StatusPage.New();
            status.SetIconName(icon);
            status.SetTitle(title);
            status.SetDescription(description);
            status.AddCssClass("compact");
            return status;
        }
    }

    /// <summary>Page d'un nouvel onglet : services du homelab, favoris et pages récentes.</summary>
    sealed class HomeView : PageView
    {
        readonly Action _servicesChanged;

        public HomeView(BrowserApplication app, BrowserWindow window) : base(app, window)
        {
            _servicesChanged = ScheduleRefresh;
        }

        protected override void Subscribe() => App.ServicesChanged += _servicesChanged;
        protected override void Unsubscribe() => App.ServicesChanged -= _servicesChanged;

        protected override void Build(Gtk.Box content)
        {
            var header = Gtk.Box.New(Gtk.Orientation.Horizontal, 14);
            header.SetHalign(Gtk.Align.Center);
            var logo = Gtk.Image.NewFromIconName(LinuxPaths.AppId);
            logo.SetPixelSize(56);
            header.Append(logo);
            var title = Gtk.Label.New(Window.IsPrivate ? Tr("Navigation privée") : "PommeBrowser");
            title.AddCssClass("home-title");
            header.Append(title);
            content.Append(header);

            if (Window.IsPrivate)
            {
                var info = Gtk.Label.New(Tr("Ni historique, ni cookies, ni cache ne sont conservés après la fermeture de cette fenêtre."));
                info.SetWrap(true);
                info.SetJustify(Gtk.Justification.Center);
                info.AddCssClass("dim-label");
                content.Append(info);
            }

            IReadOnlyList<HomelabService> services = App.Services.GetAll();
            content.Append(Heading(Tr("Services du homelab")));
            if (services.Count == 0)
            {
                var add = Gtk.Button.NewWithLabel(Tr("Ajouter un service…"));
                add.SetHalign(Gtk.Align.Start);
                add.AddCssClass("pill");
                add.OnClicked += (_, _) => Dialogs.EditService(App, Window.Window, null);
                content.Append(add);
            }
            else
            {
                content.Append(Cards(services.Select(s =>
                {
                    var tile = new ServiceTile(s) { Result = App.Monitor.GetResult(s.Id) };
                    return (tile.Name, tile.Address + " · " + tile.StatusText, s.Url, (Gtk.Widget)Panels.StatusDot(tile.State));
                })));
            }

            List<FavoriteItem> favorites = App.Favorites.All.Take(12).ToList();
            if (favorites.Count > 0)
            {
                content.Append(Heading(Tr("Favoris")));
                content.Append(Cards(favorites.Select(f =>
                    (f.Title.Length > 0 ? f.Title : f.Url, Host(f.Url), f.Url, (Gtk.Widget)Gtk.Image.NewFromIconName("starred-symbolic")))));
            }

            if (!Window.IsPrivate)
            {
                var recent = new List<HistoryEntry>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = App.History.Recent.Count - 1; i >= 0 && recent.Count < 8; i--)
                {
                    HistoryEntry entry = App.History.Recent[i];
                    if (seen.Add(entry.Url))
                        recent.Add(entry);
                }

                if (recent.Count > 0)
                {
                    content.Append(Heading(Tr("Récemment visités")));
                    Gtk.ListBox list = BoxedList();
                    foreach (HistoryEntry entry in recent)
                        list.Append(LinkRow(entry.Title, entry.Url, entry.Url));
                    content.Append(list);
                }
            }
        }

        Gtk.Widget Cards(IEnumerable<(string Title, string Subtitle, string Url, Gtk.Widget Icon)> items)
        {
            var flow = Gtk.FlowBox.New();
            flow.SetSelectionMode(Gtk.SelectionMode.None);
            flow.SetHomogeneous(true);
            flow.SetMinChildrenPerLine(1);
            flow.SetMaxChildrenPerLine(4);
            flow.SetRowSpacing(10);
            flow.SetColumnSpacing(10);

            foreach ((string title, string subtitle, string url, Gtk.Widget icon) in items)
            {
                var texts = Gtk.Box.New(Gtk.Orientation.Vertical, 2);
                var name = Gtk.Label.New(title);
                name.AddCssClass("card-title");
                name.SetXalign(0);
                name.SetEllipsize(Pango.EllipsizeMode.End);
                var detail = Gtk.Label.New(subtitle);
                detail.AddCssClass("card-subtitle");
                detail.SetXalign(0);
                detail.SetEllipsize(Pango.EllipsizeMode.End);
                texts.Append(name);
                texts.Append(detail);
                texts.SetHexpand(true);

                var inner = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
                icon.SetValign(Gtk.Align.Center);
                inner.Append(icon);
                inner.Append(texts);

                var button = Gtk.Button.New();
                button.SetChild(inner);
                button.AddCssClass("card");
                button.AddCssClass("home-card");
                button.SetTooltipText(url);
                button.OnClicked += (_, _) => Window.OpenFromPage(url);
                var middle = Gtk.GestureClick.New();
                middle.SetButton(2);
                middle.OnReleased += (_, _) => Window.OpenFromPage(url, newTab: true);
                button.AddController(middle);
                flow.Append(button);
            }
            return flow;
        }

        static string Host(string url) => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? uri.Host : url;
    }

    /// <summary>Historique : recherche, suppression d'une visite ou d'une période.</summary>
    sealed class HistoryView : PageView
    {
        const int MaxRows = 400;

        readonly Action _historyChanged;
        readonly Gtk.SearchEntry _search;

        public HistoryView(BrowserApplication app, BrowserWindow window) : base(app, window)
        {
            _historyChanged = ScheduleRefresh;
            _search = Gtk.SearchEntry.New();
            _search.SetPlaceholderText(Tr("Rechercher dans l'historique"));
            _search.SetHexpand(true);
            _search.OnSearchChanged += (_, _) => ScheduleRefresh();
        }

        protected override void Subscribe() => App.History.Changed += _historyChanged;
        protected override void Unsubscribe() => App.History.Changed -= _historyChanged;

        protected override void Build(Gtk.Box content)
        {
            var bar = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            if (_search.GetParent() is Gtk.Box previous)
                previous.Remove(_search);
            bar.Append(_search);
            var clear = Gtk.Button.NewWithLabel(Tr("Effacer…"));
            clear.AddCssClass("destructive-action");
            clear.OnClicked += (_, _) => AskClear();
            bar.Append(clear);
            content.Append(bar);

            string[] tokens = _search.GetText().ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            var entries = new List<HistoryEntry>();
            IReadOnlyList<HistoryEntry> recent = App.History.Recent;
            for (int i = recent.Count - 1; i >= 0 && entries.Count < MaxRows; i--)
            {
                HistoryEntry entry = recent[i];
                string haystack = (entry.Title + " " + entry.Url).ToLowerInvariant();
                if (tokens.All(haystack.Contains))
                    entries.Add(entry);
            }

            if (entries.Count == 0)
            {
                content.Append(EmptyState("document-open-recent-symbolic",
                    tokens.Length > 0 ? Tr("Aucun résultat") : Tr("Historique vide"),
                    tokens.Length > 0 ? Tr("Aucune page visitée ne correspond à cette recherche.") : Tr("Les pages que vous visitez apparaîtront ici.")));
                return;
            }

            foreach (IGrouping<DateTime, HistoryEntry> day in entries.GroupBy(e => e.VisitedAt.Date))
            {
                content.Append(Heading(DayLabel(day.Key)));
                Gtk.ListBox list = BoxedList();
                foreach (HistoryEntry entry in day)
                {
                    Adw.ActionRow row = LinkRow(entry.Title, entry.VisitedAt.ToString("t", Culture) + " · " + entry.Url, entry.Url);
                    var remove = Panels.SuffixButton("user-trash-symbolic", Tr("Supprimer de l'historique"));
                    HistoryEntry target = entry;
                    remove.OnClicked += (_, _) => App.History.Remove(new[] { target });
                    row.AddSuffix(remove);
                    list.Append(row);
                }
                content.Append(list);
            }
        }

        static string DayLabel(DateTime day)
        {
            if (day == DateTime.Today)
                return Tr("Aujourd'hui");
            if (day == DateTime.Today.AddDays(-1))
                return Tr("Hier");
            return day.ToString("D", Culture);
        }

        void AskClear()
        {
            var dialog = Adw.AlertDialog.New(Tr("Effacer l'historique"), Tr("Quelle période voulez-vous effacer ?"));
            dialog.AddResponse("cancel", Tr("Annuler"));
            dialog.AddResponse("hour", Tr("Dernière heure"));
            dialog.AddResponse("today", Tr("Aujourd'hui"));
            dialog.AddResponse("all", Tr("Tout"));
            dialog.SetResponseAppearance("all", Adw.ResponseAppearance.Destructive);
            dialog.SetCloseResponse("cancel");
            dialog.OnResponse += (_, args) =>
            {
                DateTime? since = args.Response switch
                {
                    "hour" => DateTime.Now.AddHours(-1),
                    "today" => DateTime.Today,
                    "all" => DateTime.MinValue,
                    _ => null
                };
                if (since is { } value)
                    App.History.RemoveSince(value);
            };
            dialog.Present(Window.Window);
        }
    }

    /// <summary>Favoris : par dossier, modification, suppression et import depuis les autres navigateurs.</summary>
    sealed class FavoritesView : PageView
    {
        readonly Action _favoritesChanged;

        public FavoritesView(BrowserApplication app, BrowserWindow window) : base(app, window)
        {
            _favoritesChanged = ScheduleRefresh;
        }

        protected override void Subscribe() => App.Favorites.Changed += _favoritesChanged;
        protected override void Unsubscribe() => App.Favorites.Changed -= _favoritesChanged;

        protected override void Build(Gtk.Box content)
        {
            var bar = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            var import = Gtk.MenuButton.New();
            import.SetLabel(Tr("Importer"));
            import.SetMenuModel(ImportMenu());
            bar.Append(import);
            content.Append(bar);

            if (App.Favorites.All.Count == 0)
            {
                content.Append(EmptyState("user-bookmarks-symbolic", Tr("Aucun favori"),
                    Tr("Ajoutez une page avec l'étoile de la barre d'adresse (Ctrl+D), ou importez les favoris d'un autre navigateur.")));
                return;
            }

            var groups = App.Favorites.All
                .GroupBy(f => f.Folder ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                .OrderBy(g => g.Key.Length == 0 ? 0 : 1)
                .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase);

            foreach (IGrouping<string, FavoriteItem> group in groups)
            {
                content.Append(Heading(group.Key.Length == 0 ? Tr("Favoris") : group.Key));
                Gtk.ListBox list = BoxedList();
                foreach (FavoriteItem favorite in group)
                {
                    Adw.ActionRow row = LinkRow(favorite.Title, favorite.Url, favorite.Url);
                    var edit = Panels.SuffixButton("document-edit-symbolic", Tr("Modifier"));
                    FavoriteItem target = favorite;
                    edit.OnClicked += (_, _) => Dialogs.EditFavorite(App, Window.Window, target);
                    row.AddSuffix(edit);
                    list.Append(row);
                }
                content.Append(list);
            }
        }

        Gio.Menu ImportMenu()
        {
            var menu = Gio.Menu.New();
            var group = Gio.SimpleActionGroup.New();
            IReadOnlyList<BookmarkSource> sources = BookmarkImporter.DetectSources();
            for (int i = 0; i < sources.Count; i++)
            {
                BookmarkSource source = sources[i];
                var action = Gio.SimpleAction.New("source" + i, null);
                action.OnActivate += (_, _) => Import(() => BookmarkImporter.Read(source), source.Name);
                group.AddAction(action);
                menu.Append(source.Name.Replace("_", "__"), "import.source" + i);
            }

            var file = Gio.SimpleAction.New("file", null);
            file.OnActivate += (_, _) => ImportHtmlFile();
            group.AddAction(file);
            menu.Append(Tr("Fichier HTML de favoris…"), "import.file");

            Window.Window.InsertActionGroup("import", group);
            return menu;
        }

        void Import(Func<IReadOnlyList<ImportedBookmark>> read, string sourceName)
        {
            try
            {
                int added = App.Favorites.Import(read());
                Window.ShowToast(added == 0
                    ? Tr("Aucun nouveau favori dans {0}.", sourceName)
                    : Tr("{0} favoris importés depuis {1}.", added, sourceName));
            }
            catch (Exception ex)
            {
                Window.ShowToast(Tr("Import impossible : {0}", ex.Message));
            }
        }

        async void ImportHtmlFile()
        {
            var filter = Gtk.FileFilter.New();
            filter.SetName(Tr("Favoris (HTML)"));
            filter.AddPattern("*.html");
            filter.AddPattern("*.htm");
            var filters = Gio.ListStore.New(Gtk.FileFilter.GetGType());
            filters.Append(filter);

            var dialog = Gtk.FileDialog.New();
            dialog.SetTitle(Tr("Importer des favoris"));
            dialog.SetFilters(filters);
            try
            {
                Gio.File? file = await dialog.OpenAsync(Window.Window);
                if (file?.GetPath() is { } path)
                    Import(() => BookmarkImporter.ParseNetscapeHtml(System.IO.File.ReadAllText(path)), System.IO.Path.GetFileName(path));
            }
            catch (GLib.GException)
            {
                // Sélection annulée.
            }
        }
    }

    /// <summary>Services du homelab : état, ajout, modification, suppression.</summary>
    sealed class ServicesView : PageView
    {
        readonly Action _servicesChanged;

        public ServicesView(BrowserApplication app, BrowserWindow window) : base(app, window)
        {
            _servicesChanged = ScheduleRefresh;
        }

        protected override void Subscribe() => App.ServicesChanged += _servicesChanged;
        protected override void Unsubscribe() => App.ServicesChanged -= _servicesChanged;

        protected override void Build(Gtk.Box content)
        {
            var bar = Gtk.Box.New(Gtk.Orientation.Horizontal, 8);
            var add = Gtk.Button.NewWithLabel(Tr("Ajouter un service"));
            add.AddCssClass("suggested-action");
            add.OnClicked += (_, _) => Dialogs.EditService(App, Window.Window, null);
            bar.Append(add);
            var check = Gtk.Button.NewWithLabel(Tr("Vérifier maintenant"));
            check.OnClicked += async (_, _) =>
            {
                check.SetSensitive(false);
                await App.Monitor.CheckAllAsync();
                check.SetSensitive(true);
            };
            bar.Append(check);
            content.Append(bar);

            IReadOnlyList<HomelabService> services = App.Services.GetAll();
            if (services.Count == 0)
            {
                content.Append(EmptyState("network-server-symbolic", Tr("Aucun service"),
                    Tr("Ajoutez l'adresse de votre NAS, de Proxmox, de votre routeur… PommeBrowser vérifie qu'ils répondent et vous prévient s'ils tombent.")));
                return;
            }

            foreach (IGrouping<string, HomelabService> group in services
                         .GroupBy(s => s.Group ?? string.Empty, StringComparer.CurrentCultureIgnoreCase)
                         .OrderBy(g => g.Key.Length == 0 ? 0 : 1)
                         .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase))
            {
                content.Append(Heading(group.Key.Length == 0 ? Tr("Services") : group.Key));
                Gtk.ListBox list = BoxedList();
                foreach (HomelabService service in group.OrderBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
                {
                    var tile = new ServiceTile(service) { Result = App.Monitor.GetResult(service.Id) };
                    Adw.ActionRow row = LinkRow(tile.Name, tile.Address + " · " + tile.StatusText, service.Url);
                    row.AddPrefix(Panels.StatusDot(tile.State));

                    var edit = Panels.SuffixButton("document-edit-symbolic", Tr("Modifier"));
                    HomelabService target = service;
                    edit.OnClicked += (_, _) => Dialogs.EditService(App, Window.Window, target);
                    var remove = Panels.SuffixButton("user-trash-symbolic", Tr("Supprimer"));
                    remove.OnClicked += (_, _) => Dialogs.Confirm(Window.Window,
                        Tr("Supprimer {0} ?", target.Name),
                        Tr("Le service ne sera plus affiché ni surveillé."),
                        Tr("Supprimer"), destructive: true,
                        () => App.Services.Remove(target.Id));
                    row.AddSuffix(edit);
                    row.AddSuffix(remove);
                    list.Append(row);
                }
                content.Append(list);
            }
        }
    }
}
