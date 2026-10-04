using System;
using System.Reflection;
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
    /// La nouvelle version est téléchargée en arrière-plan, puis installée au redémarrage demandé
    /// par l'utilisateur ou une fois PommeBrowser fermé (<see cref="ApplyOnExit"/>). Jamais pendant
    /// qu'il tourne : l'outil de Velopack n'attend la fin du processus que 60 s, puis arrête de
    /// force tous ceux du dossier de l'application (PommeBrowser et ses lecteurs Flash compris).
    /// </summary>
    [SupportedOSPlatform("windows")]
    public sealed class VelopackUpdater : IUpdater
    {
        readonly UpdateManager _manager = new(new GithubSource("https://github.com/vazer7070/PommeBrowser-release", null, prerelease: false));
        UpdateInfo? _pending;
        bool _busy;

        public string Status { get; private set; } = string.Empty;
        public bool IsBusy => _busy;

        /// <summary>
        /// Faux depuis les sources, un dossier non installé par Velopack, ou une compilation de test
        /// copiée dans une installation (voir <see cref="TestBuildNote"/>).
        /// </summary>
        public bool CanUpdate => _manager.IsInstalled && TestBuildNote == null;

        /// <summary>
        /// Version de cette compilation, donnée à la publication (-p:Version), sans l'identifiant
        /// du commit.
        /// </summary>
        static string BuildVersion
            => (typeof(VelopackUpdater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? string.Empty)
                .Split('+')[0];

        /// <summary>
        /// Compilation de test (artefact de la CI…) copiée dans le dossier d'une installation : la
        /// version que Velopack connaît n'est pas la sienne. Sans mise à jour automatique, sinon elle
        /// serait remplacée par la dernière version publiée. Null pour une installation normale.
        /// </summary>
        public string? TestBuildNote
        {
            get
            {
                try
                {
                    if (!_manager.IsInstalled || _manager.CurrentVersion is not { } installed)
                        return null;
                    string known = installed.ToNormalizedString();
                    return string.Equals(known, BuildVersion, StringComparison.OrdinalIgnoreCase)
                        ? null
                        : Tr("Compilation de test ({0}) placée dans l'installation {1} : mises à jour automatiques désactivées, pour ne pas la remplacer.", BuildVersion, known);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or NotSupportedException)
                {
                    return null;
                }
            }
        }
        public Version? Installed { get; private set; }
        public string? ReleasePage => CanUpdate ? null : "https://github.com/vazer7070/PommeBrowser-release/releases/latest";

        public event Action? Changed;

        public async Task CheckAsync()
        {
            if (_busy || Installed != null)
                return;
            if (!CanUpdate)
            {
                SetStatus(TestBuildNote ?? Tr("Les mises à jour automatiques ne concernent que la version installée."));
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
                // Installée au redémarrage, ou une fois PommeBrowser fermé (ApplyOnExit) : pas maintenant.
                _pending = info;
                Installed = Version.TryParse(version.Split('-')[0], out Version? parsed) ? parsed : new Version(0, 0);
                SetStatus(Tr("La version {0} est téléchargée : elle s'installera à la fermeture de PommeBrowser.", version));
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

        /// <summary>
        /// Fin du processus (fermeture normale, pas une relance) : la version téléchargée est
        /// installée par Velopack, qui attend que PommeBrowser soit terminé.
        /// </summary>
        public void ApplyOnExit()
        {
            if (_pending is not { } pending)
                return;
            _pending = null;
            try
            {
                _manager.WaitExitThenApplyUpdates(pending.TargetFullRelease, silent: true, restart: false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.IO.IOException or System.ComponentModel.Win32Exception)
            {
                RuntimeLogBuffer.Append("[Mise à jour] " + ex.Message);
            }
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
