using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Security;
using MyHomelabBrowser.classes.Session;
using PommeBrowser.Core;
using PommeBrowser.Engine;
using PommeBrowser.Linux.Core;
using PommeBrowser.Views.Pages;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>Ce qu'affiche un onglet : la page web, ou une page de PommeBrowser.</summary>
    public enum TabPage
    {
        Web,
        Home,
        History,
        Favorites,
        Services,
        Passwords,
        Settings,
        Diagnostics,
        Report,
        Legacy,
        Error
    }

    public enum SecurityLevel
    {
        None,
        Secure,
        Trusted,
        Mixed,
        Insecure,
        Local
    }

    /// <summary>
    /// Onglet : une page web (vue native du moteur) ou une page de PommeBrowser (accueil,
    /// historique, erreur…) affichée à sa place. La vue web n'est créée qu'au premier
    /// chargement d'une page, puis gardée pour tout l'historique de l'onglet.
    /// </summary>
    public sealed partial class BrowserTab : INotifyPropertyChanged
    {
        readonly BrowserApp _app;
        readonly Grid _host = new();
        readonly HashSet<string> _upgradedHosts = new(StringComparer.OrdinalIgnoreCase);

        // Site passé en HTTPS par PommeBrowser qui repart de lui-même en HTTP (voir OnLoadChanged).
        readonly HttpsReturnGuard _httpsReturn = new();

        // Page qui se recharge sans fin (voir OnNavigationLoop).
        readonly NavigationLoopGuard _loopGuard = new();

        NativeWebView? _web;
        IEngineTab? _engine;
        Control? _page;
        string? _pendingUrl;
        string? _pendingTitle;
        string? _expectedMainUrl;
        string? _errorUrl;
        bool _webShownOnce;

        string _title = Tr("Nouvel onglet");
        Bitmap? _favicon;
        bool _isLoading;
        bool _isSelected;
        bool _isPinned;
        TabPage _page_kind = TabPage.Home;
        SecurityLevel _security;

        public BrowserTab(MainWindow window, bool isPrivate)
        {
            _app = window.App;
            Window = window;
            IsPrivate = isPrivate;
            _host.ClipToBounds = true;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>Titre, adresse, chargement, sécurité ou historique modifiés.</summary>
        public event Action<BrowserTab>? Changed;

        public MainWindow Window { get; internal set; }
        public bool IsPrivate { get; }

        /// <summary>Onglet d'où celui-ci a été ouvert (retour à la fermeture, ordre des nouveaux onglets).</summary>
        public BrowserTab? Opener { get; set; }

        /// <summary>Contenu affiché dans la zone des pages de la fenêtre.</summary>
        public Control Content => _host;

        public IEngineTab? Engine => _engine;

        /// <summary>Dernière sélection (onglets inactifs à suspendre, vue côte à côte).</summary>
        public DateTime LastActivated { get; set; } = DateTime.Now;

        public TabPage Page
        {
            get => _page_kind;
            private set => Set(ref _page_kind, value);
        }

        public string Title
        {
            get => _title;
            private set => Set(ref _title, value);
        }

        public Bitmap? Favicon
        {
            get => _favicon;
            private set => Set(ref _favicon, value);
        }

        public bool IsLoading
        {
            get => _isLoading;
            private set => Set(ref _isLoading, value);
        }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                    return;
                Set(ref _isSelected, value);
                _basilisk?.SetBackground(!value);
                SetFlashSlotsBackground(!value);
            }
        }

        public bool IsPinned
        {
            get => _isPinned;
            set => Set(ref _isPinned, value);
        }

        public SecurityLevel Security
        {
            get => _security;
            private set => Set(ref _security, value);
        }

        /// <summary>Adresse affichée (vide pour les pages de PommeBrowser).</summary>
        public string Url => Page switch
        {
            TabPage.Web => _engine?.Uri ?? _pendingUrl ?? string.Empty,
            TabPage.Error => _errorUrl ?? string.Empty,
            TabPage.Legacy => _legacyUri?.AbsoluteUri ?? string.Empty,
            _ => string.Empty
        };

        /// <summary>Adresse de la page web, même si une page de PommeBrowser est affichée par-dessus.</summary>
        public string WebUrl => _engine?.Uri ?? _pendingUrl ?? string.Empty;

        public double Progress => IsLoading ? _engine?.Progress ?? 0 : 0;

        public bool CanGoBack => Page != TabPage.Web ? _webShownOnce : _engine?.CanGoBack ?? false;

        public bool CanGoForward => Page == TabPage.Web && (_engine?.CanGoForward ?? false);

        /// <summary>Chargement différé (onglet restauré jamais affiché).</summary>
        public bool HasPendingLoad => _pendingUrl != null && _engine == null;

        public double Zoom => _engine?.Zoom ?? 1;

        void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
                return;
            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }

        void RaiseChanged()
        {
            UpdateTitle();
            IsLoading = Page == TabPage.Web && (_engine?.IsLoading ?? false);
            Changed?.Invoke(this);
        }

        void UpdateTitle()
        {
            Title = Page switch
            {
                TabPage.Web => !string.IsNullOrWhiteSpace(_engine?.Title) ? _engine!.Title!
                    : _pendingTitle ?? (WebUrl.Length > 0 ? UrlDisplay.ForDisplay(WebUrl) : Tr("Nouvel onglet")),
                TabPage.Legacy => (_legacyUri?.Host ?? string.Empty) + " — Basilisk",
                TabPage.Home => Tr("Nouvel onglet"),
                TabPage.History => Tr("Historique"),
                TabPage.Favorites => Tr("Favoris"),
                TabPage.Services => Tr("Services du homelab"),
                TabPage.Passwords => Tr("Mots de passe"),
                TabPage.Settings => Tr("Paramètres"),
                TabPage.Diagnostics => Tr("Diagnostic"),
                TabPage.Report => Tr("Signaler un problème"),
                _ => Tr("Page indisponible")
            };
        }

        // ---------------------------------------------------------------
        // Vue web
        // ---------------------------------------------------------------

        /// <summary>
        /// Clavier à la page web. Au premier chargement la vue du moteur n'existe pas encore : le
        /// focus lui est donné tout de suite, le moteur prend le clavier dès qu'il est branché.
        /// </summary>
        public void FocusPage()
        {
            if (Page == TabPage.Legacy && _legacyView is { IsDocked: true } legacy)
            {
                legacy.Focus();
                return;
            }
            if (Page != TabPage.Web)
                return;
            if (_engine != null)
                _engine.Focus();
            else
                _web?.Focus();
        }

        /// <summary>Crée la vue du moteur (au premier chargement d'une page web).</summary>
        void EnsureWeb()
        {
            if (_web != null)
                return;

            var web = new NativeWebView();
            _web = web;
            EngineHost.Prepare(web, IsPrivate);
            // Événements d'une vue abandonnée après un échec (voir EngineFailed) : ignorés.
            web.AdapterCreated += (_, _) =>
            {
                if (_web == web)
                    AttachEngine();
            };
            web.AdapterDestroyed += (_, _) =>
            {
                if (_web == web)
                    DetachEngine();
            };
            _host.Children.Insert(0, web);
            web.IsVisible = Page == TabPage.Web;
        }

        void AttachEngine()
        {
            if (_web == null || _engine != null)
                return;

            // Toute erreur du moteur reste dans l'onglet : jamais d'arrêt de PommeBrowser.
            IEngineTab? engine = null;
            string? target = _pendingUrl;
            try
            {
                engine = EngineHost.Attach(_web, IsPrivate);
                if (engine == null)
                {
                    EngineFailed(null);
                    return;
                }

                _engine = engine;
                ConnectEngine(engine);
                AttachScripts(engine);

                if (_pendingUrl is { } url)
                {
                    _pendingUrl = null;
                    NavigateWeb(url);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ErrorLog.Write("Moteur web", ex);
                engine?.Dispose();
                _engine = null;
                _pendingUrl ??= target;
                EngineFailed(ex);
                return;
            }
            RaiseChanged();
        }

        /// <summary>
        /// Le moteur de la vue n'a pas pu démarrer (erreur relancée par Avalonia, voir
        /// BrowserApp.OnUnhandledException) : l'onglet qui l'attendait affiche l'erreur.
        /// </summary>
        internal bool OnEngineStartupFailed(Exception exception)
        {
            if (_web == null || _engine != null)
                return false;
            EngineFailed(exception);
            return true;
        }

        /// <summary>
        /// Page d'erreur à la place de la vue web, retirée : « Réessayer » en crée une nouvelle,
        /// et donc un nouveau démarrage du moteur.
        /// </summary>
        void EngineFailed(Exception? exception)
        {
            string? url = _pendingUrl ?? _engine?.Uri;
            if (_web is { } failed)
            {
                // Retirée après l'événement en cours : elle peut être celle qui l'envoie.
                _web = null;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => _host.Children.Remove(failed));
            }
            _pendingUrl = url;
            ShowError(url, Tr("Moteur web indisponible"),
                exception != null ? EngineHost.DescribeFailure(exception) : Tr("Le moteur web de ce système n'a pas pu être chargé."),
                (Tr("Réessayer"), true, () =>
                {
                    if (url != null)
                        Navigate(url);
                    else
                        ShowHome();
                }));
        }

        void DetachEngine()
        {
            if (_engine == null)
                return;
            _pendingUrl ??= _engine.Uri;
            _engine.Dispose();
            _engine = null;
        }

        void ConnectEngine(IEngineTab engine)
        {
            engine.StateChanged += () =>
            {
                if (!IsPrivate && Page == TabPage.Web && engine.Uri is { } uri && !string.IsNullOrWhiteSpace(engine.Title))
                    _app.History.UpdateTitle(uri, engine.Title);
                RaiseChanged();
            };
            engine.FaviconChanged += png =>
            {
                Favicon = FaviconStore.Decode(png);
                if (!IsPrivate && png != null)
                    FaviconStore.Save(engine.Uri, png);
            };
            engine.LoadChanged += OnLoadChanged;
            engine.LoadFailed += OnLoadFailed;
            engine.CertificateError += OnCertificateError;
            engine.NewTabRequested += request => Window.OpenTab(request.Uri, background: request.OpenInBackgroundTab, opener: this);
            engine.PermissionRequested += request => PermissionPrompt.Handle(this, request);
            engine.LinkHovered += link => Window.ShowLinkStatus(this, link);
            engine.FullscreenRequested += full => Window.SetWebFullscreen(this, full);
            engine.CloseRequested += () => Window.CloseTab(this);
            engine.InsecureContentDetected += () =>
            {
                if (Security is SecurityLevel.Secure or SecurityLevel.Trusted)
                {
                    Security = SecurityLevel.Mixed;
                    Changed?.Invoke(this);
                }
            };
            engine.Crashed += reason => ShowError(WebUrl, Tr("La page a cessé de fonctionner"),
                reason == "memory" ? Tr("Elle utilisait trop de mémoire.") : Tr("Le processus qui l'affichait s'est arrêté."),
                (Tr("Recharger"), true, () => Navigate(WebUrl)));
            engine.FindMatchesCounted += count => Window.OnFindResult(this, count);
            engine.ScriptMessage += OnScriptMessage;
            engine.ShortcutPressed += (key, modifiers) => Window.HandleShortcut(key, modifiers);
        }

        // ---------------------------------------------------------------
        // Navigation
        // ---------------------------------------------------------------

        /// <summary>
        /// Ouvre une adresse (déjà résolue) dans l'onglet. <paramref name="httpsFallback"/> : adresse
        /// saisie sans schéma et ouverte en https:// (voir UrlResolver.IsImplicitHttps), qui revient
        /// en http:// si le site ne propose pas HTTPS.
        /// </summary>
        public void Navigate(string url, bool httpsFallback = false)
        {
            _pendingTitle = null;
            _loopGuard.Reset();
            _loopStoppedAt = long.MinValue / 2;
            if (WantsBasilisk(url, out Uri? legacy))
            {
                OpenInBasilisk(legacy);
                return;
            }
            if (!TryUpgrade(ref url) && httpsFallback &&
                System.Uri.TryCreate(url, UriKind.Absolute, out Uri? typed) && typed.Scheme == System.Uri.UriSchemeHttps)
            {
                _upgradedHosts.Add(typed.IdnHost);
            }
            ShowWeb();
            if (_engine != null)
            {
                NavigateWeb(url);
            }
            else
            {
                // Adresse gardée avant la création de la vue : le moteur la charge dès qu'il est
                // prêt (parfois tout de suite), sinon la page d'erreur du moteur la reprend.
                _pendingUrl = url;
                EnsureWeb();
            }
            RaiseChanged();
        }

        void NavigateWeb(string url)
        {
            _expectedMainUrl = url;
            _engine!.Zoom = ZoomFor(url);
            _engine.Navigate(url);
        }

        /// <summary>Onglet restauré : la page n'est chargée qu'à sa première sélection.</summary>
        public void SetPending(string url, string? title)
        {
            _pendingUrl = url;
            _pendingTitle = title;
            Page = TabPage.Web;
            RaiseChanged();
        }

        public void LoadPendingIfNeeded()
        {
            if (_engine == null && _pendingUrl is { } url)
                Navigate(url);
        }

        public void GoBack()
        {
            _loopStoppedAt = long.MinValue / 2;
            if (Page != TabPage.Web)
            {
                if (_webShownOnce)
                    ShowWeb();
                return;
            }
            _expectedMainUrl = null;
            _engine?.GoBack();
        }

        public void GoForward()
        {
            _loopStoppedAt = long.MinValue / 2;
            _expectedMainUrl = null;
            _engine?.GoForward();
        }

        public void Reload(bool bypassCache = false)
        {
            _loopStoppedAt = long.MinValue / 2;
            if (Page != TabPage.Web)
            {
                if (Page == TabPage.Error && WebUrl.Length > 0)
                    Navigate(_errorUrl ?? WebUrl);
                else if (Page == TabPage.Legacy && _legacyUri != null && _basilisk == null)
                    OpenInBasilisk(_legacyUri);
                return;
            }

            if (HasPendingLoad)
            {
                LoadPendingIfNeeded();
                return;
            }
            _engine?.Reload(bypassCache);
        }

        public void Stop() => _engine?.Stop();

        /// <summary>http:// devient https:// (hors réseau local et sites autorisés en HTTP).</summary>
        bool TryUpgrade(ref string url)
        {
            if (!_app.Settings.HttpsUpgrade || !System.Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                return false;

            if (!HttpsUpgradePolicy.ShouldUpgrade(uri, host => _app.SiteSecurity.IsHttpAllowed(host) || _app.HttpOnlyHosts.Contains(host)))
                return false;

            _upgradedHosts.Add(uri.IdnHost);
            url = HttpsUpgradePolicy.Upgrade(uri).AbsoluteUri;
            return true;
        }

        double ZoomFor(string? url)
            => System.Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ? _app.Zoom.Get(uri) : 1;

        /// <summary>Zoom de la page, retenu pour le site (sauf en navigation privée).</summary>
        public void SetZoom(double zoom)
        {
            if (_engine == null)
                return;
            _engine.Zoom = zoom;
            if (!IsPrivate && System.Uri.TryCreate(WebUrl, UriKind.Absolute, out Uri? uri))
                _app.Zoom.Set(uri, zoom);
            Changed?.Invoke(this);
        }

        void OnLoadChanged(LoadStage stage, string? url)
        {
            switch (stage)
            {
                case LoadStage.Started:
                    // Lien vers un site réglé sur Basilisk : la page s'y ouvre.
                    if (WantsBasilisk(url, out Uri? legacy))
                    {
                        _engine?.Stop();
                        OpenInBasilisk(legacy);
                        return;
                    }

                    // Boucle arrêtée à l'instant : la page quittée ne relance pas sa navigation.
                    if (url != BlankPage && Environment.TickCount64 - _loopStoppedAt < LoopStopHold)
                    {
                        _engine?.Navigate(BlankPage);
                        return;
                    }

                    // Page passée en HTTPS par PommeBrowser qui repart d'elle-même vers http:// (script
                    // de la page, pas une redirection du serveur) : le site veut HTTP, il le garde.
                    // Sinon, chaque retour serait de nouveau passé en HTTPS, sans fin.
                    if (System.Uri.TryCreate(url, UriKind.Absolute, out Uri? started))
                    {
                        _loopGuard.Started(started);
                        if (_httpsReturn.ReturnsToHttp(started, Environment.TickCount64) && _app.HttpOnlyHosts.Add(started.IdnHost))
                            RuntimeLogBuffer.Append($"[HTTPS] {started.IdnHost} revient de lui-même en HTTP : laissé en HTTP pour la session.");
                    }

                    // Page atteinte par un lien : même passage en HTTPS qu'une adresse saisie.
                    if (url != null && url != _expectedMainUrl)
                    {
                        string upgraded = url;
                        if (TryUpgrade(ref upgraded))
                        {
                            _loopGuard.Upgraded(new Uri(upgraded), Environment.TickCount64);
                            _expectedMainUrl = upgraded;
                            _engine?.Navigate(upgraded);
                            return;
                        }
                    }
                    _expectedMainUrl = null;
                    // Page blanche d'une boucle arrêtée : la page d'explication reste affichée.
                    if (Page == TabPage.Error && url != BlankPage)
                        ShowWeb();
                    break;

                case LoadStage.Redirected:
                    // Un site qui renvoie de https:// vers http:// ne propose pas HTTPS : pas de boucle.
                    if (System.Uri.TryCreate(url, UriKind.Absolute, out Uri? redirected))
                    {
                        _loopGuard.Redirected(redirected);
                        if (redirected.Scheme == System.Uri.UriSchemeHttp && _upgradedHosts.Contains(redirected.IdnHost))
                            _app.HttpOnlyHosts.Add(redirected.IdnHost);
                    }
                    break;

                case LoadStage.Committed:
                    // Repère du journal (arrêt brutal) : le site seulement, rien d'une page privée.
                    System.Uri.TryCreate(url, UriKind.Absolute, out Uri? committed);
                    RuntimeLogBuffer.Append("[Page] " + (IsPrivate ? "navigation privée" : committed is { Host.Length: > 0 } shown ? shown.Host : url ?? "?"));
                    // Page ouverte en HTTPS par PommeBrowser : un retour en HTTP juste après vient du site.
                    if (committed is { } page && page.Scheme == System.Uri.UriSchemeHttps && _upgradedHosts.Contains(page.IdnHost) &&
                        _httpsReturn.UpgradedPageOpened(page.IdnHost, Environment.TickCount64) && _app.HttpOnlyHosts.Add(page.IdnHost))
                        RuntimeLogBuffer.Append($"[HTTPS] {page.IdnHost} : allers-retours entre HTTPS et HTTP, laissé en HTTP pour la session.");
                    _upgradedHosts.Clear();
                    _rufflePlaying = false;
                    _integratedStartPlanned = false;
                    _flashContent = null;
                    CloseFlashOverlay();
                    Window.OnTabCommitted(this);
                    Security = ComputeSecurity(url);
                    if (_engine != null)
                        _engine.Zoom = ZoomFor(url);
                    if (!IsPrivate && url != null)
                        _app.History.Record(url, _engine?.Title);
                    if (committed != null && _loopGuard.Opened(committed, Environment.TickCount64))
                        OnNavigationLoop(committed);
                    break;

                case LoadStage.Finished:
                    if (!IsPrivate && Page == TabPage.Web)
                        OnPageFinished();
                    break;
            }
            RaiseChanged();
        }

        SecurityLevel ComputeSecurity(string? url)
        {
            if (!System.Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed))
                return SecurityLevel.None;

            if (parsed.Scheme == System.Uri.UriSchemeHttps)
            {
                return _app.SessionTrustedHosts.Contains(parsed.Authority) || _app.CertificatePins.Get(parsed) != null
                    ? SecurityLevel.Trusted
                    : SecurityLevel.Secure;
            }

            if (parsed.Scheme == System.Uri.UriSchemeHttp)
                return UrlResolver.IsLocalHost(parsed.Host) ? SecurityLevel.Local : SecurityLevel.Insecure;

            return SecurityLevel.None;
        }

        void OnLoadFailed(string url, string message)
        {
            if (System.Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && _upgradedHosts.Contains(uri.IdnHost) &&
                uri.Scheme == System.Uri.UriSchemeHttps)
            {
                FallBackToHttp(uri, message);
                return;
            }

            ShowError(url, Tr("Impossible d'ouvrir la page"),
                (uri != null ? uri.Host + "\n" : string.Empty) + message,
                (Tr("Réessayer"), true, () => Navigate(url)),
                (Tr("Retour"), false, GoBackOrHome));
        }

        /// <summary>
        /// HTTPS essayé par PommeBrowser (adresse http:// passée en https://, ou saisie sans
        /// schéma) sur un site qui ne le propose pas (pas de réponse, réponse TLS invalide,
        /// certificat refusé) : la page est ouverte en http://, comme le font Chrome et Firefox,
        /// sans page d'erreur. La barre d'adresse la montre « non sécurisée ». Le site n'est plus
        /// essayé en HTTPS pendant la session.
        /// </summary>
        void FallBackToHttp(Uri httpsUri, string reason)
        {
            var http = new UriBuilder(httpsUri) { Scheme = System.Uri.UriSchemeHttp, Port = -1 }.Uri;
            string host = httpsUri.IdnHost;
            _upgradedHosts.Remove(host);
            _app.HttpOnlyHosts.Add(host);
            RuntimeLogBuffer.Append($"[HTTPS] {host} ne répond pas en HTTPS ({reason}) : ouvert en HTTP.");
            Navigate(http.AbsoluteUri);
        }

        void OnCertificateError(CertificateProblem problem)
        {
            if (!System.Uri.TryCreate(problem.Uri, UriKind.Absolute, out Uri? uri))
                return;

            // HTTPS essayé par PommeBrowser sur un site sans certificat valide : retour en HTTP.
            if (_upgradedHosts.Contains(uri.IdnHost) && uri.Scheme == System.Uri.UriSchemeHttps)
            {
                FallBackToHttp(uri, "certificat refusé");
                return;
            }

            CertificateInfo info = CertificateInfo.From(problem.Der);
            CertificatePinMatch match = _app.CertificatePins.Check(uri, info.Sha256);

            if (match == CertificatePinMatch.Matches)
            {
                // Certificat déjà approuvé (serveur du homelab…) : accepté sans question.
                AllowCertificate(uri, problem);
                Navigate(problem.Uri);
                return;
            }

            string details = Tr("Émis pour : {0}", info.Subject) + "\n" +
                             Tr("Émis par : {0}", info.Issuer) + "\n" +
                             Tr("Expire le : {0}", info.NotAfter.ToString("d", Culture)) + "\n" +
                             Tr("Empreinte SHA-256 : {0}", CertificatePinStore.FormatFingerprint(info.Sha256));

            ShowError(problem.Uri,
                match == CertificatePinMatch.Changed ? Tr("Le certificat de ce site a changé") : Tr("Connexion non sécurisée"),
                (match == CertificatePinMatch.Changed
                    ? Tr("{0} présente un certificat différent de celui que vous aviez approuvé. Si vous ne l'avez pas remplacé vous-même, quelqu'un tente peut-être d'intercepter la connexion.", uri.Authority)
                    : Tr("Le certificat de {0} n'est pas reconnu : {1}.", uri.Authority, CertificateInfo.DescribeErrors(problem.Errors))) + "\n\n" + details,
                (Tr("Retour"), true, GoBackOrHome),
                (Tr("Faire confiance à ce certificat"), false, () =>
                {
                    if (!IsPrivate)
                        _app.CertificatePins.Pin(uri, info.Sha256, info.Subject, info.Issuer, info.NotAfter);
                    AllowCertificate(uri, problem);
                    Navigate(problem.Uri);
                }),
                warning: true);
        }

        void AllowCertificate(Uri uri, CertificateProblem problem)
        {
            _app.SessionTrustedHosts.Add(uri.Authority);
            _engine?.AllowCertificate(problem);
        }

        const string BlankPage = "about:blank";
        const long LoopStopHold = 3_000;
        long _loopStoppedAt = long.MinValue / 2;

        /// <summary>
        /// La page se recharge sans fin (voir NavigationLoopGuard) : son trajet est noté au journal.
        /// Si PommeBrowser a passé des sites en HTTPS pendant la boucle, ils restent en HTTP (la
        /// boucle vient sans doute de là) ; si elle continue, ou sans passage en HTTPS, la page est
        /// quittée (ses scripts la relanceraient) et expliquée, avec de quoi réessayer.
        /// </summary>
        async void OnNavigationLoop(Uri page)
        {
            long now = Environment.TickCount64;
            RuntimeLogBuffer.Append($"[Page] Boucle : {NavigationLoopGuard.LoopCount} pages ouvertes en moins de {NavigationLoopGuard.LoopPeriod / 1000} s. Trajet : {_loopGuard.Trail}");
            List<string> upgraded = _loopGuard.UpgradedHosts(now).Where(host => !_app.HttpOnlyHosts.Contains(host)).ToList();
            Uri? forced = _loopGuard.ForcedToHttps(now);
            _loopGuard.Reset();
            foreach (string host in upgraded)
                _app.HttpOnlyHosts.Add(host);
            if (upgraded.Count > 0)
                RuntimeLogBuffer.Append("[HTTPS] Boucle avec le passage en HTTPS : " + string.Join(", ", upgraded) + " laissé en HTTP pour la session.");

            string url = page.AbsoluteUri;
            string site = page.Host;
            _loopStoppedAt = now;

            // La page demande http:// et le moteur la repasse aussitôt en https:// : HTTPS imposé par
            // sa mémoire (HSTS), sans doute retenue quand PommeBrowser a ouvert le site en HTTPS.
            // Elle est effacée (une fois par site et par session), puis la page est rouverte en HTTP.
            if (forced != null && _app.HttpsMemoryForgotten.Add(forced.IdnHost))
            {
                _app.HttpOnlyHosts.Add(forced.IdnHost);
                _engine?.Navigate(BlankPage);
                bool forgotten = await EngineHost.ForgetHttpsMemoryAsync(IsPrivate, TimeSpan.FromHours(1));
                RuntimeLogBuffer.Append(forgotten
                    ? $"[HTTPS] {forced.IdnHost} : la page veut HTTP mais le moteur impose HTTPS (mémoire HSTS) ; mémoire HTTPS de la dernière heure effacée, page rouverte en HTTP."
                    : $"[HTTPS] {forced.IdnHost} : la page veut HTTP mais le moteur impose HTTPS (mémoire HSTS), qui n'a pas pu être effacée.");
                if (forgotten)
                {
                    Navigate(forced.AbsoluteUri);
                    return;
                }
            }
            else if (upgraded.Count > 0 && forced == null)
            {
                // La boucle venait sans doute du passage en HTTPS : elle continue en HTTP, ou s'arrête.
                return;
            }

            _engine?.Navigate(BlankPage);
            var blocker = _app.AdBlock;
            bool filtered = AdBlockService.IsSupported && blocker.Settings.Enabled &&
                            !blocker.IsLocal(site) && !blocker.IsSiteAllowed(site);
            (string, bool, Action) retry = (Tr("Réessayer"), true, () => Navigate(url));
            (string, bool, Action) other = filtered
                ? (Tr("Réessayer sans la protection web"), false, () =>
                {
                    blocker.SetSiteAllowed(site, true);
                    RuntimeLogBuffer.Append("[Page] Protection web en pause sur " + site + " après une boucle.");
                    Navigate(url);
                })
                : (Tr("Retour"), false, () =>
                {
                    _loopStoppedAt = long.MinValue / 2;
                    GoBackOrHome();
                });
            ShowError(url, Tr("Cette page se recharge sans arrêt"),
                Tr("{0} s'est rechargée plusieurs fois par seconde : PommeBrowser l'a arrêtée pour que l'onglet reste utilisable. La protection web ou un réglage du site peut en être la cause.", site),
                retry, other, warning: true);
        }

        void GoBackOrHome()
        {
            // Échec avant l'affichage : le moteur montre encore la page précédente, on y revient.
            if (_webShownOnce && _engine?.Uri is { Length: > 0 } current && current != "about:blank" &&
                !string.Equals(current, _errorUrl, StringComparison.Ordinal))
            {
                ShowWeb();
                return;
            }

            if (_engine?.CanGoBack == true)
            {
                ShowWeb();
                _engine.GoBack();
            }
            else
            {
                ShowHome();
            }
        }

        // ---------------------------------------------------------------
        // Pages de PommeBrowser
        // ---------------------------------------------------------------

        public void ShowWeb()
        {
            if (Page == TabPage.Legacy)
            {
                StopBasilisk();
                _legacyUri = null;
            }
            RemovePage();
            Page = TabPage.Web;
            _webShownOnce = true;
            if (_web != null)
                _web.IsVisible = true;
            RaiseChanged();
        }

        /// <summary>Affiche une page de PommeBrowser à la place de la page web.</summary>
        public void ShowPage(TabPage kind, Control page)
        {
            CloseFlashOverlay();
            RemovePage();
            _page = page;
            _host.Children.Add(page);
            // La vue native passerait par-dessus : elle est masquée.
            if (_web != null)
                _web.IsVisible = false;
            Page = kind;
            Security = SecurityLevel.None;
            RaiseChanged();
        }

        public void ShowHome() => ShowPage(TabPage.Home, new HomePage(Window));

        void RemovePage()
        {
            ForgetLegacyView();
            if (_page == null)
                return;
            _host.Children.Remove(_page);
            (_page as IDisposable)?.Dispose();
            _page = null;
        }

        public Control? CurrentPage => _page;

        void ShowError(string? url, string title, string description, params (string Label, bool Primary, Action Action)[] buttons)
            => ShowError(url, title, description, buttons, warning: false);

        void ShowError(string? url, string title, string description, (string Label, bool Primary, Action Action) first, (string Label, bool Primary, Action Action) second, bool warning)
            => ShowError(url, title, description, new[] { first, second }, warning);

        void ShowError(string? url, string title, string description, (string Label, bool Primary, Action Action)[] buttons, bool warning)
        {
            _errorUrl = url;
            ShowPage(TabPage.Error, new StatusPage(warning ? "IconWarning" : "IconGlobe", title, description, buttons));
        }

        // ---------------------------------------------------------------
        // Session et fermeture
        // ---------------------------------------------------------------

        /// <summary>État enregistré dans la session (null : rien à rouvrir, ou onglet privé).</summary>
        public TabState? GetSessionState()
        {
            if (IsPrivate)
                return null;
            string url = Page == TabPage.Legacy && _legacyUri != null ? _legacyUri.AbsoluteUri : WebUrl;
            if (!SessionStore.IsRestorable(url))
                return null;
            return new TabState
            {
                Url = url,
                Title = _engine?.Title ?? _pendingTitle,
                IsPinned = IsPinned,
                IsLegacy = Page == TabPage.Legacy,
                LegacyUrl = Page == TabPage.Legacy ? _legacyUri?.AbsoluteUri : null
            };
        }

        /// <summary>Déplacement vers une autre fenêtre : la vue native garde sa page pendant le changement de parent.</summary>
        public IDisposable BeginMove() => _web?.BeginReparenting() ?? new NoMove();

        sealed class NoMove : IDisposable
        {
            public void Dispose()
            {
            }
        }

        /// <summary>
        /// Mise en veille : la vue native est fermée (mémoire libérée), l'adresse et le titre sont
        /// gardés ; la page est rechargée à la prochaine sélection.
        /// </summary>
        public void Suspend()
        {
            if (_engine == null || _web == null || Page != TabPage.Web)
                return;
            _pendingUrl = _engine.Uri ?? _pendingUrl;
            _pendingTitle = _engine.Title ?? _pendingTitle;
            CloseFlashOverlay();
            _engine.Dispose();
            _engine = null;
            _host.Children.Remove(_web);
            _web = null;
            RaiseChanged();
        }

        /// <summary>Onglet fermé : sa vue, ses boîtes et son Basilisk se ferment aussi.</summary>
        public void Close()
        {
            StopBasilisk();
            CloseFlashOverlay();
            RemovePage();
            _engine?.Dispose();
            _engine = null;
            if (_web != null)
            {
                _host.Children.Remove(_web);
                _web = null;
            }
        }
    }
}
