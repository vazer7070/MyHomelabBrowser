using Microsoft.Win32;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;

namespace MyHomelabBrowser
{
    public partial class SettingsView : UserControl
    {
        readonly SettingsService _service;
        BrowserSettings _original;
        BrowserSettings _working;
        public bool FlashDebugEnabled { get; set; } = false;

        public event Action? OpenHistoryRequested;

        public SettingsView(SettingsService service)
        {
            InitializeComponent();

            _service = service;

            _original = _service.Settings.Clone();
            _working = _service.Settings.Clone();

            DataContext = _working;

            // 🔽 synchro initiale du DownloadManager
            if (!string.IsNullOrWhiteSpace(_working.DownloadFolder))
                DownloadManager.Instance.DownloadFolder = _working.DownloadFolder;

            // 🔥 charger la whitelist Flash au démarrage
            RefreshFlashRules();
        }

        private void OpenHistory_Click(object sender, RoutedEventArgs e)
        {
            OpenHistoryRequested?.Invoke();
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            _working = _original.Clone();
            DataContext = _working;

            // 🔽 rollback dossier downloads
            if (!string.IsNullOrWhiteSpace(_working.DownloadFolder))
                DownloadManager.Instance.DownloadFolder = _working.DownloadFolder;

            // 🔁 rollback visuel whitelist
            RefreshFlashRules();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _service.Apply(_working.Clone());

            _original = _service.Settings.Clone();
            _working = _service.Settings.Clone();
            DataContext = _working;

            // 🔽 applique définitivement le dossier de téléchargement
            if (!string.IsNullOrWhiteSpace(_working.DownloadFolder))
                DownloadManager.Instance.DownloadFolder = _working.DownloadFolder;

            // 🔁 refresh whitelist après save
            RefreshFlashRules();

            ShowSaveFeedback();
        }

        // ===============================
        // DOWNLOAD FOLDER PICKER
        // ===============================
        private void PickDownloadFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Choisir le dossier de téléchargement",
                InitialDirectory = string.IsNullOrWhiteSpace(_working.DownloadFolder)
                    ? Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                        "Downloads")
                    : _working.DownloadFolder
            };

            if (dlg.ShowDialog() != true)
                return;

            var path = dlg.FolderName;

            if (string.IsNullOrWhiteSpace(path))
                return;

            _working.DownloadFolder = path;
            DownloadManager.Instance.DownloadFolder = path;
        }

        // ===============================
        // FLASH WHITELIST
        // ===============================
        void RefreshFlashRules()
        {
            FlashLegacyList.ItemsSource =
                FlashDomainRules.GetAll()
                    .Where(kv => kv.Value == FlashRuleMode.Legacy)
                    .Select(kv => kv.Key)
                    .OrderBy(x => x)
                    .ToList();
        }

        private void RemoveFlashRule_Click(object sender, RoutedEventArgs e)
        {
            if (FlashLegacyList.SelectedItem is not string host)
                return;

            FlashDomainRules.RemoveRule(new Uri("https://" + host));

            RefreshFlashRules();
        }




        // ===============================
        // BASILISK PICKER
        // ===============================
        private void PickBasiliskPath_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = "Choisir Basilisk-Portable.exe",
                Filter = "Basilisk (Basilisk-Portable.exe)|Basilisk-Portable.exe|Tous les fichiers|*.*",
                InitialDirectory = string.IsNullOrWhiteSpace(_working.BasiliskPath)
                    ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                    : Path.GetDirectoryName(_working.BasiliskPath)
            };

            if (dlg.ShowDialog() != true)
                return;

            if (!File.Exists(dlg.FileName))
                return;

            _working.BasiliskPath = dlg.FileName;

            DataContext = null;
            DataContext = _working;
        }


        // ===============================
        // UI FEEDBACK
        // ===============================
        void ShowSaveFeedback()
        {
            SaveFeedback.Opacity = 1;

            var anim = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromSeconds(2),
                BeginTime = TimeSpan.FromSeconds(1)
            };

            SaveFeedback.BeginAnimation(OpacityProperty, anim);
        }

        private void AddFlashRule_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new AddLegacySiteDialog
            {
                Owner = Window.GetWindow(this)
            };

            if (dlg.ShowDialog() != true)
                return;

            var raw = dlg.ResultDomain ?? "";
            var host = NormalizeHost(raw);

            if (!IsValidHost(host))
            {
                MessageBox.Show(
                    "Domaine invalide.\nExemple : ministryofwar.com",
                    "Erreur",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning
                );
                return;
            }

            FlashDomainRules.SetRule(new Uri("https://" + host), FlashRuleMode.Legacy);
            RefreshFlashRules();
        }

        private FlashConsoleWindow? _flashConsole;

        private void OpenFlashConsole_Click(object sender, RoutedEventArgs e)
        {
            if (_flashConsole == null || !_flashConsole.IsVisible)
            {
                _flashConsole = new FlashConsoleWindow
                {
                    Owner = Window.GetWindow(this)
                };
                _flashConsole.Show();
                return;
            }

            _flashConsole.Activate();
        }


        private static string NormalizeHost(string input)
        {
            string s = input.Trim().ToLowerInvariant();

            s = s.Replace("https://", "").Replace("http://", "");
            s = s.Split('/')[0];

            if (s.StartsWith("*."))
                s = s.Substring(2);

            return s;
        }

        private static bool IsValidHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            if (host.Contains(" ")) return false;
            if (!host.Contains(".")) return false;
            if (host.StartsWith(".")) return false;
            if (host.EndsWith(".")) return false;
            return true;
        }

        private void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {

        }
    }
}
