using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;
using MyHomelabBrowser.classes;
using PommeBrowser.Linux.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Engine.WebView2
{
    /// <summary>
    /// Sessions WebView2 (Windows) : un environnement par profil, créé par le contrôle d'Avalonia
    /// avec les options de PommeBrowser (dossier du profil, langue, DNS sécurisé), et un profil
    /// en mémoire pour la navigation privée. Tout se passe sur le fil de l'interface.
    /// </summary>
    [SupportedOSPlatform("windows")]
    static class WebView2Engine
    {
        /// <summary>Profil de la navigation privée (même nom que l'édition WPF).</summary>
        public const string PrivateProfileName = "PommeInPrivate";

        /// <summary>Certificats acceptés par l'utilisateur pour cette session (hôte et empreinte SHA-256).</summary>
        static readonly HashSet<string> AllowedCertificates = new(StringComparer.OrdinalIgnoreCase);

        static readonly List<WebView2EngineTab> Tabs = new();

        public static void Register(WebView2EngineTab tab) => Tabs.Add(tab);

        public static void Unregister(WebView2EngineTab tab) => Tabs.Remove(tab);

        public static string CertificateKey(string host, string sha256) => host + "|" + sha256;

        public static void AllowCertificate(string key) => AllowedCertificates.Add(key);

        public static bool IsCertificateAllowed(string key) => AllowedCertificates.Contains(key);

        /// <summary>Réglages modifiés (pistage, thème des pages) : appliqués aux onglets ouverts.</summary>
        public static void ApplySettings()
        {
            foreach (WebView2EngineTab tab in Tabs.ToArray())
                tab.ApplyProfileSettings();
        }

        /// <summary>Réglages du profil d'une vue (partagés par tous les onglets du profil).</summary>
        public static void ApplyProfileSettings(CoreWebView2 core)
        {
            EngineSettings settings = EngineHost.Settings;
            try
            {
                core.Profile.PreferredTrackingPreventionLevel = settings.TrackingLevel switch
                {
                    BrowserSettings.TrackingProtection.Off => CoreWebView2TrackingPreventionLevel.None,
                    BrowserSettings.TrackingProtection.Basic => CoreWebView2TrackingPreventionLevel.Basic,
                    BrowserSettings.TrackingProtection.Strict => CoreWebView2TrackingPreventionLevel.Strict,
                    _ => CoreWebView2TrackingPreventionLevel.Balanced
                };
                core.Profile.PreferredColorScheme = settings.DarkPages switch
                {
                    true => CoreWebView2PreferredColorScheme.Dark,
                    false => CoreWebView2PreferredColorScheme.Light,
                    _ => CoreWebView2PreferredColorScheme.Auto
                };
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotImplementedException or System.Runtime.InteropServices.COMException)
            {
                // Moteur WebView2 trop ancien pour ces réglages : les valeurs par défaut restent.
                RuntimeLogBuffer.Append("[WebView2] " + ex.Message);
            }
        }

        /// <summary>
        /// Données des sites du profil depuis <paramref name="since"/> (null : tout). WebView2 les
        /// efface à travers une vue ouverte du profil ; sans onglet ouvert, il n'y a rien à faire
        /// tout de suite et la demande est ignorée.
        /// </summary>
        public static async Task ClearDataAsync(TimeSpan? since, bool cookiesAndSiteData, bool cache)
        {
            CoreWebView2BrowsingDataKinds kinds = 0;
            if (cache)
                kinds |= CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage;
            if (cookiesAndSiteData)
                kinds |= CoreWebView2BrowsingDataKinds.AllSite & ~(CoreWebView2BrowsingDataKinds.DiskCache | CoreWebView2BrowsingDataKinds.CacheStorage);
            if (kinds == 0)
                return;

            CoreWebView2Profile? profile = Tabs.FirstOrDefault(t => !t.IsPrivate)?.Profile;
            if (profile == null)
            {
                RuntimeLogBuffer.Append("[WebView2] Effacement des données : aucun onglet ouvert.");
                return;
            }

            if (since is { } span)
                await profile.ClearBrowsingDataAsync(kinds, DateTime.Now - span, DateTime.Now);
            else
                await profile.ClearBrowsingDataAsync(kinds);
        }

        /// <summary>Processus de WebView2 (navigateur, pages, GPU…) de la session.</summary>
        public static IReadOnlyList<int> Processes()
        {
            var result = new HashSet<int>();
            foreach (WebView2EngineTab tab in Tabs.ToArray())
            {
                try
                {
                    foreach (CoreWebView2ProcessInfo info in tab.Environment?.GetProcessInfos() ?? (IReadOnlyList<CoreWebView2ProcessInfo>)Array.Empty<CoreWebView2ProcessInfo>())
                        result.Add(info.ProcessId);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                }
            }
            return result.ToList();
        }

        /// <summary>
        /// Téléchargement demandé par une page : enregistré sans boîte de dialogue dans le dossier des
        /// téléchargements, jamais par-dessus un fichier existant, et suivi dans la liste de PommeBrowser.
        /// </summary>
        public static void Track(CoreWebView2DownloadStartingEventArgs e, bool isPrivate)
        {
            string directory = EngineHost.Settings.DownloadDirectory;
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RuntimeLogBuffer.Append("[Téléchargements] " + ex.Message);
            }

            CoreWebView2DownloadOperation operation = e.DownloadOperation;
            string path = DownloadNames.UniquePath(directory, Path.GetFileName(e.ResultFilePath));
            e.ResultFilePath = path;
            e.Handled = true;

            var info = new EngineDownload(operation.Uri, Path.GetFileName(path), () =>
            {
                try
                {
                    operation.Cancel();
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.Runtime.InteropServices.COMException)
                {
                }
            })
            {
                Destination = path,
                IsPrivate = isPrivate,
                TotalBytes = (long)(operation.TotalBytesToReceive ?? 0)
            };

            DateTime lastNotified = DateTime.MinValue;
            operation.BytesReceivedChanged += (_, _) =>
            {
                // Au plus quatre mises à jour par seconde.
                if (DateTime.UtcNow - lastNotified < TimeSpan.FromMilliseconds(250))
                    return;
                lastNotified = DateTime.UtcNow;
                info.ReceivedBytes = operation.BytesReceived;
                info.TotalBytes = (long)(operation.TotalBytesToReceive ?? 0);
                info.RaiseChanged();
            };
            operation.StateChanged += (_, _) =>
            {
                switch (operation.State)
                {
                    case CoreWebView2DownloadState.Completed:
                        info.ReceivedBytes = operation.BytesReceived;
                        info.TotalBytes = operation.BytesReceived;
                        info.IsFinished = true;
                        break;
                    case CoreWebView2DownloadState.Interrupted:
                        info.IsCancelled = operation.InterruptReason == CoreWebView2DownloadInterruptReason.UserCanceled;
                        info.Error = info.IsCancelled ? null : DescribeInterruption(operation.InterruptReason);
                        info.IsFinished = true;
                        break;
                    default:
                        return;
                }
                info.RaiseChanged();
            };

            EngineHost.RaiseDownloadStarted(info);
        }

        static string DescribeInterruption(CoreWebView2DownloadInterruptReason reason) => reason switch
        {
            CoreWebView2DownloadInterruptReason.FileNoSpace => Tr("Espace disque insuffisant."),
            CoreWebView2DownloadInterruptReason.FileAccessDenied => Tr("Accès au dossier refusé."),
            CoreWebView2DownloadInterruptReason.FileTooLarge => Tr("Fichier trop volumineux."),
            CoreWebView2DownloadInterruptReason.FileMalicious or CoreWebView2DownloadInterruptReason.FileBlockedByPolicy
                or CoreWebView2DownloadInterruptReason.FileSecurityCheckFailed
                => Tr("Fichier bloqué par la protection de Windows."),
            CoreWebView2DownloadInterruptReason.NetworkTimeout or CoreWebView2DownloadInterruptReason.NetworkDisconnected
                or CoreWebView2DownloadInterruptReason.NetworkFailed or CoreWebView2DownloadInterruptReason.NetworkServerDown
                => Tr("Connexion interrompue."),
            _ => reason.ToString()
        };
    }
}
