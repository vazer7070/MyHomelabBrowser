using System;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using MyHomelabBrowser.classes;
using Velopack;
using Velopack.Sources;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Updates
{
    /// <summary>
    /// Windows : mises à jour Velopack depuis les versions publiées sur GitHub, comme l'édition WPF.
    /// La nouvelle version est téléchargée en arrière-plan, puis installée à la fermeture ou au
    /// redémarrage demandé par l'utilisateur.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class VelopackUpdater : IUpdater
    {
        readonly UpdateManager _manager = new(new GithubSource("https://github.com/vazer7070/PommeBrowser-release", null, prerelease: false));
        UpdateInfo? _pending;
        bool _busy;

        public string Status { get; private set; } = string.Empty;
        public bool IsBusy => _busy;

        /// <summary>Faux depuis les sources ou un dossier non installé par Velopack.</summary>
        public bool CanUpdate => _manager.IsInstalled;
        public Version? Installed { get; private set; }
        public string? ReleasePage => CanUpdate ? null : "https://github.com/vazer7070/PommeBrowser-release/releases/latest";

        public event Action? Changed;

        public async Task CheckAsync()
        {
            if (_busy || Installed != null)
                return;
            if (!CanUpdate)
            {
                SetStatus(Tr("Les mises à jour automatiques ne concernent que la version installée."));
                return;
            }

            _busy = true;
            SetStatus(Tr("Recherche de mises à jour…"));
            try
            {
                UpdateInfo? info = await _manager.CheckForUpdatesAsync();
                if (info == null)
                {
                    SetStatus(Tr("PommeBrowser est à jour (version {0}).", MyHomelabBrowser.AppVersion.Current));
                    return;
                }

                string version = info.TargetFullRelease.Version.ToString();
                SetStatus(Tr("Téléchargement de la version {0}…", version));
                await _manager.DownloadUpdatesAsync(info);
                _pending = info;
                // Installée à la fermeture si l'utilisateur ne redémarre pas avant.
                _manager.WaitExitThenApplyUpdates(info.TargetFullRelease, silent: true, restart: false);
                Installed = Version.TryParse(version.Split('-')[0], out Version? parsed) ? parsed : new Version(0, 0);
                SetStatus(Tr("La version {0} est installée : elle s'appliquera au prochain démarrage.", version));
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[Mise à jour] " + ex.Message);
                SetStatus(Tr("Impossible de vérifier les mises à jour : {0}", ex.Message));
            }
            finally
            {
                _busy = false;
                Changed?.Invoke();
            }
        }

        /// <summary>Installe tout de suite et relance PommeBrowser.</summary>
        public bool ApplyAndRestart()
        {
            if (_pending == null)
                return false;
            _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
            return true;
        }

        void SetStatus(string status)
        {
            Status = status;
            Changed?.Invoke();
        }

        public void Dispose()
        {
        }
    }
}
