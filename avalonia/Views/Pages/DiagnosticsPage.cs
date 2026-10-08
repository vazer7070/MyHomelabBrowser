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
using PommeBrowser.Legacy;
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
                    // Hors Windows, la mémoire « privée » compte l'espace réservé par .NET, pas la mémoire utilisée.
                    (Tr("Mémoire (processus)"), OperatingSystem.IsWindows()
                        ? Tr("{0} utilisés, {1} privés", DownloadEntry.FormatSize(process.WorkingSet64), DownloadEntry.FormatSize(process.PrivateMemorySize64))
                        : DownloadEntry.FormatSize(process.WorkingSet64)),
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
            if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
                sections.Add(new(Tr("Moteur Flash intégré"), FlashRows()));
            return sections;
        }

        /// <summary>Moteur Flash intégré : réglage, hôtes livrés, modules trouvés (ordre d'essai), lecteurs et dernier arrêt.</summary>
        List<(string, string)> FlashRows()
        {
            var rows = new List<(string, string)>
            {
                (Tr("Réglage"), App.Settings.FlashIntegratedEngine ? Tr("activé") : Tr("désactivé")),
                (Tr("Hôte 64 bits"), FlashHostProcess.IsAvailable ? Tr("livré") : Tr("absent de cette compilation"))
            };
            if (OperatingSystem.IsWindows())
                rows.Add((Tr("Hôte 32 bits"), FlashHostProcess.IsAvailable32 ? Tr("livré") : Tr("absent de cette compilation")));

            IReadOnlyList<string> modules = LegacyEngine.IntegratedModules;
            if (modules.Count == 0)
                rows.Add((Tr("Modules"), Tr("aucun : ajoutez votre copie de Flash Player dans Paramètres › Avancé")));
            for (int i = 0; i < modules.Count; i++)
            {
                string module = modules[i];
                Version? version = FlashModuleSearch.VersionOf(module);
                var text = new StringBuilder(Path.GetFileName(module));
                text.Append(" — ").Append(FlashModuleSearch.Is32Bit(module) && OperatingSystem.IsWindows() ? "32" : "64").Append(Tr(" bits"));
                text.Append(", ").Append(version != null ? Tr("version {0}", version) : Tr("version inconnue"));
                if (version != null && version > FlashModuleSearch.LastWithoutTimeBomb)
                    text.Append(Tr(" (peut refuser les contenus depuis janvier 2021 : préférez la 32.0.0.371 ou une version plus ancienne)"));
                if (!FlashHostProcess.IsAvailableFor(module))
                    text.Append(Tr(" — hôte de cette architecture absent"));
                text.Append(" — ").Append(module);
                rows.Add((Tr("Module {0}", i + 1), text.ToString()));
            }

            IReadOnlyList<int> players = FlashHostProcess.RunningProcessIds;
            long memory = 0;
            foreach (int pid in players)
            {
                try
                {
                    using Process p = Process.GetProcessById(pid);
                    memory += p.WorkingSet64;
                }
                catch (ArgumentException)
                {
                    // Lecteur arrêté entre-temps.
                }
            }
            rows.Add((Tr("Lecteurs ouverts"), players.Count == 0 ? "0" : Tr("{0} ({1})", players.Count, DownloadEntry.FormatSize(memory))));
            rows.Add((Tr("Dernier arrêt inattendu"), FlashHostProcess.LastStop is { } stop
                ? Tr("{0} — {1}, code {2}{3}", stop.At.ToString("T", Culture), stop.Module,
                    stop.ExitCode?.ToString(Culture) ?? "?", stop.BeforeContent ? Tr(" (avant d'afficher le contenu)") : string.Empty)
                : Tr("aucun")));
            return rows;
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
