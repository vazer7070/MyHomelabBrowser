using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MyHomelabBrowser.classes.Workspaces
{
    public sealed class WorkspaceTab
    {
        public string Url { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public bool IsPinned { get; set; }
    }

    /// <summary>
    /// Ensemble d'onglets nommé (« Réseau », « Médias »…) que l'on rouvre d'un clic.
    /// </summary>
    public sealed class Workspace
    {
        public Guid Id { get; set; } = Guid.NewGuid();
        public string Name { get; set; } = string.Empty;
        public List<WorkspaceTab> Tabs { get; set; } = new();
        public DateTime UpdatedAt { get; set; } = DateTime.Now;
    }

    public sealed class WorkspaceStore
    {
        public const int MaxTabsPerWorkspace = 60;

        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly Func<string> _pathProvider;
        private readonly object _gate = new();
        private List<Workspace> _workspaces = new();
        private string? _loadedPath;

        public WorkspaceStore(Func<string> pathProvider)
        {
            _pathProvider = pathProvider;
        }

        public IReadOnlyList<Workspace> GetAll()
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _workspaces
                    .OrderBy(w => w.Name, StringComparer.CurrentCultureIgnoreCase)
                    .Select(Clone)
                    .ToList();
            }
        }

        /// <summary>
        /// Enregistre les onglets sous ce nom ; un espace du même nom est remplacé.
        /// </summary>
        public Workspace Save(string name, IEnumerable<WorkspaceTab> tabs)
        {
            string trimmed = (name ?? string.Empty).Trim();
            if (trimmed.Length == 0)
                throw new ArgumentException("Le nom de l’espace est obligatoire.", nameof(name));

            List<WorkspaceTab> list = tabs
                .Where(t => Uri.TryCreate(t.Url, UriKind.Absolute, out Uri? uri) && uri.Scheme is "http" or "https" or "file")
                .Take(MaxTabsPerWorkspace)
                .Select(t => new WorkspaceTab { Url = t.Url, Title = t.Title ?? string.Empty, IsPinned = t.IsPinned })
                .ToList();

            if (list.Count == 0)
                throw new ArgumentException("Aucun onglet web à enregistrer.", nameof(tabs));

            lock (_gate)
            {
                EnsureLoaded();
                Workspace? existing = _workspaces.FirstOrDefault(w => w.Name.Equals(trimmed, StringComparison.CurrentCultureIgnoreCase));
                if (existing == null)
                {
                    existing = new Workspace { Name = trimmed };
                    _workspaces.Add(existing);
                }

                existing.Tabs = list;
                existing.UpdatedAt = DateTime.Now;
                Save();
                return Clone(existing);
            }
        }

        public bool Remove(Guid id)
        {
            lock (_gate)
            {
                EnsureLoaded();
                bool removed = _workspaces.RemoveAll(w => w.Id == id) > 0;
                if (removed)
                    Save();
                return removed;
            }
        }

        private void EnsureLoaded()
        {
            string path = _pathProvider();
            if (string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase))
                return;

            _loadedPath = path;
            try
            {
                _workspaces = File.Exists(path)
                    ? JsonSerializer.Deserialize<List<Workspace>>(File.ReadAllText(path)) ?? new List<Workspace>()
                    : new List<Workspace>();
            }
            catch
            {
                _workspaces = new List<Workspace>();
            }
        }

        private void Save()
        {
            if (_loadedPath == null)
                return;

            try
            {
                AtomicFile.WriteAllText(_loadedPath, JsonSerializer.Serialize(_workspaces, JsonOptions));
            }
            catch
            {
            }
        }

        private static Workspace Clone(Workspace workspace) => new()
        {
            Id = workspace.Id,
            Name = workspace.Name,
            UpdatedAt = workspace.UpdatedAt,
            Tabs = workspace.Tabs.Select(t => new WorkspaceTab { Url = t.Url, Title = t.Title, IsPinned = t.IsPinned }).ToList()
        };
    }
}
