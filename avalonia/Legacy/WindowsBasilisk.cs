using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;
using Avalonia.Threading;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;

namespace PommeBrowser.Legacy
{
    /// <summary>
    /// Basilisk sous Windows, comme dans l'édition WPF : profil propre au site (jetable en
    /// navigation privée), réglages durcis, processus enfermé dans un job Windows qui disparaît
    /// avec PommeBrowser (voir LegacyProcess).
    /// </summary>
    [SupportedOSPlatform("windows")]
    sealed class WindowsBasilisk : ILegacyBrowser
    {
        /// <summary>
        /// Ni rapport de plantage ni instance partagée. Le module Flash est cherché aussi dans le
        /// dossier des modules de PommeBrowser (MOZ_PLUGIN_PATH, lu par UXP sous Windows comme
        /// sous Linux).
        /// </summary>
        static IReadOnlyDictionary<string, string> LaunchEnvironment() => new Dictionary<string, string>
        {
            ["MOZ_CRASHREPORTER_DISABLE"] = "1",
            ["MOZ_CRASHREPORTER_NO_REPORT"] = "1",
            ["MOZ_NO_REMOTE"] = "1",
            ["MOZ_PLUGIN_PATH"] = LegacyEngine.PluginDirectory
        };

        readonly LegacyProcess _process;
        readonly LegacyProfileLease _lease;
        readonly DispatcherTimer _watch;
        bool _closed;

        WindowsBasilisk(LegacyProcess process, LegacyProfileLease lease)
        {
            _process = process;
            _lease = lease;
            // Le job Windows ne signale pas sa fin : vérification chaque seconde.
            _watch = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) =>
            {
                if (!_process.IsRunning)
                    OnExited();
            });
            _watch.Start();
        }

        public event Action? Exited;

        public static WindowsBasilisk Start(string executable, Uri url, bool isPrivate, bool embedded)
        {
            LegacyProfileLease lease = LegacyProfileManager.CreateLease(url.Host, isPrivate);
            try
            {
                LegacyProfilePreferences.Apply(lease.ProfilePath, isPrivate, embedded);
                var arguments = new List<string> { "-new-instance", "-no-remote", "-profile", lease.ProfilePath, url.AbsoluteUri };
                LegacyProcess process = LegacyProcess.Start(Path.GetFullPath(executable), arguments, LaunchEnvironment());
                RuntimeLogBuffer.Append($"[Basilisk] Lancé (PID {process.Id}) : {url.Host}");
                return new WindowsBasilisk(process, lease);
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        public bool HasExited => !_process.IsRunning;

        public IEnumerable<int> ProcessIds => new[] { _process.Id };

        /// <summary>Fenêtre de navigateur de Basilisk (la plus grande, classe MozillaWindowClass).</summary>
        public nint FindWindow() => _process.FindMainWindow().Window;

        public void SetBackground(bool background) => _process.SetBackground(background);

        void OnExited()
        {
            _watch.Stop();
            _process.Dispose();
            _lease.Dispose();
            Exited?.Invoke();
        }

        public async void Close()
        {
            if (_closed)
                return;
            _closed = true;
            try
            {
                await _process.CloseAsync(TimeSpan.FromSeconds(1.5));
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[Basilisk] " + ex.Message);
            }
            OnExited();
        }

        public static void CloseAll() => LegacyProcess.CloseAll(TimeSpan.FromSeconds(1.5));
    }
}
