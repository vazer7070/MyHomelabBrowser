using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace MyHomelabBrowser.classes.CloudTorrent.Models
{
    public enum CloudTorrentDetectedItemType
    {
        Page,
        Torrent,
        Hoster,
        Media
    }

    public sealed class CloudTorrentDetectedItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string Id { get; init; } = Guid.NewGuid().ToString("N");
        public CloudTorrentDetectedItemType Type { get; init; }
        public string Label { get; init; } = string.Empty;
        public string Url { get; init; } = string.Empty;
        public string Source { get; init; } = "Page";

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;

                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public string TypeLabel => Type switch
        {
            CloudTorrentDetectedItemType.Torrent => "Torrent",
            CloudTorrentDetectedItemType.Hoster => "Hébergeur",
            CloudTorrentDetectedItemType.Media => "Média",
            _ => "Page"
        };

        public string Icon => Type switch
        {
            CloudTorrentDetectedItemType.Torrent => "⇩",
            CloudTorrentDetectedItemType.Hoster => "⬡",
            CloudTorrentDetectedItemType.Media => "▶",
            _ => "◎"
        };

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
