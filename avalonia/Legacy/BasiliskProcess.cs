using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using PommeBrowser.Core;
using PommeBrowser.Linux.Core;

namespace PommeBrowser.Legacy
{
    /// <summary>
    /// Basilisk lancé pour un onglet : profil propre au site (jetable en navigation privée),
    /// réglages durcis communs avec l'édition Windows. Si PommeBrowser s'arrête brutalement,
    /// Basilisk est arrêté aussi (setpriv --pdeathsig, présent sur la plupart des distributions).
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    sealed class BasiliskProcess : ILegacyBrowser
    {
        static readonly HashSet<BasiliskProcess> Running = new();

        readonly Process _process;
        readonly LegacyProfileLease _lease;
        bool _closed;

        BasiliskProcess(Process process, LegacyProfileLease lease)
        {
            _process = process;
            _lease = lease;
        }

        /// <summary>Basilisk s'est fermé (fenêtre fermée par l'utilisateur, ou arrêt demandé). Sur le fil de l'interface.</summary>
        public event Action? Exited;

        public int Id => _process.Id;

        public bool HasExited
        {
            get
            {
                try
                {
                    return _process.HasExited;
                }
                catch (InvalidOperationException)
                {
                    return true;
                }
            }
        }

        /// <summary>
        /// Lance Basilisk. À appeler depuis le fil de l'interface : --pdeathsig vise le fil qui crée
        /// le processus, et celui-ci vit aussi longtemps que PommeBrowser.
        /// </summary>
        public static BasiliskProcess Start(string executable, Uri url, bool isPrivate, bool embedded)
        {
            LegacyProfileLease lease = LegacyProfileManager.CreateLease(url.IdnHost, isPrivate);
            try
            {
                LegacyProfilePreferences.Apply(lease.ProfilePath, isPrivate, embedded);

                string pluginDirectory = LegacyEngine.PluginDirectory;
                Directory.CreateDirectory(pluginDirectory);

                string? setpriv = new[] { "/usr/bin/setpriv", "/bin/setpriv" }.FirstOrDefault(File.Exists);
                var start = new ProcessStartInfo(setpriv ?? executable) { UseShellExecute = false };
                if (setpriv != null)
                {
                    foreach (string argument in new[] { "--pdeathsig", "TERM", "--", executable })
                        start.ArgumentList.Add(argument);
                }
                foreach (string argument in BasiliskInstall.Arguments(lease.ProfilePath, url))
                    start.ArgumentList.Add(argument);
                foreach ((string name, string value) in BasiliskInstall.Environment(pluginDirectory, System.Environment.GetEnvironmentVariable("MOZ_PLUGIN_PATH")))
                    start.Environment[name] = value;

                Process process = Process.Start(start) ?? throw new InvalidOperationException("Basilisk");
                var basilisk = new BasiliskProcess(process, lease);
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(basilisk.OnExited);
                Running.Add(basilisk);
                RuntimeLogBuffer.Append($"[Basilisk] Lancé (PID {process.Id}) : {url.Host}");
                return basilisk;
            }
            catch
            {
                lease.Dispose();
                throw;
            }
        }

        public IEnumerable<int> ProcessIds => new[] { _process.Id };

        /// <summary>Sous Linux, la fenêtre est cherchée par la connexion X11 de l'intégration (voir LegacyView).</summary>
        public nint FindWindow() => 0;

        /// <summary>
        /// Sans effet sous Linux : un processus peut baisser sa priorité, mais pas la remonter
        /// sans droits particuliers, et l'onglet redevenu actif resterait ralenti.
        /// </summary>
        public void SetBackground(bool background)
        {
        }

        void OnExited()
        {
            Running.Remove(this);
            _lease.Dispose();
            _process.Dispose();
            Exited?.Invoke();
        }

        [DllImport("libc", SetLastError = true)]
        static extern int kill(int pid, int signal);

        const int SigTerm = 15;

        /// <summary>Fermeture douce (comme la fermeture de la fenêtre), puis forcée après 1,5 s.</summary>
        public void Close()
        {
            if (_closed || HasExited)
                return;
            _closed = true;

            kill(_process.Id, SigTerm);
            Process process = _process;
            Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Déjà terminé.
                }
            }, TimeSpan.FromMilliseconds(1500));
        }

        /// <summary>Arrêt de PommeBrowser : tous les Basilisk lancés par lui se ferment (doucement, 1,5 s au plus).</summary>
        public static void CloseAll()
        {
            List<BasiliskProcess> all = Running.ToList();
            Running.Clear();
            foreach (BasiliskProcess basilisk in all.Where(b => !b.HasExited))
                kill(basilisk.Id, SigTerm);

            DateTime deadline = DateTime.UtcNow.AddMilliseconds(1500);
            foreach (BasiliskProcess basilisk in all)
            {
                try
                {
                    int left = (int)Math.Max(0, (deadline - DateTime.UtcNow).TotalMilliseconds);
                    if (!basilisk._process.WaitForExit(left))
                        basilisk._process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Déjà terminé.
                }
                basilisk._lease.Dispose();
            }
        }
    }
}
