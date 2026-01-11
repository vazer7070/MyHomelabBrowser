using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;

namespace MyHomelabBrowser.classes
{
    public class DownloadManager
    {
        public static DownloadManager Instance { get; } = new();
        public Action<string?>? RetryByUrl { get; set; }


        public ObservableCollection<DownloadItem> Items { get; } = new();

        public event Action? ItemsChanged;

        // À brancher sur tes settings
        public string DownloadFolder { get; set; } =
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
        public void Remove(DownloadItem it)
        {
            if (it == null)
                return;

            Items.Remove(it);
        }
        public void ClearPrivateDownloads()
        {
            var toRemove = Items.Where(d => d.IsPrivate).ToList();

            foreach (var d in toRemove)
                Items.Remove(d);
        }

        public void Retry(DownloadItem it)
        {
            if (it == null)
                return;

            // 1) si le téléchargement peut reprendre (HTTP range)
            if (it.Operation != null && it.Operation.CanResume)
            {
                it.Operation.Resume();
                return;
            }

            // 2) sinon : relancer via l’URL (nouvelle requête)
            if (!string.IsNullOrWhiteSpace(it.Url))
            {
                RetryByUrl?.Invoke(it.Url);
            }
        }

        public void LoadHistory()
        {
            try
            {
                if (!File.Exists(HistoryPath)) return;
                var json = File.ReadAllText(HistoryPath);
                var list = JsonSerializer.Deserialize<DownloadItem[]>(json) ?? Array.Empty<DownloadItem>();
                Items.Clear();
                foreach (var it in list.OrderByDescending(x => x.StartedAt))
                    Items.Add(it);
                ItemsChanged?.Invoke();
            }
            catch { /* ne casse pas l’app */ }
        }

        public void SaveHistory()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(HistoryPath)!);
                // On sérialise sans Operation (runtime)
                var list = Items.Select(x => new DownloadItem
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

                File.WriteAllText(HistoryPath, JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true }));
            }
            catch { }
        }

        public void OpenFile(DownloadItem it)
        {
            if (!File.Exists(it.ResultFilePath)) return;
            Process.Start(new ProcessStartInfo(it.ResultFilePath) { UseShellExecute = true });
        }

        public void OpenContainingFolder(DownloadItem it)
        {
            if (string.IsNullOrWhiteSpace(it.ResultFilePath)) return;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{it.ResultFilePath}\"") { UseShellExecute = true });
        }

        public void Cancel(DownloadItem it) => it.Operation?.Cancel();

        public void Pause(DownloadItem it) => it.Operation?.Pause();

        public void Resume(DownloadItem it)
        {
            if (it.Operation == null) return;
            if (it.Operation.CanResume) it.Operation.Resume(); // :contentReference[oaicite:3]{index=3}
        }

        public void NotifyChanged() => ItemsChanged?.Invoke();
    }
}
