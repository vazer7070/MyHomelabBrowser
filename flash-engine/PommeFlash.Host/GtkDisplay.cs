using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PommeFlash.Host.Native;

namespace PommeFlash.Host
{
    /// <summary>
    /// Fenêtre de l'hôte sous Linux (X11), avec GTK 2 comme Firefox l'offrait au module Flash : un
    /// cadre (celui que PommeBrowser loge dans l'onglet) et, dedans, une prise XEmbed (GtkSocket)
    /// donnée au module, qui y branche sa propre fenêtre. Boucle de messages : celle de GTK.
    /// Clavier : un clic dans le contenu donne le focus X11 au cadre (comme un navigateur), et les
    /// touches reçues par le cadre sont transmises à la fenêtre du module.
    /// </summary>
    [SupportedOSPlatform("linux")]
    sealed unsafe class GtkDisplay : IHostDisplay
    {
        // Une seule fenêtre par processus : les rappels de GTK (statiques) la retrouvent ici.
        static GtkDisplay? _current;
        static nint _previousXErrorHandler;

        readonly Dictionary<nint, Action> _delayed = new();
        // Minuteries du module : identifiant NPAPI → source GLib.
        readonly Dictionary<uint, uint> _timers = new();
        nint _nextDelay = 1;
        uint _firingTimer;
        int _woken;
        bool _resizePending;
        bool _created;
        PluginInstance? _instance;
        Action? _closeRequested;
        nint _window;
        nint _socket;
        nint _info;
        int _width;
        int _height;

        public nint Frame { get; private set; }

        public nint NetscapeWindow => Frame;

        public nint XDisplay { get; private set; }

        public (nint Window, nint Info, int Width, int Height) PluginArea
            => ((nint)Gtk.gtk_socket_get_id(_socket), _info, _width, _height);

        public void Create(HostOptions options)
        {
            _current = this;
            // Avant tout appel à Xlib : le module peut s'en servir depuis plusieurs fils.
            Gtk.XInitThreads();
            if (Gtk.gtk_init_check(null, null) == 0)
                throw new InvalidOperationException("Affichage X11 introuvable (variable DISPLAY) : le moteur Flash intégré a besoin de X11 ou de XWayland.");
            // Langue du système pour les textes, mais nombres écrits avec un point, comme le module l'attend.
            Gtk.setlocale(Gtk.LcNumeric, "C");
            // Erreurs X11 (du module le plus souvent) notées dans le journal avant le traitement de GDK.
            _previousXErrorHandler = Gtk.XSetErrorHandler((nint)(delegate* unmanaged<nint, Gtk.XErrorEvent*, int>)&OnXError);

            // Fenêtre « cachée » (PommeBrowser va la loger dans l'onglet) : une fenêtre sans
            // gestionnaire (override-redirect), affichée hors de l'écran, car GTK n'affiche la
            // prise et la fenêtre du module que dans une fenêtre affichée. Logée, elle est déplacée
            // et redimensionnée par PommeBrowser ; elle ne demande jamais sa propre taille.
            _window = Gtk.gtk_window_new(options.Hidden ? Gtk.WindowPopup : Gtk.WindowToplevel);
            Gtk.gtk_window_set_title(_window, "PommeBrowser – Flash");
            Gtk.gtk_window_set_default_size(_window, options.Width, options.Height);
            if (options.Hidden)
                Gtk.gtk_window_move(_window, Gtk.gdk_screen_width() + 64, Gtk.gdk_screen_height() + 64);
            _socket = Gtk.gtk_socket_new();
            Gtk.gtk_widget_set_size_request(_socket, 1, 1);
            // Clics que la fenêtre du module ne prend pas : ils remontent à la prise (voir FocusOnClick).
            Gtk.gtk_widget_add_events(_socket, Gtk.ButtonPressMask);
            var black = new Gtk.GdkColor();
            Gtk.gtk_widget_modify_bg(_window, Gtk.StateNormal, &black);
            Gtk.gtk_widget_modify_bg(_socket, Gtk.StateNormal, &black);
            Gtk.gtk_container_add(_window, _socket);
            Gtk.gtk_widget_show(_socket);

            Connect(_window, "delete-event", (nint)(delegate* unmanaged<nint, nint, nint, int>)&OnDeleteEvent);
            Connect(_window, "destroy", (nint)(delegate* unmanaged<nint, nint, void>)&OnDestroy);
            Connect(_window, "key-press-event", (nint)(delegate* unmanaged<nint, nint, nint, int>)&OnKey);
            Connect(_window, "key-release-event", (nint)(delegate* unmanaged<nint, nint, nint, int>)&OnKey);
            Connect(_socket, "plug-removed", (nint)(delegate* unmanaged<nint, nint, int>)&OnPlugRemoved);
            Connect(_socket, "size-allocate", (nint)(delegate* unmanaged<nint, Gtk.GtkAllocation*, nint, void>)&OnSizeAllocate);
            // Clics dans la fenêtre du module : vus ici avant GTK, quelle que soit la fenêtre X qui les reçoit.
            Gtk.gdk_window_add_filter(0, (nint)(delegate* unmanaged<nint, nint, nint, int>)&OnXEvent, 0);

            Gtk.gtk_widget_show(_window);
            Gtk.gtk_widget_grab_focus(_socket);
            // Taille de la prise une fois la fenêtre en place ; ensuite, chaque changement est suivi.
            var current = new Gtk.GtkAllocation();
            Gtk.gtk_widget_get_allocation(_socket, &current);
            (_width, _height) = current.width > 1 && current.height > 1 ? (current.width, current.height) : (options.Width, options.Height);
            _created = true;

            Frame = (nint)Gtk.gdk_x11_drawable_get_xid(Gtk.gtk_widget_get_window(_window));
            XDisplay = Gtk.gdk_x11_get_default_xdisplay();
            // Diagnostic (POMMEFLASH_XSYNC=1) : erreurs X11 signalées à l'appel fautif, avec la pile
            // de l'hôte et les derniers événements reçus ; POMMEFLASH_XTRACE=1 note chaque événement X.
            if (XSync)
                Gtk.XSynchronize(XDisplay, 1);
            int screen = Gtk.XDefaultScreen(XDisplay);
            var info = (NPSetWindowCallbackStruct*)NpMemory.AllocZeroed((nuint)sizeof(NPSetWindowCallbackStruct));
            info->type = Np.SetWindow;
            info->display = XDisplay;
            info->visual = Gtk.XDefaultVisual(XDisplay, screen);
            info->colormap = Gtk.XDefaultColormap(XDisplay, screen);
            info->depth = (uint)Gtk.XDefaultDepth(XDisplay, screen);
            _info = (nint)info;
        }

        static void Connect(nint widget, string signal, nint handler)
            => Gtk.g_signal_connect_data(widget, signal, handler, 0, 0, 0);

        public void Attach(PluginInstance instance, Action closeRequested)
        {
            _instance = instance;
            _closeRequested = closeRequested;
        }

        public int Run()
        {
            Gtk.gtk_main();
            return 0;
        }

        public void Close()
        {
            if (XTrace)
                HostChannel.Log("Fermeture de la fenêtre demandée.");
            if (_window != 0)
                Gtk.gtk_widget_destroy(_window);
        }

        /// <summary>Depuis n'importe quel fil (GLib le permet) ; un seul réveil en attente à la fois.</summary>
        public void Wake()
        {
            if (Interlocked.Exchange(ref _woken, 1) == 0)
                Gtk.g_idle_add_full(Gtk.PriorityDefault, (nint)(delegate* unmanaged<nint, int>)&OnWake, 0, 0);
        }

        public void Delay(uint milliseconds, Action action)
        {
            nint key = _nextDelay++;
            _delayed[key] = action;
            Gtk.g_timeout_add(milliseconds, (nint)(delegate* unmanaged<nint, int>)&OnDelay, key);
        }

        public void StartTimer(uint id, uint interval)
            => _timers[id] = Gtk.g_timeout_add(Math.Max(1, interval), (nint)(delegate* unmanaged<nint, int>)&OnTimer, (nint)id);

        public void StopTimer(uint id)
        {
            // Arrêtée pendant son propre rappel : GLib la retire quand le rappel répond « fini ».
            if (_timers.Remove(id, out uint source) && id != _firingTimer)
                Gtk.g_source_remove(source);
        }

        /// <summary>
        /// Attente bloquante, comme dans un navigateur. Sous X11, rien n'attend l'hôte pendant ce
        /// temps (PommeBrowser place sa fenêtre sans lui), la boucle de messages peut rester arrêtée.
        /// </summary>
        public bool Wait(WaitHandle signal, TimeSpan timeout) => signal.WaitOne(timeout);

        /// <summary>
        /// Clic dans le contenu : le clavier (focus X11) va au cadre s'il ne l'a pas déjà, comme un
        /// navigateur le fait pour ses modules, puis à la prise, donc à la fenêtre du module.
        /// Rien n'est pris si le clic vient d'une autre fenêtre du module (boîte de dialogue).
        /// </summary>
        void FocusOnClick(nint window, nuint time)
        {
            if (Frame == 0 || _socket == 0 || !IsInFrame(window))
                return;
            nint focus;
            int revert;
            Gtk.XGetInputFocus(XDisplay, &focus, &revert);
            // 0 : aucune ; 1 : la fenêtre sous le pointeur.
            if (focus > 1 && IsInFrame(focus))
                return;
            Gtk.XWindowAttributes attributes;
            if (Gtk.XGetWindowAttributes(XDisplay, Frame, &attributes) == 0 || attributes.map_state != Gtk.IsViewable)
                return;
            Gtk.gdk_error_trap_push();
            Gtk.XSetInputFocus(XDisplay, Frame, Gtk.RevertToParent, time);
            Gtk.XSync(XDisplay, 0);
            Gtk.gdk_error_trap_pop();
            Gtk.gtk_widget_grab_focus(_socket);
        }

        /// <summary>La fenêtre est le cadre ou une de ses descendantes (fenêtre du module comprise).</summary>
        bool IsInFrame(nint window)
        {
            for (int depth = 0; window != 0 && depth < 64; depth++)
            {
                if (window == Frame)
                    return true;
                nint root, parent;
                nint* children;
                uint count;
                Gtk.gdk_error_trap_push();
                int found = Gtk.XQueryTree(XDisplay, window, &root, &parent, &children, &count);
                Gtk.gdk_error_trap_pop();
                if (found == 0)
                    return false;
                if (children != null)
                    Gtk.XFree((nint)children);
                if (parent == 0 || parent == root)
                    return false;
                window = parent;
            }
            return false;
        }

        /// <summary>
        /// Touche reçue par le cadre : transmise à la fenêtre que le module a branchée dans la prise
        /// avec la GTK de l'hôte (GTK ne le fait pas de lui-même si rien n'y prend le focus).
        /// Vrai si le module l'a traitée.
        /// </summary>
        bool ForwardKey(nint gdkEvent)
        {
            if (_socket == 0)
                return false;
            nint children = Gtk.gtk_container_get_children(_socket);
            if (children == 0)
                return false;
            nint plug = *(nint*)children;
            Gtk.g_list_free(children);
            return plug != 0 && Gtk.gtk_widget_event(plug, gdkEvent);
        }

        /// <summary>Nouvelle taille de la prise : donnée au module hors du rappel de GTK.</summary>
        void Resized(int width, int height)
        {
            if (!_created || width <= 1 || height <= 1 || (width == _width && height == _height))
                return;
            _width = width;
            _height = height;
            if (_instance == null || _resizePending)
                return;
            _resizePending = true;
            UiThread.Post(() =>
            {
                _resizePending = false;
                (nint window, nint info, int w, int h) = PluginArea;
                _instance.SetWindow(window, info, w, h);
            });
        }

        /// <summary>
        /// Erreur X11 : notée, puis traitée par GDK, qui la garde pour ses pièges d'erreurs (gdk_error_trap_push)
        /// ou, hors de ceux-ci, arrête le programme comme le faisait Firefox.
        /// </summary>
        [UnmanagedCallersOnly]
        static int OnXError(nint display, Gtk.XErrorEvent* error)
        {
            try
            {
                byte* text = stackalloc byte[256];
                Gtk.XGetErrorText(display, error->error_code, text, 256);
                HostChannel.Error($"Erreur X11 : {Marshal.PtrToStringUTF8((nint)text)} (requête {error->request_code}.{error->minor_code}, ressource 0x{(ulong)error->resourceid:x}).");
                if (XSync)
                {
                    HostChannel.Error("Pile : " + Environment.StackTrace.Replace('\n', ' '));
                    HostChannel.Error("Derniers événements X : " + string.Join(", ", RecentEvents.Select(e => $"{e.Type}@0x{(ulong)e.Window:x}")));
                }
            }
            catch (Exception)
            {
                // Journal injoignable : l'erreur est quand même donnée à GDK.
            }
            return _previousXErrorHandler != 0 ? ((delegate* unmanaged<nint, Gtk.XErrorEvent*, int>)_previousXErrorHandler)(display, error) : 0;
        }

        [UnmanagedCallersOnly]
        static int OnWake(nint data)
        {
            if (_current != null)
                Volatile.Write(ref _current._woken, 0);
            UiThread.RunPending();
            return 0;
        }

        [UnmanagedCallersOnly]
        static int OnDelay(nint key)
        {
            if (_current != null && _current._delayed.Remove(key, out Action? action))
                UiThread.Run(action);
            return 0;
        }

        [UnmanagedCallersOnly]
        static int OnTimer(nint data)
        {
            GtkDisplay? self = _current;
            uint id = (uint)data;
            if (self == null || !self._timers.ContainsKey(id))
                return 0;
            self._firingTimer = id;
            try
            {
                self._instance?.OnTimer(id);
            }
            catch (Exception ex)
            {
                HostChannel.Error("Minuterie : " + ex.Message);
            }
            finally
            {
                self._firingTimer = 0;
            }
            return self._timers.ContainsKey(id) ? 1 : 0;
        }

        [UnmanagedCallersOnly]
        static int OnDeleteEvent(nint widget, nint gdkEvent, nint data)
        {
            try
            {
                if (_current?._closeRequested is { } closeRequested)
                    closeRequested();
                else
                    _current?.Close();
            }
            catch (Exception ex)
            {
                HostChannel.Error("Fenêtre : " + ex.Message);
            }
            // La fenêtre est détruite par Close, après l'instance.
            return 1;
        }

        [UnmanagedCallersOnly]
        static void OnDestroy(nint widget, nint data)
        {
            if (_current != null)
                _current._window = 0;
            Gtk.gtk_main_quit();
        }

        /// <summary>Fenêtre du module retirée de la prise : la prise reste (le module peut revenir, après le plein écran).</summary>
        [UnmanagedCallersOnly]
        static int OnPlugRemoved(nint socket, nint data) => 1;

        [UnmanagedCallersOnly]
        static int OnKey(nint widget, nint gdkEvent, nint data)
        {
            try
            {
                return _current?.ForwardKey(gdkEvent) == true ? 1 : 0;
            }
            catch (Exception ex)
            {
                HostChannel.Error("Clavier : " + ex.Message);
                return 0;
            }
        }

        /// <summary>Filtre de GDK : chaque événement X11 reçu, avant son traitement (0 : GDK continue).</summary>
        static readonly Queue<(int Type, nint Window)> RecentEvents = new();
        static readonly bool XTrace = Environment.GetEnvironmentVariable("POMMEFLASH_XTRACE") == "1";
        static readonly bool XSync = Environment.GetEnvironmentVariable("POMMEFLASH_XSYNC") == "1";

        [UnmanagedCallersOnly]
        static int OnXEvent(nint xevent, nint gdkEvent, nint data)
        {
            try
            {
                var button = (Gtk.XButtonEvent*)xevent;
                if (XTrace)
                    HostChannel.Log($"X : type {button->type} fenêtre 0x{(ulong)button->window:x} (0x{(ulong)button->root:x}, 0x{(ulong)button->subwindow:x})");
                RecentEvents.Enqueue((button->type, button->window));
                if (RecentEvents.Count > 12)
                    RecentEvents.Dequeue();
                if (button->type == Gtk.ButtonPress)
                    _current?.FocusOnClick(button->window, button->time);
            }
            catch (Exception ex)
            {
                HostChannel.Error("Clic : " + ex.Message);
            }
            return 0;
        }

        [UnmanagedCallersOnly]
        static void OnSizeAllocate(nint widget, Gtk.GtkAllocation* allocation, nint data)
        {
            try
            {
                _current?.Resized(allocation->width, allocation->height);
            }
            catch (Exception ex)
            {
                HostChannel.Error("Fenêtre : " + ex.Message);
            }
        }
    }
}
