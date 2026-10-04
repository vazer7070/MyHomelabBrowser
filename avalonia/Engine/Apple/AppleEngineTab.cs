using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.CompilerServices;
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
using MyHomelabBrowser.classes;
using static MyHomelabBrowser.classes.Localization.Loc;
using static PommeBrowser.Engine.Apple.ObjC;

namespace PommeBrowser.Engine.Apple
{
    /// <summary>
    /// Onglet WKWebView (macOS 11 et suivants) : la vue est créée par Avalonia.Controls.WebView,
    /// PommeBrowser s'y branche par le runtime Objective-C (délégués complétés, scripts dans un
    /// monde isolé, filtre de contenu de WebKit). L'état affiché (titre, adresse, chargement)
    /// est relu régulièrement : WKWebView ne le signale que par observation clé-valeur.
    /// </summary>
    [SupportedOSPlatform("macos")]
    sealed class AppleEngineTab : IEngineTab
    {
        const string LinkChannel = "pommeLink";
        const string LinkScriptId = "link-hover";
        const long InjectAtStart = 0;
        const long InjectAtEnd = 1;

        static readonly HttpClient FaviconClient = new() { Timeout = TimeSpan.FromSeconds(5) };

        sealed record Snapshot(string? Title, string? Uri, bool IsLoading, double Progress, bool CanGoBack, bool CanGoForward);

        readonly NativeWebView _host;
        readonly nint _view;
        readonly nint _controller;
        readonly nint _world;
        readonly DispatcherTimer _poll;
        readonly Dictionary<string, (string Source, bool AllFrames, bool AtStart, bool PageWorld)> _scripts = new(StringComparer.Ordinal);
        readonly HashSet<string> _channels = new(StringComparer.Ordinal);
        readonly List<PermissionRequest> _pendingPermissions = new();

        Snapshot _state = new(null, null, false, 0, false, false);
        bool _disposed;
        bool _filterApplied;
        bool _certificateShown;
        bool _insecureReported;
        string? _provisionalUri;
        string? _findText;
        bool _findCase;
        double _zoom = 1;

        AppleEngineTab(NativeWebView host, nint view, bool isPrivate)
        {
            _host = host;
            _view = view;
            IsPrivate = isPrivate;

            AppleEngine.Install();
            AppleEngine.Register(view, this);
            AppleEngine.Attach(view);

            nint configuration = Send(view, Sel("configuration"));
            _controller = Send(configuration, Sel("userContentController"));
            _world = Retain(Send(Class("WKContentWorld"), Sel("worldWithName:"), String("pommebrowser")));
            Configure(configuration);

            // Lien survolé : WKWebView ne le signale pas, un petit script du monde isolé s'en charge.
            RegisterMessageHandler(LinkChannel);
            AddUserScript(LinkScriptId, LinkHoverScript, allFrames: false, atDocumentStart: false);

            _host.NavigationCompleted += OnNavigationCompleted;
            _host.GotFocus += OnHostFocusChanged;
            _host.LostFocus += OnHostFocusChanged;

            _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Background, (_, _) => Poll());
            _poll.Start();
        }

        public static IEngineTab? Create(NativeWebView host, IAppleWKWebViewPlatformHandle handle, bool isPrivate)
        {
            if (!OperatingSystem.IsMacOSVersionAtLeast(11))
            {
                RuntimeLogBuffer.Append("[WKWebView] macOS 11 ou plus récent est nécessaire.");
                return null;
            }
            try
            {
                nint view = handle.GetWKWebViewRetained();
                return view == 0 ? null : new AppleEngineTab(host, view, isPrivate);
            }
            catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException or InvalidOperationException)
            {
                RuntimeLogBuffer.Append("[WKWebView] " + ex.Message);
                return null;
            }
        }

        void Configure(nint configuration)
        {
            nint preferences = Send(configuration, Sel("preferences"));
            SendBool(preferences, Sel("setJavaScriptCanOpenWindowsAutomatically:"), false);
            if (RespondsTo(preferences, "setElementFullscreenEnabled:"))
                SendBool(preferences, Sel("setElementFullscreenEnabled:"), true);
            if (RespondsTo(_view, "setInspectable:"))
                SendBool(_view, Sel("setInspectable:"), true);
            if (RespondsTo(_view, "setAllowsBackForwardNavigationGestures:"))
                SendBool(_view, Sel("setAllowsBackForwardNavigationGestures:"), true);
            ApplyAppearance();
        }

        internal NativeWebView Host => _host;

        internal nint DataStore => Send(Send(_view, Sel("configuration")), Sel("websiteDataStore"));

        /// <summary>Thème demandé aux sites : celui du navigateur (clair, sombre) ou du système.</summary>
        internal void ApplyAppearance()
        {
            if (_disposed)
                return;
            nint appearance = EngineHost.Settings.DarkPages switch
            {
                true => Send(Class("NSAppearance"), Sel("appearanceNamed:"), String("NSAppearanceNameDarkAqua")),
                false => Send(Class("NSAppearance"), Sel("appearanceNamed:"), String("NSAppearanceNameAqua")),
                _ => 0
            };
            Send(_view, Sel("setAppearance:"), appearance);
        }

        // ---------------------------------------------------------------
        // État
        // ---------------------------------------------------------------

        public bool IsPrivate { get; }
        public string? Title => _state.Title;
        public string? Uri => _state.Uri;
        public bool IsLoading => _state.IsLoading;
        public double Progress => _state.Progress;
        public bool CanGoBack => _state.CanGoBack;
        public bool CanGoForward => _state.CanGoForward;

        public double Zoom
        {
            get => _zoom;
            set
            {
                _zoom = Math.Clamp(value, 0.25, 5);
                if (!_disposed && RespondsTo(_view, "setPageZoom:"))
                    SendDouble(_view, Sel("setPageZoom:"), _zoom);
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
        public event Action? CloseRequested;
        public event Action<string>? Crashed;
        public event Action? InsecureContentDetected;
        public event Action<int>? FindMatchesCounted;
        public event Action<string, string>? ScriptMessage;

        /// <summary>WKWebView gère lui-même le plein écran des vidéos (fenêtre native).</summary>
        public event Action<bool>? FullscreenRequested
        {
            add { }
            remove { }
        }

        /// <summary>Avalonia renvoie déjà les raccourcis tapés dans la page à la fenêtre (Cmd + touche).</summary>
        public event Action<Key, KeyModifiers>? ShortcutPressed
        {
            add { }
            remove { }
        }

        void Poll()
        {
            if (_disposed)
                return;
            var state = new Snapshot(
                ToManaged(Send(_view, Sel("title"))),
                UrlString(Send(_view, Sel("URL"))),
                GetBool(_view, Sel("isLoading")),
                GetDouble(_view, Sel("estimatedProgress")),
                GetBool(_view, Sel("canGoBack")),
                GetBool(_view, Sel("canGoForward")));
            if (state != _state)
            {
                _state = state;
                StateChanged?.Invoke();
            }

            // Contenu HTTP dans une page HTTPS.
            if (!_insecureReported && !state.IsLoading && state.Uri?.StartsWith("https:", StringComparison.OrdinalIgnoreCase) == true &&
                !GetBool(_view, Sel("hasOnlySecureContent")))
            {
                _insecureReported = true;
                InsecureContentDetected?.Invoke();
            }
        }

        // ---------------------------------------------------------------
        // Navigation (délégué complété par AppleEngine)
        // ---------------------------------------------------------------

        internal void OnProvisionalStart()
        {
            _provisionalUri = UrlString(Send(_view, Sel("URL")));
            _certificateShown = false;
            _insecureReported = false;
            ApplyContentFilter(pageUri: _provisionalUri);
            Poll();
            LoadChanged?.Invoke(LoadStage.Started, _provisionalUri);
        }

        internal void OnCommitted()
        {
            Poll();
            LoadChanged?.Invoke(LoadStage.Committed, Uri);
        }

        void OnNavigationCompleted(object? sender, WebViewNavigationCompletedEventArgs e)
        {
            Poll();
            if (!e.IsSuccess)
                return;
            LoadChanged?.Invoke(LoadStage.Finished, Uri);
            if (!IsPrivate)
                _ = LoadFaviconAsync();
        }

        internal void OnFailed(nint error)
        {
            const long Cancelled = -999;
            const long FrameLoadInterrupted = 102;
            const long PlugInHandledLoad = 204;

            Poll();
            if (error == 0 || _certificateShown)
                return;
            string domain = ToManaged(Send(error, Sel("domain"))) ?? string.Empty;
            long code = GetLong(error, Sel("code"));
            if (domain == "NSURLErrorDomain" && code == Cancelled ||
                domain == "WebKitErrorDomain" && code is FrameLoadInterrupted or PlugInHandledLoad)
                return;

            nint info = Send(error, Sel("userInfo"));
            string uri = ToManaged(Send(info, Sel("objectForKey:"), String("NSErrorFailingURLStringKey"))) ?? _provisionalUri ?? Uri ?? string.Empty;
            string message = ToManaged(Send(error, Sel("localizedDescription"))) ?? Tr("Erreur réseau ({0}).", code.ToString());
            LoadFailed?.Invoke(uri, message);
        }

        internal void OnProcessTerminated()
        {
            Poll();
            Crashed?.Invoke("crash");
        }

        internal void OnNewTabRequested(string uri)
            => NewTabRequested?.Invoke(new NavigationRequest { Uri = uri, IsNewWindow = true });

        internal void OnCloseRequested() => CloseRequested?.Invoke();

        internal void OnPermissionRequested(PermissionRequest request)
        {
            _pendingPermissions.Add(request);
            PermissionRequested?.Invoke(request);
        }

        // ---------------------------------------------------------------
        // Certificats et identifiants
        // ---------------------------------------------------------------

        internal void OnCertificateRejected(string host, long port, byte[] der, string key)
        {
            string authority = AppleEngine.Authority(host, port);
            if (!System.Uri.TryCreate(_provisionalUri, UriKind.Absolute, out Uri? page) ||
                !string.Equals(page.Authority, authority, StringComparison.OrdinalIgnoreCase))
                return;

            _certificateShown = true;
            CertificateErrors errors = CertificateErrors.UnknownAuthority;
            try
            {
                using var certificate = X509CertificateLoader.LoadCertificate(der);
                if (certificate.NotAfter < DateTime.Now)
                    errors = CertificateErrors.Expired;
                else if (certificate.NotBefore > DateTime.Now)
                    errors = CertificateErrors.NotYetValid;
                else if (!certificate.MatchesHostname(host))
                    errors = CertificateErrors.WrongName;
            }
            catch (CryptographicException)
            {
            }

            var problem = new CertificateProblem { Uri = page.AbsoluteUri, Host = host, Der = der, Errors = errors, Native = key };
            Dispatcher.UIThread.Post(() => CertificateError?.Invoke(problem));
        }

        public void AllowCertificate(CertificateProblem problem)
        {
            if (problem.Native is string key)
                AppleEngine.AllowCertificate(key);
        }

        /// <summary>Site protégé par un mot de passe (authentification HTTP) : identifiants demandés.</summary>
        internal async Task<(string User, string Password)?> AskCredentialsAsync(string authority, string realm)
        {
            if (TopLevel.GetTopLevel(_host) is not Window owner)
                return null;
            var dialog = new Views.Dialogs.FormDialog(Tr("Connexion requise"), Tr("Se connecter"));
            dialog.AddText(realm.Length > 0 ? Tr("{0} demande un identifiant : « {1} ».", authority, realm) : Tr("{0} demande un identifiant.", authority));
            TextBox user = dialog.AddEntry(Tr("Nom d'utilisateur"));
            TextBox password = dialog.AddEntry(Tr("Mot de passe"), password: true);
            return await dialog.ShowAsync(owner) ? (user.Text ?? string.Empty, password.Text ?? string.Empty) : null;
        }

        // ---------------------------------------------------------------
        // Filtre anti-pub
        // ---------------------------------------------------------------

        internal void ApplyContentFilter(bool force = false, string? pageUri = null)
        {
            if (_disposed)
                return;
            nint filter = AppleEngine.ContentFilter;
            bool wanted = filter != 0 && (EngineHost.ContentFilterPolicy?.Invoke(pageUri ?? Uri) ?? false);
            if (wanted == _filterApplied && !force)
                return;
            Send(_controller, Sel("removeAllContentRuleLists"));
            if (wanted)
                Send(_controller, Sel("addContentRuleList:"), filter);
            _filterApplied = wanted;
        }

        public void RefreshContentFilter() => ApplyContentFilter(force: true);

        // ---------------------------------------------------------------
        // Commandes
        // ---------------------------------------------------------------

        public void Navigate(string uri)
        {
            if (!_disposed && System.Uri.TryCreate(uri, UriKind.Absolute, out Uri? target))
                _host.Navigate(target);
        }

        public void GoBack() => _host.GoBack();
        public void GoForward() => _host.GoForward();
        public void Stop() => _host.Stop();

        public void Reload(bool bypassCache = false)
        {
            if (_disposed)
                return;
            Send(_view, Sel(bypassCache ? "reloadFromOrigin" : "reload"));
        }

        public void Focus() => _host.Focus();

        void OnHostFocusChanged(object? sender, RoutedEventArgs e) => Dispatcher.UIThread.Post(() => SyncKeyboard());

        /// <summary>Un champ d'Avalonia prend le focus : la page cesse d'être la destination des touches.</summary>
        public void SyncKeyboard(bool force = false)
        {
            if (_disposed || (_host.IsKeyboardFocusWithin && _host.IsEffectivelyVisible))
                return;
            if (TopLevel.GetTopLevel(_host)?.TryGetPlatformHandle() is not IMacOSTopLevelPlatformHandle top)
                return;
            nint responder = Send(top.NSWindow, Sel("firstResponder"));
            if (responder != 0 && IsKindOf(responder, "NSView") && GetBool(responder, Sel("isDescendantOf:"), _view))
                GetBool(top.NSWindow, Sel("makeFirstResponder:"), top.NSView);
        }

        /// <summary>Recherche dans la page : sélection de WebKit (window.find) et nombre de résultats.</summary>
        public async void Find(string text, bool matchCase)
        {
            _findText = text;
            _findCase = matchCase;
            if (text.Length == 0)
            {
                StopFind();
                FindMatchesCounted?.Invoke(0);
                return;
            }

            string needle = JsonSerializer.Serialize(text);
            string caseSensitive = matchCase ? "true" : "false";
            string script = $$"""
                (() => {
                  const needle = {{needle}};
                  const body = (document.body && document.body.innerText) || '';
                  const haystack = {{caseSensitive}} ? body : body.toLowerCase();
                  const search = {{caseSensitive}} ? needle : needle.toLowerCase();
                  let count = 0, index = 0;
                  while (search.length && (index = haystack.indexOf(search, index)) !== -1 && count < 1000) { count++; index += search.length; }
                  const selection = window.getSelection();
                  if (selection) selection.removeAllRanges();
                  if (count) window.find(needle, {{caseSensitive}}, false, true);
                  return String(count);
                })()
                """;
            string? result = await EvaluateAsync(script, isolated: true);
            if (!_disposed && _findText == text)
                FindMatchesCounted?.Invoke(int.TryParse(result, out int count) ? count : 0);
        }

        public void FindNext(bool backward)
        {
            if (string.IsNullOrEmpty(_findText))
                return;
            _ = EvaluateAsync($"window.find({JsonSerializer.Serialize(_findText)}, {(_findCase ? "true" : "false")}, {(backward ? "true" : "false")}, true)", isolated: true);
        }

        public void StopFind()
        {
            _findText = null;
            _ = EvaluateAsync("(() => { const s = window.getSelection(); if (s) s.removeAllRanges(); })()", isolated: true);
        }

        public void Print() => _host.ShowPrintUI();

        /// <summary>Inspecteur web (clic droit « Inspecter l'élément » fonctionne aussi).</summary>
        public void ShowDevTools()
        {
            if (!RespondsTo(_view, "_inspector"))
                return;
            nint inspector = Send(_view, Sel("_inspector"));
            if (RespondsTo(inspector, "show"))
                Send(inspector, Sel("show"));
        }

        // ---------------------------------------------------------------
        // Scripts
        // ---------------------------------------------------------------

        /// <summary>Le moteur Flash intégré n'existe que sous Windows : pas de partage ici.</summary>
        public Task<string?> GetCookieHeaderAsync(Uri url, bool includeHttpOnly) => Task.FromResult<string?>(null);

        public Task SetCookieAsync(PageCookie cookie) => Task.CompletedTask;

        public void SetFlashBridge(Func<string, string?>? callFunction, string? token)
        {
        }

        public unsafe Task<string?> EvaluateAsync(string script, bool isolated)
        {
            var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (_disposed)
            {
                completion.SetResult(null);
                return completion.Task;
            }
            nint world = isolated ? _world : Send(Class("WKContentWorld"), Sel("pageWorld"));
            nint block = CreateBlock((nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnEvaluated, "v@?@@", completion);
            Send(_view, Sel("evaluateJavaScript:inFrame:inContentWorld:completionHandler:"), String(script), 0, world, block);
            return completion.Task;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnEvaluated(nint block, nint result, nint error)
        {
            var completion = BlockContext<TaskCompletionSource<string?>>(block);
            FreeBlock(block);
            if (error != 0 && result == 0)
                completion?.TrySetResult(null);
            else
                completion?.TrySetResult(Describe(result));
        }

        public void AddUserScript(string id, string source, bool allFrames, bool atDocumentStart, bool pageWorld = false)
        {
            _scripts[id] = (source, allFrames, atDocumentStart, pageWorld);
            RebuildScripts();
        }

        public void RemoveUserScript(string id)
        {
            if (_scripts.Remove(id))
                RebuildScripts();
        }

        /// <summary>WebKit ne sait retirer qu'en bloc : les scripts restants sont remis.</summary>
        void RebuildScripts()
        {
            if (_disposed)
                return;
            Send(_controller, Sel("removeAllUserScripts"));
            nint pageWorld = Send(Class("WKContentWorld"), Sel("pageWorld"));
            foreach ((string source, bool allFrames, bool atStart, bool inPage) in _scripts.Values)
            {
                nint script = SendObjectLongBool(Send(Class("WKUserScript"), Sel("alloc")),
                    Sel("initWithSource:injectionTime:forMainFrameOnly:inContentWorld:"),
                    String(source), atStart ? InjectAtStart : InjectAtEnd, !allFrames, inPage ? pageWorld : _world);
                Send(_controller, Sel("addUserScript:"), script);
                Release(script);
            }
        }

        public void RegisterMessageHandler(string name)
        {
            if (_disposed || !_channels.Add(name))
                return;
            Send(_controller, Sel("addScriptMessageHandler:contentWorld:name:"), AppleEngine.MessageHandler, _world, String(name));
        }

        internal void OnScriptMessage(string channel, string body)
        {
            if (_disposed || !_channels.Contains(channel))
                return;
            if (channel == LinkChannel)
            {
                LinkHovered?.Invoke(body.Length == 0 ? null : body);
                return;
            }
            ScriptMessage?.Invoke(channel, body);
        }

        const string LinkHoverScript = """
            (() => {
              let last = '';
              const post = (href) => { try { window.webkit.messageHandlers.pommeLink.postMessage(href); } catch (e) { } };
              document.addEventListener('mouseover', (event) => {
                const link = event.target && event.target.closest ? event.target.closest('a[href]') : null;
                const href = link ? link.href : '';
                if (href !== last) { last = href; post(href); }
              }, true);
              document.addEventListener('mouseleave', () => { if (last) { last = ''; post(''); } }, true);
            })();
            """;

        /// <summary>Icône du site : WKWebView n'en donne pas, elle est lue dans la page puis téléchargée.</summary>
        async Task LoadFaviconAsync()
        {
            try
            {
                string? href = await EvaluateAsync(
                    "(() => { const l = document.querySelector('link[rel~=\"icon\"]'); return l ? l.href : new URL('/favicon.ico', location.href).href; })()",
                    isolated: true);
                if (!System.Uri.TryCreate(href, UriKind.Absolute, out Uri? icon) || icon.Scheme is not ("http" or "https"))
                    return;
                using HttpResponseMessage response = await FaviconClient.GetAsync(icon, HttpCompletionOption.ResponseHeadersRead);
                if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength > 512 * 1024)
                    return;
                byte[] bytes = await response.Content.ReadAsByteArrayAsync();
                if (!_disposed && bytes.Length is > 0 and <= 512 * 1024)
                    FaviconChanged?.Invoke(bytes);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or IOException)
            {
                // Pas d'icône : celle par défaut reste.
            }
        }

        // ---------------------------------------------------------------

        public void Dispose()
        {
            if (_disposed)
                return;

            foreach (PermissionRequest request in _pendingPermissions)
                request.Deny();
            _pendingPermissions.Clear();

            _disposed = true;
            _poll.Stop();
            _host.NavigationCompleted -= OnNavigationCompleted;
            _host.GotFocus -= OnHostFocusChanged;
            _host.LostFocus -= OnHostFocusChanged;
            AppleEngine.Unregister(_view);
            if (RespondsTo(_controller, "removeAllScriptMessageHandlersFromContentWorld:"))
                Send(_controller, Sel("removeAllScriptMessageHandlersFromContentWorld:"), _world);
            Release(_world);
            Release(_view);
        }
    }
}
