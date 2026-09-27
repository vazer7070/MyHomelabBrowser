using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MyHomelabBrowser.classes
{
    public class DownloadManager
    {
        // Au-delà, les plus anciennes entrées terminées sont oubliées (pas les fichiers).
        private const int MaxHistoryEntries = 200;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        public static DownloadManager Instance { get; } = new();

        /// <summary>
        /// Relance un téléchargement qui ne peut pas reprendre (branché par la fenêtre principale).
        /// </summary>
        public Action<string?>? RetryByUrl { get; set; }

        public ObservableCollection<DownloadItem> Items { get; } = new();

        public event Action? ItemsChanged;

        public string DownloadFolder { get; set; } = DefaultDownloadFolder;

        public static string DefaultDownloadFolder =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

        string HistoryPath => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MyHomelabBrowser",
            "downloads.json"
        );

        DownloadManager()
        {
            Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
            LoadHistory();
        }

        public int ActiveCount => Items.Count(d => d.IsInProgress);

        public void Remove(DownloadItem it)
        {
            if (it == null)
                return;

            Items.Remove(it);
            SaveHistory();
            ItemsChanged?.Invoke();
        }

        public void RemoveFinished()
        {
            foreach (var d in Items.Where(d => !d.IsInProgress).ToList())
                Items.Remove(d);

            SaveHistory();
            ItemsChanged?.Invoke();
        }

        /// <summary>
        /// Retire de la liste les téléchargements terminés commencés après <paramref name="since"/>.
        /// </summary>
        public void RemoveFinishedSince(DateTime since)
        {
            foreach (var d in Items.Where(d => !d.IsInProgress && d.StartedAt >= since).ToList())
                Items.Remove(d);

            SaveHistory();
            ItemsChanged?.Invoke();
        }

        public void ClearPrivateDownloads()
        {
            var toRemove = Items.Where(d => d.IsPrivate).ToList();

            foreach (var d in toRemove)
                Items.Remove(d);

            ItemsChanged?.Invoke();
        }

        public void Retry(DownloadItem it)
        {
            if (it == null)
                return;

            // 1) Reprise HTTP (Range) si WebView2 la permet.
            if (it.Operation != null && it.Operation.CanResume)
            {
                it.Operation.Resume();
                it.IsPaused = false;
                return;
            }

            // 2) Sinon : nouvelle requête sur l'URL d'origine.
            if (!string.IsNullOrWhiteSpace(it.Url))
                RetryByUrl?.Invoke(it.Url);
        }

        public void LoadHistory()
        {
            try
            {
                if (!File.Exists(HistoryPath)) return;
                var json = File.ReadAllText(HistoryPath);
                var list = JsonSerializer.Deserialize<DownloadItem[]>(json) ?? Array.Empty<DownloadItem>();
                Items.Clear();
                foreach (var it in list.OrderByDescending(x => x.StartedAt).Take(MaxHistoryEntries))
                {
                    // Un téléchargement « en cours » à la fermeture ne reprendra jamais :
                    // sans cette correction il restait affiché en cours et gonflait le badge.
                    if (it.State == DownloadUiState.InProgress)
                        it.State = DownloadUiState.Interrupted;

                    Items.Add(it);
                }
                ItemsChanged?.Invoke();
            }
            catch { /* ne casse pas l’app */ }
        }

        public void SaveHistory()
        {
            try
            {
                // Les téléchargements des onglets privés ne sont jamais écrits sur le disque.
                var list = Items
                    .Where(x => !x.IsPrivate)
                    .Take(MaxHistoryEntries)
                    .Select(x => new DownloadItem
                    {
                        FileName = x.FileName,
                        Url = x.Url,
                        ResultFilePath = x.ResultFilePath,
                        StartedAt = x.StartedAt,
                        FinishedAt = x.FinishedAt,
                        ReceivedBytes = x.ReceivedBytes,
                        TotalBytes = x.TotalBytes,
                        State = x.State,
                        InterruptReason = x.InterruptReason,
                        CanResume = x.CanResume
                    }).ToArray();

                AtomicFile.WriteAllText(HistoryPath, JsonSerializer.Serialize(list, JsonOptions));
            }
            catch { }
        }

        public void OpenFile(DownloadItem? it)
        {
            if (it == null || !File.Exists(it.ResultFilePath)) return;

            try
            {
                Process.Start(new ProcessStartInfo(it.ResultFilePath) { UseShellExecute = true });
            }
            catch
            {
                // Aucun programme associé : l'explorateur reste accessible via le dossier.
            }
        }

        public void OpenContainingFolder(DownloadItem? it)
        {
            if (it == null || string.IsNullOrWhiteSpace(it.ResultFilePath)) return;

            try
            {
                if (File.Exists(it.ResultFilePath))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{it.ResultFilePath}\"") { UseShellExecute = true });
                    return;
                }

                string? folder = Path.GetDirectoryName(it.ResultFilePath);
                if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
                    Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            }
            catch
            {
            }
        }

        public void Cancel(DownloadItem it)
        {
            if (it.Operation == null)
                return;

            it.CancelRequested = true;
            it.Operation.Cancel();
        }

        public void Pause(DownloadItem it)
        {
            if (it.Operation == null)
                return;

            it.IsPaused = true;
            it.Operation.Pause();
        }

        public void Resume(DownloadItem it)
        {
            if (it.Operation == null) return;
            if (!it.Operation.CanResume) return;

            it.Operation.Resume();
            it.IsPaused = false;
        }

        public void NotifyChanged() => ItemsChanged?.Invoke();
    }
}
