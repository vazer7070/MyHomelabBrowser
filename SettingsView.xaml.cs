using Microsoft.Win32;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.classes.AdBlock.Services;
using MyHomelabBrowser.controles;
using MyHomelabBrowser.controles.settings;
using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser
{
    public partial class SettingsView : UserControl
    {
        readonly SettingsService _service;
        BrowserSettings _original;
        BrowserSettings _working;
        private int _requestedSectionIndex;
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
            SelectSection(_requestedSectionIndex);
        }

        public void SelectSection(string sectionName)
        {
            int section = sectionName switch
            {
                "Téléchargements" => 1,
                "Historique" => 2,
                "Mises à jour" => 3,
                "Bloqueur de publicités" or "AdBlock" => 4,
                "Avancé" or "⚠ Avancé" => 5,
                _ => 0
            };

            SelectSection(section);
        }

        private void SelectSection(int section)
        {
            _requestedSectionIndex = Math.Clamp(section, 0, 5);
            if (!IsLoaded)
                return;

            // Le séparateur occupe une ligne de la liste avant la section Avancé.
            int navIndex = _requestedSectionIndex == 5 ? 6 : _requestedSectionIndex;
            if (NavList.SelectedIndex != navIndex)
                NavList.SelectedIndex = navIndex;
            else
                LoadSection(_requestedSectionIndex);
        }


        private void SettingsSearchBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (SettingsSearchPlaceholder != null)
                SettingsSearchPlaceholder.Visibility = string.IsNullOrEmpty(SettingsSearchBox.Text)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        private void SettingsSearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
        {
            if (e.Key != System.Windows.Input.Key.Enter)
                return;

            string query = (SettingsSearchBox.Text ?? string.Empty).Trim().ToLowerInvariant();
            if (query.Length == 0)
                return;

            int section = query switch
            {
                var value when ContainsAny(value, "pub", "publicité", "adblock", "easylist", "traqueur", "tracker", "protection") => 4,
                var value when ContainsAny(value, "télécharg", "dossier", "fichier") => 1,
                var value when ContainsAny(value, "historique", "navigation", "données") => 2,
                var value when ContainsAny(value, "mise à jour", "version", "update", "changelog") => 3,
                var value when ContainsAny(value, "flash", "basilisk", "avancé", "debug", "legacy") => 5,
                _ => 0
            };

            SelectSection(section);
            e.Handled = true;
        }

        private static bool ContainsAny(string value, params string[] terms)
            => terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));

        void NavList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (NavList.SelectedItem is not ListBoxItem item)
                return;

            var text = item.Tag?.ToString() ?? item.Content?.ToString() ?? "";

            int section = text switch
            {
                "Général" => 0,
                "Téléchargements" => 1,
                "Historique" => 2,
                "Mises à jour" => 3,
                "Bloqueur de publicités" => 4,
                "⚠ Avancé" => 5,
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
                4 => new SettingsAdBlockView(AdBlockModuleHost.Current),
                5 => new SettingsAdvancedView(),
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

            _basiliskStatusText = fe.FindName("BasiliskStatusText") as TextBlock;
            UpdateBasiliskStatus();

            var addFlashBtn = fe.FindName("AddFlashRuleBtn") as Button;
            if (addFlashBtn != null) addFlashBtn.Click += AddFlashRule_Click;

            var removeFlashBtn = fe.FindName("RemoveFlashRuleBtn") as Button;
            if (removeFlashBtn != null) removeFlashBtn.Click += RemoveFlashRule_Click;

            var clearFlashCompatibilityBtn = fe.FindName("ClearFlashCompatibilityBtn") as Button;
            if (clearFlashCompatibilityBtn != null)
                clearFlashCompatibilityBtn.Click += ClearFlashCompatibility_Click;

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
                SetUpdateStatus(Tr("Prêt à vérifier les mises à jour."));
                CheckUpdatesRequested?.Invoke();
            }

        }
        private void OpenReportIssue_Click(object sender, RoutedEventArgs e)
        {
            ReportModule module = ReportModule.Browser;

            if (NavList.SelectedItem is ListBoxItem selectedItem &&
                selectedItem.Tag is string selectedSection)
            {
                if (selectedSection.Equals("Bloqueur de publicités", StringComparison.OrdinalIgnoreCase))
                    module = ReportModule.AdBlock;
            }

            var options = new ReportIssueOptions
            {
                IncludeLogs = _working.ReportIncludeLogs,
                IncludePcInfo = _working.ReportIncludePcInfo,
                IncludeMode = _working.ReportIncludeMode,
                Module = module
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
                Title = Tr("Choisir le dossier de téléchargement"),
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

        void ClearFlashCompatibility_Click(object sender, RoutedEventArgs e)
        {
            MessageBoxResult result = MessageDialog.Show(
                Tr("Effacer les choix Ruffle/Legacy appris pour le profil actuel ?"),
                Tr("Compatibilité Flash"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (result != MessageBoxResult.Yes)
                return;

            FlashCompatibilityMemory.ClearForCurrentProfile();
            MessageDialog.Show(
                Tr("La compatibilité apprise a été réinitialisée."),
                Tr("Compatibilité Flash"),
                MessageBoxButton.OK,
                MessageBoxImage.Information);
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
                Title = Tr("Choisir Basilisk"),
                Filter = Tr("Basilisk (Basilisk-Portable.exe, basilisk.exe)|Basilisk-Portable.exe;basilisk.exe|Programmes (*.exe)|*.exe"),
                InitialDirectory = initialDir
            };

            if (dlg.ShowDialog() != true)
                return;

            if (!BasiliskExecutable.IsLaunchable(dlg.FileName))
                return;

            _working.BasiliskPath = dlg.FileName;

            DataContext = null;
            DataContext = _working;
            UpdateBasiliskStatus();
        }

        TextBlock? _basiliskStatusText;

        /// <summary>Programme détecté au chemin choisi, pour repérer une erreur de fichier.</summary>
        void UpdateBasiliskStatus()
        {
            if (_basiliskStatusText == null)
                return;

            string? path = _working.BasiliskPath;
            bool warning = false;
            string text;

            if (string.IsNullOrWhiteSpace(path))
            {
                text = Tr("Aucun Basilisk configuré : les contenus Flash passent uniquement par Ruffle.");
            }
            else if (!BasiliskExecutable.IsLaunchable(path))
            {
                text = Tr("Programme introuvable à cet emplacement.");
                warning = true;
            }
            else
            {
                var (product, version) = BasiliskExecutable.Describe(path);
                text = product == null
                    ? Tr("Programme trouvé (version inconnue).")
                    : Tr("{0} {1} détecté.", product, version ?? string.Empty).Replace("  ", " ");

                if (product != null && !BasiliskExecutable.LooksLikeUxpBrowser(product))
                {
                    text += " " + Tr("Ce programme ne semble pas être Basilisk : le mode Legacy risque de ne pas fonctionner.");
                    warning = true;
                }
            }

            _basiliskStatusText.Text = text;
            _basiliskStatusText.SetResourceReference(TextBlock.ForegroundProperty, warning ? "WarningBrush" : "TextSecondaryBrush");
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
                MessageDialog.Show(
                    Tr("Domaine invalide.\nExemple : jeu.exemple.com"),
                    Tr("Erreur"),
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
