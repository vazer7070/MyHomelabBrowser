using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Homelab;
using PommeBrowser.Core;
using PommeBrowser.Engine;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Pages
{
    /// <summary>Diagnostic : application, onglets, profil, protection web, services, moteur et journal récent.</summary>
    public sealed class DiagnosticsPage : PageBase
    {
        sealed record Section(string Title, IReadOnlyList<(string Label, string Value)> Rows);

        public DiagnosticsPage(MainWindow window) : base(window, Tr("Diagnostic"))
        {
            AddToolbar(TextButton(Tr("Actualiser"), Refresh));
            AddToolbar(TextButton(Tr("Copier le rapport"), CopyReport));
            AddToolbar(TextButton(Tr("Ouvrir le dossier des données"), () => _ = Window.OpenFolderAsync(AppPaths.DataDirectory)));
        }

        List<Section> Collect()
        {
            using Process process = Process.GetCurrentProcess();
            var sections = new List<Section>
            {
                new(Tr("Application"), new List<(string, string)>
                {
                    (Tr("Version"), AppInfo.DisplayVersion + " (Avalonia)"),
                    (Tr("Système"), $"{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})"),
                    (Tr("Démarrée depuis"), FormatDuration(DateTime.Now - process.StartTime)),
                    (Tr("Mémoire (processus)"), Tr("{0} utilisés, {1} privés", DownloadEntry.FormatSize(process.WorkingSet64), DownloadEntry.FormatSize(process.PrivateMemorySize64))),
                    (Tr("Mémoire gérée"), DownloadEntry.FormatSize(GC.GetTotalMemory(false)))
                })
            };

            var tabs = App.Windows.SelectMany(w => w.Tabs).ToList();
            sections.Add(new(Tr("Onglets"), new List<(string, string)>
            {
                (Tr("Fenêtres"), App.Windows.Count.ToString(Culture)),
                (Tr("Ouverts"), tabs.Count.ToString(Culture)),
                (Tr("Pages web"), tabs.Count(t => t.Engine != null).ToString(Culture)),
                (Tr("En veille"), tabs.Count(t => t.HasPendingLoad).ToString(Culture)),
                (Tr("Privés"), tabs.Count(t => t.IsPrivate).ToString(Culture)),
                (Tr("Épinglés"), tabs.Count(t => t.IsPinned).ToString(Culture)),
                (Tr("Mise en veille auto"), App.Settings.EnableSuspension ? Tr("après {0} min", App.Settings.SuspendDelayMinutes) : Tr("désactivée"))
            }));

            sections.Add(new(Tr("Profil"), new List<(string, string)>
            {
                (Tr("Profil"), App.Profiles.Current?.Username ?? Tr("par défaut")),
                (Tr("Réglages"), AppPaths.Profile(string.Empty)),
                (Tr("Données"), AppPaths.DataDirectory),
                (Tr("Historique"), Tr("{0} entrées", App.History.Recent.Count)),
                (Tr("Favoris"), App.Favorites.All.Count.ToString(Culture)),
                (Tr("Coffre"), App.Vault.Service.VaultExists ? (App.Vault.IsUnlocked ? Tr("déverrouillé") : Tr("verrouillé")) : Tr("non créé"))
            }));

            sections.Add(new(Tr("Protection web"), App.AdBlock.DiagnosticRows()));

            var services = App.Services.GetAll();
            var results = services.Select(s => App.Monitor.GetResult(s.Id)).ToList();
            sections.Add(new(Tr("Services du homelab"), new List<(string, string)>
            {
                (Tr("Services"), services.Count.ToString(Culture)),
                (Tr("En ligne"), results.Count(r => r.State is ServiceState.Online or ServiceState.Degraded).ToString(Culture)),
                (Tr("Hors ligne"), results.Count(r => r.State == ServiceState.Offline).ToString(Culture)),
                (Tr("Surveillance"), App.Settings.ServiceMonitoring ? Tr("toutes les {0} s", App.Settings.ServiceCheckIntervalSeconds) : Tr("désactivée"))
            }));

            IReadOnlyList<int> engineProcesses = EngineHost.Processes();
            long engineMemory = 0;
            foreach (int pid in engineProcesses)
            {
                try
                {
                    using Process p = Process.GetProcessById(pid);
                    engineMemory += p.WorkingSet64;
                }
                catch (ArgumentException)
                {
                    // Processus terminé entre-temps.
                }
            }
            var engineRows = new List<(string, string)>
            {
                (Tr("Moteur"), EngineHost.Describe()),
                (Tr("Ruffle"), RuffleContent.InstalledVersion is { } ruffle ? ruffle : Tr("absent de cette compilation")),
                (Tr("Basilisk"), App.BasiliskExecutable ?? Tr("introuvable"))
            };
            if (engineProcesses.Count > 0)
            {
                engineRows.Add((Tr("Processus du moteur"), engineProcesses.Count.ToString(Culture)));
                engineRows.Add((Tr("Mémoire du moteur"), DownloadEntry.FormatSize(engineMemory)));
            }
            sections.Add(new(Tr("Moteur web"), engineRows));
            return sections;
        }

        static string Log()
        {
            string log = RuntimeLogBuffer.GetSnapshot();
            string[] lines = log.Split('\n');
            return lines.Length > 300 ? string.Join('\n', lines[^300..]) : log;
        }

        protected override void Build(StackPanel content)
        {
            foreach (Section section in Collect())
            {
                content.Children.Add(Heading(section.Title));
                content.Children.Add(Card(section.Rows.Select(r => ValueRow(r.Label, r.Value)).ToArray()));
            }

            content.Children.Add(Heading(Tr("Journal récent")));
            var log = new TextBox
            {
                Text = Log(),
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.NoWrap,
                FontFamily = new FontFamily("monospace"),
                FontSize = 11.5,
                Height = 260
            };
            content.Children.Add(log);
        }

        Control ValueRow(string label, string value)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("220,*"), Margin = new Thickness(14, 8) };
            var title = new TextBlock { Text = label };
            title.Classes.Add("subtitle");
            grid.Children.Add(title);
            var text = new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            return grid;
        }

        async void CopyReport()
        {
            var builder = new StringBuilder();
            builder.AppendLine("PommeBrowser — " + Tr("Diagnostic") + " — " + DateTime.Now.ToString("g", Culture));
            foreach (Section section in Collect())
            {
                builder.AppendLine().AppendLine("## " + section.Title);
                foreach ((string label, string value) in section.Rows)
                    builder.AppendLine($"{label} : {value}");
            }
            builder.AppendLine().AppendLine("## " + Tr("Journal récent")).AppendLine(Log());
            if (await SecureClipboard.CopyAsync(Window, builder.ToString(), secret: false))
                Window.ShowToast(Tr("Rapport copié dans le presse-papiers."));
        }

        static string FormatDuration(TimeSpan duration)
            => duration.TotalHours >= 1
                ? $"{(int)duration.TotalHours} h {duration.Minutes:00} min"
                : $"{duration.Minutes} min {duration.Seconds:00} s";
    }
}
