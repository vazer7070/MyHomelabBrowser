using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.classes.Homelab
{
    /// <summary>
    /// Service du homelab affiché sur la page d'accueil (NAS, Proxmox, routeur…).
    /// </summary>
    public sealed class HomelabService
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public string? Group { get; set; }

        /// <summary>
        /// Vérifier régulièrement que le service répond et prévenir s'il tombe.
        /// </summary>
        public bool Monitor { get; set; } = true;

        public DateTime CreatedAt { get; set; } = DateTime.Now;
    }

    public enum ServiceState
    {
        Unknown,
        Online,
        Degraded,
        Offline
    }

    public sealed record ServiceCheckResult(ServiceState State, int? StatusCode, TimeSpan? Latency, string? Error, DateTime CheckedAt)
    {
        public static ServiceCheckResult Unknown { get; } = new(ServiceState.Unknown, null, null, null, DateTime.MinValue);
    }

    /// <summary>
    /// État affiché d'un service, partagé par toutes les pages d'accueil ouvertes.
    /// </summary>
    public sealed class ServiceTile : INotifyPropertyChanged
    {
        private ServiceCheckResult _result = ServiceCheckResult.Unknown;
        private bool _isChecking;

        public ServiceTile(HomelabService service)
        {
            Service = service;
        }

        public HomelabService Service { get; private set; }

        public Guid Id => Service.Id;
        public string Name => string.IsNullOrWhiteSpace(Service.Name) ? Address : Service.Name;
        public string Url => Service.Url;
        public string? Group => Service.Group;

        public string Address => Uri.TryCreate(Service.Url, UriKind.Absolute, out Uri? uri) ? uri.Authority : Service.Url;

        public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

        public ServiceCheckResult Result
        {
            get => _result;
            set
            {
                _result = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(State));
                OnPropertyChanged(nameof(StatusText));
            }
        }

        public bool IsChecking
        {
            get => _isChecking;
            set
            {
                _isChecking = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(StatusText));
            }
        }

        public ServiceState State => Service.Monitor ? _result.State : ServiceState.Unknown;

        public string StatusText
        {
            get
            {
                if (!Service.Monitor)
                    return Tr("Surveillance désactivée");
                if (_isChecking && _result.State == ServiceState.Unknown)
                    return Tr("Vérification…");

                return _result.State switch
                {
                    ServiceState.Online => _result.Latency is { } latency ? Tr("En ligne · {0:0} ms", latency.TotalMilliseconds) : Tr("En ligne"),
                    ServiceState.Degraded => Tr("Erreur {0}", _result.StatusCode),
                    ServiceState.Offline => Tr("Hors ligne") + (string.IsNullOrWhiteSpace(_result.Error) ? string.Empty : " · " + _result.Error),
                    _ => Tr("Pas encore vérifié")
                };
            }
        }

        public void Update(HomelabService service)
        {
            Service = service;
            OnPropertyChanged(string.Empty);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
