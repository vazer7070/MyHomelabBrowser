using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Platform;
using Avalonia.Threading;
using PommeBrowser.Engine.Gtk;
using static MyHomelabBrowser.classes.Localization.Loc;

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

        /// <summary>Niveau de protection contre le pistage (WebView2 : aucun, simple, équilibré, strict).</summary>
        public MyHomelabBrowser.classes.BrowserSettings.TrackingProtection TrackingLevel { get; init; } =
            MyHomelabBrowser.classes.BrowserSettings.TrackingProtection.Balanced;

        /// <summary>Thème demandé aux sites (prefers-color-scheme) : null pour suivre le système.</summary>
        public bool? DarkPages { get; init; }

        /// <summary>Arguments du moteur Chromium (WebView2) : DNS sécurisé…</summary>
        public string? BrowserArguments { get; init; }

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

        /// <summary>Moteur de règles des requêtes (WebView2 : le filtre est appliqué requête par requête).</summary>
        public static MyHomelabBrowser.classes.AdBlock.Services.AdBlockModuleService? RequestFilter { get; set; }

        /// <summary>Adresse d'où la page charge les fichiers de Ruffle (servis par PommeBrowser).</summary>
        public static string RuffleBaseUrl => Kind switch
        {
            EngineKind.WebKitGtk => GtkEngine.RuffleBaseUrl,
            // WKWebView : pas de schéma ajoutable à une vue déjà créée, fichiers servis en local.
            EngineKind.WebKitApple => RuffleServer.BaseUrl,
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
            else if (Kind == EngineKind.WebView2 && OperatingSystem.IsWindows())
                WebView2.WebView2Engine.ApplySettings();
            else if (Kind == EngineKind.WebKitApple && OperatingSystem.IsMacOS())
                Apple.AppleEngine.ApplySettings();
        }

        /// <summary>
        /// Profil quitté sans relancer PommeBrowser : ce que le moteur garde pour la session (certificats
        /// acceptés) est oublié. Les nouvelles vues ouvrent les données du nouveau profil (voir Configure).
        /// </summary>
        public static void ForgetSession()
        {
            if (Kind == EngineKind.WebView2 && OperatingSystem.IsWindows())
                WebView2.WebView2Engine.ForgetAllowedCertificates();
            else if (Kind == EngineKind.WebKitApple && OperatingSystem.IsMacOS())
                Apple.AppleEngine.ForgetAllowedCertificates();
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
                        // Même environnement pour tous les onglets du profil (mêmes options) ; la
                        // navigation privée a son profil en mémoire, comme dans l'édition WPF.
                        // Langue et arguments identiques à ceux de l'édition WPF, qui partage le
                        // dossier WebView2 du profil : WebView2 refuse d'ouvrir un dossier déjà
                        // utilisé avec d'autres options (les deux éditions ouvertes en même temps).
                        webView2.UserDataFolder = _settings.WebView2UserDataFolder;
                        webView2.IsInPrivateModeEnabled = isPrivate;
                        webView2.ProfileName = isPrivate && OperatingSystem.IsWindows() ? WebView2.WebView2Engine.PrivateProfileName : null;
                        webView2.Language = WebView2Language;
                        webView2.AdditionalBrowserArguments = _settings.BrowserArguments ?? string.Empty;
                        webView2.EnableDevTools = true;
                        break;

                    case AppleWKWebViewEnvironmentRequestedEventArgs apple:
                        apple.NonPersistentDataStore = isPrivate;
                        apple.EnableDevTools = true;
                        if (!isPrivate && _settings.AppleDataStoreId is Guid id)
                            apple.DataStoreIdentifier = id;
                        break;
                }
            };
        }

        /// <summary>
        /// Langue de WebView2 (menus, boîtes de dialogue, Accept-Language) : celle de l'édition WPF,
        /// en-US en anglais et celle de Windows en français.
        /// </summary>
        static string? WebView2Language => MyHomelabBrowser.classes.Localization.Loc.Language == "en" ? "en-US" : null;

        /// <summary>
        /// Erreur venue de la vue web elle-même (création du moteur, de sa fenêtre…), par exemple
        /// relancée par Avalonia après l'échec du démarrage de WebView2.
        /// </summary>
        public static bool IsWebViewFailure(Exception exception)
            => exception.ToString().Contains("WebView", StringComparison.OrdinalIgnoreCase);

        /// <summary>Explication d'un échec du moteur, avec son code pour les rapports.</summary>
        public static string DescribeFailure(Exception exception)
        {
            string message = exception.Message;
            string reason = exception.HResult switch
            {
                // HRESULT_FROM_WIN32(ERROR_INVALID_STATE) : dossier ouvert ailleurs avec d'autres options.
                unchecked((int)0x8007139F) => Tr("Le dossier de données WebView2 de ce profil est déjà utilisé avec d'autres réglages, probablement par l'édition classique de PommeBrowser encore ouverte. Fermez-la, puis réessayez."),
                unchecked((int)0x80070005) => Tr("Accès refusé au dossier de données WebView2 de ce profil."),
                _ when message.Contains("runtime not found", StringComparison.OrdinalIgnoreCase) || message.Contains("runtime is not installed", StringComparison.OrdinalIgnoreCase)
                    => Tr("Le moteur Microsoft Edge WebView2 n'est pas installé sur cet ordinateur. Installez « Microsoft Edge WebView2 Runtime », puis réessayez."),
                _ => Tr("Le moteur web de ce système n'a pas pu démarrer.")
            };
            return reason + "\n\n" + exception.GetType().Name + " (0x" + exception.HResult.ToString("X8") + ") : " + message;
        }

        /// <summary>Adaptateur de l'onglet, une fois la vue native créée (événement AdapterCreated).</summary>
        public static IEngineTab? Attach(NativeWebView view, bool isPrivate)
        {
            switch (view.TryGetPlatformHandle())
            {
                case IGtkWebViewPlatformHandle gtk:
                    return new GtkEngineTab(view, gtk.WebKitWebView, isPrivate);
                case IWindowsWebView2PlatformHandle webView2 when OperatingSystem.IsWindows():
                    return WebView2.WebView2EngineTab.Create(view, webView2, isPrivate);
                case IAppleWKWebViewPlatformHandle apple when OperatingSystem.IsMacOS():
                    return Apple.AppleEngineTab.Create(view, apple, isPrivate);
                default:
                    return null;
            }
        }

        /// <summary>
        /// Efface les données des sites du profil depuis <paramref name="since"/> (null : tout) :
        /// cookies et stockage, et/ou cache.
        /// </summary>
        public static Task ClearBrowsingDataAsync(TimeSpan? since, bool cookiesAndSiteData, bool cache)
        {
            if (Kind == EngineKind.WebKitGtk)
                return GtkEngine.ClearDataAsync(since, cookiesAndSiteData, cache);
            if (Kind == EngineKind.WebView2 && OperatingSystem.IsWindows())
                return WebView2.WebView2Engine.ClearDataAsync(since, cookiesAndSiteData, cache);
            if (Kind == EngineKind.WebKitApple && OperatingSystem.IsMacOS())
                return Apple.AppleEngine.ClearDataAsync(since, cookiesAndSiteData, cache);
            return Task.CompletedTask;
        }

        // ---------------------------------------------------------------
        // Filtre anti-pub compilé par WebKit (WebKitGTK et WKWebView : même format de règles)
        // ---------------------------------------------------------------

        /// <summary>Filtre déjà compilé (0 s'il n'existe pas ou si le moteur n'en a pas).</summary>
        public static Task<nint> LoadContentFilterAsync(string storeDirectory, string id)
        {
            if (Kind == EngineKind.WebKitGtk)
                return GtkEngine.LoadFilterAsync(storeDirectory, id);
            if (Kind == EngineKind.WebKitApple && OperatingSystem.IsMacOS())
                return Apple.AppleEngine.LoadFilterAsync(storeDirectory, id);
            return Task.FromResult<nint>(0);
        }

        /// <summary>Compile des règles (JSON de WebKit) et les garde en cache.</summary>
        public static Task<nint> CompileContentFilterAsync(string storeDirectory, string id, string json)
        {
            if (Kind == EngineKind.WebKitGtk)
                return GtkEngine.CompileFilterAsync(storeDirectory, id, System.Text.Encoding.UTF8.GetBytes(json));
            if (Kind == EngineKind.WebKitApple && OperatingSystem.IsMacOS())
                return Apple.AppleEngine.CompileFilterAsync(storeDirectory, id, json);
            return Task.FromResult<nint>(0);
        }

        /// <summary>Nouveau filtre actif : repris par tous les onglets.</summary>
        public static void SetContentFilter(nint filter)
        {
            if (Kind == EngineKind.WebKitGtk)
                GtkEngine.SetContentFilter(filter);
            else if (Kind == EngineKind.WebKitApple && OperatingSystem.IsMacOS())
                Apple.AppleEngine.SetContentFilter(filter);
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
        {
            if (Kind == EngineKind.WebKitGtk)
                return GtkEngine.ChildProcesses();
            if (Kind == EngineKind.WebView2 && OperatingSystem.IsWindows())
                return WebView2.WebView2Engine.Processes();
            return Array.Empty<int>();
        }

        internal static void RaiseDownloadStarted(EngineDownload download)
            => Dispatcher.UIThread.Post(() => DownloadStarted?.Invoke(download));
    }
}
