using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles
{
    /// <summary>
    /// Remplace MessageBox.Show : même signature, mais au style de l'application
    /// (la boîte Windows reste claire et détonne avec le thème sombre).
    /// </summary>
    public partial class MessageDialog : DialogWindow
    {
        MessageBoxResult _result;

        MessageDialog(string message, string title, MessageBoxButton buttons, MessageBoxImage image)
        {
            InitializeComponent();

            Title = string.IsNullOrWhiteSpace(title) ? "PommeBrowser" : title;
            MessageText.Text = message.TrimEnd();

            // Fermer (Échap, croix) : réponse neutre.
            _result = buttons switch
            {
                MessageBoxButton.OK => MessageBoxResult.OK,
                MessageBoxButton.YesNo => MessageBoxResult.No,
                _ => MessageBoxResult.Cancel
            };

            SetIcon(image);
            bool destructive = image is MessageBoxImage.Warning or MessageBoxImage.Error;

            switch (buttons)
            {
                case MessageBoxButton.OK:
                    AddButton(Tr("OK"), MessageBoxResult.OK, primary: true);
                    break;
                case MessageBoxButton.OKCancel:
                    AddButton(Tr("Annuler"), MessageBoxResult.Cancel);
                    AddButton(Tr("OK"), MessageBoxResult.OK, primary: true, danger: destructive);
                    break;
                case MessageBoxButton.YesNo:
                    AddButton(Tr("Non"), MessageBoxResult.No);
                    AddButton(Tr("Oui"), MessageBoxResult.Yes, primary: true, danger: destructive);
                    break;
                case MessageBoxButton.YesNoCancel:
                    AddButton(Tr("Annuler"), MessageBoxResult.Cancel);
                    AddButton(Tr("Non"), MessageBoxResult.No);
                    AddButton(Tr("Oui"), MessageBoxResult.Yes, primary: true, danger: destructive);
                    break;
            }
        }

        void SetIcon(MessageBoxImage image)
        {
            (string glyph, string brush, string soft)? icon = image switch
            {
                MessageBoxImage.Error => ("", "DangerBrush", "DangerSoftBrush"),
                MessageBoxImage.Warning => ("", "WarningBrush", "WarningSoftBrush"),
                MessageBoxImage.Question => ("", "AccentBrush", "AccentSoftBrush"),
                MessageBoxImage.Information => ("", "AccentBrush", "AccentSoftBrush"),
                _ => null
            };

            if (icon is not { } i)
                return;

            IconGlyph.Text = i.glyph;
            IconGlyph.SetResourceReference(TextBlock.ForegroundProperty, i.brush);
            IconBadge.SetResourceReference(Border.BackgroundProperty, i.soft);
            IconBadge.Visibility = Visibility.Visible;
        }

        void AddButton(string label, MessageBoxResult result, bool primary = false, bool danger = false)
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 88,
                Margin = new Thickness(ButtonBar.Children.Count == 0 ? 0 : 8, 0, 0, 0),
                IsDefault = primary
            };

            if (danger)
                button.SetResourceReference(StyleProperty, "DangerButtonStyle");
            else if (primary)
                button.SetResourceReference(StyleProperty, "PrimaryButtonStyle");

            button.Click += (_, _) =>
            {
                _result = result;
                DialogResult = true;
            };

            ButtonBar.Children.Add(button);

            if (primary)
                Loaded += (_, _) => button.Focus();
        }

        public static MessageBoxResult Show(string message)
            => Show(null, message, string.Empty);

        public static MessageBoxResult Show(string message, string title, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
            => Show(null, message, title, buttons, image);

        public static MessageBoxResult Show(Window? owner, string message, string title, MessageBoxButton buttons = MessageBoxButton.OK, MessageBoxImage image = MessageBoxImage.None)
        {
            var dialog = new MessageDialog(message, title, buttons, image);
            dialog.ShowFor(owner ?? ActiveWindow());
            return dialog._result;
        }

        static Window? ActiveWindow()
        {
            if (Application.Current == null)
                return null;

            return Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
                   ?? Application.Current.MainWindow;
        }
    }
}
