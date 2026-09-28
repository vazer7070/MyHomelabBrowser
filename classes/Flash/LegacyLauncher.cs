using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace MyHomelabBrowser
{
    /// <summary>
    /// Lance Basilisk pour un onglet Legacy : profil dédié durci, arguments passés un
    /// par un, processus enfermé dans un job Windows (voir LegacyProcess).
    /// </summary>
    public class LegacyLauncher
    {
        // Pas de fenêtre de rapport de plantage ni d'envoi à Mozilla.
        static readonly IReadOnlyDictionary<string, string> LaunchEnvironment = new Dictionary<string, string>
        {
            ["MOZ_CRASHREPORTER_DISABLE"] = "1",
            ["MOZ_CRASHREPORTER_NO_REPORT"] = "1",
            ["MOZ_NO_REMOTE"] = "1"
        };

        private readonly SettingsService _settings;
        public event Action<string>? OnDebug;
        private void Dbg(string msg) => OnDebug?.Invoke(msg);

        public LegacyLauncher(SettingsService settings)
        {
            _settings = settings;
        }

        public bool CanLaunch()
        {
            var s = _settings.Settings;
            return s.EnableFlashSupport && BasiliskExecutable.IsLaunchable(s.BasiliskPath);
        }

        public LegacyProcess? Launch(string url, string profileDir, bool isPrivate)
        {
            if (!CanLaunch())
                return null;

            if (string.IsNullOrWhiteSpace(profileDir))
                throw new ArgumentException(nameof(profileDir));

            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("Adresse Legacy invalide : " + url, nameof(url));
            }

            LegacyProfilePreferences.Apply(profileDir, isPrivate);
            Dbg("[LegacyProfile] Réglages appliqués : " + profileDir);

            string exe = Path.GetFullPath(_settings.Settings.BasiliskPath!);
            var arguments = new List<string> { "-new-instance", "-no-remote", "-profile", profileDir, uri.AbsoluteUri };

            LegacyProcess process = LegacyProcess.Start(exe, arguments, LaunchEnvironment);
            Dbg($"[Legacy] Basilisk lancé (PID {process.Id}) : {uri.Host}");
            return process;
        }
    }

    /// <summary>Vérification du chemin de Basilisk choisi dans les paramètres.</summary>
    public static class BasiliskExecutable
    {
        public static bool IsLaunchable(string? path)
            => !string.IsNullOrWhiteSpace(path) &&
               string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) &&
               File.Exists(path);

        /// <summary>Produit et version lus dans l'exécutable, pour les afficher dans les paramètres.</summary>
        public static (string? Product, string? Version) Describe(string? path)
        {
            if (!IsLaunchable(path))
                return (null, null);

            try
            {
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(path!);
                string? product = string.IsNullOrWhiteSpace(info.ProductName) ? null : info.ProductName.Trim();
                string? version = string.IsNullOrWhiteSpace(info.ProductVersion) ? null : info.ProductVersion.Trim();
                return (product, version);
            }
            catch
            {
                return (null, null);
            }
        }

        /// <summary>Navigateur de la famille Gecko/UXP capable de lancer le plugin Flash NPAPI.</summary>
        public static bool LooksLikeUxpBrowser(string? product)
            => product != null &&
               (product.Contains("Basilisk", StringComparison.OrdinalIgnoreCase) ||
                product.Contains("Pale Moon", StringComparison.OrdinalIgnoreCase) ||
                product.Contains("Serpent", StringComparison.OrdinalIgnoreCase) ||
                product.Contains("Waterfox", StringComparison.OrdinalIgnoreCase) ||
                product.Contains("Firefox", StringComparison.OrdinalIgnoreCase));
    }
}
