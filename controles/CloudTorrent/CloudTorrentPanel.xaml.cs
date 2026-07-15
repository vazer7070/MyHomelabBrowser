using MyHomelabBrowser.classes.CloudTorrent.Integration;
using MyHomelabBrowser.classes.CloudTorrent.Models;
using MyHomelabBrowser.classes.CloudTorrent.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MyHomelabBrowser.controles.CloudTorrent
{
    public partial class CloudTorrentPanel : UserControl
    {
        private readonly CloudTorrentModuleService _module;
        private readonly CloudTorrentBrowserController _browser;
        private CloudTorrentTabSession? _session;

        public CloudTorrentPanel(
            CloudTorrentModuleService module,
            CloudTorrentBrowserController browser)
        {
            InitializeComponent();
            _module = module ?? throw new ArgumentNullException(nameof(module));
            _browser = browser ?? throw new ArgumentNullException(nameof(browser));

            _browser.ActiveSessionChanged += Browser_ActiveSessionChanged;
            _browser.ActiveSessionUpdated += Browser_ActiveSessionUpdated;
            _browser.ModuleStateChanged += Browser_ModuleStateChanged;
            Loaded += CloudTorrentPanel_Loaded;
            Unloaded += CloudTorrentPanel_Unloaded;
        }

        public event Action? OpenSettingsRequested;
        public event Action<string>? OpenUrlRequested;

        private void CloudTorrentPanel_Loaded(object sender, RoutedEventArgs e)
        {
            _session = _browser.ActiveSession;
            Render();
        }

        private void CloudTorrentPanel_Unloaded(object sender, RoutedEventArgs e)
        {
            // Le panneau est conservé dans le Popup : les événements du contrôleur restent utiles
            // lorsqu'il est rouvert. Ils seront libérés avec la fenêtre principale.
        }

        private void Browser_ActiveSessionChanged(CloudTorrentTabSession? session)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => Browser_ActiveSessionChanged(session));
                return;
            }

            _session = session;
            Render();
        }

        private void Browser_ActiveSessionUpdated(CloudTorrentTabSession session)
        {
            if (!ReferenceEquals(_session, session))
                return;

            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(Render));
                return;
            }

            Render();
        }

        private void Browser_ModuleStateChanged()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(Render));
                return;
            }

            Render();
        }

        private void Render()
        {
            CloudTorrentModuleSnapshot snapshot = _module.Snapshot;
            HeaderStatusText.Text = snapshot.IsActive
                ? $"Connecté · {snapshot.Username}"
                : snapshot.Message;
            OpenSiteButton.IsEnabled = snapshot.IsActive && !string.IsNullOrWhiteSpace(snapshot.ServerUrl);

            if (!snapshot.IsActive)
            {
                ShowUnavailable("Module inactif", "Renseignez l’adresse CloudTorrent et une clé API valide dans les paramètres.");
                return;
            }

            if (_session == null)
            {
                ShowUnavailable("Aucun onglet web actif", "Sélectionnez une page web pour lancer l’analyse CloudTorrent.");
                return;
            }

            if (_session.IsPrivate)
            {
                ShowUnavailable("Onglet privé", "Par sécurité, l’analyse et l’envoi vers CloudTorrent sont désactivés dans les onglets privés.");
                return;
            }

            UnavailableCard.Visibility = Visibility.Collapsed;
            ActiveContent.Visibility = Visibility.Visible;

            CloudTorrentAccount? account = snapshot.Account;
            AccountText.Text = string.IsNullOrWhiteSpace(snapshot.Username)
                ? "Module actif"
                : snapshot.Username;
            ApiVersionText.Text = string.IsNullOrWhiteSpace(snapshot.ApiVersion)
                ? "API"
                : $"API {snapshot.ApiVersion}";
            ActivityText.Text = account == null
                ? "Connexion active"
                : $"{account.Counts.TotalActive} téléchargement{(account.Counts.TotalActive > 1 ? "s" : string.Empty)} actif{(account.Counts.TotalActive > 1 ? "s" : string.Empty)}";

            CloudTorrentPageAnalysis page = _session.Analysis;
            string currentUrl = _session.WebView.Source?.AbsoluteUri
                                ?? _session.WebView.CoreWebView2?.Source
                                ?? string.Empty;
            bool canAnalyzeCurrentPage = IsHttpPage(currentUrl);

            string currentTitle = _session.WebView.CoreWebView2?.DocumentTitle ?? string.Empty;
            PageTitleText.Text = !string.IsNullOrWhiteSpace(page.Title)
                ? page.Title
                : !string.IsNullOrWhiteSpace(currentTitle)
                    ? currentTitle
                    : "Page prête à être analysée";
            PageMetaText.Text = page.IsCompatible
                ? page.Summary
                : canAnalyzeCurrentPage
                    ? "Lancez l’analyse pour rechercher les liens disponibles."
                    : page.Summary;
            PageUrlText.Text = page.IsCompatible ? page.PageUrl : currentUrl;
            PageUrlText.ToolTip = PageUrlText.Text;
            DetectedItemsControl.ItemsSource = page.Items;
            NoDetectionText.Visibility = page.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            RenderSelectionSummary();
            RenderServerAnalysis(_session.ServerAnalysis);

            bool busy = _session.IsBusy;
            AnalyzePageButton.IsEnabled = !busy && canAnalyzeCurrentPage;
            RefreshDetectionButton.IsEnabled = !busy && canAnalyzeCurrentPage;
            ServerAnalyzeButton.IsEnabled = !busy && canAnalyzeCurrentPage && snapshot.HasPermission(CloudTorrentPermission.MediaAnalyze);
            QueueSelectedButton.IsEnabled = !busy && page.Items.Any(item => item.IsSelected);
            BusyProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
            FooterStatusText.Text = _session.StatusMessage;
        }

        private void ShowUnavailable(string title, string message)
        {
            ActiveContent.Visibility = Visibility.Collapsed;
            UnavailableCard.Visibility = Visibility.Visible;
            UnavailableTitle.Text = title;
            UnavailableMessage.Text = message;
        }

        private void RenderSelectionSummary()
        {
            if (_session == null)
            {
                SelectionSummaryText.Text = "0 élément";
                return;
            }

            int total = _session.Analysis.Items.Count;
            int selected = _session.Analysis.Items.Count(item => item.IsSelected);
            SelectionSummaryText.Text = $"{selected} sélectionné{(selected > 1 ? "s" : string.Empty)} sur {total}";
            QueueSelectedButton.IsEnabled = !_session.IsBusy && selected > 0;
        }

        private void RenderServerAnalysis(CloudTorrentMediaAnalysis? analysis)
        {
            if (analysis == null)
            {
                ServerAnalysisCard.Visibility = Visibility.Collapsed;
                QualityComboBox.ItemsSource = null;
                return;
            }

            ServerAnalysisCard.Visibility = Visibility.Visible;
            ServerAnalysisTitleText.Text = string.IsNullOrWhiteSpace(analysis.Title) ? "Média" : analysis.Title;
            ServerAnalysisMetaText.Text = BuildAnalysisMeta(analysis);

            CloudTorrentQualityChoice? previousChoice = QualityComboBox.SelectedItem as CloudTorrentQualityChoice;
            IReadOnlyList<CloudTorrentQualityChoice> choices = BuildQualityChoices(analysis);
            QualityComboBox.ItemsSource = choices;

            CloudTorrentQualityChoice? restoredChoice = previousChoice == null
                ? null
                : choices.FirstOrDefault(choice =>
                    string.Equals(choice.Kind, previousChoice.Kind, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(choice.Id, previousChoice.Id, StringComparison.OrdinalIgnoreCase));

            QualityComboBox.SelectedItem = restoredChoice ?? choices.FirstOrDefault();
        }

        private static IReadOnlyList<CloudTorrentQualityChoice> BuildQualityChoices(CloudTorrentMediaAnalysis analysis)
        {
            var choices = new List<CloudTorrentQualityChoice>();
            foreach (CloudTorrentMediaPreset preset in analysis.Presets)
            {
                choices.Add(new CloudTorrentQualityChoice
                {
                    Kind = "preset",
                    Id = preset.Id,
                    Label = preset.Label,
                    IsAudio = string.Equals(preset.Id, "audio", StringComparison.OrdinalIgnoreCase)
                });
            }

            foreach (CloudTorrentMediaFormat format in analysis.Formats)
            {
                var details = new List<string>();
                if (format.Height.HasValue) details.Add($"{format.Height.Value}p");
                if (!string.IsNullOrWhiteSpace(format.Extension)) details.Add(format.Extension.ToUpperInvariant());
                if (!string.IsNullOrWhiteSpace(format.VideoCodec)) details.Add(format.VideoCodec);
                if (!string.IsNullOrWhiteSpace(format.AudioCodec)) details.Add(format.AudioCodec);
                string label = details.Count > 0 ? string.Join(" · ", details) : format.Label;
                if (format.Filesize.HasValue && format.Filesize.Value > 0)
                    label += $" — {FormatBytes(format.Filesize.Value)}";

                choices.Add(new CloudTorrentQualityChoice
                {
                    Kind = "format",
                    Id = format.Id,
                    Label = label,
                    IsAudio = string.IsNullOrWhiteSpace(format.VideoCodec) || format.VideoCodec == "none"
                });
            }

            if (choices.Count == 0)
            {
                choices.Add(new CloudTorrentQualityChoice
                {
                    Kind = "preset",
                    Id = "best",
                    Label = "Meilleure qualité"
                });
            }

            return choices;
        }

        private static string BuildAnalysisMeta(CloudTorrentMediaAnalysis analysis)
        {
            var parts = new List<string>();
            if (analysis.Duration.HasValue && analysis.Duration.Value > 0)
                parts.Add(FormatDuration(analysis.Duration.Value));
            if (!string.IsNullOrWhiteSpace(analysis.Extractor))
                parts.Add(analysis.Extractor);
            if (analysis.IsLive)
                parts.Add("Direct");
            if (analysis.Formats.Count > 0)
                parts.Add($"{analysis.Formats.Count} format{(analysis.Formats.Count > 1 ? "s" : string.Empty)}");
            return parts.Count > 0 ? string.Join(" · ", parts) : "Analyse disponible";
        }

        private async void AnalyzePageButton_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null)
                return;

            try
            {
                await _session.AnalyzeNowAsync();
            }
            catch
            {
            }
        }

        private async void ServerAnalyzeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null)
                return;

            try
            {
                await _session.AnalyzeOnServerAsync();
            }
            catch
            {
            }
        }

        private async void QueueSelectedButton_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null)
                return;

            try
            {
                await _session.QueueSelectedAsync(QualityComboBox.SelectedItem as CloudTorrentQualityChoice);
            }
            catch
            {
            }
        }

        private void SelectAllButton_Click(object sender, RoutedEventArgs e)
        {
            if (_session == null)
                return;

            bool select = _session.Analysis.Items.Any(item => !item.IsSelected);
            foreach (CloudTorrentDetectedItem item in _session.Analysis.Items)
                item.IsSelected = select;

            RenderSelectionSummary();
        }

        private void DetectedItemCheckBox_Click(object sender, RoutedEventArgs e)
            => RenderSelectionSummary();

        private void SettingsButton_Click(object sender, RoutedEventArgs e)
            => OpenSettingsRequested?.Invoke();

        private void OpenSiteButton_Click(object sender, RoutedEventArgs e)
        {
            string url = _module.Snapshot.ServerUrl;
            if (!string.IsNullOrWhiteSpace(url))
                OpenUrlRequested?.Invoke(url);
        }


        private static bool IsHttpPage(string value)
            => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        private static string FormatDuration(double seconds)
        {
            TimeSpan duration = TimeSpan.FromSeconds(seconds);
            return duration.TotalHours >= 1
                ? duration.ToString(@"h\:mm\:ss")
                : duration.ToString(@"m\:ss");
        }

        private static string FormatBytes(long value)
        {
            double bytes = Math.Max(0, value);
            string[] units = { "o", "Ko", "Mo", "Go", "To" };
            int index = 0;
            while (bytes >= 1024 && index < units.Length - 1)
            {
                bytes /= 1024;
                index++;
            }
            return index == 0 ? $"{bytes:0} {units[index]}" : $"{bytes:0.0} {units[index]}";
        }
    }
}
