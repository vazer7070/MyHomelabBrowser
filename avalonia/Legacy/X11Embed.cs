using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input;
using PommeBrowser.Engine;
using PommeBrowser.Engine.Gtk;

namespace PommeBrowser.Legacy
{
    /// <summary>
    /// Connexion X11 réservée à l'intégration de Basilisk, avec son propre fil : toutes ses
    /// requêtes et ses événements passent par lui. Les erreurs (fenêtre de Basilisk déjà
    /// fermée…) vont au gestionnaire d'Avalonia, qui les ignore ; GDK ne s'occupe que de
    /// sa propre connexion.
    /// </summary>
    [SupportedOSPlatform("linux")]
    sealed unsafe class X11Embed
    {
        static readonly Lazy<X11Embed?> Shared = new(Open);

        readonly nint _display;
        readonly ConcurrentQueue<Action> _queue = new();
        readonly Dictionary<nint, X11Dock> _docks = new();
        readonly int[] _wake = new int[2];

        X11Embed(nint display)
        {
            _display = display;
            Root = X.XDefaultRootWindow(display);
            Screen = X.XDefaultScreen(display);
            if (pipe(_wake) != 0)
                throw new IOException("pipe");
            var thread = new Thread(Run) { IsBackground = true, Name = "Basilisk X11" };
            thread.Start();
        }

        /// <summary>Connexion ouverte au premier besoin ; null hors X11 (Wayland pur, pas de DISPLAY).</summary>
        public static X11Embed? Instance => Shared.Value;

        public nint Display => _display;
        public nint Root { get; }
        public int Screen { get; }

        static X11Embed? Open()
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")))
                return null;
            try
            {
                nint display = X.XOpenDisplay(0);
                return display == 0 ? null : new X11Embed(display);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException)
            {
                return null;
            }
        }

        public void Post(Action action)
        {
            _queue.Enqueue(action);
            byte one = 1;
            write(_wake[1], &one, 1);
        }

        public Task<T> Invoke<T>(Func<T> function)
        {
            var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try
                {
                    result.SetResult(function());
                }
                catch (Exception ex)
                {
                    result.SetException(ex);
                }
            });
            return result.Task;
        }

        internal void Register(nint window, X11Dock dock) => _docks[window] = dock;
        internal void Unregister(nint window) => _docks.Remove(window);

        void Run()
        {
            int connection = X.XConnectionNumber(_display);
            var fds = stackalloc PollFd[2];
            fds[0] = new PollFd { Fd = connection, Events = PollIn };
            fds[1] = new PollFd { Fd = _wake[0], Events = PollIn };
            byte* drain = stackalloc byte[64];
            byte* ev = stackalloc byte[X.EventSize];

            while (true)
            {
                while (X.XPending(_display) > 0)
                {
                    X.XNextEvent(_display, ev);
                    Dispatch(ev);
                }

                while (_queue.TryDequeue(out Action? action))
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        MyHomelabBrowser.classes.RuntimeLogBuffer.Append("[Basilisk X11] " + ex.Message);
                    }
                }
                X.XFlush(_display);

                if (X.XPending(_display) > 0)
                    continue;
                fds[0].Revents = 0;
                fds[1].Revents = 0;
                poll(fds, 2, 1000);
                if ((fds[1].Revents & PollIn) != 0)
                    read(_wake[0], drain, 64);
            }
        }

        void Dispatch(byte* ev)
        {
            int type = *(int*)ev;
            nint window = *(nint*)(ev + 32);
            if (type == X.ConfigureNotify || type == X.ReparentNotify || type == X.UnmapNotify || type == X.DestroyNotify)
                window = *(nint*)(ev + 40);
            if (_docks.TryGetValue(window, out X11Dock? dock))
                dock.OnEvent(type, ev);
        }

        // ---------------------------------------------------------------
        // Recherche de la fenêtre de Basilisk
        // ---------------------------------------------------------------

        /// <summary>
        /// Fenêtre principale d'un des processus : la plus grande fenêtre affichée portant son
        /// numéro (_NET_WM_PID), parmi les fenêtres de premier niveau et celles cadrées par le
        /// gestionnaire de fenêtres. À appeler sur le fil X11.
        /// </summary>
        public nint FindClientWindow(IReadOnlySet<int> pids)
        {
            nint pidAtom = X.XInternAtom(_display, "_NET_WM_PID", false);
            nint best = 0;
            long bestArea = 0;

            void Visit(nint window, int depth)
            {
                if (WindowPid(window, pidAtom) is int pid && pids.Contains(pid))
                {
                    if (Attributes(window) is { MapState: X.IsViewable } attributes && attributes.Width > 100 && attributes.Height > 80)
                    {
                        long area = (long)attributes.Width * attributes.Height;
                        if (area > bestArea)
                        {
                            best = window;
                            bestArea = area;
                        }
                    }
                    return;
                }
                if (depth < 2)
                {
                    foreach (nint child in Children(window))
                        Visit(child, depth + 1);
                }
            }

            foreach (nint top in Children(Root))
                Visit(top, 1);
            return best;
        }

        int? WindowPid(nint window, nint pidAtom)
        {
            nint type;
            int format;
            nuint count, after;
            byte* data;
            if (X.XGetWindowProperty(_display, window, pidAtom, 0, 1, false, X.XaCardinal, &type, &format, &count, &after, &data) != 0 || data == null)
                return null;
            try
            {
                return count > 0 && format == 32 ? (int)*(long*)data : null;
            }
            finally
            {
                X.XFree(data);
            }
        }

        public List<nint> Children(nint window)
        {
            var result = new List<nint>();
            nint root, parent;
            nint* children;
            uint count;
            if (X.XQueryTree(_display, window, &root, &parent, &children, &count) == 0)
                return result;
            for (uint i = 0; i < count; i++)
                result.Add(children[i]);
            if (children != null)
                X.XFree(children);
            return result;
        }

        public nint Parent(nint window)
        {
            nint root, parent;
            nint* children;
            uint count;
            if (X.XQueryTree(_display, window, &root, &parent, &children, &count) == 0)
                return 0;
            if (children != null)
                X.XFree(children);
            return parent;
        }

        /// <summary>Fenêtre de premier niveau (enfant de la racine) qui contient celle-ci ; 0 si elle n'existe plus.</summary>
        public nint TopWindow(nint window)
        {
            for (int i = 0; i < 64 && window != 0; i++)
            {
                nint parent = Parent(window);
                if (parent == 0 || parent == Root)
                    return parent == Root ? window : 0;
                window = parent;
            }
            return 0;
        }

        public bool IsInside(nint window, nint ancestor)
        {
            for (int i = 0; i < 64 && window != 0; i++)
            {
                if (window == ancestor)
                    return true;
                nint parent = Parent(window);
                if (parent == Root)
                    return false;
                window = parent;
            }
            return false;
        }

        public (int Width, int Height, int MapState)? Attributes(nint window)
        {
            byte* attributes = stackalloc byte[256];
            if (X.XGetWindowAttributes(_display, window, attributes) == 0)
                return null;
            return (*(int*)(attributes + 8), *(int*)(attributes + 12), *(int*)(attributes + 92));
        }

        // ---------------------------------------------------------------
        // Interop
        // ---------------------------------------------------------------

        [StructLayout(LayoutKind.Sequential)]
        struct PollFd
        {
            public int Fd;
            public short Events;
            public short Revents;
        }

        const short PollIn = 1;

        [DllImport("libc", SetLastError = true)] static extern int pipe(int[] fds);
        [DllImport("libc")] static extern int poll(PollFd* fds, nuint count, int timeout);
        [DllImport("libc")] static extern nint read(int fd, byte* buffer, nuint count);
        [DllImport("libc")] static extern nint write(int fd, byte* buffer, nuint count);
    }

    /// <summary>
    /// Fenêtre de Basilisk logée dans l'onglet (sous X11), dans une fenêtre d'accueil de
    /// PommeBrowser. Tant que Basilisk n'a pas le clavier :
    /// - un « écran » invisible le recouvre : le simple survol ne lui donne pas les touches
    ///   tapées dans la barre d'adresse (sous X11, les touches vont à la fenêtre sous le pointeur
    ///   si elle est dans la fenêtre active) ;
    /// - le premier clic est pris par la fenêtre d'accueil, qui donne le clavier à Basilisk et
    ///   lui rejoue le clic.
    /// Quand il a le clavier, il le garde : le gestionnaire de fenêtres le rend à la fenêtre de
    /// PommeBrowser à chaque clic, il lui est aussitôt redonné. Les raccourcis des onglets et de
    /// la fenêtre lui sont pris.
    /// </summary>
    [SupportedOSPlatform("linux")]
    sealed unsafe class X11Dock
    {
        readonly X11Embed _x;
        nint _shield;
        nint _client;
        int _width = 1;
        int _height = 1;
        int _focusAttempts;
        bool _wantKeyboard;
        nint _topLevel;
        long _dockedAt;

        public X11Dock(X11Embed x) => _x = x;

        // Appels depuis le fil de l'interface : exécutés sur le fil X11.
        public Task<nint> CreateHostAsync(nint parent) => _x.Invoke(() => CreateHost(parent));
        public Task<bool> DockAsync(nint client) => _x.Invoke(() => Dock(client));
        public Task<bool> DestroyAsync() => _x.Invoke(() => { Destroy(); return true; });
        public void PostKeyboard(bool page, nint topLevel) => _x.Post(() => SetKeyboard(page, topLevel));
        public Task<nint> FindClientWindowAsync(IReadOnlySet<int> pids) => _x.Invoke(() => _x.FindClientWindow(pids));

        public nint Host { get; private set; }

        /// <summary>Clic dans Basilisk alors qu'il n'avait pas le clavier (fil X11).</summary>
        public event Action? Clicked;

        /// <summary>Raccourci de la fenêtre tapé dans Basilisk (fil X11).</summary>
        public event Action<Key, KeyModifiers>? Shortcut;

        /// <summary>Fenêtre d'accueil, enfant de celle que fournit Avalonia. Fil X11.</summary>
        public nint CreateHost(nint parent)
        {
            nint display = _x.Display;
            Host = X.XCreateSimpleWindow(display, parent, 0, 0, 1, 1, 0, 0, 0);
            X.XSelectInput(display, Host, X.StructureNotifyMask);
            _shield = X.XCreateWindow(display, Host, 0, 0, 1, 1, 0, 0, X.InputOnly, 0, 0, null);
            _x.Register(Host, this);
            ShieldOn();
            X.XSync(display, false);
            return Host;
        }

        /// <summary>
        /// Basilisk sans le clavier : l'écran le recouvre, et un clic est pris par la fenêtre
        /// d'accueil (prise « synchrone » : le clic attend qu'on le rejoue).
        /// </summary>
        void ShieldOn()
        {
            X.XMapRaised(_x.Display, _shield);
            X.XGrabButton(_x.Display, X.AnyButton, X.AnyModifier, Host, false, (uint)X.ButtonPressMask, X.GrabModeSync, X.GrabModeAsync, 0, 0);
        }

        void ShieldOff()
        {
            X.XUnmapWindow(_x.Display, _shield);
            X.XUngrabButton(_x.Display, X.AnyButton, X.AnyModifier, Host);
        }

        /// <summary>
        /// Loge la fenêtre de Basilisk : retirée au gestionnaire de fenêtres (plus de cadre ni
        /// d'entrée dans la barre des tâches), puis placée dans la fenêtre d'accueil. Fil X11.
        /// </summary>
        public bool Dock(nint client)
        {
            nint display = _x.Display;
            if (Host == 0 || client == 0 || _x.Attributes(client) == null)
                return false;
            if (_client == client)
                return true;
            Undock();

            _client = client;
            X.XSelectInput(display, client, X.StructureNotifyMask | X.FocusChangeMask);
            _x.Register(client, this);

            if (_x.Parent(client) != _x.Root || _x.Attributes(client) is { MapState: not X.IsUnmapped })
            {
                X.XWithdrawWindow(display, client, _x.Screen);
                X.XSync(display, false);
                // Le gestionnaire de fenêtres rend la fenêtre à la racine en la libérant de son cadre.
                for (int i = 0; i < 100 && _x.Parent(client) is var parent && parent != _x.Root && parent != 0; i++)
                    Thread.Sleep(10);
            }

            _dockedAt = Environment.TickCount64;
            X.XReparentWindow(display, client, Host, 0, 0);
            X.XMoveResizeWindow(display, client, 0, 0, _width, _height);
            X.XMapWindow(display, client);
            X.XRaiseWindow(display, _shield);
            GrabShortcuts(client);
            X.XSync(display, false);
            return true;
        }

        void GrabShortcuts(nint client)
        {
            nint display = _x.Display;
            foreach ((Key key, KeyModifiers modifiers) in BrowserShortcuts.LegacyWindowShortcuts)
            {
                uint keysym = Keysym(key);
                int keycode = keysym == 0 ? 0 : X.XKeysymToKeycode(display, keysym);
                if (keycode == 0)
                    continue;
                uint mask = (modifiers.HasFlag(KeyModifiers.Control) ? X.ControlMask : 0) |
                            (modifiers.HasFlag(KeyModifiers.Shift) ? X.ShiftMask : 0) |
                            (modifiers.HasFlag(KeyModifiers.Alt) ? X.Mod1Mask : 0);
                // Verr. Maj et Verr. Num ne doivent pas empêcher le raccourci.
                foreach (uint locks in new[] { 0u, X.LockMask, X.Mod2Mask, X.LockMask | X.Mod2Mask })
                    X.XGrabKey(display, keycode, mask | locks, client, false, X.GrabModeAsync, X.GrabModeAsync);
            }
        }

        /// <summary>Rend la fenêtre de Basilisk à la racine, cachée (onglet fermé ou vue détruite). Fil X11.</summary>
        public void Undock()
        {
            if (_client == 0)
                return;
            nint display = _x.Display;
            nint client = _client;
            _client = 0;
            _x.Unregister(client);
            if (_x.Attributes(client) == null)
                return;
            X.XUngrabKey(display, X.AnyKey, X.AnyModifier, client);
            X.XSelectInput(display, client, 0);
            X.XUnmapWindow(display, client);
            X.XReparentWindow(display, client, _x.Root, 0, 0);
            X.XSync(display, false);
        }

        /// <summary>Fenêtre d'accueil détruite par Avalonia : Basilisk en sort d'abord (il serait détruit avec elle). Fil X11.</summary>
        public void Destroy()
        {
            Undock();
            if (Host == 0)
                return;
            _x.Unregister(Host);
            X.XDestroyWindow(_x.Display, Host);
            X.XSync(_x.Display, false);
            Host = 0;
            _shield = 0;
        }

        /// <summary>
        /// Clavier à Basilisk (la vue a le focus d'Avalonia) ou à la fenêtre de PommeBrowser.
        /// Rien n'est pris à une autre application : le clavier ne bouge que s'il est déjà dans
        /// cette fenêtre. Fil X11.
        /// </summary>
        public void SetKeyboard(bool page, nint topLevel)
        {
            nint display = _x.Display;
            if (Host == 0)
                return;
            _topLevel = topLevel;
            _wantKeyboard = page && _client != 0;

            nint focus;
            int revert;
            X.XGetInputFocus(display, &focus, &revert);
            bool inClient = _client != 0 && focus > 1 && _x.IsInside(focus, _client);

            if (!_wantKeyboard)
            {
                _focusAttempts = 0;
                ShieldOn();
                if (inClient && topLevel != 0 && _x.Attributes(topLevel) is { MapState: X.IsViewable })
                    X.XSetInputFocus(display, topLevel, X.RevertToParent, 0);
                X.XFlush(display);
                return;
            }

            ShieldOff();
            if (inClient)
            {
                _focusAttempts = 0;
                return;
            }
            bool ours = focus <= 1 || (topLevel != 0 && _x.TopWindow(focus) == _x.TopWindow(topLevel));
            if (!ours)
                return;
            if (_x.Attributes(_client) is { MapState: X.IsViewable })
            {
                _focusAttempts = 0;
                X.XSetInputFocus(display, _client, X.RevertToParent, 0);
                X.XFlush(display);
                CheckFocusAfterDock();
            }
            else if (++_focusAttempts <= 20)
            {
                // Onglet qui vient d'être affiché : la fenêtre sera visible au prochain tour de mise en page.
                Task.Delay(50).ContinueWith(_ => _x.Post(() =>
                {
                    if (_wantKeyboard)
                        SetKeyboard(true, _topLevel);
                }));
            }
        }

        /// <summary>
        /// Basilisk reprend le clavier s'il est passé ailleurs dans la fenêtre de PommeBrowser, mais
        /// pas s'il est parti dans une autre application ou une boîte de dialogue. Fil X11.
        /// </summary>
        void ReclaimFocus()
        {
            if (!_wantKeyboard || _client == 0)
                return;
            nint display = _x.Display;
            nint focus;
            int revert;
            X.XGetInputFocus(display, &focus, &revert);
            if (focus > 1 && !_x.IsInside(focus, _client) && _x.TopWindow(focus) == _x.TopWindow(Host) &&
                _x.Attributes(_client) is { MapState: X.IsViewable })
            {
                X.XSetInputFocus(display, _client, X.RevertToParent, 0);
                X.XFlush(display);
            }
        }

        /// <summary>
        /// Juste après l'accueil de Basilisk, le gestionnaire de fenêtres rend le clavier (retrait de
        /// sa fenêtre) avec un peu de retard, parfois après que Basilisk l'a reçu : vérifié encore
        /// pendant deux secondes.
        /// </summary>
        void CheckFocusAfterDock()
        {
            if (Environment.TickCount64 - _dockedAt > 2000)
                return;
            foreach (int delay in new[] { 250, 800, 2000 })
                Task.Delay(delay).ContinueWith(_ => _x.Post(ReclaimFocus));
        }

        internal void OnEvent(int type, byte* ev)
        {
            nint display = _x.Display;
            switch (type)
            {
                case X.ConfigureNotify when *(nint*)(ev + 40) == Host:
                    _width = Math.Max(1, *(int*)(ev + 56));
                    _height = Math.Max(1, *(int*)(ev + 60));
                    X.XResizeWindow(display, _shield, (uint)_width, (uint)_height);
                    if (_client != 0)
                        X.XMoveResizeWindow(display, _client, 0, 0, _width, _height);
                    break;

                case X.ConfigureNotify when _client != 0 && *(nint*)(ev + 40) == _client:
                    // Basilisk ne choisit pas sa taille : il remplit l'onglet.
                    if (*(int*)(ev + 48) != 0 || *(int*)(ev + 52) != 0 || *(int*)(ev + 56) != _width || *(int*)(ev + 60) != _height)
                        X.XMoveResizeWindow(display, _client, 0, 0, _width, _height);
                    break;

                case X.ButtonPress:
                {
                    // Premier clic : Basilisk prend le clavier, puis le clic lui est rejoué (sans
                    // l'écran, le serveur X le remet à la fenêtre désormais sous le pointeur).
                    nint time = *(nint*)(ev + 56);
                    ShieldOff();
                    if (_client != 0 && _x.Attributes(_client) is { MapState: X.IsViewable })
                    {
                        _wantKeyboard = true;
                        X.XSetInputFocus(display, _client, X.RevertToParent, time);
                    }
                    X.XAllowEvents(display, X.ReplayPointer, time);
                    X.XFlush(display);
                    Clicked?.Invoke();
                    break;
                }

                case X.FocusOut when _wantKeyboard && _client != 0:
                    // Clic dans la fenêtre : le gestionnaire de fenêtres lui a rendu le clavier.
                    ReclaimFocus();
                    break;

                case X.KeyPress:
                {
                    // Symboles X11 et keyvals GDK ont les mêmes valeurs.
                    Key key = GdkKeys.ToKey((uint)X.XLookupKeysym(ev, 0));
                    if (key != Key.None)
                        Shortcut?.Invoke(key, GdkKeys.Modifiers(*(uint*)(ev + 80)));
                    break;
                }

                case X.DestroyNotify when *(nint*)(ev + 40) == _client:
                    _x.Unregister(_client);
                    _client = 0;
                    _wantKeyboard = false;
                    ShieldOn();
                    break;
            }
        }

        /// <summary>Symbole X11 de la touche (raccourcis pris à Basilisk).</summary>
        internal static uint Keysym(Key key) => key switch
        {
            >= Key.A and <= Key.Z => 0x61u + (uint)(key - Key.A),
            >= Key.D0 and <= Key.D9 => 0x30u + (uint)(key - Key.D0),
            >= Key.F1 and <= Key.F12 => 0xffbeu + (uint)(key - Key.F1),
            Key.Tab => 0xff09,
            Key.Home => 0xff50,
            Key.PageUp => 0xff55,
            Key.PageDown => 0xff56,
            Key.Delete => 0xffff,
            Key.OemComma => 0x2c,
            _ => 0
        };
    }

    /// <summary>Xlib (libX11.so.6), sur 64 bits.</summary>
    [SupportedOSPlatform("linux")]
    static unsafe class X
    {
        const string Lib = "libX11.so.6";

        public const int EventSize = 192;

        public const int KeyPress = 2;
        public const int ButtonPress = 4;
        public const int FocusOut = 10;
        public const int DestroyNotify = 17;
        public const int UnmapNotify = 18;
        public const int ReparentNotify = 21;
        public const int ConfigureNotify = 22;

        public const long ButtonPressMask = 1L << 2;
        public const long StructureNotifyMask = 1L << 17;
        public const long FocusChangeMask = 1L << 21;

        public const uint ShiftMask = 1;
        public const uint LockMask = 2;
        public const uint ControlMask = 4;
        public const uint Mod1Mask = 8;
        public const uint Mod2Mask = 16;
        public const uint AnyModifier = 1 << 15;
        public const uint AnyButton = 0;
        public const int AnyKey = 0;

        public const int GrabModeSync = 0;
        public const int GrabModeAsync = 1;
        public const int ReplayPointer = 2;
        public const int RevertToParent = 2;
        public const uint InputOnly = 2;

        public const int IsUnmapped = 0;
        public const int IsViewable = 2;

        public const nint XaCardinal = 6;

        [DllImport(Lib)] public static extern nint XOpenDisplay(nint name);
        [DllImport(Lib)] public static extern nint XDefaultRootWindow(nint display);
        [DllImport(Lib)] public static extern int XDefaultScreen(nint display);
        [DllImport(Lib)] public static extern int XConnectionNumber(nint display);
        [DllImport(Lib)] public static extern int XPending(nint display);
        [DllImport(Lib)] public static extern int XNextEvent(nint display, byte* ev);
        [DllImport(Lib)] public static extern int XFlush(nint display);
        [DllImport(Lib)] public static extern int XSync(nint display, [MarshalAs(UnmanagedType.Bool)] bool discard);
        [DllImport(Lib)] public static extern int XFree(void* data);

        [DllImport(Lib)] public static extern nint XCreateSimpleWindow(nint display, nint parent, int x, int y, uint width, uint height, uint borderWidth, nuint border, nuint background);
        [DllImport(Lib)] public static extern nint XCreateWindow(nint display, nint parent, int x, int y, uint width, uint height, uint borderWidth, int depth, uint windowClass, nint visual, nuint valueMask, void* attributes);
        [DllImport(Lib)] public static extern int XDestroyWindow(nint display, nint window);
        [DllImport(Lib)] public static extern int XMapWindow(nint display, nint window);
        [DllImport(Lib)] public static extern int XMapRaised(nint display, nint window);
        [DllImport(Lib)] public static extern int XUnmapWindow(nint display, nint window);
        [DllImport(Lib)] public static extern int XRaiseWindow(nint display, nint window);
        [DllImport(Lib)] public static extern int XWithdrawWindow(nint display, nint window, int screen);
        [DllImport(Lib)] public static extern int XReparentWindow(nint display, nint window, nint parent, int x, int y);
        [DllImport(Lib)] public static extern int XMoveResizeWindow(nint display, nint window, int x, int y, int width, int height);
        [DllImport(Lib)] public static extern int XResizeWindow(nint display, nint window, uint width, uint height);
        [DllImport(Lib)] public static extern int XSelectInput(nint display, nint window, long mask);
        [DllImport(Lib)] public static extern int XQueryTree(nint display, nint window, nint* root, nint* parent, nint** children, uint* count);
        [DllImport(Lib)] public static extern int XGetWindowAttributes(nint display, nint window, byte* attributes);
        [DllImport(Lib)] public static extern nint XInternAtom(nint display, string name, [MarshalAs(UnmanagedType.Bool)] bool onlyIfExists);
        [DllImport(Lib)] public static extern int XGetWindowProperty(nint display, nint window, nint property, long offset, long length, [MarshalAs(UnmanagedType.Bool)] bool delete, nint requestedType, nint* actualType, int* actualFormat, nuint* count, nuint* bytesAfter, byte** data);

        [DllImport(Lib)] public static extern int XGetInputFocus(nint display, nint* focus, int* revertTo);
        [DllImport(Lib)] public static extern int XSetInputFocus(nint display, nint window, int revertTo, nint time);
        [DllImport(Lib)] public static extern int XGrabButton(nint display, uint button, uint modifiers, nint window, [MarshalAs(UnmanagedType.Bool)] bool ownerEvents, uint eventMask, int pointerMode, int keyboardMode, nint confineTo, nint cursor);
        [DllImport(Lib)] public static extern int XUngrabButton(nint display, uint button, uint modifiers, nint window);
        [DllImport(Lib)] public static extern int XAllowEvents(nint display, int mode, nint time);
        [DllImport(Lib)] public static extern int XGrabKey(nint display, int keycode, uint modifiers, nint window, [MarshalAs(UnmanagedType.Bool)] bool ownerEvents, int pointerMode, int keyboardMode);
        [DllImport(Lib)] public static extern int XUngrabKey(nint display, int keycode, uint modifiers, nint window);
        [DllImport(Lib)] public static extern byte XKeysymToKeycode(nint display, nuint keysym);
        [DllImport(Lib)] public static extern nint XLookupKeysym(byte* keyEvent, int index);
    }
}
