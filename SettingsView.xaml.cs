using Microsoft.Win32;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.controles;
using MyHomelabBrowser.controles.settings;
using System;
using System.IO;
using System.Linq;
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
        public event Action? CheckUpdatesRequested;
        public event Action? InstallUpdateRequested;
        public event Action<ReportIssueOptions>? OpenReportIssueRequested;
        public event Action? ChangelogRequested;

        // === References (mêmes noms que ton code utilisait avant)
        Button? CheckUpdatesBtn;
        Button? InstallUpdateBtn;
        Button? ChangelogBtn;

        TextBlock? CurrentVersionLabel;
        TextBlock? LatestVersionLabel;
        TextBlock? UpdateStatusLabel;

        ProgressBar? UpdateProgress;
        TextBlock? UpdateProgressLabel;

        ListBox? FlashLegacyList;

        public void SetUpdateStatus(string text)
        {
            if (UpdateStatusLabel != null)
                UpdateStatusLabel.Text = text;
        }

        public string UpdateStatusText
        {
            get => UpdateStatusLabel?.Text ?? "";
            set { if (UpdateStatusLabel != null) UpdateStatusLabel.Text = value; }
        }

        public bool IsUpdateButtonEnabled
        {
            get => CheckUpdatesBtn?.IsEnabled ?? false;
            set { if (CheckUpdatesBtn != null) CheckUpdatesBtn.IsEnabled = value; }
        }

        public void ShowUpdateProgress(bool show)
        {
            if (UpdateProgress != null)
                UpdateProgress.Visibility = show ? Visibility.Visible : Visibility.Collapsed;

            if (UpdateProgressLabel != null)
                UpdateProgressLabel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        }

        public void SetUpdateProgress(double percent)
        {
            if (percent < 0) percent = 0;
            if (percent > 100) percent = 100;

            if (UpdateProgress != null)
                UpdateProgress.Value = percent;

            if (UpdateProgressLabel != null)
                UpdateProgressLabel.Text = $"{percent:0}%";
        }

        public void SetUpdateBusy(bool busy)
        {
            if (CheckUpdatesBtn != null)
                CheckUpdatesBtn.IsEnabled = !busy;

            if (busy && InstallUpdateBtn != null)
                InstallUpdateBtn.IsEnabled = false;
        }

        void InstallUpdateBtn_Click(object sender, RoutedEventArgs e)
        {
            InstallUpdateRequested?.Invoke();
        }

        public void SetInstallAvailable(bool available)
        {
            if (InstallUpdateBtn != null)
                InstallUpdateBtn.IsEnabled = available;
        }

        public SettingsView(SettingsService service)
        {
            InitializeComponent();
            _service = service;

            _original = _service.Settings.Clone();
            _working = _service.Settings.Clone();

            DataContext = _working;

            Loaded += SettingsView_Loaded;

            // ✅ 1er lancement : Basilisk par défaut depuis le dossier du navigateur
            if (string.IsNullOrWhiteSpace(_working.BasiliskPath))
            {
                try
                {
                    string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                    string defaultBasilisk = Path.Combine(exeDir, "Basilisk", "Basilisk-Portable.exe");

                    if (File.Exists(defaultBasilisk))
                    {
                        _working.BasiliskPath = defaultBasilisk;
                        _service.Apply(_working.Clone());

                        _original = _service.Settings.Clone();
                        _working = _service.Settings.Clone();
                        DataContext = _working;
                    }
                }
                catch { }
            }

            if (!string.IsNullOrWhiteSpace(_working.DownloadFolder))
                DownloadManager.Instance.DownloadFolder = _working.DownloadFolder;

            RefreshFlashRules();
        }
        private void SettingsView_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= SettingsView_Loaded;
            LoadSection(0);
        }

        void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NavList.SelectedItem is not ListBoxItem item)
                return;

            var text = item.Content?.ToString() ?? "";

            int section = text switch
            {
                "Général" => 0,
                "Téléchargements" => 1,
                "Historique" => 2,
                "Mises à jour" => 3,
                "⚠ Avancé" => 4,
                _ => 0
            };

            LoadSection(section);
        }


        void LoadSection(int index)
        {
            UserControl? view = index switch
            {
                0 => new SettingsGeneralView(),
                1 => new SettingsDownloadsView(),
                2 => new SettingsHistoryView(),
                3 => new SettingsUpdatesView(),
                4 => new SettingsAdvancedView(),
                _ => null
            };

            ContentHost.Content = view;

            // Reset refs
            CheckUpdatesBtn = null;
            InstallUpdateBtn = null;
            ChangelogBtn = null;
            CurrentVersionLabel = null;
            LatestVersionLabel = null;
            UpdateStatusLabel = null;
            UpdateProgress = null;
            UpdateProgressLabel = null;
            FlashLegacyList = null;

            if (view is not FrameworkElement fe)
                return;

            // === Updates view wiring
            CheckUpdatesBtn = fe.FindName("CheckUpdatesBtn") as Button;
            InstallUpdateBtn = fe.FindName("InstallUpdateBtn") as Button;
            ChangelogBtn = fe.FindName("ChangelogBtn") as Button;

            CurrentVersionLabel = fe.FindName("CurrentVersionLabel") as TextBlock;
            LatestVersionLabel = fe.FindName("LatestVersionLabel") as TextBlock;
            UpdateStatusLabel = fe.FindName("UpdateStatusLabel") as TextBlock;

            UpdateProgress = fe.FindName("UpdateProgress") as ProgressBar;
            UpdateProgressLabel = fe.FindName("UpdateProgressLabel") as TextBlock;

            if (CheckUpdatesBtn != null) CheckUpdatesBtn.Click += CheckUpdatesBtn_Click;
            if (InstallUpdateBtn != null) InstallUpdateBtn.Click += InstallUpdateBtn_Click;
            if (ChangelogBtn != null) ChangelogBtn.Click += ChangelogBtn_Click;

            // === History view wiring
            var openHistoryBtn = fe.FindName("OpenHistoryBtn") as Button;
            if (openHistoryBtn != null) openHistoryBtn.Click += OpenHistory_Click;

            // === Downloads view wiring
            var pickDownloadBtn = fe.FindName("PickDownloadFolderBtn") as Button;
            if (pickDownloadBtn != null) pickDownloadBtn.Click += PickDownloadFolder_Click;

            // === General view wiring
            var newTabBox = fe.FindName("NewTabPageBox") as TextBox;
            if (newTabBox != null) newTabBox.TextChanged += TextBox_TextChanged;

            // === Advanced/Flash wiring
            FlashLegacyList = fe.FindName("FlashLegacyList") as ListBox;

            var openFlashConsoleBtn = fe.FindName("OpenFlashConsoleBtn") as Button;
            if (openFlashConsoleBtn != null) openFlashConsoleBtn.Click += OpenFlashConsole_Click;

            var pickBasiliskBtn = fe.FindName("PickBasiliskPathBtn") as Button;
            if (pickBasiliskBtn != null) pickBasiliskBtn.Click += PickBasiliskPath_Click;

            var addFlashBtn = fe.FindName("AddFlashRuleBtn") as Button;
            if (addFlashBtn != null) addFlashBtn.Click += AddFlashRule_Click;

            var removeFlashBtn = fe.FindName("RemoveFlashRuleBtn") as Button;
            if (removeFlashBtn != null) removeFlashBtn.Click += RemoveFlashRule_Click;

            if (FlashLegacyList != null)
                RefreshFlashRules();

            // === Auto-fill versions when Updates view is loaded
            if (index == 3)
            {
                SetCurrentVersion(
     System.Reflection.Assembly
         .GetExecutingAssembly()
         .GetName()
         .Version?
         .ToString() ?? "?"
 );


                // Statut initial
                SetUpdateStatus("Prêt à vérifier les mises à jour.");
                CheckUpdatesRequested?.Invoke();
            }

        }
        private void OpenReportIssue_Click(object sender, RoutedEventArgs e)
        {
            var options = new ReportIssueOptions
            {
                IncludeLogs = _working.ReportIncludeLogs,
                IncludePcInfo = _working.ReportIncludePcInfo,
                IncludeMode = _working.ReportIncludeMode
            };

            OpenReportIssueRequested?.Invoke(options);
        }

        void CheckUpdatesBtn_Click(object sender, RoutedEventArgs e)
        {
            CheckUpdatesRequested?.Invoke();
        }

        void ChangelogBtn_Click(object sender, RoutedEventArgs e)
        {
            ChangelogRequested?.Invoke();
        }

        void OpenHistory_Click(object sender, RoutedEventArgs e)
        {
            OpenHistoryRequested?.Invoke();
        }

        void RefreshFlashRules()
        {
            if (FlashLegacyList == null)
                return;

            FlashLegacyList.ItemsSource =
                FlashDomainRules.GetAll()
                    .Where(kv => kv.Value == FlashRuleMode.Legacy)
                    .Select(kv => kv.Key)
                    .OrderBy(x => x)
                    .ToList();
        }

        public void SetCurrentVersion(string version)
        {
            if (CurrentVersionLabel != null)
                CurrentVersionLabel.Text = version;
        }

        public void SetLatestVersion(string version)
        {
            if (LatestVersionLabel != null)
                LatestVersionLabel.Text = version;
        }

        public void SetChangelogAvailable(bool available)
        {
            if (ChangelogBtn != null)
                ChangelogBtn.IsEnabled = true;
        }

        void Save_Click(object sender, RoutedEventArgs e)
        {
            _service.Apply(_working.Clone());

            _original = _service.Settings.Clone();
            _working = _service.Settings.Clone();
            DataContext = _working;

            if (!string.IsNullOrWhiteSpace(_working.DownloadFolder))
                DownloadManager.Instance.DownloadFolder = _working.DownloadFolder;

            RefreshFlashRules();
            ShowSaveFeedback();
        }

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

        void PickDownloadFolder_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFolderDialog
            {
                Title = "Choisir le dossier de téléchargement",
                InitialDirectory = string.IsNullOrWhiteSpace(_working.DownloadFolder)
                    ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")
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

        void RemoveFlashRule_Click(object sender, RoutedEventArgs e)
        {
            if (FlashLegacyList?.SelectedItem is not string host)
                return;

            FlashDomainRules.RemoveRule(new Uri("https://" + host));
            RefreshFlashRules();
        }

        void PickBasiliskPath_Click(object sender, RoutedEventArgs e)
        {
            string exeDir = AppDomain.CurrentDomain.BaseDirectory;
            string defaultBasiliskDir = Path.Combine(exeDir, "Basilisk");

            string initialDir =
                !string.IsNullOrWhiteSpace(_working.BasiliskPath)
                    ? Path.GetDirectoryName(_working.BasiliskPath) ?? exeDir
                    : (Directory.Exists(defaultBasiliskDir) ? defaultBasiliskDir : exeDir);

            var dlg = new OpenFileDialog
            {
                Title = "Choisir Basilisk-Portable.exe",
                Filter = "Basilisk (Basilisk-Portable.exe)|Basilisk-Portable.exe|Tous les fichiers|*.*",
                InitialDirectory = initialDir
            };

            if (dlg.ShowDialog() != true)
                return;

            if (!File.Exists(dlg.FileName))
                return;

            _working.BasiliskPath = dlg.FileName;

            DataContext = null;
            DataContext = _working;
        }

        void AddFlashRule_Click(object sender, RoutedEventArgs e)
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

        FlashConsoleWindow? _flashConsole;

        void OpenFlashConsole_Click(object sender, RoutedEventArgs e)
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

        static string NormalizeHost(string input)
        {
            string s = input.Trim().ToLowerInvariant();
            s = s.Replace("https://", "").Replace("http://", "");
            s = s.Split('/')[0];

            if (s.StartsWith("*."))
                s = s.Substring(2);

            return s;
        }

        static bool IsValidHost(string host)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            if (host.Contains(" ")) return false;
            if (!host.Contains(".")) return false;
            if (host.StartsWith(".")) return false;
            if (host.EndsWith(".")) return false;
            return true;
        }

        void TextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
        }
    }
}
