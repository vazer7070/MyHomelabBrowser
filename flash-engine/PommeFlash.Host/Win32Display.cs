using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using PommeFlash.Host.Native;

namespace PommeFlash.Host
{
    /// <summary>
    /// Fenêtres de l'hôte sous Windows : un cadre (celui que PommeBrowser loge dans l'onglet) et,
    /// dedans, la fenêtre donnée au module, où il crée la sienne. Boucle de messages du fil du module.
    /// </summary>
    [SupportedOSPlatform("windows")]
    sealed unsafe class Win32Display : IHostDisplay
    {
        const string FrameClass = "PommeFlashFrame";
        const string PluginClass = "PommeFlashPlugin";
        const int BlackBrush = 4;
        const uint RunMessage = Win32.WM_APP + 1;

        // Une seule fenêtre par processus : ses procédures (statiques) la retrouvent ici.
        static Win32Display? _current;

        // Minuteries de Delay : 1 à 999 ; celles du module commencent à 1000.
        readonly Dictionary<nuint, Action> _delayed = new();
        nuint _nextDelay = 1;
        PluginInstance? _instance;
        Action? _closeRequested;
        nint _pluginWindow;

        public nint Frame { get; private set; }

        public nint NetscapeWindow => Frame;

        public nint XDisplay => 0;

        public (nint Window, nint Info, int Width, int Height) PluginArea
        {
            get
            {
                Win32.RECT rect;
                Win32.GetClientRect(_pluginWindow, &rect);
                return (_pluginWindow, 0, rect.right - rect.left, rect.bottom - rect.top);
            }
        }

        public void Create(HostOptions options)
        {
            _current = this;
            nint module = Win32.GetModuleHandleW(null);
            Register(FrameClass, (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&FrameProc, module);
            Register(PluginClass, (nint)(delegate* unmanaged<nint, uint, nuint, nint, nint>)&PluginProc, module);

            // Taille demandée = zone donnée au module (bordures et titre en plus).
            const uint style = Win32.WS_OVERLAPPEDWINDOW | Win32.WS_CLIPCHILDREN;
            var outer = new Win32.RECT { right = options.Width, bottom = options.Height };
            Win32.AdjustWindowRectEx(&outer, style, false, 0);
            Frame = Win32.CreateWindowExW(0, FrameClass, "PommeBrowser – Flash", style,
                Win32.CW_USEDEFAULT, Win32.CW_USEDEFAULT, outer.right - outer.left, outer.bottom - outer.top, 0, 0, module, 0);
            if (Frame == 0)
                throw new InvalidOperationException($"Fenêtre impossible à créer (erreur {Marshal.GetLastPInvokeError()}).");

            Win32.RECT client;
            Win32.GetClientRect(Frame, &client);
            _pluginWindow = Win32.CreateWindowExW(0, PluginClass, null,
                Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPCHILDREN | Win32.WS_CLIPSIBLINGS,
                0, 0, client.right - client.left, client.bottom - client.top, Frame, 0, module, 0);
            if (_pluginWindow == 0)
                throw new InvalidOperationException($"Fenêtre du module impossible à créer (erreur {Marshal.GetLastPInvokeError()}).");

            if (!options.Hidden)
                Win32.ShowWindow(Frame, Win32.SW_SHOW);
        }

        static void Register(string name, nint procedure, nint module)
        {
            nint className = Marshal.StringToHGlobalUni(name);
            var wc = new Win32.WNDCLASSEXW
            {
                cbSize = (uint)sizeof(Win32.WNDCLASSEXW),
                lpfnWndProc = procedure,
                hInstance = module,
                hCursor = Win32.LoadCursorW(0, Win32.IDC_ARROW),
                hbrBackground = Win32.GetStockObject(BlackBrush),
                lpszClassName = className
            };
            if (Win32.RegisterClassExW(&wc) == 0)
                throw new InvalidOperationException($"Classe de fenêtre {name} impossible à enregistrer (erreur {Marshal.GetLastPInvokeError()}).");
            // Le nom de classe reste utilisé par Windows : jamais libéré.
        }

        public void Attach(PluginInstance instance, Action closeRequested)
        {
            _instance = instance;
            _closeRequested = closeRequested;
        }

        public int Run()
        {
            Win32.MSG msg;
            while (Win32.GetMessageW(&msg, 0, 0, 0) > 0)
            {
                Win32.TranslateMessage(&msg);
                Win32.DispatchMessageW(&msg);
            }
            return (int)msg.wParam;
        }

        public void Close() => Win32.DestroyWindow(Frame);

        public void Wake() => Win32.PostMessageW(Frame, RunMessage, 0, 0);

        public void Delay(uint milliseconds, Action action)
        {
            nuint id = _nextDelay;
            _nextDelay = _nextDelay >= 999 ? 1 : _nextDelay + 1;
            _delayed[id] = action;
            Win32.SetTimer(Frame, id, milliseconds, 0);
        }

        public void StartTimer(uint id, uint interval) => Win32.SetTimer(Frame, id, Math.Max(1, interval), 0);

        public void StopTimer(uint id) => Win32.KillTimer(Frame, id);

        /// <summary>
        /// Attente bloquante, comme dans un navigateur ; pendant ce temps, les messages envoyés par
        /// les autres fils sont traités (PommeBrowser place et affiche la fenêtre du module : sans
        /// cela, les deux processus s'attendraient l'un l'autre).
        /// </summary>
        public bool Wait(WaitHandle signal, TimeSpan timeout)
        {
            nint handle = signal.SafeWaitHandle.DangerousGetHandle();
            var clock = Stopwatch.StartNew();
            while (true)
            {
                long remaining = (long)(timeout - clock.Elapsed).TotalMilliseconds;
                if (remaining <= 0)
                    return signal.WaitOne(0);
                uint wait = Win32.MsgWaitForMultipleObjectsEx(1, &handle, (uint)remaining, Win32.QS_SENDMESSAGE, 0);
                if (wait == Win32.WAIT_OBJECT_0)
                    return true;
                if (wait == Win32.WAIT_OBJECT_0 + 1)
                {
                    Win32.MSG message;
                    Win32.PeekMessageW(&message, 0, 0, 0, Win32.PM_NOREMOVE | Win32.PM_QS_SENDMESSAGE);
                }
            }
        }

        /// <summary>Clavier à la fenêtre du module (puis à celle qu'il y a créée), s'il ne l'a pas déjà.</summary>
        void FocusPlugin()
        {
            nint focus = Win32.GetFocus();
            if (_pluginWindow != 0 && focus != _pluginWindow && !Win32.IsChild(_pluginWindow, focus))
                Win32.SetFocus(_pluginWindow);
        }

        bool RunDelayed(nuint id)
        {
            if (!_delayed.Remove(id, out Action? action))
                return false;
            Win32.KillTimer(Frame, id);
            UiThread.Run(action);
            return true;
        }

        [UnmanagedCallersOnly]
        static nint FrameProc(nint hwnd, uint message, nuint wParam, nint lParam)
        {
            Win32Display? self = _current;
            try
            {
                switch (message)
                {
                    case RunMessage:
                        UiThread.RunPending();
                        return 0;
                    case Win32.WM_TIMER:
                        if (self != null && !self.RunDelayed(wParam))
                            self._instance?.OnTimer((uint)wParam);
                        return 0;
                    case Win32.WM_SIZE:
                        int width = (int)(lParam & 0xFFFF);
                        int height = (int)((lParam >> 16) & 0xFFFF);
                        if (width > 0 && height > 0 && self is { _pluginWindow: not 0 })
                        {
                            Win32.MoveWindow(self._pluginWindow, 0, 0, width, height, true);
                            self._instance?.SetWindow(self._pluginWindow, 0, width, height);
                        }
                        return 0;
                    case Win32.WM_SETFOCUS:
                        if (self is { _pluginWindow: not 0 })
                            Win32.SetFocus(self._pluginWindow);
                        return 0;
                    case Win32.WM_MOUSEACTIVATE:
                        // Clic dans le contenu : le clavier lui va, comme Firefox le faisait pour ses
                        // modules (Flash compte sur le navigateur ; sans cela, les touches restent à
                        // la page). Puis Windows active la fenêtre de PommeBrowser s'il le faut.
                        self?.FocusPlugin();
                        break;
                    case Win32.WM_PARENTNOTIFY:
                        // Même clic, signalé à chaque parent : au cas où le module garde WM_MOUSEACTIVATE.
                        if ((uint)(wParam & 0xFFFF) is Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN or Win32.WM_MBUTTONDOWN or Win32.WM_XBUTTONDOWN)
                            self?.FocusPlugin();
                        break;
                    case Win32.WM_CLOSE:
                        if (self?._closeRequested != null)
                            self._closeRequested();
                        else
                            Win32.DestroyWindow(hwnd);
                        return 0;
                    case Win32.WM_DESTROY:
                        Win32.PostQuitMessage(0);
                        return 0;
                }
            }
            catch (Exception ex)
            {
                HostChannel.Error("Fenêtre : " + ex.Message);
            }
            return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
        }

        /// <summary>Fenêtre du module : le clavier va à la fenêtre qu'il y a créée.</summary>
        [UnmanagedCallersOnly]
        static nint PluginProc(nint hwnd, uint message, nuint wParam, nint lParam)
        {
            if (message == Win32.WM_SETFOCUS)
            {
                nint child = Win32.GetWindow(hwnd, Win32.GW_CHILD);
                if (child != 0)
                    Win32.SetFocus(child);
                return 0;
            }
            if (message == Win32.WM_ERASEBKGND)
                return 1;
            return Win32.DefWindowProcW(hwnd, message, wParam, lParam);
        }
    }
}
