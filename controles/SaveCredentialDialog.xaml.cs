using System;
using System.Threading.Tasks;
using System.Windows;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles
{
    public enum SaveCredentialDecision
    {
        NotNow,
        Save,
        NeverSave
    }

    public sealed class SaveCredentialDialogResult
    {
        public SaveCredentialDecision Decision { get; init; }
        public bool AlwaysSave { get; init; }
    }

    public partial class SaveCredentialDialog : Window
    {
        private readonly TaskCompletionSource<SaveCredentialDialogResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private bool _shown;
        private bool _completed;

        public string Host { get; }
        public string Username { get; }
        public bool AlwaysSave => AlwaysSaveCheckBox.IsChecked == true;
        public SaveCredentialDecision Decision { get; private set; } = SaveCredentialDecision.NotNow;

        public SaveCredentialDialog(string host, string username)
        {
            InitializeComponent();

            Host = host;
            Username = string.IsNullOrWhiteSpace(username)
                ? Tr("(aucun utilisateur)")
                : username;

            DataContext = this;
        }

        /// <summary>
        /// Affiche la fenêtre sans bloquer la fenêtre principale.
        /// Une seule instance de ce dialogue doit être ouverte à la fois par MainWindow.
        /// </summary>
        public Task<SaveCredentialDialogResult> ShowAsync(Window owner)
        {
            ArgumentNullException.ThrowIfNull(owner);

            if (_shown)
                return _completion.Task;

            _shown = true;
            Owner = owner;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;

            Show();
            Activate();
            Focus();

            return _completion.Task;
        }

        public void CancelAndClose()
        {
            Complete(SaveCredentialDecision.NotNow);
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            Complete(SaveCredentialDecision.Save);
        }

        private void NeverSave_Click(object sender, RoutedEventArgs e)
        {
            Complete(SaveCredentialDecision.NeverSave);
        }

        private void NotNow_Click(object sender, RoutedEventArgs e)
        {
            Complete(SaveCredentialDecision.NotNow);
        }

        private void Complete(SaveCredentialDecision decision)
        {
            if (_completed)
                return;

            _completed = true;
            Decision = decision;

            _completion.TrySetResult(new SaveCredentialDialogResult
            {
                Decision = decision,
                AlwaysSave = decision == SaveCredentialDecision.Save && AlwaysSave
            });

            Close();
        }

        protected override void OnClosed(EventArgs e)
        {
            if (!_completed)
            {
                _completed = true;
                Decision = SaveCredentialDecision.NotNow;
                _completion.TrySetResult(new SaveCredentialDialogResult
                {
                    Decision = SaveCredentialDecision.NotNow,
                    AlwaysSave = false
                });
            }

            base.OnClosed(e);
        }
    }
}
