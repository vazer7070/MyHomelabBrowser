using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Dialogs
{
    /// <summary>
    /// Boîte de saisie : titre, texte, champs, message d'erreur et boutons. Elle reste ouverte
    /// tant que la saisie est refusée (mot de passe incorrect, nom déjà pris…), et la
    /// validation peut être longue sans figer l'interface. Entrée valide, Échap annule.
    /// </summary>
    public class FormDialog : Window
    {
        readonly StackPanel _body = new() { Spacing = 12 };
        readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        readonly Button _confirm;
        bool _busy;
        bool _enabled = true;

        public FormDialog(string title, string? confirmLabel, bool destructive = false, double width = 440, string? cancelLabel = null)
        {
            Title = title;
            Width = width;
            SizeToContent = SizeToContent.Height;
            CanResize = false;
            ShowInTaskbar = false;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            _error.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("DangerBrush"));

            var heading = new TextBlock { Text = title, Classes = { "section" }, FontSize = 17, TextWrapping = TextWrapping.Wrap };

            var cancel = new Button { Content = cancelLabel ?? Tr("Annuler"), IsCancel = true };
            cancel.Click += (_, _) => Close();
            _confirm = new Button { Content = confirmLabel ?? Tr("OK"), IsDefault = true };
            _confirm.Classes.Add(destructive ? "danger" : "primary");
            _confirm.Click += (_, _) => _ = SubmitAsync();
            if (cancelLabel != string.Empty)
                _buttons.Children.Add(cancel);
            if (confirmLabel != string.Empty)
                _buttons.Children.Add(_confirm);

            var root = new DockPanel { Margin = new Thickness(24, 20, 24, 20) };
            DockPanel.SetDock(heading, Dock.Top);
            DockPanel.SetDock(_buttons, Dock.Bottom);
            _buttons.Margin = new Thickness(0, 20, 0, 0);
            heading.Margin = new Thickness(0, 0, 0, 14);
            root.Children.Add(heading);
            root.Children.Add(_buttons);
            _body.Children.Add(_error);
            root.Children.Add(_body);
            Content = root;
        }

        /// <summary>Validation : message d'erreur à afficher, ou null pour fermer la boîte.</summary>
        public Func<Task<string?>>? Submit { get; set; }

        /// <summary>Vrai si la boîte a été validée (et non annulée).</summary>
        public bool Confirmed { get; private set; }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            // Premier champ prêt à la saisie.
            foreach (Control child in _body.Children)
            {
                if (FirstInput(child) is { } input)
                {
                    input.Focus();
                    break;
                }
            }
        }

        static Control? FirstInput(Control control) => control switch
        {
            TextBox box => box,
            Panel panel => FindIn(panel.Children),
            Decorator { Child: { } child } => FirstInput(child),
            _ => null
        };

        static Control? FindIn(IEnumerable<Control> children)
        {
            foreach (Control child in children)
            {
                if (FirstInput(child) is { } input)
                    return input;
            }
            return null;
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Key == Key.Escape && !e.Handled)
            {
                Close();
                e.Handled = true;
            }
        }

        /// <summary>Les champs viennent avant le message d'erreur.</summary>
        void Insert(Control control) => _body.Children.Insert(_body.Children.Count - 1, control);

        public void Add(Control control) => Insert(control);

        public TextBlock AddText(string text, bool hint = false)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            block.Classes.Add(hint ? "hint" : "subtitle");
            Insert(block);
            return block;
        }

        /// <summary>Champ de texte avec son libellé ; <paramref name="password"/> : caractères masqués.</summary>
        public TextBox AddEntry(string label, string? text = null, bool password = false, string? watermark = null)
        {
            var box = new TextBox { Text = text ?? string.Empty, PlaceholderText = watermark };
            if (password)
            {
                box.PasswordChar = '•';
                box.Classes.Add("revealPasswordButton");
            }
            Insert(Labeled(label, box));
            return box;
        }

        public CheckBox AddCheck(string label, bool value)
        {
            var check = new CheckBox { Content = label, IsChecked = value };
            Insert(check);
            return check;
        }

        public ComboBox AddChoice(string label, IEnumerable<string> items, int selected)
        {
            var combo = new ComboBox { ItemsSource = items, SelectedIndex = selected, HorizontalAlignment = HorizontalAlignment.Stretch };
            Insert(Labeled(label, combo));
            return combo;
        }

        /// <summary>Bouton supplémentaire à gauche des boutons de validation (ex. « Supprimer »).</summary>
        public Button AddExtraButton(string label, bool destructive, Action onClick)
        {
            var button = new Button { Content = label };
            if (destructive)
                button.Classes.Add("danger");
            button.Click += (_, _) => onClick();
            _buttons.Children.Insert(0, button);
            return button;
        }

        public static Control Labeled(string label, Control control)
        {
            var panel = new StackPanel { Spacing = 6 };
            var text = new TextBlock { Text = label, FontSize = 12.5, FontWeight = FontWeight.SemiBold };
            text.Classes.Add("subtitle");
            panel.Children.Add(text);
            panel.Children.Add(control);
            return panel;
        }

        public void SetConfirmEnabled(bool enabled)
        {
            _enabled = enabled;
            _confirm.IsEnabled = _enabled && !_busy;
        }

        public void ShowError(string? message)
        {
            _error.Text = message ?? string.Empty;
            _error.IsVisible = !string.IsNullOrEmpty(message);
        }

        async Task SubmitAsync()
        {
            if (_busy || !_enabled)
                return;

            if (Submit == null)
            {
                Confirmed = true;
                Close();
                return;
            }

            _busy = true;
            _confirm.IsEnabled = false;
            ShowError(null);
            try
            {
                string? error = await Submit();
                if (error == null)
                {
                    Confirmed = true;
                    Close();
                    return;
                }
                ShowError(error);
            }
            finally
            {
                _busy = false;
                _confirm.IsEnabled = _enabled;
            }
        }

        /// <summary>Affiche la boîte au-dessus de <paramref name="owner"/> ; vrai si elle a été validée.</summary>
        public async Task<bool> ShowAsync(Window owner)
        {
            await ShowDialog(owner);
            return Confirmed;
        }
    }
}
