using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using PommeFlash.Host.Native;

namespace PommeFlash.Host
{
    /// <summary>
    /// Fenêtres de l'hôte : un cadre (celui que PommeBrowser loge dans l'onglet) et, dedans, la
    /// fenêtre donnée au module, où il crée la sienne. Boucle de messages du fil du module.
    /// </summary>
    static unsafe class HostWindow
    {
        const string FrameClass = "PommeFlashFrame";
        const string PluginClass = "PommeFlashPlugin";
        const int BlackBrush = 4;

        static PluginInstance? _instance;
        static Action? _closeRequested;

        public static nint Frame { get; private set; }

        public static nint PluginWindow { get; private set; }

        public static void Create(HostOptions options)
        {
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

            (int width, int height) = ClientSize(Frame);
            PluginWindow = Win32.CreateWindowExW(0, PluginClass, null,
                Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPCHILDREN | Win32.WS_CLIPSIBLINGS,
                0, 0, width, height, Frame, 0, module, 0);
            if (PluginWindow == 0)
                throw new InvalidOperationException($"Fenêtre du module impossible à créer (erreur {Marshal.GetLastPInvokeError()}).");

            UiThread.Attach(Frame);
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

        public static (int Width, int Height) ClientSize(nint window)
        {
            Win32.RECT rect;
            Win32.GetClientRect(window, &rect);
            return (rect.right - rect.left, rect.bottom - rect.top);
        }

        /// <summary>Instance affichée (tailles transmises au module) et fermeture demandée.</summary>
        public static void Attach(PluginInstance instance, Action closeRequested)
        {
            _instance = instance;
            _closeRequested = closeRequested;
        }

        public static int Run()
        {
            Win32.MSG msg;
            while (Win32.GetMessageW(&msg, 0, 0, 0) > 0)
            {
                Win32.TranslateMessage(&msg);
                Win32.DispatchMessageW(&msg);
            }
            return (int)msg.wParam;
        }

        [UnmanagedCallersOnly]
        static nint FrameProc(nint hwnd, uint message, nuint wParam, nint lParam)
        {
            try
            {
                switch (message)
                {
                    case UiThread.RunMessage:
                        UiThread.RunPending();
                        return 0;
                    case Win32.WM_TIMER:
                        if (!UiThread.RunDelayed(wParam))
                            _instance?.OnTimer((uint)wParam);
                        return 0;
                    case Win32.WM_SIZE:
                        int width = (int)(lParam & 0xFFFF);
                        int height = (int)((lParam >> 16) & 0xFFFF);
                        if (width > 0 && height > 0 && PluginWindow != 0)
                        {
                            Win32.MoveWindow(PluginWindow, 0, 0, width, height, true);
                            _instance?.SetWindow(PluginWindow, width, height);
                        }
                        return 0;
                    case Win32.WM_SETFOCUS:
                        if (PluginWindow != 0)
                            Win32.SetFocus(PluginWindow);
                        return 0;
                    case Win32.WM_MOUSEACTIVATE:
                        // Clic dans le contenu : le clavier lui va, comme Firefox le faisait pour ses
                        // modules (Flash compte sur le navigateur ; sans cela, les touches restent à
                        // la page). Puis Windows active la fenêtre de PommeBrowser s'il le faut.
                        FocusPlugin();
                        break;
                    case Win32.WM_PARENTNOTIFY:
                        // Même clic, signalé à chaque parent : au cas où le module garde WM_MOUSEACTIVATE.
                        if ((uint)(wParam & 0xFFFF) is Win32.WM_LBUTTONDOWN or Win32.WM_RBUTTONDOWN or Win32.WM_MBUTTONDOWN or Win32.WM_XBUTTONDOWN)
                            FocusPlugin();
                        break;
                    case Win32.WM_CLOSE:
                        if (_closeRequested != null)
                            _closeRequested();
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

        /// <summary>Clavier à la fenêtre du module (puis à celle qu'il y a créée), s'il ne l'a pas déjà.</summary>
        static void FocusPlugin()
        {
            nint focus = Win32.GetFocus();
            if (PluginWindow != 0 && focus != PluginWindow && !Win32.IsChild(PluginWindow, focus))
                Win32.SetFocus(PluginWindow);
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
