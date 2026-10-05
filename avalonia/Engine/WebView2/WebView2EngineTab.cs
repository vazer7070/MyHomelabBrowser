using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Threading;
using Microsoft.Web.WebView2.Core;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.AdBlock.Integration;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Engine.WebView2
{
    /// <summary>
    /// Onglet WebView2 (Windows) : la vue est créée par Avalonia.Controls.WebView, PommeBrowser
    /// reprend son CoreWebView2 (API complète de WebView2) et son contrôleur (zoom, clavier).
    /// Les événements de WebView2 arrivent sur le fil de l'interface : pas de relais nécessaire.
    /// Le bloqueur est celui de l'édition WPF (AdBlockTabSession), requête par requête.
    /// </summary>
    [SupportedOSPlatform("windows")]
    sealed class WebView2EngineTab : IEngineTab, IAdBlockWebView
    {
        readonly NativeWebView _host;
        readonly nint _viewWindow;
        readonly CoreWebView2 _core;
        readonly CoreWebView2Controller? _controller;
        readonly HashSet<string> _channels = new(StringComparer.Ordinal);
        readonly Dictionary<string, Task<string>> _scripts = new(StringComparer.Ordinal);
        readonly List<PermissionRequest> _pendingPermissions = new();
        readonly AdBlockTabSession? _adBlock;

        bool _disposed;
        bool _loading;
        double _progress;
        double _zoom = 1;
        string? _navigationUri;
        bool _certificateShown;
        // La navigation en cours affiche la page d'erreur de WebView2.
        bool _errorPage;
        string? _hoveredLink;
        bool _findAttached;

        WebView2EngineTab(NativeWebView host, nint viewWindow, CoreWebView2 core, CoreWebView2Controller? controller, bool isPrivate)
        {
            _host = host;
            _viewWindow = viewWindow;
            _core = core;
            _controller = controller;
            IsPrivate = isPrivate;

            WebView2Engine.Register(this);
            ConfigureSettings();

            _core.NavigationStarting += OnNavigationStarting;
            _core.ContentLoading += OnContentLoading;
            _core.NavigationCompleted += OnNavigationCompleted;
            _core.SourceChanged += OnSourceChanged;
            _core.DocumentTitleChanged += OnStateChanged;
            _core.HistoryChanged += OnStateChanged;
            _core.FaviconChanged += OnFaviconChanged;
            _core.ServerCertificateErrorDetected += OnCertificateError;
            _core.NewWindowRequested += OnNewWindowRequested;
            _core.PermissionRequested += OnPermissionRequested;
            _core.StatusBarTextChanged += OnStatusBarTextChanged;
            _core.ContainsFullScreenElementChanged += OnFullscreenChanged;
            _core.WindowCloseRequested += OnWindowCloseRequested;
            _core.ProcessFailed += OnProcessFailed;
            _core.WebMessageReceived += OnWebMessageReceived;
            _core.DownloadStarting += OnDownloadStarting;
            _core.AddWebResourceRequestedFilter(EngineHost.RuffleBaseUrl + "*", CoreWebView2WebResourceContext.All);
            _core.WebResourceRequested += OnWebResourceRequested;

            if (_controller != null)
                _controller.AcceleratorKeyPressed += OnAcceleratorKeyPressed;

            if (EngineHost.RequestFilter is { } module)
            {
                _adBlock = new AdBlockTabSession(this, module, isPrivate);
                _ = _adBlock.AttachAsync();
            }

            _host.GotFocus += OnHostFocusChanged;
            _host.LostFocus += OnHostFocusChanged;
        }

        /// <summary>
        /// Onglet branché sur la vue créée par Avalonia. Une erreur remonte à l'onglet, qui l'affiche
        /// (voir BrowserTab.AttachEngine).
        /// </summary>
        public static IEngineTab? Create(NativeWebView host, IWindowsWebView2PlatformHandle handle, bool isPrivate)
        {
            // Chaque lecture du handle donne une référence COM à rendre : les objets du SDK
            // (GetObjectForIUnknown) prennent la leur.
            nint corePointer = handle.CoreWebView2;
            nint controllerPointer = handle.CoreWebView2Controller;
            try
            {
                if (corePointer == 0)
                    throw new InvalidOperationException("WebView2 : vue sans CoreWebView2.");
                CoreWebView2 core = CoreWebView2.CreateFromComICoreWebView2(corePointer);
                return new WebView2EngineTab(host, handle.Handle, core, WrapController(controllerPointer), isPrivate);
            }
            finally
            {
                if (corePointer != 0)
                    Marshal.Release(corePointer);
                if (controllerPointer != 0)
                    Marshal.Release(controllerPointer);
            }
        }

        /// <summary>
        /// Contrôleur de la vue (zoom, raccourcis clavier, focus). Le SDK ne propose pas de fabrique
        /// publique pour lui, contrairement à CoreWebView2 : son constructeur interne est utilisé,
        /// comme le fait CreateFromComICoreWebView2. Sans lui, l'onglet fonctionne quand même.
        /// </summary>
        static CoreWebView2Controller? WrapController(nint pointer)
        {
            if (pointer == 0)
                return null;
            try
            {
                ConstructorInfo? constructor = typeof(CoreWebView2Controller).GetConstructor(
                    BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(object) }, null);
                return constructor?.Invoke(new[] { Marshal.GetObjectForIUnknown(pointer) }) as CoreWebView2Controller;
            }
            catch (Exception ex) when (ex is TargetInvocationException or InvalidCastException or COMException or MemberAccessException or ArgumentException)
            {
                RuntimeLogBuffer.Append("[WebView2] Contrôleur indisponible : " + ex.Message);
                return null;
            }
        }

        void ConfigureSettings()
        {
            try
            {
                CoreWebView2Settings settings = _core.Settings;
                // Lien survolé et identifiants : affichés et gardés par PommeBrowser lui-même.
                settings.IsStatusBarEnabled = false;
                settings.IsPasswordAutosaveEnabled = false;
                settings.IsGeneralAutofillEnabled = !IsPrivate;
                settings.AreDevToolsEnabled = true;
                settings.IsWebMessageEnabled = true;
                settings.AreDefaultContextMenusEnabled = true;
                settings.AreBrowserAcceleratorKeysEnabled = true;
                settings.IsZoomControlEnabled = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or NotImplementedException or COMException)
            {
                RuntimeLogBuffer.Append("[WebView2] " + ex.Message);
            }
            WebView2Engine.ApplyProfileSettings(_core);
        }

        internal void ApplyProfileSettings()
        {
            if (!_disposed)
                WebView2Engine.ApplyProfileSettings(_core);
        }

        internal CoreWebView2Profile? Profile => Safe(() => _core.Profile, null);

        internal CoreWebView2Environment? Environment => Safe(() => _core.Environment, null);

        /// <summary>La vue peut être fermée par Avalonia avant l'onglet : ses membres lèvent alors une exception.</summary>
        T Safe<T>(Func<T> read, T fallback)
        {
            if (_disposed)
                return fallback;
            try
            {
                return read();
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                return fallback;
            }
        }

        // ---------------------------------------------------------------
        // État
        // ---------------------------------------------------------------

        public bool IsPrivate { get; }
        public string? Title => Safe(() => _core.DocumentTitle, null);
        public string? Uri => Safe(() => _core.Source, null);
        public bool IsLoading => _loading;
        public double Progress => _progress;
        public bool CanGoBack => Safe(() => _core.CanGoBack, false);
        public bool CanGoForward => Safe(() => _core.CanGoForward, false);

        public double Zoom
        {
            get => _controller != null ? Safe(() => _controller.ZoomFactor, _zoom) : _zoom;
            set
            {
                _zoom = Math.Clamp(value, 0.25, 5);
                if (_controller != null)
                    Safe(() => _controller.ZoomFactor = _zoom, 0d);
            }
        }

        public event Action? StateChanged;
        public event Action<byte[]?>? FaviconChanged;
        public event Action<LoadStage, string?>? LoadChanged;
        public event Action<string, string>? LoadFailed;
        public event Action<CertificateProblem>? CertificateError;
        public event Action<NavigationRequest>? NewTabRequested;
        public event Action<PermissionRequest>? PermissionRequested;
        public event Action<string?>? LinkHovered;
        public event Action<bool>? FullscreenRequested;
        public event Action? CloseRequested;
        public event Action<string>? Crashed;
        public event Action<int>? FindMatchesCounted;
        public event Action<string, string>? ScriptMessage;
        public event Action<Key, KeyModifiers>? ShortcutPressed;

        /// <summary>Chromium bloque lui-même le contenu HTTP des pages HTTPS : rien à signaler.</summary>
        public event Action? InsecureContentDetected
        {
            add { }
            remove { }
        }

        // ---------------------------------------------------------------
        // Navigation
        // ---------------------------------------------------------------

        void OnNavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            _navigationUri = e.Uri;
            _certificateShown = false;
            _errorPage = false;
            _loading = true;
            _progress = 0.1;
            StateChanged?.Invoke();
            LoadChanged?.Invoke(e.IsRedirected ? LoadStage.Redirected : LoadStage.Started, e.Uri);
        }

        void OnContentLoading(object? sender, CoreWebView2ContentLoadingEventArgs e)
        {
            _progress = 0.5;
            // Page d'erreur de WebView2 : la page de PommeBrowser la remplace (voir OnNavigationCompleted).
            _errorPage = e.IsErrorPage;
            if (!e.IsErrorPage)
                LoadChanged?.Invoke(LoadStage.Committed, Uri);
            StateChanged?.Invoke();
        }

        void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            _loading = false;
            _progress = 1;

            if (e.IsSuccess)
            {
                LoadChanged?.Invoke(LoadStage.Finished, Uri);
            }
            // WebView2 affiche sa propre page d'erreur : c'est un échec, même quand son code est
            // « inconnu » (réponse TLS invalide, ERR_SSL_PROTOCOL_ERROR…).
            else if (!_certificateShown && (IsNetworkFailure(e) || (_errorPage && e.WebErrorStatus != CoreWebView2WebErrorStatus.OperationCanceled)))
            {
                string uri = _navigationUri ?? Uri ?? string.Empty;
                LoadFailed?.Invoke(uri, DescribeError(e.WebErrorStatus));
            }
            StateChanged?.Invoke();
        }

        /// <summary>
        /// Vrai échec réseau : pas une navigation remplacée ou changée en téléchargement, ni une
        /// page d'erreur envoyée par le serveur lui-même (404…), que la page affiche normalement.
        /// </summary>
        static bool IsNetworkFailure(CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.WebErrorStatus is CoreWebView2WebErrorStatus.OperationCanceled or CoreWebView2WebErrorStatus.Unknown
                or CoreWebView2WebErrorStatus.ValidAuthenticationCredentialsRequired or CoreWebView2WebErrorStatus.ValidProxyAuthenticationRequired)
                return false;
            try
            {
                return e.HttpStatusCode == 0;
            }
            catch (Exception ex) when (ex is NotImplementedException or COMException)
            {
                return true;
            }
        }

        static string DescribeError(CoreWebView2WebErrorStatus status) => status switch
        {
            CoreWebView2WebErrorStatus.HostNameNotResolved => Tr("Adresse introuvable : le nom du serveur n'existe pas ou le DNS ne répond pas."),
            CoreWebView2WebErrorStatus.CannotConnect or CoreWebView2WebErrorStatus.ServerUnreachable => Tr("Le serveur ne répond pas."),
            CoreWebView2WebErrorStatus.Timeout => Tr("Le serveur met trop de temps à répondre."),
            CoreWebView2WebErrorStatus.ConnectionAborted or CoreWebView2WebErrorStatus.ConnectionReset or CoreWebView2WebErrorStatus.Disconnected
                => Tr("La connexion a été interrompue."),
            CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect or CoreWebView2WebErrorStatus.CertificateExpired
                or CoreWebView2WebErrorStatus.CertificateIsInvalid or CoreWebView2WebErrorStatus.CertificateRevoked
                or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors
                => Tr("Le certificat du site a été refusé."),
            CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse => Tr("Le serveur a envoyé une réponse invalide."),
            CoreWebView2WebErrorStatus.RedirectFailed => Tr("Trop de redirections, ou redirection impossible."),
            CoreWebView2WebErrorStatus.Unknown => Tr("La connexion au serveur a échoué (réponse invalide ou connexion sécurisée impossible)."),
            _ => Tr("Erreur réseau ({0}).", status.ToString())
        };

        void OnSourceChanged(object? sender, CoreWebView2SourceChangedEventArgs e) => StateChanged?.Invoke();

        void OnStateChanged(object? sender, object e) => StateChanged?.Invoke();

        async void OnFaviconChanged(object? sender, object e)
        {
            try
            {
                if (string.IsNullOrEmpty(_core.FaviconUri))
                {
                    FaviconChanged?.Invoke(null);
                    return;
                }
                using Stream stream = await _core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                using var memory = new MemoryStream();
                await stream.CopyToAsync(memory);
                if (!_disposed)
                    FaviconChanged?.Invoke(memory.Length > 0 ? memory.ToArray() : null);
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or IOException or NullReferenceException)
            {
                // Icône indisponible (page fermée entre-temps…) : l'icône par défaut reste.
            }
        }

        // ---------------------------------------------------------------
        // Certificats
        // ---------------------------------------------------------------

        /// <summary>
        /// Certificat refusé : la page de PommeBrowser l'explique (empreinte, émetteur) et propose de
        /// lui faire confiance ; un certificat déjà accepté pour cet hôte est admis sans question.
        /// Les ressources d'une page (images, scripts) au certificat refusé sont simplement bloquées.
        /// </summary>
        void OnCertificateError(object? sender, CoreWebView2ServerCertificateErrorDetectedEventArgs e)
        {
            System.Uri.TryCreate(e.RequestUri, UriKind.Absolute, out Uri? request);
            byte[] der = PemToDer(Safe(() => e.ServerCertificate?.ToPemEncoding(), null));
            string key = WebView2Engine.CertificateKey(request?.Authority ?? string.Empty, Convert.ToHexString(SHA256.HashData(der)));

            if (der.Length > 0 && WebView2Engine.IsCertificateAllowed(key))
            {
                e.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
                return;
            }

            e.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
            if (request == null || !IsMainDocument(request))
                return;

            _certificateShown = true;
            CertificateError?.Invoke(new CertificateProblem
            {
                Uri = e.RequestUri,
                Host = request.Host,
                Der = der,
                Errors = e.ErrorStatus switch
                {
                    CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect => CertificateErrors.WrongName,
                    CoreWebView2WebErrorStatus.CertificateExpired => CertificateErrors.Expired,
                    CoreWebView2WebErrorStatus.CertificateRevoked => CertificateErrors.Revoked,
                    CoreWebView2WebErrorStatus.CertificateIsInvalid => CertificateErrors.UnknownAuthority,
                    _ => CertificateErrors.Other
                },
                Native = key
            });
        }

        bool IsMainDocument(Uri request)
            => System.Uri.TryCreate(_navigationUri, UriKind.Absolute, out Uri? navigation) &&
               string.Equals(navigation.GetLeftPart(UriPartial.Query), request.GetLeftPart(UriPartial.Query), StringComparison.OrdinalIgnoreCase);

        static byte[] PemToDer(string? pem)
        {
            if (string.IsNullOrEmpty(pem))
                return Array.Empty<byte>();
            try
            {
                using var certificate = X509Certificate2.CreateFromPem(pem);
                return certificate.RawData;
            }
            catch (CryptographicException)
            {
                return Array.Empty<byte>();
            }
        }

        public void AllowCertificate(CertificateProblem problem)
        {
            if (problem.Native is string key)
                WebView2Engine.AllowCertificate(key);
        }

        // ---------------------------------------------------------------
        // Fenêtres, autorisations, plein écran
        // ---------------------------------------------------------------

        /// <summary>
        /// Lien target=_blank, clic du milieu ou Ctrl+clic : nouvel onglet. Fenêtre demandée par un
        /// script avec une taille (connexion OAuth…) : fenêtre séparée reliée à la page. Une fenêtre
        /// ouverte sans action de l'utilisateur (publicité) est bloquée.
        /// </summary>
        void OnNewWindowRequested(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
        {
            string uri = e.Uri ?? string.Empty;
            bool blank = uri.Length == 0 || uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase);
            bool popup = blank || Safe(() => e.WindowFeatures is { } features && (features.HasSize || features.HasPosition), false);

            if (!e.IsUserInitiated)
            {
                e.Handled = true;
                return;
            }

            if (!popup)
            {
                e.Handled = true;
                NewTabRequested?.Invoke(new NavigationRequest
                {
                    Uri = uri,
                    IsNewWindow = true,
                    OpenInBackgroundTab = VirtualKeys.IsDown(VirtualKeys.Control) || VirtualKeys.IsDown(VirtualKeys.MiddleButton)
                });
                return;
            }

            WebView2PopupWindow.Open(_host, e, IsPrivate);
        }

        void OnPermissionRequested(object? sender, CoreWebView2PermissionRequestedEventArgs e)
        {
            PermissionKind kind = e.PermissionKind switch
            {
                CoreWebView2PermissionKind.Microphone => PermissionKind.Microphone,
                CoreWebView2PermissionKind.Camera => PermissionKind.Camera,
                CoreWebView2PermissionKind.Geolocation => PermissionKind.Geolocation,
                CoreWebView2PermissionKind.Notifications => PermissionKind.Notifications,
                CoreWebView2PermissionKind.ClipboardRead => PermissionKind.Clipboard,
                _ => PermissionKind.Other
            };

            if (kind == PermissionKind.Other)
            {
                // Lecture automatique : décision de WebView2 ; le reste (fichiers, polices…) est refusé.
                if (e.PermissionKind != CoreWebView2PermissionKind.Autoplay)
                    e.State = CoreWebView2PermissionState.Deny;
                return;
            }

            string origin = System.Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? page) ? page.GetLeftPart(UriPartial.Authority) : string.Empty;
            CoreWebView2Deferral deferral = e.GetDeferral();
            try
            {
                // Les choix sont gardés par PommeBrowser (autorisations par site), pas par WebView2.
                e.SavesInProfile = false;
            }
            catch (Exception ex) when (ex is NotImplementedException or COMException)
            {
            }

            var request = new PermissionRequest(kind, origin, allow =>
            {
                try
                {
                    e.State = allow ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                    deferral.Complete();
                }
                catch (Exception ex) when (ex is InvalidOperationException or COMException)
                {
                    // Page fermée avant la réponse.
                }
            });
            _pendingPermissions.Add(request);
            PermissionRequested?.Invoke(request);
        }

        void OnStatusBarTextChanged(object? sender, object e)
        {
            string? text = Safe(() => _core.StatusBarText, null);
            string? link = string.IsNullOrWhiteSpace(text) ? null : text;
            if (link == _hoveredLink)
                return;
            _hoveredLink = link;
            LinkHovered?.Invoke(link);
        }

        void OnFullscreenChanged(object? sender, object e)
            => FullscreenRequested?.Invoke(Safe(() => _core.ContainsFullScreenElement, false));

        void OnWindowCloseRequested(object? sender, object e) => CloseRequested?.Invoke();

        void OnProcessFailed(object? sender, CoreWebView2ProcessFailedEventArgs e)
        {
            // Journal (rapports, arrêt brutal) : quel processus du moteur, pourquoi, et le module fautif.
            RuntimeLogBuffer.Append($"[WebView2] Processus arrêté : {e.ProcessFailedKind}, " +
                                    $"raison {Safe(() => e.Reason.ToString(), "?")}, code {Safe(() => e.ExitCode, 0)}" +
                                    (Safe(() => e.ProcessDescription, (string?)null) is { Length: > 0 } description ? ", " + description : string.Empty) +
                                    (Safe(() => e.FailureSourceModulePath, (string?)null) is { Length: > 0 } module ? ", module " + module : string.Empty) + ".");
            if (e.ProcessFailedKind is not (CoreWebView2ProcessFailedKind.RenderProcessExited or CoreWebView2ProcessFailedKind.BrowserProcessExited))
                return;
            _loading = false;
            StateChanged?.Invoke();
            Crashed?.Invoke(Safe(() => e.Reason, CoreWebView2ProcessFailedReason.Unexpected) == CoreWebView2ProcessFailedReason.OutOfMemory ? "memory" : "crash");
        }

        void OnDownloadStarting(object? sender, CoreWebView2DownloadStartingEventArgs e)
        {
            try
            {
                WebView2Engine.Track(e, IsPrivate);
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or IOException or ArgumentException)
            {
                RuntimeLogBuffer.Append("[Téléchargements] " + ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // Ruffle : fichiers servis par PommeBrowser (https://ruffle.pommebrowser.invalid/…)
        // ---------------------------------------------------------------

        void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            string baseUrl = EngineHost.RuffleBaseUrl;
            string url = e.Request.Uri;
            if (!url.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase))
                return;

            string name = url[baseUrl.Length..];
            int end = name.IndexOfAny(new[] { '?', '#' });
            if (end >= 0)
                name = name[..end];

            try
            {
                CoreWebView2Environment environment = _core.Environment;
                e.Response = RuffleContent.Read(name, baseUrl) is { } file
                    ? environment.CreateWebResourceResponse(new MemoryStream(file.Data), 200, "OK",
                        "Content-Type: " + file.ContentType + "\r\n" +
                        "Access-Control-Allow-Origin: *\r\n" +
                        "X-Content-Type-Options: nosniff\r\n" +
                        "Cache-Control: max-age=604800, immutable")
                    : environment.CreateWebResourceResponse(null, 404, "Not Found", "Content-Type: text/plain");
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or ArgumentException)
            {
                RuntimeLogBuffer.Append("[Ruffle] " + ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // Clavier
        // ---------------------------------------------------------------

        /// <summary>Raccourcis du navigateur tapés dans la page : pris avant WebView2 et renvoyés à la fenêtre.</summary>
        void OnAcceleratorKeyPressed(object? sender, CoreWebView2AcceleratorKeyPressedEventArgs e)
        {
            if (e.KeyEventKind is not (CoreWebView2KeyEventKind.KeyDown or CoreWebView2KeyEventKind.SystemKeyDown))
                return;

            Key key = VirtualKeys.ToKey(e.VirtualKey);
            KeyModifiers modifiers = VirtualKeys.Modifiers();
            if (key == Key.None || !BrowserShortcuts.IsShortcut(key, modifiers))
                return;

            e.Handled = true;
            Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                    ShortcutPressed?.Invoke(key, modifiers);
            });
        }

        void OnHostFocusChanged(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() => SyncKeyboard());

        /// <summary>
        /// Un champ d'Avalonia prend le focus, ou la page est cachée : Windows laisse le clavier à
        /// WebView2 tant qu'on ne le reprend pas (Avalonia ne le fait pas), et la saisie irait dans
        /// la page. Seul le clavier gardé par cette page-ci est repris, pas celui d'un autre onglet
        /// ni de Basilisk logé dans la fenêtre.
        /// Aucun élément d'Avalonia n'a le focus : Windows l'a donné à une fenêtre enfant (clic dans
        /// la page ou dans Basilisk), et Avalonia l'a perdu avec la fenêtre. Le clavier y reste :
        /// le reprendre couperait la saisie de la page qu'on vient de cliquer.
        /// </summary>
        public void SyncKeyboard(bool force = false)
        {
            if (_disposed || _viewWindow == 0)
                return;
            bool shown = _host.IsEffectivelyVisible;
            if (shown && _host.IsKeyboardFocusWithin)
                return;
            TopLevel? topLevel = TopLevel.GetTopLevel(_host);
            if (shown && topLevel?.FocusManager?.GetFocusedElement() == null)
                return;
            if (topLevel?.TryGetPlatformHandle() is { HandleDescriptor: "HWND" } handle)
                VirtualKeys.TakeFocusFromChild(handle.Handle, _viewWindow);
        }

        public void Focus()
        {
            _host.Focus();
            if (_controller != null)
                Safe(() => { _controller.MoveFocus(CoreWebView2MoveFocusReason.Programmatic); return 0; }, 0);
        }

        // ---------------------------------------------------------------
        // Commandes
        // ---------------------------------------------------------------

        public void Navigate(string uri) => Safe(() => { _core.Navigate(uri); return 0; }, 0);
        public void GoBack() => Safe(() => { _core.GoBack(); return 0; }, 0);
        public void GoForward() => Safe(() => { _core.GoForward(); return 0; }, 0);
        public void Stop() => Safe(() => { _core.Stop(); return 0; }, 0);

        public void Reload(bool bypassCache = false)
        {
            if (bypassCache)
                _ = Safe(() => _core.CallDevToolsProtocolMethodAsync("Page.reload", "{\"ignoreCache\":true}"), Task.FromResult(string.Empty));
            else
                Safe(() => { _core.Reload(); return 0; }, 0);
        }

        public async void Find(string text, bool matchCase)
        {
            if (text.Length == 0)
            {
                StopFind();
                FindMatchesCounted?.Invoke(0);
                return;
            }

            try
            {
                CoreWebView2Find find = _core.Find;
                if (!_findAttached)
                {
                    _findAttached = true;
                    find.MatchCountChanged += (_, _) =>
                    {
                        if (!_disposed)
                            FindMatchesCounted?.Invoke(find.MatchCount);
                    };
                }

                CoreWebView2FindOptions options = _core.Environment.CreateFindOptions();
                options.FindTerm = text;
                options.IsCaseSensitive = matchCase;
                options.ShouldHighlightAllMatches = true;
                options.SuppressDefaultFindDialog = true;
                await find.StartAsync(options);
                if (!_disposed)
                    FindMatchesCounted?.Invoke(find.MatchCount);
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or NotImplementedException)
            {
                RuntimeLogBuffer.Append("[WebView2] Recherche : " + ex.Message);
            }
        }

        public void FindNext(bool backward)
            => Safe(() =>
            {
                if (backward)
                    _core.Find.FindPrevious();
                else
                    _core.Find.FindNext();
                return 0;
            }, 0);

        public void StopFind() => Safe(() => { _core.Find.Stop(); return 0; }, 0);

        public void Print() => Safe(() => { _core.ShowPrintUI(CoreWebView2PrintDialogKind.Browser); return 0; }, 0);

        public void ShowDevTools() => Safe(() => { _core.OpenDevToolsWindow(); return 0; }, 0);

        /// <summary>Cookies du profil de la page pour cette adresse (en-tête Cookie).</summary>
        public async Task<string?> GetCookieHeaderAsync(Uri url, bool includeHttpOnly)
        {
            if (_disposed)
                return null;
            List<CoreWebView2Cookie> cookies = await _core.CookieManager.GetCookiesAsync(url.AbsoluteUri);
            return FlashCookies.Header(cookies.Where(c => includeHttpOnly || !c.IsHttpOnly).Select(c => (c.Name, c.Value)));
        }

        /// <summary>Cookie enregistré (ou retiré, s'il est expiré) dans le profil de la page.</summary>
        public Task SetCookieAsync(PageCookie cookie)
        {
            if (_disposed)
                return Task.CompletedTask;
            CoreWebView2CookieManager manager = _core.CookieManager;
            // Domaine d'un cookie de domaine : avec un point initial ; cookie d'hôte : l'hôte exact.
            string domain = cookie.HostOnly ? cookie.Domain : "." + cookie.Domain;
            if (cookie.IsExpired(DateTimeOffset.UtcNow))
            {
                manager.DeleteCookiesWithDomainAndPath(cookie.Name, domain, cookie.Path);
                return Task.CompletedTask;
            }
            CoreWebView2Cookie created = manager.CreateCookie(cookie.Name, cookie.Value, domain, cookie.Path);
            created.IsHttpOnly = cookie.HttpOnly;
            created.IsSecure = cookie.Secure;
            created.SameSite = cookie.SameSite switch
            {
                "Strict" => CoreWebView2CookieSameSiteKind.Strict,
                "None" => CoreWebView2CookieSameSiteKind.None,
                _ => CoreWebView2CookieSameSiteKind.Lax
            };
            if (cookie.Expires is { } expires)
                created.Expires = expires.UtcDateTime;
            manager.AddOrUpdateCookie(created);
            return Task.CompletedTask;
        }

        /// <summary>
        /// Objet du pont vers le contenu Flash, offert au document principal seulement
        /// (AddHostObjectToScript : les cadres ne le voient pas, d'où un jeton inutile ici) ; la
        /// page l'appelle de façon synchrone.
        /// </summary>
        public void SetFlashBridge(Func<string, string?>? callFunction, string? token)
        {
            if (_disposed)
                return;
            try
            {
                // Retrait sans effet si l'objet n'était pas offert : selon la version, ArgumentException
                // ou COMException « Élément introuvable » (0x80070490), qui empêchait l'ajout qui suit.
                try
                {
                    _core.RemoveHostObjectFromScript(RuffleContent.FlashBridgeName);
                }
                catch (Exception ex) when (ex is ArgumentException or COMException)
                {
                }
                if (callFunction != null)
                    _core.AddHostObjectToScript(RuffleContent.FlashBridgeName, new FlashBridgeObject(callFunction));
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException or ArgumentException)
            {
                RuntimeLogBuffer.Append("[Flash] Appels de la page vers le contenu indisponibles : " + ex.Message);
            }
        }

        /// <summary>WebView2 n'a pas de monde isolé : les scripts de PommeBrowser tournent dans celui de la page.</summary>
        public async Task<string?> EvaluateAsync(string script, bool isolated)
        {
            if (_disposed)
                return null;
            string json = await _core.ExecuteScriptAsync(script);
            return FromJson(json);
        }

        // Cadre de chaque contenu (adresse → identifiant du protocole DevTools), et monde isolé de
        // PommeBrowser dans chaque cadre (identifiant → contexte d'exécution) : deux échanges de
        // moins par appel. Un document remplacé emporte son monde : tout est relu une fois.
        readonly Dictionary<string, string> _flashFrames = new(StringComparer.Ordinal);
        readonly Dictionary<string, int> _flashFrameWorlds = new(StringComparer.Ordinal);

        /// <summary>
        /// Voir <see cref="IEngineTab.EvaluateInFrameAsync"/> : par le protocole DevTools (arbre des
        /// cadres, monde isolé du cadre, puis évaluation dans ce monde), sans abonnement aux cadres
        /// de WebView2 (voir <see cref="OnWebMessageReceived"/>). Un cadre d'un autre site, tenu par
        /// un autre processus, n'est pas dans l'arbre : refus.
        /// </summary>
        public async Task<(bool Ok, string? Value)> EvaluateInFrameAsync(Uri frame, string script)
        {
            if (frame.Scheme is not ("http" or "https"))
                return (false, null);
            string expression = FlashFrames.InFrameScript(frame, script);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                if (_disposed)
                    return (false, null);
                try
                {
                    if (!_flashFrames.TryGetValue(frame.AbsoluteUri, out string? frameId))
                    {
                        using JsonDocument tree = JsonDocument.Parse(await _core.CallDevToolsProtocolMethodAsync("Page.getFrameTree", "{}"));
                        if (!tree.RootElement.TryGetProperty("frameTree", out JsonElement root) || FlashFrames.FindFrameId(root, frame) is not { } found)
                            return (false, null);
                        _flashFrames[frame.AbsoluteUri] = frameId = found;
                    }
                    if (!_flashFrameWorlds.TryGetValue(frameId, out int context))
                    {
                        using JsonDocument world = JsonDocument.Parse(await _core.CallDevToolsProtocolMethodAsync("Page.createIsolatedWorld",
                            "{\"frameId\":" + JsonSerializer.Serialize(frameId) + ",\"worldName\":\"PommeBrowser\"}"));
                        if (!world.RootElement.TryGetProperty("executionContextId", out JsonElement id) || !id.TryGetInt32(out context))
                            throw new InvalidOperationException("monde isolé du cadre non créé");
                        _flashFrameWorlds[frameId] = context;
                    }
                    string reply = await _core.CallDevToolsProtocolMethodAsync("Runtime.evaluate",
                        "{\"expression\":" + JsonSerializer.Serialize(expression) + ",\"contextId\":" +
                        context.ToString(System.Globalization.CultureInfo.InvariantCulture) + ",\"returnByValue\":true}");
                    if (FlashFrames.ReadResult(reply) is { } result)
                        return result;
                    throw new InvalidOperationException("contexte du cadre disparu");
                }
                catch (Exception ex) when (ex is COMException or InvalidOperationException or JsonException or ArgumentException)
                {
                    // Cadre rechargé ou retiré depuis : son monde et son identifiant sont relus.
                    _flashFrames.Remove(frame.AbsoluteUri);
                    _flashFrameWorlds.Clear();
                    if (attempt == 1)
                        throw new InvalidOperationException(ex.Message, ex);
                }
            }
            return (false, null);
        }

        static string? FromJson(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return null;
            try
            {
                using JsonDocument document = JsonDocument.Parse(json);
                return document.RootElement.ValueKind switch
                {
                    JsonValueKind.String => document.RootElement.GetString(),
                    JsonValueKind.Null or JsonValueKind.Undefined => null,
                    _ => document.RootElement.GetRawText()
                };
            }
            catch (JsonException)
            {
                return json;
            }
        }

        /// <summary>Toujours dans le monde de la page (WebView2 n'en a pas d'autre).</summary>
        public void AddUserScript(string id, string source, bool allFrames, bool atDocumentStart, bool pageWorld = false)
        {
            if (_disposed)
                return;
            RemoveUserScript(id);
            _scripts[id] = _core.AddScriptToExecuteOnDocumentCreatedAsync(UserScripts.Wrap(source, allFrames, atDocumentStart));
        }

        public async void RemoveUserScript(string id)
        {
            if (!_scripts.Remove(id, out Task<string>? pending))
                return;
            try
            {
                string scriptId = await pending;
                if (!_disposed)
                    _core.RemoveScriptToExecuteOnDocumentCreated(scriptId);
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
            }
        }

        public void RegisterMessageHandler(string name) => _channels.Add(name);

        /// <summary>
        /// Message d'un script de PommeBrowser : { channel, body } (voir EngineHost.ScriptPost), du
        /// document principal seulement. Ceux de Ruffle dans un cadre (jeu dans une iframe, Evony…)
        /// lui sont relayés par le script de détection du document principal, qui vérifie leur
        /// origine (voir RuffleContent.ProbeScript). Pas d'abonnement aux cadres de WebView2
        /// (CoreWebView2.FrameCreated, CoreWebView2Frame.WebMessageReceived…) : sur une page qui crée
        /// et détruit beaucoup de cadres (Google), le moteur arrêtait PommeBrowser sur une
        /// vérification interne (0x80000003 dans EmbeddedBrowserWebView.dll).
        /// </summary>
        void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                using JsonDocument document = JsonDocument.Parse(e.WebMessageAsJson);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("channel", out JsonElement channelElement) || channelElement.ValueKind != JsonValueKind.String)
                    return;

                string channel = channelElement.GetString()!;
                if (!_channels.Contains(channel))
                    return;

                string body = root.TryGetProperty("body", out JsonElement bodyElement)
                    ? bodyElement.ValueKind == JsonValueKind.String ? bodyElement.GetString() ?? string.Empty : bodyElement.GetRawText()
                    : string.Empty;
                ScriptMessage?.Invoke(channel, body);
            }
            catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException)
            {
                // Message d'un autre script de la page : ignoré.
            }
        }

        public void RefreshContentFilter()
        {
            if (_adBlock != null)
                _ = _adBlock.RefreshFilteringAsync();
        }

        // ---------------------------------------------------------------
        // Bloqueur (IAdBlockWebView)
        // ---------------------------------------------------------------

        CoreWebView2? IAdBlockWebView.CoreWebView2 => _disposed ? null : _core;

        void IAdBlockWebView.Post(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.Background);

        // ---------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed)
                return;

            foreach (PermissionRequest request in _pendingPermissions)
                request.Deny();
            _pendingPermissions.Clear();

            _adBlock?.Dispose();
            _disposed = true;
            WebView2Engine.Unregister(this);
            _host.GotFocus -= OnHostFocusChanged;
            _host.LostFocus -= OnHostFocusChanged;

            try
            {
                _core.NavigationStarting -= OnNavigationStarting;
                _core.ContentLoading -= OnContentLoading;
                _core.NavigationCompleted -= OnNavigationCompleted;
                _core.SourceChanged -= OnSourceChanged;
                _core.DocumentTitleChanged -= OnStateChanged;
                _core.HistoryChanged -= OnStateChanged;
                _core.FaviconChanged -= OnFaviconChanged;
                _core.ServerCertificateErrorDetected -= OnCertificateError;
                _core.NewWindowRequested -= OnNewWindowRequested;
                _core.PermissionRequested -= OnPermissionRequested;
                _core.StatusBarTextChanged -= OnStatusBarTextChanged;
                _core.ContainsFullScreenElementChanged -= OnFullscreenChanged;
                _core.WindowCloseRequested -= OnWindowCloseRequested;
                _core.ProcessFailed -= OnProcessFailed;
                _core.WebMessageReceived -= OnWebMessageReceived;
                _core.DownloadStarting -= OnDownloadStarting;
                _core.WebResourceRequested -= OnWebResourceRequested;
                if (_controller != null)
                    _controller.AcceleratorKeyPressed -= OnAcceleratorKeyPressed;
            }
            catch (Exception ex) when (ex is InvalidOperationException or COMException)
            {
                // Vue déjà fermée par Avalonia : ses événements sont partis avec elle.
            }
        }
    }
}
