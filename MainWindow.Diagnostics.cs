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
using static MyHomelabBrowser.classes.Localization.Loc;

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

            OpenViewTab(new DiagnosticsView(BuildDiagnosticsReportAsync), Tr("Diagnostic"));
        }

        async Task<DiagnosticsReport> BuildDiagnosticsReportAsync()
        {
            var sections = new List<DiagnosticsSection>();
            Process process = Process.GetCurrentProcess();
            process.Refresh();

            sections.Add(new DiagnosticsSection(Tr("Application"), new List<DiagnosticsRow>
            {
                new(Tr("Version"), AppVersion.Current),
                new(Tr("Installation"), _updates?.IsInstalled == true ? Tr("installée (mises à jour automatiques)") : Tr("portable / développement")),
                new(".NET", RuntimeInformation.FrameworkDescription),
                new(Tr("Système"), $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})"),
                new(Tr("Démarrée depuis"), FormatDuration(DateTime.Now - process.StartTime)),
                new(Tr("Mémoire (processus)"), Tr("{0} utilisés, {1} privés", FormatBytes(process.WorkingSet64), FormatBytes(process.PrivateMemorySize64))),
                new(Tr("Mémoire gérée"), FormatBytes(GC.GetTotalMemory(false)))
            }));

            sections.Add(await BuildWebEngineSectionAsync());

            var webTabs = Tabs.Items.OfType<TabItem>().Select(t => t.Tag).OfType<WebTabContent>().ToList();
            sections.Add(new DiagnosticsSection(Tr("Onglets"), new List<DiagnosticsRow>
            {
                new(Tr("Ouverts"), Tabs.Items.Count.ToString()),
                new(Tr("Pages web"), webTabs.Count(t => !t.IsCustomView && !t.IsLegacyExternal).ToString()),
                new(Tr("En veille"), webTabs.Count(t => t.IsSuspended).ToString()),
                new(Tr("Privés"), webTabs.Count(t => t.IsPrivate).ToString()),
                new(Tr("Épinglés"), webTabs.Count(t => t.IsPinned).ToString()),
                new("Flash Legacy", webTabs.Count(t => t.IsLegacyExternal).ToString()),
                new(Tr("Vue côte à côte"), IsSplitViewActive ? Tr("active") : Tr("non")),
                new(Tr("Mise en veille auto"), _settings.Settings.EnableSuspension ? Tr("après {0} min", _settings.Settings.SuspendDelayMinutes) : Tr("désactivée"))
            }));

            string profileRoot = AppDataContext.Root;
            string webData = WebViewProfileData.GetProfileFolder(_profileService.Current?.Username);
            long webDataSize = await Task.Run(() => DirectorySize(webData));
            sections.Add(new DiagnosticsSection(Tr("Profil"), new List<DiagnosticsRow>
            {
                new(Tr("Profil"), _profileService.Current?.Username ?? Tr("par défaut")),
                new(Tr("Données"), profileRoot),
                new(Tr("Données web"), $"{webData} ({FormatBytes(webDataSize)})"),
                new(Tr("Historique"), Tr("{0} entrées", _historyStore?.Count() ?? _history.Count)),
                new(Tr("Favoris"), _favorites.Count.ToString()),
                new(Tr("Coffre"), _vault.VaultExists ? (_vault.IsUnlocked ? Tr("déverrouillé") : Tr("verrouillé")) : Tr("non créé"))
            }));

            var adblock = _adBlock.GetSnapshot();
            sections.Add(new DiagnosticsSection(Tr("Protection web"), new List<DiagnosticsRow>
            {
                new(Tr("État"), adblock.Enabled ? Tr("activée") : Tr("désactivée")),
                new(Tr("Règles"), Tr("{0:N0} réseau, {1:N0} visuelles", adblock.NetworkRuleCount, adblock.CosmeticRuleCount)),
                new(Tr("Bloqué (session)"), adblock.SessionBlockedCount.ToString("N0")),
                new(Tr("Listes"), adblock.LastSuccessfulUpdateUtc?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? Tr("jamais mises à jour"))
            }));

            sections.Add(new DiagnosticsSection(Tr("Services du homelab"), new List<DiagnosticsRow>
            {
                new(Tr("Services"), _serviceTiles.Count.ToString()),
                new(Tr("En ligne"), _serviceTiles.Count(t => t.State is ServiceState.Online or ServiceState.Degraded).ToString()),
                new(Tr("Hors ligne"), _serviceTiles.Count(t => t.State == ServiceState.Offline).ToString()),
                new(Tr("Surveillance"), _settings.Settings.ServiceMonitoring ? Tr("toutes les {0} s", _settings.Settings.ServiceCheckIntervalSeconds) : Tr("désactivée"))
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
                rows.Add(new("WebView2", CoreWebView2Environment.GetAvailableBrowserVersionString() ?? Tr("introuvable")));
            }
            catch (Exception ex)
            {
                rows.Add(new("WebView2", Tr("indisponible : {0}", ex.Message)));
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

            rows.Add(new(Tr("Processus du moteur"), processes.Count == 0 ? Tr("aucun") : $"{processes.Count} ({kinds})"));
            rows.Add(new(Tr("Mémoire du moteur"), FormatBytes(total)));

            return new DiagnosticsSection(Tr("Moteur web"), rows);
        }

        static string DescribeProcessKind(CoreWebView2ProcessKind kind) => kind switch
        {
            CoreWebView2ProcessKind.Browser => Tr("navigateur"),
            CoreWebView2ProcessKind.Renderer => Tr("rendu"),
            CoreWebView2ProcessKind.Gpu => "GPU",
            CoreWebView2ProcessKind.Utility => Tr("utilitaire"),
            CoreWebView2ProcessKind.PpapiPlugin => "plugin",
            CoreWebView2ProcessKind.PpapiBroker => Tr("courtier"),
            _ => Tr("autre")
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
            string[] units = MyHomelabBrowser.classes.Localization.Loc.Language == "en"
                ? new[] { "B", "KB", "MB", "GB", "TB" }
                : new[] { "o", "Ko", "Mo", "Go", "To" };
            double value = bytes;
            int unit = 0;
            while (value >= 1024 && unit < units.Length - 1)
            {
                value /= 1024;
                unit++;
            }
            return unit == 0 ? $"{bytes} {units[0]}" : $"{value:0.#} {units[unit]}";
        }

        static string FormatDuration(TimeSpan duration)
            => duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours} h {duration.Minutes:00} min"
                : $"{duration.Minutes} min {duration.Seconds:00} s";

        private void MainMenu_Diagnostics_Click(object sender, RoutedEventArgs e) => OpenDiagnostics();
    }
}
