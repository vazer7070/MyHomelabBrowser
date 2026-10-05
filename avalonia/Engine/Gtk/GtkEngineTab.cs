using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Threading;
using MyHomelabBrowser.classes;
using static PommeBrowser.Engine.Gtk.WebKitGtk;

namespace PommeBrowser.Engine.Gtk
{
    /// <summary>
    /// Onglet WebKitGTK : la vue est créée par Avalonia.Controls.WebView, PommeBrowser s'y
    /// branche directement (signaux de WebKit) pour tout ce que le contrôle n'offre pas.
    /// Les signaux arrivent sur le fil GLib ; l'état est recopié puis signalé à l'interface.
    /// </summary>
    sealed unsafe class GtkEngineTab : IEngineTab
    {
        const int GSignalMatchId = 1;
        const int InjectAllFrames = 0;
        const int InjectTopFrame = 1;
        const int InjectAtStart = 0;
        const int InjectAtEnd = 1;
        const int MouseMiddle = 2;
        const int TerminatedByApi = 2;
        const int ExceededMemoryLimit = 1;

        static readonly nint LoadChangedCallback = (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&OnLoadChanged;
        static readonly nint LoadFailedCallback = (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, nint, nint, int>)&OnLoadFailed;
        static readonly nint TlsErrorCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int, nint, int>)&OnTlsError;
        static readonly nint DecidePolicyCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, int, nint, int>)&OnDecidePolicy;
        static readonly nint NotifyCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnNotify;
        static readonly nint HistoryChangedCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint, void>)&OnHistoryChanged;
        static readonly nint MouseTargetCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, uint, nint, void>)&OnMouseTarget;
        static readonly nint PermissionCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnPermission;
        static readonly nint EnterFullscreenCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&OnEnterFullscreen;
        static readonly nint LeaveFullscreenCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, int>)&OnLeaveFullscreen;
        static readonly nint CloseCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnClose;
        static readonly nint TerminatedCallback = (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&OnTerminated;
        static readonly nint InsecureCallback = (nint)(delegate* unmanaged[Cdecl]<nint, int, nint, void>)&OnInsecure;
        static readonly nint CreateCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, nint>)&OnCreate;
        static readonly nint CountedCallback = (nint)(delegate* unmanaged[Cdecl]<nint, uint, nint, void>)&OnCounted;
        static readonly nint NotFoundCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnNotFound;
        static readonly nint ScriptMessageCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnScriptMessage;
        static readonly nint EvaluatedCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnEvaluated;
        static readonly nint CookiesReadCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnCookiesRead;
        static readonly nint CookieStoredCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnCookieStored;
        static readonly nint KeyPressCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnKeyPress;
        static readonly nint ButtonPressCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnButtonPress;
        static readonly nint WindowFocusInCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnWindowFocusIn;

        /// <summary>État lu par l'interface, remplacé d'un bloc depuis le fil GLib.</summary>
        sealed record Snapshot(string? Title, string? Uri, bool IsLoading, double Progress, bool CanGoBack, bool CanGoForward);

        sealed record MessageChannel(GtkEngineTab Tab, string Name);

        readonly NativeWebView _host;
        readonly List<GSignal> _signals = new();
        readonly Dictionary<string, nint> _scripts = new();
        readonly HashSet<string> _handlers = new();
        readonly List<PermissionRequest> _pendingPermissions = new();
        readonly List<nint> _certificates = new();

        nint _view;
        nint _window;
        nint _manager;
        nint _finder;
        bool _filterApplied;
        string? _hoveredLink;
        string? _tlsFailedUri;
        volatile bool _disposed;
        volatile Snapshot _state = new(null, null, false, 0, false, false);
        volatile bool _pageHasKeyboard;
        nint _parentWindow;
        int _focusAttempts;
        double _zoom = 1;

        public GtkEngineTab(NativeWebView host, nint view, bool isPrivate)
        {
            _host = host;
            _view = view;
            IsPrivate = isPrivate;
            Glib.Post(Setup);
            // Focus quitté pour un champ d'Avalonia, y compris dans un Popup (recherche dans la page).
            _host.GotFocus += OnHostFocusChanged;
            _host.LostFocus += OnHostFocusChanged;
            SyncKeyboard(force: true);
        }

        void OnHostFocusChanged(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
            => Dispatcher.UIThread.Post(() => SyncKeyboard());

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
                double zoom = _zoom;
                OnView(view => webkit_web_view_set_zoom_level(view, zoom));
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
        public event Action? InsecureContentDetected;
        public event Action<int>? FindMatchesCounted;
        public event Action<string, string>? ScriptMessage;
        public event Action<Avalonia.Input.Key, Avalonia.Input.KeyModifiers>? ShortcutPressed;

        // ---------------------------------------------------------------
        // Mise en place (fil GLib)
        // ---------------------------------------------------------------

        void Setup()
        {
            if (_disposed || _view == 0)
                return;

            g_object_ref(_view);
            GtkEngine.Register(_view, this);

            // Le gestionnaire du contrôle Avalonia répond lui-même aux décisions de réponse (et
            // interroge l'interface de façon bloquante) : il est remplacé par celui de PommeBrowser.
            nuint type = webkit_web_view_get_type();
            g_signal_handlers_disconnect_matched(_view, GSignalMatchId, g_signal_lookup("decide-policy", type), 0, 0, 0, 0);
            g_signal_handlers_disconnect_matched(_view, GSignalMatchId, g_signal_lookup("resource-load-started", type), 0, 0, 0, 0);

            Connect(_view, "decide-policy", DecidePolicyCallback);
            Connect(_view, "load-changed", LoadChangedCallback);
            Connect(_view, "load-failed", LoadFailedCallback);
            Connect(_view, "load-failed-with-tls-errors", TlsErrorCallback);
            foreach (string property in new[] { "title", "uri", "estimated-load-progress", "is-loading", "favicon" })
                Connect(_view, "notify::" + property, NotifyCallback);
            Connect(webkit_web_view_get_back_forward_list(_view), "changed", HistoryChangedCallback);
            Connect(_view, "mouse-target-changed", MouseTargetCallback);
            Connect(_view, "permission-request", PermissionCallback);
            Connect(_view, "enter-fullscreen", EnterFullscreenCallback);
            Connect(_view, "leave-fullscreen", LeaveFullscreenCallback);
            Connect(_view, "close", CloseCallback);
            Connect(_view, "web-process-terminated", TerminatedCallback);
            Connect(_view, "insecure-content-detected", InsecureCallback);
            Connect(_view, "create", CreateCallback);
            Connect(_view, "key-press-event", KeyPressCallback);
            Connect(_view, "button-press-event", ButtonPressCallback);

            nint toplevel = gtk_widget_get_toplevel(_view);
            if (toplevel != 0 && toplevel != _view && gtk_widget_is_toplevel(toplevel) != 0)
            {
                _window = g_object_ref(toplevel);
                Connect(_window, "focus-in-event", WindowFocusInCallback);
            }

            _manager = webkit_web_view_get_user_content_manager(_view);
            _finder = webkit_web_view_get_find_controller(_view);
            Connect(_finder, "counted-matches", CountedCallback);
            Connect(_finder, "failed-to-find-text", NotFoundCallback);

            GtkEngine.ConfigureContext(webkit_web_view_get_context(_view));
            GtkEngine.ConfigureView(_view);
            webkit_web_view_set_zoom_level(_view, _zoom);
            UpdateState();
        }

        void Connect(nint instance, string signal, nint callback) => _signals.Add(new GSignal(instance, signal, callback, this));

        void OnView(Action<nint> action)
            => Glib.Post(() =>
            {
                if (!_disposed && _view != 0)
                    action(_view);
            });

        void Post(Action action)
            => Dispatcher.UIThread.Post(() =>
            {
                if (!_disposed)
                    action();
            });

        void UpdateState()
        {
            _state = new Snapshot(
                String(webkit_web_view_get_title(_view)),
                String(webkit_web_view_get_uri(_view)),
                webkit_web_view_is_loading(_view) != 0,
                webkit_web_view_get_estimated_load_progress(_view),
                webkit_web_view_can_go_back(_view) != 0,
                webkit_web_view_can_go_forward(_view) != 0);
            Post(() => StateChanged?.Invoke());
        }

        /// <summary>Ajoute ou retire le filtre anti-pub selon la page (fil GLib).</summary>
        internal void ApplyContentFilterOnGlib(bool force = false, string? pageUri = null)
        {
            if (_disposed || _manager == 0)
                return;

            nint filter = GtkEngine.ContentFilter;
            bool wanted = filter != 0 && (EngineHost.ContentFilterPolicy?.Invoke(pageUri ?? String(webkit_web_view_get_uri(_view))) ?? false);
            if (wanted == _filterApplied && !force)
                return;

            webkit_user_content_manager_remove_all_filters(_manager);
            if (wanted)
                webkit_user_content_manager_add_filter(_manager, filter);
            _filterApplied = wanted;
        }

        static GtkEngineTab? Tab(nint data)
        {
            GtkEngineTab? tab = GSignal.State<GtkEngineTab>(data);
            return tab is { _disposed: false } ? tab : null;
        }

        // ---------------------------------------------------------------
        // Clavier
        //
        // La vue est une fenêtre GTK placée dans la fenêtre X11 d'Avalonia. Sous X11, une touche
        // va à la fenêtre sous le pointeur dès qu'elle descend de celle qui a le focus : sans
        // précaution, taper une adresse la souris posée sur la page écrirait dans la page, et
        // GTK se croit même active au simple survol. Le clavier suit donc le focus d'Avalonia :
        // la page le reçoit quand sa vue a le focus, sinon elle ne l'écoute plus du tout.
        // ---------------------------------------------------------------

        public void SyncKeyboard(bool force = false)
        {
            if (_disposed)
                return;
            bool page = _host.IsKeyboardFocusWithin && _host.IsEffectivelyVisible;
            if (page == _pageHasKeyboard && !force)
                return;
            nint parent = TopLevel.GetTopLevel(_host)?.TryGetPlatformHandle() is { HandleDescriptor: "XID" } handle ? handle.Handle : 0;
            Glib.Post(() => SetKeyboardOnGlib(page, parent));
        }

        /// <summary>
        /// Donne le clavier à la page ou le lui retire (fil GLib) : touches écoutées ou non par la
        /// fenêtre GTK, focus X11 déplacé à l'intérieur de la fenêtre de PommeBrowser seulement
        /// (jamais pris à une autre application), et état actif de la fenêtre GTK.
        /// </summary>
        void SetKeyboardOnGlib(bool page, nint parent)
        {
            if (_disposed || _window == 0)
                return;
            nint gdkWindow = gtk_widget_get_window(_window);
            if (gdkWindow == 0)
                return;

            _pageHasKeyboard = page;
            if (parent != 0)
                _parentWindow = parent;
            int events = gdk_window_get_events(gdkWindow);
            int wanted = page ? events | GdkKeyPressMask | GdkKeyReleaseMask : events & ~(GdkKeyPressMask | GdkKeyReleaseMask);
            if (wanted != events)
                gdk_window_set_events(gdkWindow, wanted);

            nint display = gdk_window_get_display(gdkWindow);
            nint xdisplay = gdk_x11_display_get_xdisplay(display);
            nint xid = gdk_x11_window_get_xid(gdkWindow);
            bool focused = false;
            bool attempted = false;
            gdk_x11_display_error_trap_push(display);
            try
            {
                XGetInputFocus(xdisplay, out nint focus, out _);
                bool inPage = focus > 1 && IsInside(xdisplay, focus, xid);
                if (page && !inPage && (focus <= 1 || TopWindow(xdisplay, focus) == TopWindow(xdisplay, xid)))
                {
                    XSetInputFocus(xdisplay, xid, XRevertToParent, 0);
                    focused = attempted = true;
                }
                else if (!page && inPage && _parentWindow != 0)
                {
                    XSetInputFocus(xdisplay, _parentWindow, XRevertToParent, 0);
                }
                else
                {
                    focused = inPage;
                }
            }
            finally
            {
                if (gdk_x11_display_error_trap_pop(display) != 0)
                    focused = false;
            }

            if (attempted && !focused && _focusAttempts < 20)
            {
                // Vue pas encore réaffichée par Avalonia (changement d'onglet) : X11 refuse le
                // focus à une fenêtre invisible, nouvel essai un peu plus tard.
                _focusAttempts++;
                _ = Task.Delay(50).ContinueWith(_ => Glib.Post(() =>
                {
                    if (_pageHasKeyboard)
                        SetKeyboardOnGlib(true, 0);
                }), TaskScheduler.Default);
            }
            else
            {
                _focusAttempts = 0;
            }

            if (page ? focused && gtk_window_is_active(_window) == 0 : gtk_window_is_active(_window) != 0)
                SendFocusChange(gdkWindow, page);
        }

        /// <summary>
        /// GTK ne se rend active qu'une fois : quand le survol l'a déjà « activée » (et que ce
        /// faux focus a été ignoré), le vrai focus n'est plus signalé. Il est donc envoyé ici.
        /// </summary>
        void SendFocusChange(nint gdkWindow, bool focusIn)
        {
            nint gdkEvent = gdk_event_new(GdkFocusChange);
            if (gdkEvent == 0)
                return;
            // GdkEventFocus : fenêtre à +8 (libérée avec l'événement), send_event à +16, in à +18.
            Marshal.WriteIntPtr(gdkEvent, 8, g_object_ref(gdkWindow));
            Marshal.WriteByte(gdkEvent, 16, 1);
            Marshal.WriteInt16(gdkEvent, 18, (short)(focusIn ? 1 : 0));
            gtk_widget_event(_window, gdkEvent);
            gdk_event_free(gdkEvent);
        }

        static bool IsInside(nint display, nint window, nint ancestor)
        {
            for (int depth = 0; depth < 64 && window != 0; depth++)
            {
                if (window == ancestor)
                    return true;
                if (XQueryTree(display, window, out nint root, out nint parent, out nint children, out _) == 0)
                    return false;
                if (children != 0)
                    XFree(children);
                if (parent == root)
                    return false;
                window = parent;
            }
            return false;
        }

        /// <summary>Fenêtre de premier niveau (cadre du gestionnaire de fenêtres compris).</summary>
        static nint TopWindow(nint display, nint window)
        {
            for (int depth = 0; depth < 64 && window != 0; depth++)
            {
                if (XQueryTree(display, window, out nint root, out nint parent, out nint children, out _) == 0)
                    return 0;
                if (children != 0)
                    XFree(children);
                if (parent == root || parent == 0)
                    return window;
                window = parent;
            }
            return 0;
        }

        /// <summary>Focus que GTK croit recevoir alors que la page n'a pas le clavier (survol) : ignoré.</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int OnWindowFocusIn(nint window, nint gdkEvent, nint data)
            => Tab(data) is { _pageHasKeyboard: false } ? 1 : 0;

        // ---------------------------------------------------------------
        // Signaux de la vue
        // ---------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnLoadChanged(nint view, int loadEvent, nint data)
        {
            if (Tab(data) is not { } tab)
                return;

            string? uri = String(webkit_web_view_get_uri(view));
            if (loadEvent == 0)
                tab._tlsFailedUri = null;
            // Le filtre suit la page qui s'affiche, avant ses premières ressources.
            if (loadEvent is 0 or 1)
                tab.ApplyContentFilterOnGlib(pageUri: uri);
            tab.UpdateState();
            tab.Post(() => tab.LoadChanged?.Invoke((LoadStage)loadEvent, uri));
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int OnLoadFailed(nint view, int loadEvent, nint failingUri, nint error, nint data)
        {
            if (Tab(data) is not { } tab)
                return 0;

            string uri = String(failingUri) ?? string.Empty;
            (uint domain, int code) = ErrorCode(error);

            // Certificat refusé : sa page est déjà affichée, l'annulation qui suit ne compte pas.
            if (uri == tab._tlsFailedUri)
                return 1;

            // Navigation remplacée par une autre, ou changée en téléchargement : rien à afficher.
            if (domain == webkit_network_error_quark() && code == NetworkErrorCancelled ||
                domain == g_io_error_quark() && code == GIoErrorCancelled ||
                domain == webkit_policy_error_quark() && code == PolicyErrorFrameLoadInterrupted ||
                domain == webkit_network_error_quark() && code == NetworkErrorFileDoesNotExist && uri.StartsWith("about:", StringComparison.Ordinal))
            {
                return 0;
            }

            string message = ErrorMessage(error);
            tab.Post(() => tab.LoadFailed?.Invoke(uri, message));
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int OnTlsError(nint view, nint failingUri, nint certificate, int errors, nint data)
        {
            if (Tab(data) is not { } tab)
                return 0;

            string uri = String(failingUri) ?? string.Empty;
            tab._tlsFailedUri = uri;
            g_object_ref(certificate);
            tab._certificates.Add(certificate);

            var problem = new CertificateProblem
            {
                Uri = uri,
                Host = System.Uri.TryCreate(uri, UriKind.Absolute, out Uri? parsed) ? parsed.Host : string.Empty,
                Der = PemToDer(GetStringProperty(certificate, "certificate-pem")),
                Errors = (CertificateErrors)(errors & 0x7F),
                Native = certificate
            };
            tab.Post(() => tab.CertificateError?.Invoke(problem));
            return 1;
        }

        static byte[] PemToDer(string? pem)
        {
            if (string.IsNullOrEmpty(pem))
                return Array.Empty<byte>();
            try
            {
                using var certificate = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(pem);
                return certificate.RawData;
            }
            catch (System.Security.Cryptography.CryptographicException)
            {
                return Array.Empty<byte>();
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int OnDecidePolicy(nint view, nint decision, int type, nint data)
        {
            if (Tab(data) is not { } tab)
                return 0;

            try
            {
                if (type == PolicyResponse)
                {
                    // Fichier que WebKit ne sait pas afficher, ou envoyé « en pièce jointe » : téléchargement.
                    if (webkit_response_policy_decision_is_main_frame_main_resource(decision) != 0 &&
                        (webkit_response_policy_decision_is_mime_type_supported(decision) == 0 ||
                         IsAttachment(webkit_response_policy_decision_get_response(decision))))
                    {
                        webkit_policy_decision_download(decision);
                        return 1;
                    }
                    return 0;
                }

                nint action = webkit_navigation_policy_decision_get_navigation_action(decision);
                string? target = String(webkit_uri_request_get_uri(webkit_navigation_action_get_request(action)));
                if (string.IsNullOrEmpty(target))
                    return 0;

                bool linkClicked = webkit_navigation_action_get_navigation_type(action) == NavigationLinkClicked;
                bool middle = webkit_navigation_action_get_mouse_button(action) == MouseMiddle;
                bool control = (webkit_navigation_action_get_modifiers(action) & GdkControlMask) != 0;

                // Lien ouvert dans un nouvel onglet : target=_blank, clic du milieu, Ctrl+clic.
                // Les fenêtres ouvertes par un script (window.open) gardent leur lien avec la page (connexions).
                if (linkClicked && (type == PolicyNewWindowAction || middle || control))
                {
                    webkit_policy_decision_ignore(decision);
                    var request = new NavigationRequest { Uri = target, IsNewWindow = true, OpenInBackgroundTab = middle || control };
                    tab.Post(() => tab.NewTabRequested?.Invoke(request));
                    return 1;
                }
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[WebKitGTK] " + ex.Message);
            }
            return 0;
        }

        static bool IsAttachment(nint response)
        {
            nint headers = response == 0 ? 0 : webkit_uri_response_get_http_headers(response);
            if (headers == 0)
                return false;
            string? disposition = String(soup_message_headers_get_one(headers, "Content-Disposition"));
            return disposition != null && disposition.TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnNotify(nint view, nint pspec, nint data)
        {
            if (Tab(data) is not { } tab)
                return;

            if (String(g_param_spec_get_name(pspec)) == "favicon")
            {
                byte[]? png = SurfaceToPng(webkit_web_view_get_favicon(view));
                tab.Post(() => tab.FaviconChanged?.Invoke(png));
                return;
            }
            tab.UpdateState();
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnHistoryChanged(nint list, nint added, nint removed, nint data)
            => Tab(data)?.UpdateState();

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnMouseTarget(nint view, nint hit, uint modifiers, nint data)
        {
            if (Tab(data) is not { } tab)
                return;

            string? link = String(webkit_hit_test_result_get_link_uri(hit));
            if (link == tab._hoveredLink)
                return;
            tab._hoveredLink = link;
            tab.Post(() => tab.LinkHovered?.Invoke(link));
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int OnPermission(nint view, nint request, nint data)
        {
            if (Tab(data) is not { } tab)
                return 0;

            PermissionKind kind = Classify(request);
            if (kind == PermissionKind.Other)
            {
                webkit_permission_request_deny(request);
                return 1;
            }

            g_object_ref(request);
            string origin = System.Uri.TryCreate(String(webkit_web_view_get_uri(view)), UriKind.Absolute, out Uri? page)
                ? page.GetLeftPart(UriPartial.Authority)
                : string.Empty;

            var permission = new PermissionRequest(kind, origin, allow => Glib.Post(() =>
            {
                if (allow)
                    webkit_permission_request_allow(request);
                else
                    webkit_permission_request_deny(request);
                g_object_unref(request);
            }));

            tab.Post(() =>
            {
                tab._pendingPermissions.Add(permission);
                tab.PermissionRequested?.Invoke(permission);
            });
            return 1;
        }

        static PermissionKind Classify(nint request)
        {
            if (IsA(request, webkit_geolocation_permission_request_get_type()))
                return PermissionKind.Geolocation;
            if (IsA(request, webkit_notification_permission_request_get_type()))
                return PermissionKind.Notifications;
            if (IsA(request, webkit_pointer_lock_permission_request_get_type()))
                return PermissionKind.PointerLock;
            if (IsA(request, webkit_device_info_permission_request_get_type()))
                return PermissionKind.MediaDevices;
            if (IsA(request, webkit_clipboard_permission_request_get_type()))
                return PermissionKind.Clipboard;
            if (IsA(request, webkit_website_data_access_permission_request_get_type()))
                return PermissionKind.StorageAccess;
            if (IsA(request, webkit_user_media_permission_request_get_type()))
            {
                if (webkit_user_media_permission_is_for_display_device(request) != 0)
                    return PermissionKind.ScreenCapture;
                bool audio = webkit_user_media_permission_is_for_audio_device(request) != 0;
                bool video = webkit_user_media_permission_is_for_video_device(request) != 0;
                return audio && video ? PermissionKind.CameraAndMicrophone : video ? PermissionKind.Camera : PermissionKind.Microphone;
            }
            return PermissionKind.Other;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int OnEnterFullscreen(nint view, nint data)
        {
            if (Tab(data) is { } tab)
                tab.Post(() => tab.FullscreenRequested?.Invoke(true));
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int OnLeaveFullscreen(nint view, nint data)
        {
            if (Tab(data) is { } tab)
                tab.Post(() => tab.FullscreenRequested?.Invoke(false));
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnClose(nint view, nint data)
        {
            if (Tab(data) is { } tab)
                tab.Post(() => tab.CloseRequested?.Invoke());
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnTerminated(nint view, int reason, nint data)
        {
            if (reason == TerminatedByApi || Tab(data) is not { } tab)
                return;

            string message = reason == ExceededMemoryLimit ? "memory" : "crash";
            tab.UpdateState();
            tab.Post(() => tab.Crashed?.Invoke(message));
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnInsecure(nint view, int kind, nint data)
        {
            if (Tab(data) is { } tab)
                tab.Post(() => tab.InsecureContentDetected?.Invoke());
        }

        /// <summary>window.open : fenêtre séparée qui partage la session et le lien avec la page.</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static nint OnCreate(nint view, nint action, nint data)
        {
            if (Tab(data) is null)
                return 0;
            try
            {
                return GtkPopupWindow.Create(view);
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[WebKitGTK] " + ex.Message);
                return 0;
            }
        }

        /// <summary>Raccourcis du navigateur : pris avant la page (GdkEventKey : état à +24, touche à +28, code matériel à +48).</summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int OnKeyPress(nint view, nint gdkEvent, nint data)
        {
            if (Tab(data) is not { } tab || gdkEvent == 0)
                return 0;

            uint state = (uint)Marshal.ReadInt32(gdkEvent, 24);
            uint keyval = (uint)Marshal.ReadInt32(gdkEvent, 28);
            ushort hardware = (ushort)Marshal.ReadInt16(gdkEvent, 48);
            Avalonia.Input.KeyModifiers modifiers = GdkKeys.Modifiers(state);
            Avalonia.Input.Key key = GdkKeys.ToKey(keyval);

            // Chiffres de la rangée du haut quelle que soit la disposition du clavier (codes X11 10 à 19).
            if (modifiers.HasFlag(Avalonia.Input.KeyModifiers.Control) && hardware is >= 10 and <= 19)
                key = hardware == 19 ? Avalonia.Input.Key.D0 : Avalonia.Input.Key.D1 + (hardware - 10);

            if (key == Avalonia.Input.Key.None || !BrowserShortcuts.IsShortcut(key, modifiers))
                return 0;
            if (BrowserShortcuts.MovesKeyboardToWindow(key, modifiers))
                tab.SetKeyboardOnGlib(page: false, parent: 0);
            tab.Post(() =>
            {
                tab.ShortcutPressed?.Invoke(key, modifiers);
                // Le clavier suit le focus laissé par le raccourci (rendu à la page si besoin).
                tab.SyncKeyboard(force: true);
            });
            return 1;
        }

        /// <summary>
        /// Clic dans la page : elle prend le clavier. Boutons « précédent » et « suivant » de la
        /// souris (GdkEventButton : bouton à +52).
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int OnButtonPress(nint view, nint gdkEvent, nint data)
        {
            if (Tab(data) is not { } tab || gdkEvent == 0)
                return 0;
            uint button = (uint)Marshal.ReadInt32(gdkEvent, 52);
            if (button <= 3)
            {
                // À chaque clic : le gestionnaire de fenêtres vient peut-être de redonner le focus
                // X11 à la fenêtre d'Avalonia.
                tab.SetKeyboardOnGlib(page: true, parent: 0);
                tab.Post(() =>
                {
                    if (!tab._host.IsKeyboardFocusWithin)
                        tab._host.Focus();
                });
            }
            if (button == 8 && webkit_web_view_can_go_back(view) != 0)
            {
                webkit_web_view_go_back(view);
                return 1;
            }
            if (button == 9 && webkit_web_view_can_go_forward(view) != 0)
            {
                webkit_web_view_go_forward(view);
                return 1;
            }
            return 0;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnCounted(nint finder, uint count, nint data)
        {
            if (Tab(data) is { } tab)
                tab.Post(() => tab.FindMatchesCounted?.Invoke((int)count));
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnNotFound(nint finder, nint data)
        {
            if (Tab(data) is { } tab)
                tab.Post(() => tab.FindMatchesCounted?.Invoke(0));
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnScriptMessage(nint manager, nint result, nint data)
        {
            if (GSignal.State<MessageChannel>(data) is not { } channel || channel.Tab._disposed)
                return;

            nint value = webkit_javascript_result_get_js_value(result);
            string body = value == 0 ? string.Empty : TakeString(jsc_value_to_string(value)) ?? string.Empty;
            channel.Tab.Post(() => channel.Tab.ScriptMessage?.Invoke(channel.Name, body));
        }

        // ---------------------------------------------------------------
        // Commandes (depuis l'interface)
        // ---------------------------------------------------------------

        public void Navigate(string uri) => OnView(view => webkit_web_view_load_uri(view, uri));
        public void GoBack() => OnView(webkit_web_view_go_back);
        public void GoForward() => OnView(webkit_web_view_go_forward);
        public void Stop() => OnView(webkit_web_view_stop_loading);

        public void Reload(bool bypassCache = false)
            => OnView(view =>
            {
                if (bypassCache)
                    webkit_web_view_reload_bypass_cache(view);
                else
                    webkit_web_view_reload(view);
            });

        public void Focus()
        {
            _host.Focus();
            SyncKeyboard(force: true);
            OnView(gtk_widget_grab_focus);
        }

        string? _findText;
        uint _findOptions;

        public void Find(string text, bool matchCase)
        {
            _findText = text;
            _findOptions = (matchCase ? 0 : FindCaseInsensitive) | FindWrapAround;
            uint options = _findOptions;
            Glib.Post(() =>
            {
                if (_disposed || _finder == 0)
                    return;
                if (text.Length == 0)
                {
                    webkit_find_controller_search_finish(_finder);
                    return;
                }
                webkit_find_controller_count_matches(_finder, text, options, 1000);
                webkit_find_controller_search(_finder, text, options, 1000);
            });
            if (text.Length == 0)
                FindMatchesCounted?.Invoke(0);
        }

        public void FindNext(bool backward)
        {
            if (string.IsNullOrEmpty(_findText))
                return;
            Glib.Post(() =>
            {
                if (_disposed || _finder == 0)
                    return;
                if (backward)
                    webkit_find_controller_search_previous(_finder);
                else
                    webkit_find_controller_search_next(_finder);
            });
        }

        public void StopFind()
        {
            _findText = null;
            Glib.Post(() =>
            {
                if (!_disposed && _finder != 0)
                    webkit_find_controller_search_finish(_finder);
            });
        }

        public void Print() => _host.ShowPrintUI();

        public void ShowDevTools() => OnView(view => webkit_web_inspector_show(webkit_web_view_get_inspector(view)));

        // ---------------------------------------------------------------
        // Moteur Flash intégré : cookies de la page et appels de la page vers le contenu
        // ---------------------------------------------------------------

        /// <summary>
        /// Réponse aux appels de la page vers le contenu Flash (fil de l'interface) et jeton que ces
        /// appels doivent porter ; null : aucun lecteur.
        /// </summary>
        public (Func<string, string?> Call, string Token)? FlashBridge => _flashBridge?.Value;

        // Boîte : lue et remplacée d'un seul coup depuis d'autres fils.
        volatile StrongBox<(Func<string, string?> Call, string Token)>? _flashBridge;

        /// <summary>Cookies de la page pour une adresse (en-tête Cookie), lus dans le gestionnaire de cookies de WebKit.</summary>
        public Task<string?> GetCookieHeaderAsync(Uri url, bool includeHttpOnly)
        {
            var query = new CookieQuery(includeHttpOnly);
            GCHandle handle = GCHandle.Alloc(query);
            Glib.Post(() =>
            {
                if (_disposed || _view == 0)
                {
                    handle.Free();
                    query.Completion.TrySetResult(null);
                    return;
                }
                nint manager = webkit_web_context_get_cookie_manager(webkit_web_view_get_context(_view));
                webkit_cookie_manager_get_cookies(manager, url.AbsoluteUri, 0, CookiesReadCallback, GCHandle.ToIntPtr(handle));
            });
            return query.Completion.Task;
        }

        sealed class CookieQuery
        {
            public CookieQuery(bool includeHttpOnly) => IncludeHttpOnly = includeHttpOnly;
            public bool IncludeHttpOnly { get; }
            public TaskCompletionSource<string?> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnCookiesRead(nint manager, nint result, nint data)
        {
            GCHandle handle = GCHandle.FromIntPtr(data);
            var query = (CookieQuery)handle.Target!;
            handle.Free();
            try
            {
                nint list = webkit_cookie_manager_get_cookies_finish(manager, result, out nint error);
                if (error != 0)
                {
                    RuntimeLogBuffer.Append("[Flash] Cookies de la page illisibles : " + ErrorMessage(error));
                    g_error_free(error);
                    query.Completion.TrySetResult(null);
                    return;
                }
                var cookies = new List<(string Name, string Value)>();
                // GList : data, next, prev.
                for (nint node = list; node != 0; node = *(nint*)(node + sizeof(nint)))
                {
                    nint cookie = *(nint*)node;
                    if (query.IncludeHttpOnly || soup_cookie_get_http_only(cookie) == 0)
                        cookies.Add((String(soup_cookie_get_name(cookie)) ?? string.Empty, String(soup_cookie_get_value(cookie)) ?? string.Empty));
                    soup_cookie_free(cookie);
                }
                g_list_free(list);
                query.Completion.TrySetResult(FlashCookies.Header(cookies));
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[Flash] Cookies de la page : " + ex.Message);
                query.Completion.TrySetResult(null);
            }
        }

        /// <summary>Cookie reçu par le lecteur Flash : enregistré dans le gestionnaire de cookies de WebKit (retiré s'il est expiré).</summary>
        public Task SetCookieAsync(PageCookie cookie)
        {
            var stored = new CookieStore();
            GCHandle handle = GCHandle.Alloc(stored);
            Glib.Post(() =>
            {
                if (_disposed || _view == 0)
                {
                    handle.Free();
                    stored.Completion.TrySetResult(false);
                    return;
                }
                nint manager = webkit_web_context_get_cookie_manager(webkit_web_view_get_context(_view));
                // Un domaine qui commence par un point vaut pour ses sous-domaines (cookie de domaine).
                nint soup = soup_cookie_new(cookie.Name, cookie.Value, cookie.HostOnly ? cookie.Domain : "." + cookie.Domain, cookie.Path, -1);
                if (cookie.Expires is { } expires)
                {
                    nint when = g_date_time_new_from_unix_utc(expires.ToUnixTimeSeconds());
                    soup_cookie_set_expires(soup, when);
                    g_date_time_unref(when);
                }
                soup_cookie_set_secure(soup, cookie.Secure ? 1 : 0);
                soup_cookie_set_http_only(soup, cookie.HttpOnly ? 1 : 0);
                soup_cookie_set_same_site_policy(soup, cookie.SameSite switch { "Strict" => SoupSameSiteStrict, "Lax" => SoupSameSiteLax, _ => SoupSameSiteNone });
                stored.Cookie = soup;
                stored.Deleting = cookie.IsExpired(DateTimeOffset.UtcNow);
                if (stored.Deleting)
                    webkit_cookie_manager_delete_cookie(manager, soup, 0, CookieStoredCallback, GCHandle.ToIntPtr(handle));
                else
                    webkit_cookie_manager_add_cookie(manager, soup, 0, CookieStoredCallback, GCHandle.ToIntPtr(handle));
            });
            return stored.Completion.Task;
        }

        sealed class CookieStore
        {
            public nint Cookie;
            public bool Deleting;
            public TaskCompletionSource<bool> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnCookieStored(nint manager, nint result, nint data)
        {
            GCHandle handle = GCHandle.FromIntPtr(data);
            var stored = (CookieStore)handle.Target!;
            handle.Free();
            try
            {
                int ok = stored.Deleting
                    ? webkit_cookie_manager_delete_cookie_finish(manager, result, out nint error)
                    : webkit_cookie_manager_add_cookie_finish(manager, result, out error);
                if (error != 0)
                {
                    RuntimeLogBuffer.Append("[Flash] Cookie du lecteur non enregistré : " + ErrorMessage(error));
                    g_error_free(error);
                }
                stored.Completion.TrySetResult(ok != 0);
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[Flash] Cookie du lecteur : " + ex.Message);
                stored.Completion.TrySetResult(false);
            }
            finally
            {
                if (stored.Cookie != 0)
                    soup_cookie_free(stored.Cookie);
            }
        }

        /// <summary>
        /// Appels de la page vers le contenu Flash : la page y accède par une requête synchrone au
        /// schéma <see cref="RuffleContent.FlashBridgeScheme"/> (voir GtkEngine.ServeFlashBridge),
        /// portant <paramref name="token"/>, à laquelle <paramref name="callFunction"/> répond ; null
        /// quand le lecteur s'arrête.
        /// </summary>
        public void SetFlashBridge(Func<string, string?>? callFunction, string? token)
            => _flashBridge = callFunction != null && !string.IsNullOrEmpty(token)
                ? new StrongBox<(Func<string, string?> Call, string Token)>((callFunction, token))
                : null;

        /// <summary>Cadres d'une autre origine hors d'atteinte depuis l'application (WebKit) : refus.</summary>
        public Task<(bool Ok, string? Value)> EvaluateInFrameAsync(Uri frame, string script) => Task.FromResult((false, (string?)null));

        public Task<string?> EvaluateAsync(string script, bool isolated)
        {
            var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            GCHandle handle = GCHandle.Alloc(completion);
            Glib.Post(() =>
            {
                if (_disposed || _view == 0)
                {
                    handle.Free();
                    completion.TrySetResult(null);
                    return;
                }
                webkit_web_view_evaluate_javascript(_view, script, -1, isolated ? GtkEngine.World : null, null, 0, EvaluatedCallback, GCHandle.ToIntPtr(handle));
            });
            return completion.Task;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnEvaluated(nint view, nint result, nint data)
        {
            GCHandle handle = GCHandle.FromIntPtr(data);
            var completion = (TaskCompletionSource<string?>)handle.Target!;
            handle.Free();

            nint value = webkit_web_view_evaluate_javascript_finish(view, result, out nint error);
            if (error != 0)
            {
                completion.TrySetException(new InvalidOperationException(TakeError(error)));
                return;
            }

            string? text = value == 0 || jsc_value_is_undefined(value) != 0 || jsc_value_is_null(value) != 0
                ? null
                : TakeString(jsc_value_to_string(value));
            if (value != 0)
                g_object_unref(value);
            completion.TrySetResult(text);
        }

        public void AddUserScript(string id, string source, bool allFrames, bool atDocumentStart, bool pageWorld = false)
            => Glib.Post(() =>
            {
                if (_disposed || _manager == 0)
                    return;
                RemoveScriptOnGlib(id);
                int frames = allFrames ? InjectAllFrames : InjectTopFrame;
                int time = atDocumentStart ? InjectAtStart : InjectAtEnd;
                nint script = pageWorld
                    ? webkit_user_script_new(source, frames, time, 0, 0)
                    : webkit_user_script_new_for_world(source, frames, time, GtkEngine.World, 0, 0);
                webkit_user_content_manager_add_script(_manager, script);
                _scripts[id] = script;
            });

        public void RemoveUserScript(string id)
            => Glib.Post(() =>
            {
                if (!_disposed)
                    RemoveScriptOnGlib(id);
            });

        void RemoveScriptOnGlib(string id)
        {
            if (_manager == 0 || !_scripts.Remove(id, out nint script))
                return;
            webkit_user_content_manager_remove_script(_manager, script);
            webkit_user_script_unref(script);
        }

        public void RegisterMessageHandler(string name)
            => Glib.Post(() =>
            {
                if (_disposed || _manager == 0 || !_handlers.Add(name))
                    return;
                _signals.Add(new GSignal(_manager, "script-message-received::" + name, ScriptMessageCallback, new MessageChannel(this, name)));
                webkit_user_content_manager_register_script_message_handler_in_world(_manager, name, GtkEngine.World);
            });

        public void AllowCertificate(CertificateProblem problem)
        {
            if (problem.Native is not nint certificate || problem.Host.Length == 0)
                return;
            string host = problem.Host;
            OnView(view => webkit_web_context_allow_tls_certificate_for_host(webkit_web_view_get_context(view), certificate, host));
        }

        public void RefreshContentFilter() => Glib.Post(() => ApplyContentFilterOnGlib(force: true));

        /// <summary>
        /// La vue elle-même est détruite par le contrôle Avalonia ; PommeBrowser se débranche
        /// et refuse les autorisations restées sans réponse.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            _host.GotFocus -= OnHostFocusChanged;
            _host.LostFocus -= OnHostFocusChanged;

            foreach (PermissionRequest request in _pendingPermissions)
                request.Deny();
            _pendingPermissions.Clear();

            Glib.Post(() =>
            {
                foreach (GSignal signal in _signals)
                    signal.Dispose();
                _signals.Clear();

                if (_manager != 0)
                {
                    foreach (nint script in _scripts.Values)
                    {
                        webkit_user_content_manager_remove_script(_manager, script);
                        webkit_user_script_unref(script);
                    }
                }
                _scripts.Clear();

                foreach (nint certificate in _certificates)
                    g_object_unref(certificate);
                _certificates.Clear();

                if (_window != 0)
                {
                    g_object_unref(_window);
                    _window = 0;
                }

                if (_view != 0)
                {
                    GtkEngine.Unregister(_view);
                    g_object_unref(_view);
                    _view = 0;
                }
            });
        }
    }
}
