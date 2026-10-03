using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PommeFlash.Host.Native;

namespace PommeFlash.Host
{
    /// <summary>
    /// Fenêtre de l'hôte sous Linux (X11), avec GTK 2 comme Firefox l'offrait au module Flash : un
    /// cadre (celui que PommeBrowser loge dans l'onglet) et, dedans, une prise XEmbed (GtkSocket)
    /// donnée au module, qui y branche sa propre fenêtre. Boucle de messages : celle de GTK.
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

            _window = Gtk.gtk_window_new(Gtk.WindowToplevel);
            Gtk.gtk_window_set_title(_window, "PommeBrowser – Flash");
            Gtk.gtk_window_set_default_size(_window, options.Width, options.Height);
            _socket = Gtk.gtk_socket_new();
            var black = new Gtk.GdkColor();
            Gtk.gtk_widget_modify_bg(_window, Gtk.StateNormal, &black);
            Gtk.gtk_widget_modify_bg(_socket, Gtk.StateNormal, &black);
            Gtk.gtk_container_add(_window, _socket);
            Gtk.gtk_widget_show(_socket);

            Connect(_window, "delete-event", (nint)(delegate* unmanaged<nint, nint, nint, int>)&OnDeleteEvent);
            Connect(_window, "destroy", (nint)(delegate* unmanaged<nint, nint, void>)&OnDestroy);
            Connect(_socket, "plug-removed", (nint)(delegate* unmanaged<nint, nint, int>)&OnPlugRemoved);
            Connect(_socket, "size-allocate", (nint)(delegate* unmanaged<nint, Gtk.GtkAllocation*, nint, void>)&OnSizeAllocate);

            if (options.Hidden)
            {
                // Créée sans être affichée : GTK ne lui donne pas encore sa taille, l'hôte le fait.
                Gtk.gtk_widget_realize(_window);
                Gtk.gdk_window_resize(Gtk.gtk_widget_get_window(_window), options.Width, options.Height);
                var allocation = new Gtk.GtkAllocation { width = options.Width, height = options.Height };
                Gtk.gtk_widget_size_allocate(_window, &allocation);
            }
            else
            {
                Gtk.gtk_widget_show(_window);
            }
            Gtk.gtk_widget_realize(_socket);
            // Taille de la prise une fois la fenêtre en place ; ensuite, chaque changement est suivi.
            var current = new Gtk.GtkAllocation();
            Gtk.gtk_widget_get_allocation(_socket, &current);
            (_width, _height) = current.width > 1 && current.height > 1 ? (current.width, current.height) : (options.Width, options.Height);
            _created = true;

            Frame = (nint)Gtk.gdk_x11_drawable_get_xid(Gtk.gtk_widget_get_window(_window));
            XDisplay = Gtk.gdk_x11_get_default_xdisplay();
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
