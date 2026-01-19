using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MyHomelabBrowser.controles
{
    public partial class LegacyFlashView : UserControl
    {
        public event Action? RetryRequested;
        public event Action? SettingsRequested;

        readonly DispatcherTimer _spinTimer;
        int _spinState;

        public LegacyFlashView()
        {
            InitializeComponent();

            RetryBtn.Click += (_, _) => RetryRequested?.Invoke();
            SettingsBtn.Click += (_, _) => SettingsRequested?.Invoke();

            _spinTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _spinTimer.Tick += (_, _) =>
            {
                _spinState = (_spinState + 1) % 4;
                Spinner.Text = _spinState switch
                {
                    0 => "⏳",
                    1 => "⌛",
                    2 => "⏳",
                    _ => "⌛"
                };
            };
        }

        public void SetUrl(string url)
        {
            UrlText.Text = url;
        }

        public void SetLaunching()
        {
            ErrorText.Visibility = Visibility.Collapsed;
            StatusText.Text = "Lancement de Basilisk…";
            Spinner.Visibility = Visibility.Visible;
            _spinTimer.Start();
        }

        public void SetLaunched(int pid)
        {
            _spinTimer.Stop();
            Spinner.Visibility = Visibility.Collapsed;
            StatusText.Text = $"Basilisk lancé ✅ (PID {pid})";
        }

        public void SetError(string message)
        {
            _spinTimer.Stop();
            Spinner.Visibility = Visibility.Collapsed;
            StatusText.Text = "Échec du lancement ❌";
            ErrorText.Text = message;
            ErrorText.Visibility = Visibility.Visible;
        }
    }
}
