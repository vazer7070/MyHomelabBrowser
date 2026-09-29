using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using PommeBrowser.Engine.Gtk;

namespace PommeBrowser.Engine
{
    public enum EngineKind
    {
        None,
        WebKitGtk,
        WebView2,
        WebKitApple
    }

    /// <summary>Réglages communs à tous les onglets, lus depuis le fil du moteur : jamais modifiés, remplacés.</summary>
    public sealed record EngineSettings
    {
        /// <summary>Langues envoyées aux sites (Accept-Language), par ordre de préférence.</summary>
        public IReadOnlyList<string> Languages { get; init; } = new[] { "fr-FR", "fr", "en-US", "en" };

        /// <summary>Dictionnaires du correcteur orthographique (fr_FR, en_US…).</summary>
        public IReadOnlyList<string> SpellCheckingLanguages { get; init; } = new[] { "fr_FR" };

        /// <summary>Protection contre le pistage intégrée au moteur (ITP de WebKit, prévention stricte de WebView2).</summary>
        public bool TrackingPrevention { get; init; } = true;

        public bool BlockThirdPartyCookies { get; init; } = true;

        /// <summary>Dossier où les téléchargements sont enregistrés.</summary>
        public string DownloadDirectory { get; init; } = string.Empty;

        /// <summary>Base des cookies du profil (WebKitGTK ; ailleurs, le moteur range ses cookies lui-même).</summary>
        public string? CookieDatabase { get; init; }

        /// <summary>Dossier des données WebView2 du profil (Windows).</summary>
        public string? WebView2UserDataFolder { get; init; }

        /// <summary>Identifiant du magasin de données WebKit du profil (macOS).</summary>
        public Guid? AppleDataStoreId { get; init; }
    }

    /// <summary>
    /// Point d'entrée du moteur web du système : prépare les vues (données du profil, navigation
    /// privée), crée l'adaptateur de chaque onglet et regroupe ce qui concerne toute la session
    /// (téléchargements, effacement des données, filtre anti-pub).
    /// </summary>
    public static class EngineHost
    {
        static EngineSettings _settings = new();

        public static EngineKind Kind { get; } =
            OperatingSystem.IsLinux() ? EngineKind.WebKitGtk :
            OperatingSystem.IsWindows() ? EngineKind.WebView2 :
            OperatingSystem.IsMacOS() ? EngineKind.WebKitApple :
            EngineKind.None;

        public static EngineSettings Settings => _settings;

        /// <summary>
        /// Le filtre anti-pub s'applique-t-il à cette page ? Appelé depuis le fil du moteur,
        /// au début de chaque chargement : doit être rapide et sans état partagé modifiable.
        /// </summary>
        public static Func<string?, bool>? ContentFilterPolicy { get; set; }

        /// <summary>Adresse d'où la page charge les fichiers de Ruffle (servis par PommeBrowser).</summary>
        public static string RuffleBaseUrl => Kind switch
        {
            EngineKind.WebKitGtk => GtkEngine.RuffleBaseUrl,
            _ => "https://ruffle.pommebrowser.invalid/"
        };

        /// <summary>
        /// Instruction JavaScript qui envoie <paramref name="value"/> (expression) à PommeBrowser
        /// sur le canal <paramref name="channel"/> (voir IEngineTab.ScriptMessage).
        /// </summary>
        public static string ScriptPost(string channel, string value) => Kind switch
        {
            EngineKind.WebView2 => "window.chrome?.webview?.postMessage({ channel: '" + channel + "', body: String(" + value + ") })",
            _ => "window.webkit?.messageHandlers?." + channel + "?.postMessage(" + value + ")"
        };

        /// <summary>Téléchargement lancé par une page (fil de l'interface).</summary>
        public static event Action<EngineDownload>? DownloadStarted;

        /// <summary>Applique de nouveaux réglages à toutes les sessions ouvertes.</summary>
        public static void Configure(EngineSettings settings)
        {
            _settings = settings;
            if (Kind == EngineKind.WebKitGtk)
                GtkEngine.ApplySettings();
        }

        /// <summary>À appeler avant l'insertion de la vue dans la fenêtre : données du profil ou navigation privée.</summary>
        public static void Prepare(NativeWebView view, bool isPrivate)
        {
            view.EnvironmentRequested += (_, args) =>
            {
                switch (args)
                {
                    // Même moteur partout sous Linux : WebKitGTK (WPE, s'il est installé, n'est pas utilisé).
                    case LinuxWpeWebViewEnvironmentRequestedEventArgs wpe:
                        wpe.PreferWebKitGtkInstead = true;
                        break;

                    case GtkWebViewEnvironmentRequestedEventArgs gtk:
                        // Session du profil : contexte par défaut, dont les dossiers suivent le nom
                        // du programme (voir Program). Navigation privée : session en mémoire seulement.
                        gtk.EphemeralDataManager = isPrivate;
                        gtk.EnableDevTools = true;
                        break;

                    case WindowsWebView2EnvironmentRequestedEventArgs webView2:
                        webView2.UserDataFolder = _settings.WebView2UserDataFolder;
                        webView2.IsInPrivateModeEnabled = isPrivate;
                        webView2.Language = _settings.Languages.Count > 0 ? _settings.Languages[0] : null;
                        break;

                    case AppleWKWebViewEnvironmentRequestedEventArgs apple:
                        apple.NonPersistentDataStore = isPrivate;
                        if (!isPrivate && _settings.AppleDataStoreId is Guid id)
                            apple.DataStoreIdentifier = id;
                        break;
                }
            };
        }

        /// <summary>Adaptateur de l'onglet, une fois la vue native créée (événement AdapterCreated).</summary>
        public static IEngineTab? Attach(NativeWebView view, bool isPrivate)
            => view.TryGetPlatformHandle() switch
            {
                IGtkWebViewPlatformHandle gtk => new GtkEngineTab(view, gtk.WebKitWebView, isPrivate),
                _ => null
            };

        /// <summary>
        /// Efface les données des sites du profil depuis <paramref name="since"/> (null : tout) :
        /// cookies et stockage, et/ou cache.
        /// </summary>
        public static Task ClearBrowsingDataAsync(TimeSpan? since, bool cookiesAndSiteData, bool cache)
            => Kind == EngineKind.WebKitGtk ? GtkEngine.ClearDataAsync(since, cookiesAndSiteData, cache) : Task.CompletedTask;

        /// <summary>
        /// Rend le clavier à la fenêtre (avant de donner le focus à un champ d'Avalonia) quand la
        /// vue native l'a gardé.
        /// </summary>
        public static void ReclaimKeyboard(TopLevel topLevel)
        {
            if (Kind == EngineKind.WebKitGtk)
                X11Focus.TakeFocus(topLevel);
        }

        /// <summary>Nom et version du moteur (diagnostic, rapports).</summary>
        public static string Describe() => Kind switch
        {
            EngineKind.WebKitGtk => "WebKitGTK " + (GtkEngine.Version() ?? "?"),
            EngineKind.WebView2 => "WebView2 " + (WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WebView2).Version ?? "?"),
            EngineKind.WebKitApple => "WKWebView " + (WebViewAdapterInfo.GetAdapterInfo(WebViewAdapterType.WkWebView).Version ?? "?"),
            _ => "?"
        };

        /// <summary>Processus du moteur lancés par PommeBrowser (PID), pour la mémoire utilisée.</summary>
        public static IReadOnlyList<int> Processes()
            => Kind == EngineKind.WebKitGtk ? GtkEngine.ChildProcesses() : Array.Empty<int>();

        internal static void RaiseDownloadStarted(EngineDownload download)
            => Dispatcher.UIThread.Post(() => DownloadStarted?.Invoke(download));
    }
}
