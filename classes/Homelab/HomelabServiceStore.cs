using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.classes.Homelab
{
    /// <summary>
    /// Liste des services du profil (services.json).
    /// </summary>
    public sealed class HomelabServiceStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly Func<string> _pathProvider;
        private readonly object _gate = new();
        private List<HomelabService> _services = new();
        private string? _loadedPath;

        public HomelabServiceStore(Func<string> pathProvider)
        {
            _pathProvider = pathProvider;
        }

        public event Action? Changed;

        public IReadOnlyList<HomelabService> GetAll()
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _services.Select(Clone).ToList();
            }
        }

        public HomelabService? Get(Guid id)
        {
            lock (_gate)
            {
                EnsureLoaded();
                HomelabService? service = _services.FirstOrDefault(s => s.Id == id);
                return service == null ? null : Clone(service);
            }
        }

        public void AddOrUpdate(HomelabService service)
        {
            ArgumentNullException.ThrowIfNull(service);
            string? error = Validate(service);
            if (error != null)
                throw new ArgumentException(error, nameof(service));

            lock (_gate)
            {
                EnsureLoaded();
                int index = _services.FindIndex(s => s.Id == service.Id);
                HomelabService copy = Clone(service);
                copy.Name = copy.Name.Trim();
                copy.Url = copy.Url.Trim();
                copy.Group = string.IsNullOrWhiteSpace(copy.Group) ? null : copy.Group.Trim();

                if (index >= 0)
                    _services[index] = copy;
                else
                    _services.Add(copy);

                Save();
            }

            Changed?.Invoke();
        }

        /// <summary>
        /// Ajoute plusieurs services d'un coup ; les adresses déjà présentes sont ignorées.
        /// </summary>
        public int AddRange(IEnumerable<HomelabService> services)
        {
            int added = 0;
            lock (_gate)
            {
                EnsureLoaded();
                var known = new HashSet<string>(_services.Select(s => s.Url.TrimEnd('/')), StringComparer.OrdinalIgnoreCase);
                foreach (HomelabService service in services)
                {
                    if (Validate(service) != null || !known.Add(service.Url.Trim().TrimEnd('/')))
                        continue;

                    _services.Add(Clone(service));
                    added++;
                }

                if (added > 0)
                    Save();
            }

            if (added > 0)
                Changed?.Invoke();
            return added;
        }

        public bool Remove(Guid id)
        {
            bool removed;
            lock (_gate)
            {
                EnsureLoaded();
                removed = _services.RemoveAll(s => s.Id == id) > 0;
                if (removed)
                    Save();
            }

            if (removed)
                Changed?.Invoke();
            return removed;
        }

        public static string? Validate(HomelabService service)
        {
            if (!Uri.TryCreate(service.Url?.Trim(), UriKind.Absolute, out Uri? uri) ||
                !(uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) ||
                uri.Host.Length == 0)
            {
                return Tr("L’adresse doit commencer par http:// ou https://.");
            }

            return null;
        }

        private void EnsureLoaded()
        {
            string path = _pathProvider();
            if (string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase))
                return;

            _loadedPath = path;
            _services = new List<HomelabService>();

            try
            {
                if (File.Exists(path))
                    _services = JsonSerializer.Deserialize<List<HomelabService>>(File.ReadAllText(path)) ?? new List<HomelabService>();
            }
            catch
            {
                _services = new List<HomelabService>();
            }
        }

        private void Save()
        {
            if (_loadedPath == null)
                return;

            try
            {
                AtomicFile.WriteAllText(_loadedPath, JsonSerializer.Serialize(_services, JsonOptions));
            }
            catch
            {
            }
        }

        private static HomelabService Clone(HomelabService service) => new()
        {
            Id = service.Id,
            Name = service.Name,
            Url = service.Url,
            Group = service.Group,
            Monitor = service.Monitor,
            CreatedAt = service.CreatedAt
        };
    }
}
