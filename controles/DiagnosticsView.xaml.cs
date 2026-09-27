using MyHomelabBrowser.classes;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MyHomelabBrowser.controles
{
    public sealed record DiagnosticsRow(string Label, string Value);

    public sealed record DiagnosticsSection(string Title, IReadOnlyList<DiagnosticsRow> Rows);

    public sealed record DiagnosticsReport(DateTime GeneratedAt, IReadOnlyList<DiagnosticsSection> Sections, string Log, string DataFolder);

    /// <summary>
    /// Page de diagnostic : version, moteur web, mémoire, onglets, modules et journal.
    /// Le rapport est construit par la fenêtre principale, qui connaît l'état du navigateur.
    /// </summary>
    public partial class DiagnosticsView : UserControl
    {
        private readonly Func<Task<DiagnosticsReport>> _buildReport;
        private DiagnosticsReport? _report;

        public DiagnosticsView(Func<Task<DiagnosticsReport>> buildReport)
        {
            InitializeComponent();
            _buildReport = buildReport;
            Loaded += async (_, _) => await RefreshAsync();
        }

        private async Task RefreshAsync()
        {
            RefreshButton.IsEnabled = false;
            GeneratedText.Text = "Collecte des informations…";

            try
            {
                _report = await _buildReport();
                SectionsList.ItemsSource = _report.Sections;
                LogBox.Text = string.IsNullOrWhiteSpace(_report.Log) ? "(journal vide)" : _report.Log;
                LogBox.ScrollToEnd();
                GeneratedText.Text = $"Relevé du {_report.GeneratedAt:dd/MM/yyyy à HH:mm:ss}";
            }
            catch (Exception ex)
            {
                GeneratedText.Text = "Collecte impossible : " + ex.Message;
            }
            finally
            {
                RefreshButton.IsEnabled = true;
            }
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            if (_report == null)
                return;

            var builder = new StringBuilder();
            builder.AppendLine($"Diagnostic PommeBrowser — {_report.GeneratedAt:yyyy-MM-dd HH:mm:ss}");
            foreach (DiagnosticsSection section in _report.Sections)
            {
                builder.AppendLine();
                builder.AppendLine("## " + section.Title);
                foreach (DiagnosticsRow row in section.Rows)
                    builder.AppendLine($"- {row.Label} : {row.Value}");
            }

            try
            {
                Clipboard.SetText(builder.ToString());
                CopyButton.Content = "Copié";
            }
            catch
            {
                CopyButton.Content = "Presse-papiers indisponible";
            }
        }

        private void OpenDataFolder_Click(object sender, RoutedEventArgs e)
        {
            if (_report == null)
                return;

            try
            {
                Process.Start(new ProcessStartInfo { FileName = _report.DataFolder, UseShellExecute = true });
            }
            catch
            {
            }
        }
    }
}
