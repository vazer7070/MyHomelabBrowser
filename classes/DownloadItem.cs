using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.Web.WebView2.Core;

namespace MyHomelabBrowser.classes
{
    public enum DownloadUiState
    {
        InProgress,
        Completed,
        Interrupted,
        Cancelled
    }

    public class DownloadItem : INotifyPropertyChanged
    {
        public Guid Id { get; } = Guid.NewGuid();

        public string FileName { get; set; } = "";
        public string? Url { get; set; }
        public string ResultFilePath { get; set; } = "";
        public bool IsPrivate { get; set; }

        public bool IsInProgress => State == DownloadUiState.InProgress;

        bool _isPaused;

        // WebView2 garde l'état InProgress pendant une pause : on le suit à part
        // pour afficher « Reprendre » au lieu de « Pause ».
        [System.Text.Json.Serialization.JsonIgnore]
        public bool IsPaused
        {
            get => _isPaused;
            set
            {
                if (_isPaused == value)
                    return;

                _isPaused = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(SpeedText));
            }
        }

        public bool IsRunning => IsInProgress && !IsPaused;

        // Posé par Annuler pour distinguer l'annulation d'une pause : WebView2
        // signale les deux comme une interruption.
        [System.Text.Json.Serialization.JsonIgnore]
        public bool CancelRequested { get; set; }

        public bool IsCancelled => State == DownloadUiState.Cancelled;

        public bool IsFinished => State != DownloadUiState.InProgress;

        public bool IsCompleted => State == DownloadUiState.Completed;

        // Interrupted = ÉCHEC côté UI
        public bool IsFailed => State == DownloadUiState.Interrupted;

        DateTime _lastSpeedSampleAt = DateTime.Now;
        long _lastSpeedSampleBytes;
        double _speedBytesPerSec;


        public DateTime StartedAt { get; set; } = DateTime.Now;
        public DateTime? FinishedAt { get; set; }

        long _received;
        public long ReceivedBytes
        {
            get => _received;
            set
            {
                _received = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(Progress01));
                OnPropertyChanged(nameof(SizeText));

                UpdateSpeed();
            }
        }


        ulong? _total;
        public ulong? TotalBytes { get => _total; set { _total = value; OnPropertyChanged(); OnPropertyChanged(nameof(Progress01)); } }

        public double Progress01 => (TotalBytes.HasValue && TotalBytes.Value > 0)
            ? Math.Clamp((double)ReceivedBytes / TotalBytes.Value, 0, 1)
            : 0;

        DownloadUiState _state;
        public DownloadUiState State
        {
            get => _state;
            set
            {
                if (_state == value)
                    return;
                if (value != DownloadUiState.InProgress)
                {
                    SpeedBytesPerSec = 0;
                }


                _state = value;
                if (value != DownloadUiState.InProgress)
                    _isPaused = false;

                OnPropertyChanged();
                OnPropertyChanged(nameof(IsInProgress));
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(IsRunning));
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsCancelled));
                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(IsActionable));
                OnPropertyChanged(nameof(SizeText));
            }
        }
        void UpdateSpeed()
        {
            var now = DateTime.Now;
            var elapsed = (now - _lastSpeedSampleAt).TotalSeconds;

            if (elapsed < 0.5)
                return;

            var delta = ReceivedBytes - _lastSpeedSampleBytes;

            if (delta >= 0)
                SpeedBytesPerSec = delta / elapsed;

            _lastSpeedSampleBytes = ReceivedBytes;
            _lastSpeedSampleAt = now;
        }

        static string FormatSize(long bytes)
        {
            const double KB = 1024;
            const double MB = KB * 1024;
            const double GB = MB * 1024;

            if (bytes >= GB) return $"{bytes / GB:0.##} Go";
            if (bytes >= MB) return $"{bytes / MB:0.##} Mo";
            if (bytes >= KB) return $"{bytes / KB:0.#} Ko";
            return $"{bytes} o";
        }

        public string SizeText
        {
            get
            {
                if (IsCompleted && TotalBytes.HasValue)
                    return FormatSize((long)TotalBytes.Value);

                if (TotalBytes.HasValue)
                    return $"{FormatSize(ReceivedBytes)} / {FormatSize((long)TotalBytes.Value)}";

                return FormatSize(ReceivedBytes);
            }
        }


        public string StatusText => State switch
        {
            DownloadUiState.Completed => "Terminé",
            DownloadUiState.Interrupted => "Échec",
            DownloadUiState.Cancelled => "Annulé",
            _ => IsPaused ? "En pause" : "En cours"
        };

        public string SpeedText
        {
            get
            {
                if (IsPaused)
                    return "En pause";

                if (State != DownloadUiState.InProgress || SpeedBytesPerSec <= 0)
                    return "";

                return $"{FormatSize((long)SpeedBytesPerSec)}/s";
            }
        }

        public double SpeedBytesPerSec
        {
            get => _speedBytesPerSec;
            private set
            {
                _speedBytesPerSec = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(SpeedText));
            }
        }

        public CoreWebView2DownloadInterruptReason? InterruptReason { get; set; }

        public bool CanResume { get; set; }
        public bool IsActionable => State == DownloadUiState.Completed && System.IO.File.Exists(ResultFilePath);

        // Référence runtime (non persistée)
        [System.Text.Json.Serialization.JsonIgnore]
        public CoreWebView2DownloadOperation? Operation { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
