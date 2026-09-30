using System;
using System.Threading.Tasks;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Ui
{
    /// <summary>
    /// Boîte de saisie libadwaita : champs, message d'erreur et bouton de validation.
    /// Contrairement à Adw.AlertDialog, elle reste ouverte tant que la saisie est refusée
    /// (mot de passe incorrect, nom déjà pris…), et la validation peut être longue sans figer l'interface.
    /// </summary>
    sealed class FormDialog
    {
        readonly Gtk.Box _body;
        readonly Gtk.Label _error;
        readonly Gtk.Button _confirm;
        bool _busy;
        bool _enabled = true;

        public FormDialog(string title, string confirmLabel, bool destructive = false, int width = 420)
        {
            Dialog = Adw.Dialog.New();
            Dialog.SetTitle(title);
            Dialog.SetContentWidth(width);

            var cancel = Gtk.Button.NewWithLabel(Tr("Annuler"));
            cancel.OnClicked += (_, _) => Dialog.Close();
            _confirm = Gtk.Button.NewWithLabel(confirmLabel);
            _confirm.AddCssClass(destructive ? "destructive-action" : "suggested-action");
            _confirm.OnClicked += (_, _) => _ = SubmitAsync();

            var header = Adw.HeaderBar.New();
            header.SetShowStartTitleButtons(false);
            header.SetShowEndTitleButtons(false);
            header.PackStart(cancel);
            header.PackEnd(_confirm);

            _body = Gtk.Box.New(Gtk.Orientation.Vertical, 18);
            _error = Gtk.Label.New(null);
            _error.AddCssClass("error");
            _error.SetWrap(true);
            _error.SetXalign(0);
            _error.SetVisible(false);
            _body.Append(_error);

            var content = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            content.SetMarginTop(12);
            content.SetMarginBottom(18);
            content.SetMarginStart(18);
            content.SetMarginEnd(18);
            content.Append(_body);

            var toolbar = Adw.ToolbarView.New();
            toolbar.AddTopBar(header);
            toolbar.SetContent(content);
            Dialog.SetChild(toolbar);
            Dialog.SetDefaultWidget(_confirm);
        }

        public Adw.Dialog Dialog { get; }

        /// <summary>Validation : message d'erreur à afficher, ou null pour fermer la boîte.</summary>
        public Func<Task<string?>>? Submit { get; set; }

        /// <summary>Texte d'explication au-dessus des champs.</summary>
        public void AddText(string text)
        {
            var label = Gtk.Label.New(text);
            label.SetWrap(true);
            label.SetXalign(0);
            Insert(label);
        }

        public Adw.PreferencesGroup AddGroup(string? title, params Gtk.Widget[] rows)
        {
            var group = Adw.PreferencesGroup.New();
            if (title != null)
                group.SetTitle(title);
            foreach (Gtk.Widget row in rows)
            {
                if (row is Adw.EntryRow entry)
                    entry.SetActivatesDefault(true);
                group.Add(row);
            }
            Insert(group);
            return group;
        }

        public void Add(Gtk.Widget widget) => Insert(widget);

        /// <summary>Les champs viennent avant le message d'erreur.</summary>
        void Insert(Gtk.Widget widget) => _body.InsertChildAfter(widget, _error.GetPrevSibling());

        public void SetConfirmEnabled(bool enabled)
        {
            _enabled = enabled;
            _confirm.SetSensitive(_enabled && !_busy);
        }

        public void ShowError(string? message)
        {
            _error.SetLabel(message ?? string.Empty);
            _error.SetVisible(!string.IsNullOrEmpty(message));
        }

        public void Present(Gtk.Widget parent) => Dialog.Present(parent);

        async Task SubmitAsync()
        {
            if (_busy || !_enabled || Submit == null)
                return;

            _busy = true;
            _confirm.SetSensitive(false);
            ShowError(null);
            try
            {
                string? error = await Submit();
                if (error == null)
                {
                    Dialog.Close();
                    return;
                }
                ShowError(error);
            }
            finally
            {
                _busy = false;
                _confirm.SetSensitive(_enabled);
            }
        }

        /// <summary>Ligne de mot de passe (masqué, bouton pour l'afficher).</summary>
        public static Adw.PasswordEntryRow PasswordRow(string title)
        {
            var row = Adw.PasswordEntryRow.New();
            row.SetTitle(title);
            return row;
        }

        public static Adw.EntryRow EntryRow(string title, string? text = null)
        {
            var row = Adw.EntryRow.New();
            row.SetTitle(title);
            if (text != null)
                row.SetText(text);
            return row;
        }
    }
}
