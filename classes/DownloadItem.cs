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
                OnPropertyChanged();
                OnPropertyChanged(nameof(IsInProgress));
                OnPropertyChanged(nameof(IsCompleted));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(IsActionable));
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


        public string SpeedText
        {
            get
            {
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
        public CoreWebView2DownloadOperation? Operation { get; set; }

        public event PropertyChangedEventHandler? PropertyChanged;
        void OnPropertyChanged([CallerMemberName] string? n = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n));
    }
}
