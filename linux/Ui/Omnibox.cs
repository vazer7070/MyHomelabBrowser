using System;
using System.Collections.Generic;
using MyHomelabBrowser.classes;
using PommeBrowser.Linux.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Ui
{
    /// <summary>
    /// Barre d'adresse : adresse ou recherche, niveau de sécurité de la page et propositions
    /// (services du homelab, favoris, historique) au fil de la saisie.
    /// </summary>
    sealed class Omnibox
    {
        const uint KeyUp = 0xff52;
        const uint KeyDown = 0xff54;
        const uint KeyEscape = 0xff1b;
        const uint KeyReturn = 0xff0d;
        const uint KeyKpEnter = 0xff8d;

        readonly BrowserApplication _app;
        readonly Gtk.Entry _entry;
        readonly Gtk.Popover _popover;
        readonly Gtk.ListBox _list;
        IReadOnlyList<Suggestion> _suggestions = Array.Empty<Suggestion>();
        string _displayedUrl = string.Empty;
        bool _settingText;

        public Omnibox(BrowserApplication app)
        {
            _app = app;

            _entry = Gtk.Entry.New();
            _entry.AddCssClass("omnibox");
            _entry.SetHexpand(true);
            _entry.SetPlaceholderText(Tr("Rechercher ou saisir une adresse"));
            _entry.SetInputPurpose(Gtk.InputPurpose.Url);
            _entry.SetInputHints(Gtk.InputHints.NoSpellcheck | Gtk.InputHints.NoEmoji);
            _entry.OnActivate += (_, _) => ActivateSelection();
            _entry.OnChanged += (_, _) =>
            {
                if (!_settingText && HasFocus)
                    UpdateSuggestions();
            };
            _entry.OnIconPress += (_, args) =>
            {
                if (args.IconPos == Gtk.EntryIconPosition.Primary)
                    SecurityIconClicked?.Invoke();
            };

            var keys = Gtk.EventControllerKey.New();
            keys.SetPropagationPhase(Gtk.PropagationPhase.Capture);
            keys.OnKeyPressed += (_, args) => OnKey(args.Keyval);
            _entry.AddController(keys);

            var focus = Gtk.EventControllerFocus.New();
            focus.OnEnter += (_, _) => GLib.Functions.IdleAdd(0, () =>
            {
                _entry.SelectRegion(0, -1);
                return false;
            });
            focus.OnLeave += (_, _) => GLib.Functions.TimeoutAdd(0, 150, () =>
            {
                if (!HasFocus)
                {
                    Hide();
                    ShowUrl(_displayedUrl);
                }
                return false;
            });
            _entry.AddController(focus);

            _list = Gtk.ListBox.New();
            _list.AddCssClass("omnibox-suggestions");
            _list.AddCssClass("navigation-sidebar");
            _list.SetSelectionMode(Gtk.SelectionMode.Single);
            _list.SetCanFocus(false);
            _list.SetFocusable(false);
            _list.OnRowActivated += (_, args) => Activate(args.Row.GetIndex());

            _popover = Gtk.Popover.New();
            _popover.SetChild(_list);
            _popover.SetParent(_entry);
            _popover.SetAutohide(false);
            _popover.SetHasArrow(false);
            _popover.SetCanFocus(false);
            _popover.SetPosition(Gtk.PositionType.Bottom);
            _popover.SetHalign(Gtk.Align.Start);
        }

        public Gtk.Widget Widget => _entry;

        // Le focus est sur le champ texte interne de Gtk.Entry, pas sur l'entrée elle-même.
        bool HasFocus => (_entry.GetStateFlags() & Gtk.StateFlags.FocusWithin) != 0;

        /// <summary>Adresse à ouvrir dans l'onglet courant.</summary>
        public event Action<string>? Navigate;

        public event Action? SecurityIconClicked;

        public void Focus()
        {
            _entry.GrabFocus();
            _entry.SelectRegion(0, -1);
        }

        /// <summary>Adresse de la page affichée (sans effet pendant la saisie).</summary>
        public void ShowUrl(string url)
        {
            _displayedUrl = url;
            if (HasFocus && _popover.GetVisible())
                return;

            _settingText = true;
            _entry.SetText(url);
            _settingText = false;
        }

        public void SetProgress(double fraction) => _entry.SetProgressFraction(fraction >= 1 ? 0 : fraction);

        public void SetSecurity(SecurityLevel level)
        {
            (string? icon, string tooltip) = level switch
            {
                SecurityLevel.Secure => ("channel-secure-symbolic", Tr("Connexion sécurisée")),
                SecurityLevel.Trusted => ("channel-secure-symbolic", Tr("Connexion chiffrée avec un certificat que vous avez approuvé")),
                SecurityLevel.Mixed => ("channel-insecure-symbolic", Tr("Une partie de la page n'est pas chiffrée")),
                SecurityLevel.Insecure => ("channel-insecure-symbolic", Tr("Connexion non sécurisée")),
                SecurityLevel.Local => ("network-server-symbolic", Tr("Réseau local, connexion non chiffrée")),
                _ => ("system-search-symbolic", string.Empty)
            };
            _entry.SetIconFromIconName(Gtk.EntryIconPosition.Primary, icon);
            _entry.SetIconTooltipText(Gtk.EntryIconPosition.Primary, tooltip);
            _entry.SetIconActivatable(Gtk.EntryIconPosition.Primary, level != SecurityLevel.None);
        }

        bool OnKey(uint key)
        {
            bool open = _popover.GetVisible();
            switch (key)
            {
                case KeyDown when open:
                    Move(1);
                    return true;
                case KeyUp when open:
                    Move(-1);
                    return true;
                case KeyEscape:
                    if (open)
                        Hide();
                    ShowUrl(_displayedUrl);
                    _entry.SelectRegion(0, -1);
                    return true;
                case KeyReturn or KeyKpEnter:
                    ActivateSelection();
                    return true;
            }
            return false;
        }

        void Move(int direction)
        {
            int count = _suggestions.Count;
            if (count == 0)
                return;

            int index = _list.GetSelectedRow()?.GetIndex() ?? -1;
            index = (index + direction + count) % count;
            _list.SelectRow(_list.GetRowAtIndex(index));

            // La proposition choisie s'affiche dans la barre, comme dans les autres navigateurs.
            _settingText = true;
            _entry.SetText(_suggestions[index].Kind == SuggestionKind.Search ? _entry.GetText() : _suggestions[index].Url);
            _entry.SetPosition(-1);
            _settingText = false;
        }

        void ActivateSelection()
        {
            int index = _popover.GetVisible() ? _list.GetSelectedRow()?.GetIndex() ?? -1 : -1;
            if (index >= 0)
            {
                Activate(index);
                return;
            }

            string text = _entry.GetText().Trim();
            if (text.Length == 0)
                return;

            Hide();
            Navigate?.Invoke(UrlResolver.ResolveOrSearch(text, _app.Settings.Search));
        }

        void Activate(int index)
        {
            if (index < 0 || index >= _suggestions.Count)
                return;

            string url = _suggestions[index].Url;
            Hide();
            Navigate?.Invoke(url);
        }

        void UpdateSuggestions()
        {
            _suggestions = OmniboxSuggestions.Build(
                _entry.GetText(),
                _app.Settings.Search,
                _app.Services.GetAll(),
                _app.Favorites.All,
                _app.History.Recent);

            _list.RemoveAll();
            if (_suggestions.Count == 0)
            {
                Hide();
                return;
            }

            foreach (Suggestion suggestion in _suggestions)
                _list.Append(BuildRow(suggestion));

            _list.SelectRow(_list.GetRowAtIndex(0));
            _popover.SetSizeRequest(Math.Max(_entry.GetWidth(), 320), -1);
            if (!_popover.GetVisible())
                _popover.Popup();
        }

        static Gtk.Widget BuildRow(Suggestion suggestion)
        {
            string icon = suggestion.Kind switch
            {
                SuggestionKind.Search => "system-search-symbolic",
                SuggestionKind.Service => "network-server-symbolic",
                SuggestionKind.Favorite => "starred-symbolic",
                SuggestionKind.History => "document-open-recent-symbolic",
                _ => "web-browser-symbolic"
            };

            var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 10);
            box.Append(Gtk.Image.NewFromIconName(icon));

            var texts = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            var title = Gtk.Label.New(suggestion.Title);
            title.SetXalign(0);
            title.SetEllipsize(Pango.EllipsizeMode.End);
            texts.Append(title);

            if (suggestion.Kind is SuggestionKind.Service or SuggestionKind.Favorite or SuggestionKind.History && suggestion.Title != suggestion.Url)
            {
                var url = Gtk.Label.New(suggestion.Url);
                url.SetXalign(0);
                url.SetEllipsize(Pango.EllipsizeMode.Middle);
                url.AddCssClass("dim-label");
                texts.Append(url);
            }
            texts.SetHexpand(true);
            box.Append(texts);

            var row = Gtk.ListBoxRow.New();
            row.SetChild(box);
            row.SetFocusable(false);
            return row;
        }

        void Hide()
        {
            if (_popover.GetVisible())
                _popover.Popdown();
        }
    }
}
