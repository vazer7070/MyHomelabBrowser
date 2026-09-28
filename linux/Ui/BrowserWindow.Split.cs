using System;
using System.Linq;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Ui
{
    /// <summary>
    /// Vue côte à côte : l'onglet sélectionné occupe le volet actif, son partenaire reste affiché
    /// dans l'autre. Sélectionner le partenaire (onglet, en-tête ou clic dans son volet) échange les
    /// rôles sans déplacer les volets ; sélectionner un autre onglet le place dans le volet actif.
    ///
    /// Les volets sont affichés à la place de la vue des onglets, dont la barre reste utilisable :
    /// les pages ne sont déplacées que lorsqu'un onglet entre ou sort de la vue côte à côte.
    /// </summary>
    sealed partial class BrowserWindow
    {
        sealed class SplitPane
        {
            public required Gtk.Box Root { get; init; }
            public required Gtk.Box Header { get; init; }
            public required Gtk.Label Title { get; init; }
            public required Gtk.Box Holder { get; init; }
            public BrowserTab? Tab { get; set; }
        }

        Gtk.Stack _content = null!;
        Gtk.Paned _paned = null!;
        readonly SplitPane[] _panes = new SplitPane[2];
        BrowserTab? _splitPartner;
        bool _splitActiveIsLeft = true;
        bool _splitPositioned;

        public bool IsSplit => _splitPartner != null;

        Gtk.Widget CreateContentArea()
        {
            _panes[0] = CreatePane();
            _panes[1] = CreatePane();

            _paned = Gtk.Paned.New(Gtk.Orientation.Horizontal);
            _paned.SetStartChild(_panes[0].Root);
            _paned.SetEndChild(_panes[1].Root);
            _paned.SetResizeStartChild(true);
            _paned.SetResizeEndChild(true);
            _paned.SetShrinkStartChild(false);
            _paned.SetShrinkEndChild(false);
            _paned.SetWideHandle(true);

            _content = Gtk.Stack.New();
            _content.AddNamed(_tabs, "tabs");
            _content.AddNamed(_paned, "split");
            _content.SetVisibleChildName("tabs");
            return _content;
        }

        SplitPane CreatePane()
        {
            var title = Gtk.Label.New(null);
            title.SetEllipsize(Pango.EllipsizeMode.End);
            title.SetHexpand(true);
            title.SetXalign(0);

            var close = Gtk.Button.NewFromIconName("window-close-symbolic");
            close.AddCssClass("flat");
            close.AddCssClass("circular");
            close.SetTooltipText(Tr("Quitter la vue côte à côte"));
            close.OnClicked += (_, _) => ExitSplit();

            var header = Gtk.Box.New(Gtk.Orientation.Horizontal, 6);
            header.AddCssClass("split-header");
            header.Append(title);
            header.Append(close);

            var holder = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            holder.SetVexpand(true);
            holder.SetHexpand(true);

            var root = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            root.SetSizeRequest(280, -1);
            root.Append(header);
            root.Append(holder);

            var pane = new SplitPane { Root = root, Header = header, Title = title, Holder = holder };

            // Un clic dans le volet inactif (page comprise) le rend actif ; le clic continue vers la page.
            var click = Gtk.GestureClick.New();
            click.SetPropagationPhase(Gtk.PropagationPhase.Capture);
            click.OnPressed += (_, _) =>
            {
                if (pane.Tab?.Page is { } page && pane.Tab != Current)
                    _tabs.SetSelectedPage(page);
            };
            root.AddController(click);
            return pane;
        }

        /// <summary>Affiche <paramref name="partner"/> à côté de l'onglet sélectionné.</summary>
        public void ShowSideBySide(BrowserTab partner)
        {
            if (Current is not { } selected || selected == partner || partner.Window != this)
                return;

            if (IsSplit)
                ReleasePanes();
            _splitPartner = partner;
            _splitActiveIsLeft = true;
            ComposeSplit();
        }

        /// <summary>Menu « Vue côte à côte » : avec l'onglet utilisé le plus récemment, ou un nouvel onglet.</summary>
        void ToggleSplit()
        {
            if (IsSplit)
            {
                ExitSplit();
                return;
            }

            if (Current is not { } selected)
                return;

            BrowserTab? partner = Tabs.Where(t => t != selected).OrderByDescending(t => t.LastActivated).FirstOrDefault();
            if (partner == null)
            {
                partner = new BrowserTab(_app, this);
                partner.ShowHome();
                AddTab(partner, select: false, after: selected);
            }
            ShowSideBySide(partner);
        }

        public void ExitSplit()
        {
            if (!IsSplit)
                return;

            ReleasePanes();
            _splitPartner = null;
            _content.SetVisibleChildName("tabs");
            UpdateChrome();
        }

        /// <summary>Remet chaque contenu dans son onglet.</summary>
        void ReleasePanes()
        {
            foreach (SplitPane pane in _panes)
            {
                pane.Tab?.MoveViewTo(null);
                pane.Tab = null;
            }
        }

        /// <summary>Sélection changée pendant la vue côte à côte.</summary>
        void OnSplitSelectionChanged(BrowserTab selected)
        {
            if (_splitPartner == null)
                return;

            SplitPane active = _panes[_splitActiveIsLeft ? 0 : 1];
            if (selected == _splitPartner)
            {
                // Onglet de l'autre volet : les rôles s'échangent, rien ne bouge.
                _splitPartner = active.Tab;
                _splitActiveIsLeft = !_splitActiveIsLeft;
                if (_splitPartner == null)
                    ExitSplit();
                else
                    UpdateSplitHeaders();
                return;
            }

            if (selected != active.Tab)
            {
                // Autre onglet : il prend la place de l'onglet actif.
                active.Tab?.MoveViewTo(null);
                active.Tab = null;
                ComposeSplit();
            }
        }

        void ComposeSplit()
        {
            if (_splitPartner == null || Current is not { } selected)
                return;

            SplitPane active = _panes[_splitActiveIsLeft ? 0 : 1];
            SplitPane other = _panes[_splitActiveIsLeft ? 1 : 0];

            if (active.Tab != selected)
            {
                active.Tab?.MoveViewTo(null);
                selected.MoveViewTo(active.Holder);
                active.Tab = selected;
            }
            if (other.Tab != _splitPartner)
            {
                other.Tab?.MoveViewTo(null);
                _splitPartner.MoveViewTo(other.Holder);
                _splitPartner.LoadPendingIfNeeded();
                other.Tab = _splitPartner;
            }

            _content.SetVisibleChildName("split");
            if (!_splitPositioned)
            {
                // Deux moitiés égales à la première ouverture ; ensuite, la position choisie est gardée.
                _splitPositioned = true;
                GLib.Functions.IdleAdd(0, () =>
                {
                    int width = _paned.GetWidth();
                    if (width > 0)
                        _paned.SetPosition(width / 2);
                    return false;
                });
            }
            UpdateSplitHeaders();
        }

        void UpdateSplitHeaders()
        {
            if (!IsSplit)
                return;

            for (int i = 0; i < 2; i++)
            {
                SplitPane pane = _panes[i];
                bool isActive = (i == 0) == _splitActiveIsLeft;
                pane.Title.SetLabel(pane.Tab?.Title ?? Tr("Nouvel onglet"));
                if (isActive)
                    pane.Header.AddCssClass("active");
                else
                    pane.Header.RemoveCssClass("active");
                pane.Header.SetTooltipText(isActive ? null : Tr("Cliquer pour activer ce volet"));
            }
        }

        /// <summary>Onglet fermé ou emmené dans une autre fenêtre : la vue côte à côte s'arrête avant.</summary>
        void LeaveSplitIfShown(BrowserTab tab)
        {
            if (IsSplit && _panes.Any(p => p.Tab == tab))
                ExitSplit();
        }
    }
}
