using System;
using System.Collections.Generic;
using System.IO;
using MyHomelabBrowser.classes;
using PommeBrowser.Linux.Core;

namespace PommeBrowser.Linux.Web
{
    enum DownloadState
    {
        Running,
        Finished,
        Failed,
        Cancelled
    }

    sealed class DownloadEntry
    {
        public DownloadEntry(WebKit.Download download)
        {
            Download = download;
        }

        public WebKit.Download Download { get; }
        public string FileName { get; set; } = string.Empty;
        public string? Destination { get; set; }
        public double Progress { get; set; }
        public DownloadState State { get; set; } = DownloadState.Running;
        public string? Error { get; set; }
        public DateTime StartedAt { get; } = DateTime.Now;
    }

    /// <summary>
    /// Téléchargements : enregistrés directement dans le dossier choisi (jamais par-dessus un
    /// fichier existant), suivis dans le panneau Téléchargements.
    /// </summary>
    sealed class DownloadManager
    {
        const int MaxEntries = 50;

        readonly Func<string?> _directory;
        readonly HashSet<WebKit.NetworkSession> _sessions = new();
        readonly List<DownloadEntry> _entries = new();

        public DownloadManager(Func<string?> directory)
        {
            _directory = directory;
        }

        /// <summary>Liste ou progression modifiée (fil de l'interface).</summary>
        public event Action? Changed;

        public event Action<DownloadEntry>? Started;
        public event Action<DownloadEntry>? Completed;

        public IReadOnlyList<DownloadEntry> Entries => _entries;

        public int ActiveCount => _entries.FindAll(e => e.State == DownloadState.Running).Count;

        public string Directory => _directory() ?? LinuxPaths.DefaultDownloadDirectory();

        public void Attach(WebKit.NetworkSession session)
        {
            if (!_sessions.Add(session))
                return;
            session.OnDownloadStarted += (_, args) => Track(args.Download);
        }

        public void Clear()
        {
            _entries.RemoveAll(e => e.State != DownloadState.Running);
            Changed?.Invoke();
        }

        void Track(WebKit.Download download)
        {
            var entry = new DownloadEntry(download);
            _entries.Insert(0, entry);
            for (int i = _entries.Count - 1; i >= 0 && _entries.Count > MaxEntries; i--)
            {
                if (_entries[i].State != DownloadState.Running)
                    _entries.RemoveAt(i);
            }

            DateTime lastNotified = DateTime.MinValue;

            download.OnDecideDestination += (_, args) =>
            {
                string directory = Directory;
                try
                {
                    System.IO.Directory.CreateDirectory(directory);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    RuntimeLogBuffer.Append("[Téléchargements] " + ex.Message);
                }

                string path = DownloadNames.UniquePath(directory, args.SuggestedFilename);
                entry.Destination = path;
                entry.FileName = Path.GetFileName(path);
                download.SetAllowOverwrite(false);
                download.SetDestination(path);
                Started?.Invoke(entry);
                Changed?.Invoke();
                return true;
            };

            download.OnReceivedData += (_, _) =>
            {
                entry.Progress = download.GetEstimatedProgress();
                if (DateTime.UtcNow - lastNotified > TimeSpan.FromMilliseconds(250))
                {
                    lastNotified = DateTime.UtcNow;
                    Changed?.Invoke();
                }
            };

            download.OnFailed += (_, args) =>
            {
                entry.State = args.Error.Matches(WebKit.Functions.DownloadErrorQuark(), (int)WebKit.DownloadError.CancelledByUser)
                    ? DownloadState.Cancelled
                    : DownloadState.Failed;
                entry.Error = args.Error.Message;
            };

            download.OnFinished += (_, _) =>
            {
                if (entry.State == DownloadState.Running)
                {
                    entry.State = DownloadState.Finished;
                    entry.Progress = 1;
                }
                Completed?.Invoke(entry);
                Changed?.Invoke();
            };

            Changed?.Invoke();
        }
    }
}
