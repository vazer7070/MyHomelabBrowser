using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using PommeBrowser.Engine;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Core
{
    /// <summary>Téléchargement affiché dans le panneau (progression, vitesse, état).</summary>
    public sealed class DownloadEntry : INotifyPropertyChanged
    {
        DateTime _lastSample = DateTime.UtcNow;
        long _lastBytes;
        double _speed;

        public DownloadEntry(EngineDownload download)
        {
            Download = download;
            download.Changed += _ =>
            {
                UpdateSpeed();
                Raise(string.Empty);
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public EngineDownload Download { get; }

        public string FileName => Download.SuggestedFileName;
        public string? Path => Download.Destination;

        public bool IsRunning => !Download.IsFinished;
        public bool IsCompleted => Download.IsFinished && Download.Error == null && !Download.IsCancelled;
        public bool IsFailed => Download.IsFinished && Download.Error != null;
        public bool IsCancelled => Download.IsFinished && Download.IsCancelled;
        public bool IsFinished => Download.IsFinished;

        public double Progress => Download.TotalBytes > 0 ? Math.Clamp((double)Download.ReceivedBytes / Download.TotalBytes, 0, 1) : 0;
        public bool HasProgress => IsRunning && Download.TotalBytes > 0;

        public string SizeText
            => IsCompleted
                ? FormatSize(Download.ReceivedBytes)
                : Download.TotalBytes > 0
                    ? FormatSize(Download.ReceivedBytes) + " / " + FormatSize(Download.TotalBytes)
                    : FormatSize(Download.ReceivedBytes);

        public string StatusText
            => IsCompleted ? Tr("Terminé")
               : IsCancelled ? Tr("Annulé")
               : IsFailed ? Tr("Échec") + (string.IsNullOrWhiteSpace(Download.Error) ? string.Empty : " · " + Download.Error)
               : _speed > 0 ? FormatSize((long)_speed) + "/s"
               : Tr("En cours");

        void UpdateSpeed()
        {
            DateTime now = DateTime.UtcNow;
            double elapsed = (now - _lastSample).TotalSeconds;
            if (elapsed < 0.5)
                return;
            _speed = Math.Max(0, (Download.ReceivedBytes - _lastBytes) / elapsed);
            _lastBytes = Download.ReceivedBytes;
            _lastSample = now;
        }

        public static string FormatSize(long bytes)
        {
            string[] units = { Tr("o"), Tr("Ko"), Tr("Mo"), Tr("Go"), Tr("To") };
            double size = bytes;
            int unit = 0;
            while (size >= 1024 && unit < units.Length - 1)
            {
                size /= 1024;
                unit++;
            }
            return (unit == 0 ? size.ToString("0", Culture) : size.ToString(size < 10 ? "0.0" : "0", Culture)) + " " + units[unit];
        }

        void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// Téléchargements de la session, communs à toutes les fenêtres (les plus récents d'abord).
    /// Ceux des onglets privés disparaissent de la liste une fois terminés.
    /// </summary>
    public sealed class DownloadList
    {
        const int MaxEntries = 50;

        public DownloadList()
        {
            EngineHost.DownloadStarted += OnStarted;
        }

        public ObservableCollection<DownloadEntry> Entries { get; } = new();

        public int ActiveCount => Entries.Count(e => e.IsRunning);

        /// <summary>Liste ou progression modifiée.</summary>
        public event Action? Changed;

        /// <summary>Téléchargement démarré.</summary>
        public event Action<DownloadEntry>? Started;

        /// <summary>Téléchargement terminé avec succès.</summary>
        public event Action<DownloadEntry>? Completed;

        void OnStarted(EngineDownload download)
        {
            var entry = new DownloadEntry(download);
            bool finished = false;
            download.Changed += _ =>
            {
                if (download.IsFinished && !finished)
                {
                    finished = true;
                    if (entry.IsCompleted)
                        Completed?.Invoke(entry);
                    if (download.IsPrivate)
                        Entries.Remove(entry);
                }
                Changed?.Invoke();
            };

            Entries.Insert(0, entry);
            for (int i = Entries.Count - 1; i >= 0 && Entries.Count > MaxEntries; i--)
            {
                if (Entries[i].IsFinished)
                    Entries.RemoveAt(i);
            }
            Started?.Invoke(entry);
            Changed?.Invoke();
        }

        /// <summary>Retire de la liste les téléchargements terminés (les fichiers restent).</summary>
        public void ClearFinished()
        {
            foreach (DownloadEntry entry in Entries.Where(e => e.IsFinished).ToList())
                Entries.Remove(entry);
            Changed?.Invoke();
        }

        public void Remove(DownloadEntry entry)
        {
            Entries.Remove(entry);
            Changed?.Invoke();
        }

        public static bool FileExists(DownloadEntry entry) => entry.Path != null && File.Exists(entry.Path);
    }
}
