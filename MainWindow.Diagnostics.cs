using Microsoft.Web.WebView2.Core;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Homelab;
using MyHomelabBrowser.classes.Profiles;
using MyHomelabBrowser.controles;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // ---------------------------
        // Page de diagnostic
        // ---------------------------
        void OpenDiagnostics()
        {
            // Une seule page de diagnostic : on réutilise l'onglet existant.
            foreach (TabItem tab in Tabs.Items.OfType<TabItem>())
            {
                if (tab.Tag is ViewTabContent { View: DiagnosticsView })
                {
                    Tabs.SelectedItem = tab;
                    return;
                }
            }

            OpenViewTab(new DiagnosticsView(BuildDiagnosticsReportAsync), "Diagnostic");
        }

        async Task<DiagnosticsReport> BuildDiagnosticsReportAsync()
        {
            var sections = new List<DiagnosticsSection>();
            Process process = Process.GetCurrentProcess();
            process.Refresh();

            sections.Add(new DiagnosticsSection("Application", new List<DiagnosticsRow>
            {
                new("Version", AppVersion.Current),
                new("Installation", _updates?.IsInstalled == true ? "installée (mises à jour automatiques)" : "portable / développement"),
                new(".NET", RuntimeInformation.FrameworkDescription),
                new("Système", $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})"),
                new("Démarrée depuis", FormatDuration(DateTime.Now - process.StartTime)),
                new("Mémoire (processus)", $"{FormatBytes(process.WorkingSet64)} utilisés, {FormatBytes(process.PrivateMemorySize64)} privés"),
                new("Mémoire gérée", FormatBytes(GC.GetTotalMemory(false)))
            }));

            sections.Add(await BuildWebEngineSectionAsync());

            var webTabs = Tabs.Items.OfType<TabItem>().Select(t => t.Tag).OfType<WebTabContent>().ToList();
            sections.Add(new DiagnosticsSection("Onglets", new List<DiagnosticsRow>
            {
                new("Ouverts", Tabs.Items.Count.ToString()),
                new("Pages web", webTabs.Count(t => !t.IsCustomView && !t.IsLegacyExternal).ToString()),
                new("En veille", webTabs.Count(t => t.IsSuspended).ToString()),
                new("Privés", webTabs.Count(t => t.IsPrivate).ToString()),
                new("Épinglés", webTabs.Count(t => t.IsPinned).ToString()),
                new("Flash Legacy", webTabs.Count(t => t.IsLegacyExternal).ToString()),
                new("Vue côte à côte", IsSplitViewActive ? "active" : "non"),
                new("Mise en veille auto", _settings.Settings.EnableSuspension ? $"après {_settings.Settings.SuspendDelayMinutes} min" : "désactivée")
            }));

            string profileRoot = AppDataContext.Root;
            string webData = WebViewProfileData.GetProfileFolder(_profileService.Current?.Username);
            long webDataSize = await Task.Run(() => DirectorySize(webData));
            sections.Add(new DiagnosticsSection("Profil", new List<DiagnosticsRow>
            {
                new("Profil", _profileService.Current?.Username ?? "par défaut"),
                new("Données", profileRoot),
                new("Données web", $"{webData} ({FormatBytes(webDataSize)})"),
                new("Historique", $"{_history.Count} entrées"),
                new("Favoris", _favorites.Count.ToString()),
                new("Coffre", _vault.VaultExists ? (_vault.IsUnlocked ? "déverrouillé" : "verrouillé") : "non créé")
            }));

            var adblock = _adBlock.GetSnapshot();
            sections.Add(new DiagnosticsSection("Protection web", new List<DiagnosticsRow>
            {
                new("État", adblock.Enabled ? "activée" : "désactivée"),
                new("Règles", $"{adblock.NetworkRuleCount:N0} réseau, {adblock.CosmeticRuleCount:N0} visuelles"),
                new("Bloqué (session)", adblock.SessionBlockedCount.ToString("N0")),
                new("Listes", adblock.LastSuccessfulUpdateUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "jamais mises à jour")
            }));

            sections.Add(new DiagnosticsSection("Services du homelab", new List<DiagnosticsRow>
            {
                new("Services", _serviceTiles.Count.ToString()),
                new("En ligne", _serviceTiles.Count(t => t.State is ServiceState.Online or ServiceState.Degraded).ToString()),
                new("Hors ligne", _serviceTiles.Count(t => t.State == ServiceState.Offline).ToString()),
                new("Surveillance", _settings.Settings.ServiceMonitoring ? $"toutes les {_settings.Settings.ServiceCheckIntervalSeconds} s" : "désactivée")
            }));

            string log = RuntimeLogBuffer.GetSnapshot();
            string[] lines = log.Split('\n');
            if (lines.Length > 300)
                log = string.Join('\n', lines[^300..]);

            return new DiagnosticsReport(DateTime.Now, sections, log, profileRoot);
        }

        async Task<DiagnosticsSection> BuildWebEngineSectionAsync()
        {
            var rows = new List<DiagnosticsRow>();

            try
            {
                rows.Add(new("WebView2", CoreWebView2Environment.GetAvailableBrowserVersionString() ?? "introuvable"));
            }
            catch (Exception ex)
            {
                rows.Add(new("WebView2", "indisponible : " + ex.Message));
            }

            var environments = new List<CoreWebView2Environment>();
            try
            {
                environments.Add(await GetEnvironmentForCurrentProfileAsync());
            }
            catch
            {
            }

            if (_privateEnvironment != null)
                environments.Add(_privateEnvironment);

            var processes = environments
                .SelectMany(env =>
                {
                    try { return env.GetProcessInfos().ToList(); }
                    catch { return new List<CoreWebView2ProcessInfo>(); }
                })
                .GroupBy(p => p.ProcessId)
                .Select(g => g.First())
                .ToList();

            long total = 0;
            foreach (CoreWebView2ProcessInfo info in processes)
            {
                try
                {
                    using Process p = Process.GetProcessById(info.ProcessId);
                    total += p.WorkingSet64;
                }
                catch
                {
                    // Processus terminé entre-temps.
                }
            }

            string kinds = string.Join(", ", processes
                .GroupBy(p => p.Kind)
                .OrderByDescending(g => g.Count())
                .Select(g => $"{g.Count()} {DescribeProcessKind(g.Key)}"));

            rows.Add(new("Processus du moteur", processes.Count == 0 ? "aucun" : $"{processes.Count} ({kinds})"));
            rows.Add(new("Mémoire du moteur", FormatBytes(total)));

            return new DiagnosticsSection("Moteur web", rows);
        }

        static string DescribeProcessKind(CoreWebView2ProcessKind kind) => kind switch
        {
            CoreWebView2ProcessKind.Browser => "navigateur",
            CoreWebView2ProcessKind.Renderer => "rendu",
            CoreWebView2ProcessKind.Gpu => "GPU",
            CoreWebView2ProcessKind.Utility => "utilitaire",
            CoreWebView2ProcessKind.PpapiPlugin => "plugin",
            CoreWebView2ProcessKind.PpapiBroker => "courtier",
            _ => "autre"
        };

        static long DirectorySize(string path)
        {
            try
            {
                if (!Directory.Exists(path))
                    return 0;

                long size = 0;
                foreach (string file in Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                {
                    try { size += new FileInfo(file).Length; } catch { }
                }
                return size;
            }
            catch
            {
                return 0;
            }
        }

        static string FormatBytes(long bytes)
        {
            string[] units = { "o", "Ko", "Mo", "Go", "To" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? $"{bytes} o" : $"{value:0.#} {units[unit]}";
        }

        static string FormatDuration(TimeSpan duration)
            => duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours} h {duration.Minutes:00} min"
                : $"{duration.Minutes} min {duration.Seconds:00} s";

        private void MainMenu_Diagnostics_Click(object sender, RoutedEventArgs e) => OpenDiagnostics();
    }
}
