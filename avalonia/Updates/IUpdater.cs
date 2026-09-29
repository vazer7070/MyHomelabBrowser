using System;
using System.Threading.Tasks;

namespace PommeBrowser.Updates
{
    /// <summary>Mises à jour de PommeBrowser, selon le système et le mode d'installation.</summary>
    public interface IUpdater : IDisposable
    {
        /// <summary>Dernier état, en clair.</summary>
        string Status { get; }

        bool IsBusy { get; }

        /// <summary>Installation automatique possible (AppImage, installation Velopack).</summary>
        bool CanUpdate { get; }

        /// <summary>Version installée, prise en compte au redémarrage (null : rien en attente).</summary>
        Version? Installed { get; }

        /// <summary>Page de la version disponible, quand elle ne peut pas être installée automatiquement.</summary>
        string? ReleasePage { get; }

        event Action? Changed;

        Task CheckAsync();
    }

    /// <summary>Système sans mise à jour intégrée (macOS, exécution depuis les sources).</summary>
    public sealed class NoUpdater : IUpdater
    {
        public string Status => MyHomelabBrowser.classes.Localization.Loc.Tr("Les mises à jour automatiques ne sont pas disponibles pour cette installation.");
        public bool IsBusy => false;
        public bool CanUpdate => false;
        public Version? Installed => null;
        public string? ReleasePage => "https://github.com/vazer7070/PommeBrowser-release/releases/latest";

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public Task CheckAsync() => Task.CompletedTask;

        public void Dispose()
        {
        }
    }
}
