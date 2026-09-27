using System.Windows;
using System.Windows.Threading;

namespace MyHomelabBrowser.controles
{
    /// <summary>
    /// Saisie d'un texte court (nom d'un espace de travail…).
    /// </summary>
    public partial class TextPromptDialog : DialogWindow
    {
        public TextPromptDialog(string title, string prompt, string confirmLabel, string initialValue = "")
        {
            InitializeComponent();
            Title = title;
            PromptText.Text = prompt;
            OkButton.Content = confirmLabel;
            InputBox.Text = initialValue;
            OkButton.IsEnabled = initialValue.Trim().Length > 0;

            Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
            {
                InputBox.Focus();
                InputBox.SelectAll();
            }, DispatcherPriority.Input);
        }

        public string Value => InputBox.Text.Trim();

        private void InputBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
            => OkButton.IsEnabled = Value.Length > 0;

        private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
