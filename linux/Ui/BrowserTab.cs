using System;
using System.Collections.Generic;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Profiles.Credentials;
using MyHomelabBrowser.classes.Security;
using PommeBrowser.Linux.Core;
using PommeBrowser.Linux.Web;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Ui
{
    enum TabContent
    {
        Web,
        Home,
        History,
        Favorites,
        Services,
        Passwords,
        Error
    }

    enum SecurityLevel
    {
        None,
        Secure,
        Trusted,
        Mixed,
        Insecure,
        Local
    }

    /// <summary>
    /// Onglet : une page web (WebKit.WebView) ou une page de PommeBrowser (accueil, historique,
    /// favoris, services, erreur), affichées tour à tour dans une pile.
    /// </summary>
    sealed class BrowserTab
    {
        readonly BrowserApplication _app;
        readonly Gtk.Box _slot;
        readonly Gtk.Stack _stack;
        readonly Gtk.Label _linkStatus;
        readonly WebKit.UserContentManager _content;
        readonly TabFilterState _filter = new();
        readonly HashSet<string> _upgradedHosts = new(StringComparer.OrdinalIgnoreCase);
        readonly List<Adw.Dialog> _dialogs = new();

        Gtk.Widget? _page;
        string? _pendingUri;
        string? _pendingTitle;
        string? _expectedMainUri;
        string? _errorUri;
        bool _ruffleAttached;
        bool _webShownOnce;

        public BrowserTab(BrowserApplication app, BrowserWindow window, WebKit.WebView? related = null)
        {
            _app = app;
            Window = window;
            IsPrivate = window.IsPrivate;

            _content = WebKit.UserContentManager.New();
            WebKit.UserContentManager.ScriptMessageReceivedSignal.Connect(_content, (_, args) => OnRuffleMessage(args.Value.ToString()), false, RuffleSupport.MessageHandler);
            AttachRuffle();

            // Coffre : pas de capture en navigation privée (rien n'y est conservé).
            if (!IsPrivate)
            {
                WebKit.UserContentManager.ScriptMessageReceivedSignal.Connect(_content, (_, args) => OnCredentialMessage(args.Value.ToString()), false, CredentialCapture.MessageHandler);
                app.Engine.Credentials.Attach(_content);
            }

            var properties = new List<GObject.ConstructArgument>
            {
                new("user-content-manager", new GObject.Value(_content)),
                new("settings", new GObject.Value(app.Engine.WebSettings))
            };
            properties.Add(related != null
                ? new GObject.ConstructArgument("related-view", new GObject.Value(related))
                : new GObject.ConstructArgument("network-session", new GObject.Value(window.Session)));

            Web = WebKit.WebView.NewWithProperties(properties.ToArray());
            Web.SetVexpand(true);
            Web.SetHexpand(true);

            _linkStatus = Gtk.Label.New(null);
            _linkStatus.AddCssClass("link-status");
            _linkStatus.SetHalign(Gtk.Align.Start);
            _linkStatus.SetValign(Gtk.Align.End);
            _linkStatus.SetEllipsize(Pango.EllipsizeMode.Middle);
            _linkStatus.SetMaxWidthChars(90);
            _linkStatus.SetCanTarget(false);
            _linkStatus.SetVisible(false);

            var overlay = Gtk.Overlay.New();
            overlay.SetChild(Web);
            overlay.AddOverlay(_linkStatus);

            _stack = Gtk.Stack.New();
            _stack.SetTransitionType(Gtk.StackTransitionType.Crossfade);
            _stack.SetTransitionDuration(120);
            _stack.AddNamed(overlay, "web");
            _stack.SetVexpand(true);
            _stack.SetHexpand(true);

            // L'onglet affiche son contenu dans son emplacement, sauf en vue côte à côte où le
            // contenu passe dans un volet (voir BrowserWindow.Split).
            _slot = Gtk.Box.New(Gtk.Orientation.Vertical, 0);
            _slot.Append(_stack);
            _holder = _slot;

            ConnectWebSignals();
            ApplyFilter(null);
        }

        public BrowserWindow Window { get; set; }

        /// <summary>Emplacement de l'onglet dans la vue des onglets (Adw.TabView).</summary>
        public Gtk.Widget Widget => _slot;

        /// <summary>Dernière sélection (onglet associé par défaut à la vue côte à côte).</summary>
        public DateTime LastActivated { get; set; } = DateTime.MinValue;
        /// <summary>Page de l'onglet dans sa fenêtre (null une fois l'onglet fermé).</summary>
        public Adw.TabPage? Page { get; set; }
        public WebKit.WebView Web { get; }
        public bool IsPrivate { get; }
        public TabContent Content { get; private set; } = TabContent.Web;
        public SecurityLevel Security { get; private set; }
        public bool HasPendingLoad => _pendingUri != null;

        /// <summary>Icône du site (null tant qu'il n'en a pas fourni).</summary>
        public Gdk.Texture? Favicon { get; private set; }

        /// <summary>Titre, adresse, chargement ou sécurité modifiés.</summary>
        public event Action<BrowserTab>? Changed;

        public string Title
        {
            get
            {
                if (Content != TabContent.Web)
                    return ContentTitle(Content);
                string? title = Web.Title();
                if (!string.IsNullOrWhiteSpace(title))
                    return title;
                if (_pendingTitle != null)
                    return _pendingTitle;
                string uri = Uri;
                return uri.Length > 0 ? WebViewExtensions.ForDisplay(uri) : Tr("Nouvel onglet");
            }
        }

        /// <summary>Adresse de la page web (vide pour les pages de PommeBrowser).</summary>
        public string Uri => Content switch
        {
            TabContent.Web => Web.Url() ?? _pendingUri ?? string.Empty,
            TabContent.Error => _errorUri ?? string.Empty,
            _ => string.Empty
        };

        /// <summary>Adresse de la page web, même si une page de PommeBrowser est affichée par-dessus.</summary>
        public string WebUri => Web.Url() ?? _pendingUri ?? string.Empty;

        public bool IsLoading => Content == TabContent.Web && Web.GetIsLoading();

        public double Progress => IsLoading ? Web.GetEstimatedLoadProgress() : 0;

        public bool CanGoBack => Content != TabContent.Web ? _webShownOnce : Web.CanGoBack();

        public bool CanGoForward => Content == TabContent.Web && Web.CanGoForward();

        static string ContentTitle(TabContent content) => content switch
        {
            TabContent.Home => Tr("Nouvel onglet"),
            TabContent.History => Tr("Historique"),
            TabContent.Favorites => Tr("Favoris"),
            TabContent.Services => Tr("Services du homelab"),
            TabContent.Passwords => Tr("Mots de passe"),
            _ => Tr("Page indisponible")
        };

        // ---------------------------------------------------------------
        // Navigation
        // ---------------------------------------------------------------

        public void Navigate(string url)
        {
            _pendingUri = null;
            _pendingTitle = null;
            TryUpgrade(ref url);
            _expectedMainUri = url;
            ApplyFilter(url);
            ShowWeb();
            Web.LoadUri(url);
        }

        /// <summary>Onglet restauré : la page n'est chargée qu'à sa première sélection.</summary>
        public void SetPending(string url, string? title)
        {
            _pendingUri = url;
            _pendingTitle = title;
            ShowWeb();
            Changed?.Invoke(this);
        }

        public void LoadPendingIfNeeded()
        {
            if (_pendingUri is { } url)
                Navigate(url);
        }

        public void GoBack()
        {
            if (Content != TabContent.Web)
            {
                if (_webShownOnce)
                    ShowWeb();
                return;
            }

            if (Web.GetBackForwardList().GetBackItem() is { } item)
                ExpectMainNavigation(item.GetUri());
            Web.GoBack();
        }

        public void GoForward()
        {
            if (Web.GetBackForwardList().GetForwardItem() is { } item)
                ExpectMainNavigation(item.GetUri());
            Web.GoForward();
        }

        public void Reload(bool bypassCache = false)
        {
            if (Content != TabContent.Web)
            {
                if (Content == TabContent.Error && WebUri.Length > 0)
                    Navigate(WebUri);
                return;
            }

            if (_pendingUri != null)
            {
                LoadPendingIfNeeded();
                return;
            }

            ExpectMainNavigation(Web.Url());
            ApplyFilter(Web.Url());
            if (bypassCache)
                Web.ReloadBypassCache();
            else
                Web.Reload();
        }

        public void Stop() => Web.StopLoading();

        void ExpectMainNavigation(string? uri)
        {
            _expectedMainUri = uri;
            ApplyFilter(uri);
        }

        /// <summary>http:// devient https:// (hors réseau local et sites autorisés en HTTP).</summary>
        bool TryUpgrade(ref string url)
        {
            if (!_app.Settings.HttpsUpgrade || !System.Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                return false;

            if (!HttpsUpgradePolicy.ShouldUpgrade(uri, host => _app.SiteSecurity.IsHttpAllowed(host) || Window.HttpOnlyHosts.Contains(host)))
                return false;

            _upgradedHosts.Add(uri.IdnHost);
            url = HttpsUpgradePolicy.Upgrade(uri).AbsoluteUri;
            return true;
        }

        void ApplyFilter(string? pageUrl)
            => _app.Engine.AdBlocker.ApplyTo(_content, _filter, pageUrl);

        /// <summary>Bloqueur remplacé ou site autorisé/interdit : l'onglet se met à jour.</summary>
        public void RefreshFilter() => ApplyFilter(WebUri);

        /// <summary>Boîte liée à la page affichée : fermée si l'onglet change de page ou se ferme.</summary>
        public void TrackDialog(Adw.Dialog dialog)
        {
            _dialogs.Add(dialog);
            dialog.OnClosed += (_, _) => _dialogs.Remove(dialog);
        }

        void CloseDialogs()
        {
            foreach (Adw.Dialog dialog in _dialogs.ToArray())
                dialog.ForceClose();
            _dialogs.Clear();
        }

        /// <summary>Onglet fermé.</summary>
        public void OnClosed() => CloseDialogs();

        Gtk.Box _holder;

        /// <summary>Place le contenu de l'onglet dans un volet de la vue côte à côte (null : son emplacement).</summary>
        public void MoveViewTo(Gtk.Box? holder)
        {
            holder ??= _slot;
            if (ReferenceEquals(holder, _holder))
                return;
            _holder.Remove(_stack);
            holder.Append(_stack);
            _holder = holder;
        }

        public bool IsViewInSlot => ReferenceEquals(_holder, _slot);

        public void OnSettingsChanged()
        {
            AttachRuffle();
            RefreshFilter();
        }

        void AttachRuffle()
        {
            bool wanted = _app.Settings.EnableRuffle && _app.Engine.Ruffle.IsAvailable;
            if (wanted == _ruffleAttached)
                return;

            if (wanted)
                _app.Engine.Ruffle.Attach(_content);
            else
                _app.Engine.Ruffle.Detach(_content);
            _ruffleAttached = wanted;
        }

        // ---------------------------------------------------------------
        // Pages de PommeBrowser
        // ---------------------------------------------------------------

        public void ShowWeb()
        {
            Content = TabContent.Web;
            _webShownOnce = true;
            _stack.SetVisibleChildName("web");
            RemovePage();
            Changed?.Invoke(this);
        }

        public void ShowPage(TabContent content, Gtk.Widget page)
        {
            RemovePage();
            _page = page;
            _stack.AddChild(page);
            _stack.SetVisibleChild(page);
            Content = content;
            Security = SecurityLevel.None;
            Changed?.Invoke(this);
        }

        public void ShowHome() => ShowPage(TabContent.Home, new HomeView(_app, Window).Widget);

        void RemovePage()
        {
            if (_page == null)
                return;

            Gtk.Widget page = _page;
            _page = null;
            // Retiré après la transition, pour ne pas couper l'animation.
            GLib.Functions.TimeoutAdd(0, 200, () =>
            {
                if (page.GetParent() != null)
                    _stack.Remove(page);
                return false;
            });
        }

        void ShowError(string? uri, string icon, string title, string description, params (string Label, string? Style, Action Action)[] buttons)
        {
            _errorUri = uri;
            var status = Adw.StatusPage.New();
            status.SetIconName(icon);
            status.SetTitle(title);
            status.SetDescription(description);

            var box = Gtk.Box.New(Gtk.Orientation.Horizontal, 12);
            box.SetHalign(Gtk.Align.Center);
            foreach ((string label, string? style, Action action) in buttons)
            {
                var button = Gtk.Button.NewWithLabel(label);
                button.AddCssClass("pill");
                if (style != null)
                    button.AddCssClass(style);
                button.OnClicked += (_, _) => action();
                box.Append(button);
            }
            status.SetChild(box);

            ShowPage(TabContent.Error, status);
        }

        void GoBackOrHome()
        {
            if (Web.CanGoBack())
            {
                ShowWeb();
                GoBack();
            }
            else
            {
                ShowHome();
            }
        }

        // ---------------------------------------------------------------
        // Signaux de WebKit
        // ---------------------------------------------------------------

        void ConnectWebSignals()
        {
            Web.OnNotify += (_, args) =>
            {
                switch (args.Pspec.GetName())
                {
                    case "title":
                        if (!IsPrivate && Web.Url() is { } uri && Web.Title() is { Length: > 0 } title)
                            _app.History.UpdateTitle(uri, title);
                        Changed?.Invoke(this);
                        break;
                    case "uri":
                    case "estimated-load-progress":
                    case "is-loading":
                        Changed?.Invoke(this);
                        break;
                    case "favicon":
                        Favicon = Web.GetFavicon();
                        Changed?.Invoke(this);
                        break;
                }
            };

            Web.OnLoadChanged += (_, args) => OnLoadChanged(args.LoadEvent);
            Web.OnLoadFailed += (_, args) => OnLoadFailed(args.FailingUri, args.Error);
            Web.OnLoadFailedWithTlsErrors += (_, args) => OnTlsError(args.FailingUri, args.Certificate, args.Errors);
            Web.OnDecidePolicy += (_, args) => OnDecidePolicy(args.Decision, args.DecisionType);
            Web.OnCreate += (_, args) => Window.CreateRelatedTab(this, args.NavigationAction.GetRequest().Url()).Web;
            Web.OnClose += (_, _) => Window.CloseTab(this);
            Web.OnPermissionRequest += (_, args) => PermissionPrompt.Handle(_app, this, args.Request);
            Web.OnEnterFullscreen += (_, _) => { Window.SetWebFullscreen(true); return false; };
            Web.OnLeaveFullscreen += (_, _) => { Window.SetWebFullscreen(false); return false; };
            Web.OnInsecureContentDetected += (_, _) =>
            {
                if (Security is SecurityLevel.Secure or SecurityLevel.Trusted)
                {
                    Security = SecurityLevel.Mixed;
                    Changed?.Invoke(this);
                }
            };
            Web.OnMouseTargetChanged += (_, args) =>
            {
                string? link = args.HitTestResult.LinkUrl();
                _linkStatus.SetLabel(link ?? string.Empty);
                _linkStatus.SetVisible(!string.IsNullOrEmpty(link));
            };
            Web.OnWebProcessTerminated += (_, args) =>
            {
                if (args.Reason == WebKit.WebProcessTerminationReason.TerminatedByApi)
                    return;

                ShowError(WebUri, "computer-fail-symbolic",
                    Tr("La page a cessé de fonctionner"),
                    args.Reason == WebKit.WebProcessTerminationReason.ExceededMemoryLimit
                        ? Tr("Elle utilisait trop de mémoire.")
                        : Tr("Le processus qui l'affichait s'est arrêté."),
                    (Tr("Recharger"), "suggested-action", () => Navigate(WebUri)));
            };
        }

        void OnLoadChanged(WebKit.LoadEvent loadEvent)
        {
            string? uri = Web.Url();
            switch (loadEvent)
            {
                case WebKit.LoadEvent.Started:
                    // Seule la page principale émet cet événement : les cadres passent aussi par
                    // decide-policy, sans pouvoir y être distingués.
                    if (uri != null && uri != _expectedMainUri)
                    {
                        string upgraded = uri;
                        if (TryUpgrade(ref upgraded))
                        {
                            _expectedMainUri = upgraded;
                            ApplyFilter(upgraded);
                            Web.LoadUri(upgraded);
                            return;
                        }
                        ApplyFilter(uri);
                    }
                    _expectedMainUri = null;
                    if (Content == TabContent.Error)
                        ShowWeb();
                    break;

                case WebKit.LoadEvent.Redirected:
                    // Un site qui renvoie de https:// vers http:// ne propose pas HTTPS : pas de boucle.
                    if (System.Uri.TryCreate(uri, UriKind.Absolute, out Uri? redirected) &&
                        redirected.Scheme == System.Uri.UriSchemeHttp &&
                        _upgradedHosts.Contains(redirected.IdnHost))
                    {
                        Window.HttpOnlyHosts.Add(redirected.IdnHost);
                    }
                    ApplyFilter(uri);
                    break;

                case WebKit.LoadEvent.Committed:
                    _upgradedHosts.Clear();
                    CloseDialogs();
                    Security = ComputeSecurity(uri);
                    if (System.Uri.TryCreate(uri, UriKind.Absolute, out Uri? committed))
                        Web.SetZoomLevel(_app.Zoom.Get(committed));
                    if (!IsPrivate && uri != null)
                        _app.History.Record(uri, Web.Title());
                    break;

                case WebKit.LoadEvent.Finished:
                    if (!IsPrivate && Content == TabContent.Web)
                        _app.Vault.AutoFill(this);
                    break;
            }

            Changed?.Invoke(this);
        }

        SecurityLevel ComputeSecurity(string? uri)
        {
            if (!System.Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed))
                return SecurityLevel.None;

            if (parsed.Scheme == System.Uri.UriSchemeHttps)
            {
                return _app.SessionTrustedHosts.Contains(parsed.Authority) ||
                       _app.CertificatePins.Get(parsed) != null
                    ? SecurityLevel.Trusted
                    : SecurityLevel.Secure;
            }

            if (parsed.Scheme == System.Uri.UriSchemeHttp)
                return UrlResolver.IsLocalHost(parsed.Host) ? SecurityLevel.Local : SecurityLevel.Insecure;

            return SecurityLevel.None;
        }

        bool OnLoadFailed(string failingUri, GLib.Error error)
        {
            // Navigation remplacée par une autre, ou changée en téléchargement : rien à afficher.
            if (error.Matches(WebKit.Functions.NetworkErrorQuark(), (int)WebKit.NetworkError.Cancelled) ||
                error.Matches(WebKit.Functions.PolicyErrorQuark(), (int)WebKit.PolicyError.FrameLoadInterruptedByPolicyChange) ||
                error.Matches(WebKit.Functions.NetworkErrorQuark(), (int)WebKit.NetworkError.FileDoesNotExist) && failingUri.StartsWith("about:", StringComparison.Ordinal))
            {
                return false;
            }

            if (System.Uri.TryCreate(failingUri, UriKind.Absolute, out Uri? uri) && _upgradedHosts.Contains(uri.IdnHost))
            {
                ShowHttpsUnavailable(uri);
                return true;
            }

            ShowError(failingUri, "network-error-symbolic",
                Tr("Impossible d'ouvrir la page"),
                (uri != null ? uri.Host + "\n" : string.Empty) + error.Message,
                (Tr("Réessayer"), "suggested-action", () => Navigate(failingUri)),
                (Tr("Retour"), null, GoBackOrHome));
            return true;
        }

        void ShowHttpsUnavailable(Uri httpsUri)
        {
            var http = new UriBuilder(httpsUri) { Scheme = System.Uri.UriSchemeHttp, Port = -1 }.Uri;
            string host = httpsUri.IdnHost;
            ShowError(http.AbsoluteUri, "channel-insecure-symbolic",
                Tr("Ce site ne propose pas de connexion sécurisée"),
                Tr("{0} n'a pas répondu en HTTPS. En HTTP, ce que vous envoyez et recevez peut être lu ou modifié sur le réseau.", host),
                (Tr("Retour"), "suggested-action", GoBackOrHome),
                (Tr("Continuer en HTTP"), "destructive-action", () =>
                {
                    _app.SiteSecurity.Set(host, SiteSecurityStore.InsecureHttp, true);
                    _upgradedHosts.Remove(host);
                    Navigate(http.AbsoluteUri);
                }));
        }

        bool OnTlsError(string failingUri, Gio.TlsCertificate certificate, Gio.TlsCertificateFlags errors)
        {
            if (!System.Uri.TryCreate(failingUri, UriKind.Absolute, out Uri? uri))
                return false;

            // Passage automatique en HTTPS sur un site qui n'a pas de certificat valide : on propose HTTP.
            if (_upgradedHosts.Contains(uri.IdnHost))
            {
                ShowHttpsUnavailable(uri);
                return true;
            }

            CertificateInfo info = CertificateInfo.From(certificate);
            CertificatePinMatch match = _app.CertificatePins.Check(uri, info.Sha256);

            if (match == CertificatePinMatch.Matches)
            {
                // Certificat déjà approuvé (serveur du homelab…) : accepté sans question.
                AllowCertificate(uri, certificate);
                Navigate(failingUri);
                return true;
            }

            string reasons = CertificateInfo.DescribeErrors(errors);
            string details = Tr("Émis pour : {0}", info.Subject) + "\n" +
                             Tr("Émis par : {0}", info.Issuer) + "\n" +
                             Tr("Expire le : {0}", info.NotAfter.ToString("d", Culture)) + "\n" +
                             Tr("Empreinte SHA-256 : {0}", CertificatePinStore.FormatFingerprint(info.Sha256));

            ShowError(failingUri, match == CertificatePinMatch.Changed ? "dialog-warning-symbolic" : "channel-insecure-symbolic",
                match == CertificatePinMatch.Changed ? Tr("Le certificat de ce site a changé") : Tr("Connexion non sécurisée"),
                (match == CertificatePinMatch.Changed
                    ? Tr("{0} présente un certificat différent de celui que vous aviez approuvé. Si vous ne l'avez pas remplacé vous-même, quelqu'un tente peut-être d'intercepter la connexion.", uri.Authority)
                    : Tr("Le certificat de {0} n'est pas reconnu : {1}.", uri.Authority, reasons)) + "\n\n" + details,
                (Tr("Retour"), "suggested-action", GoBackOrHome),
                (Tr("Faire confiance à ce certificat"), "destructive-action", () =>
                {
                    if (!IsPrivate)
                        _app.CertificatePins.Pin(uri, info.Sha256, info.Subject, info.Issuer, info.NotAfter);
                    AllowCertificate(uri, certificate);
                    Navigate(failingUri);
                }));
            return true;
        }

        void AllowCertificate(Uri uri, Gio.TlsCertificate certificate)
        {
            _app.SessionTrustedHosts.Add(uri.Authority);
            Window.Session.AllowTlsCertificateForHost(certificate, uri.Host);
        }

        bool OnDecidePolicy(WebKit.PolicyDecision decision, WebKit.PolicyDecisionType type)
        {
            // Fichier que WebKit ne sait pas afficher, ou envoyé « en pièce jointe » : téléchargement.
            // (WebKitGTK 6 ne le fait pas de lui-même : la page resterait bloquée.)
            if (type == WebKit.PolicyDecisionType.Response && decision is WebKit.ResponsePolicyDecision response)
            {
                if (response.IsMainFrameMainResource() && (!response.IsMimeTypeSupported() || IsAttachment(response.GetResponse())))
                {
                    decision.Download();
                    return true;
                }
                return false;
            }

            if (type != WebKit.PolicyDecisionType.NavigationAction || decision is not WebKit.NavigationPolicyDecision navigation)
                return false;

            WebKit.NavigationAction action = navigation.GetNavigationAction();
            string? target = action.GetRequest().Url();
            if (target == null)
                return false;

            // Clic du milieu ou Ctrl+clic sur un lien : nouvel onglet en arrière-plan.
            bool control = (action.GetModifiers() & (uint)Gdk.ModifierType.ControlMask) != 0;
            if (action.GetNavigationType() == WebKit.NavigationType.LinkClicked && (action.GetMouseButton() == 2 || control))
            {
                Window.OpenInNewTab(target, background: true, opener: this);
                decision.Ignore();
                return true;
            }

            if (target == _expectedMainUri)
                ApplyFilter(target);
            return false;
        }

        static bool IsAttachment(WebKit.URIResponse response)
        {
            try
            {
                string? disposition = response.GetHttpHeaders()?.GetOne("Content-Disposition");
                return disposition != null && disposition.TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception)
            {
                // Réponse sans en-têtes HTTP (file://, data:…).
                return false;
            }
        }

        void OnRuffleMessage(string? status)
        {
            if (status == "blocked")
                Window.ShowToast(Tr("Ce site empêche Ruffle de démarrer : le contenu Flash ne peut pas être lu."));
        }

        /// <summary>
        /// Identifiants envoyés par le formulaire de la page. L'origine retenue est celle de la page
        /// affichée : le script ne peut que la confirmer (un message arrivé après un changement de page est écarté).
        /// </summary>
        void OnCredentialMessage(string? json)
        {
            if (IsPrivate || Content != TabContent.Web || !CredentialOrigin.TryCreateTrusted(Web.Url(), out string origin))
                return;

            if (CredentialScripts.TryParseSubmission(json, origin, out CredentialCandidate? candidate))
                _app.Vault.OnSubmitted(Window, candidate!);
        }

        public SessionTab? GetSessionTab()
        {
            string url = WebUri;
            return SessionStore.IsRestorable(url) ? new SessionTab { Url = url, Title = Web.Title() ?? _pendingTitle } : null;
        }
    }
}
