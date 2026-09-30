using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Input;
using Avalonia.Threading;
using PommeBrowser.Engine;
using PommeBrowser.Engine.WebView2;

namespace PommeBrowser.Legacy
{
    /// <summary>
    /// Fenêtre de Basilisk logée dans l'onglet (sous Windows), comme dans l'édition WPF : elle
    /// devient enfant d'une fenêtre d'accueil de PommeBrowser, sans cadre ni bouton dans la barre
    /// des tâches. Windows relie alors les files de saisie des deux processus : un clic dans
    /// Basilisk lui donne le clavier, et PommeBrowser le reprend quand un de ses champs a le focus.
    /// Les raccourcis des onglets et de la fenêtre sont pris à Basilisk par un crochet clavier.
    /// Tout se passe sur le fil de l'interface.
    /// </summary>
    [SupportedOSPlatform("windows")]
    sealed unsafe class Win32Dock
    {
        const string ClassName = "PommeBrowserLegacyHost";
        static readonly Dictionary<nint, Win32Dock> Docks = new();
        static bool _registered;
        static nint _keyboardHook;
        static nint _focusHook;

        nint _client;
        nint _focusTarget;
        (int X, int Y, int Width, int Height)? _placement;

        public nint Host { get; private set; }

        /// <summary>Clic dans Basilisk (il a pris le clavier).</summary>
        public event Action? Clicked;

        /// <summary>Raccourci de la fenêtre tapé dans Basilisk.</summary>
        public event Action<Key, KeyModifiers>? Shortcut;

        public nint CreateHost(nint parent)
        {
            RegisterClass();
            Host = CreateWindowExW(0, ClassName, null, WsChild | WsVisible | WsClipChildren, 0, 0, 1, 1, parent, 0, GetModuleHandleW(null), 0);
            if (Host == 0)
                throw new InvalidOperationException("CreateWindowEx: " + Marshal.GetLastPInvokeError());
            Docks[Host] = this;
            InstallHooks();
            return Host;
        }

        public bool Dock(nint client)
        {
            if (Host == 0 || client == 0 || !IsWindow(client))
                return false;
            if (_client == client)
                return true;
            Undock();

            _client = client;
            SetParent(client, Host);
            int style = GetWindowLongW(client, GwlStyle);
            // Ni cadre ni boutons de fenêtre : Basilisk n'est plus qu'une page de l'onglet.
            style = (style & ~(WsPopup | WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox)) | WsChild | WsVisible;
            SetWindowLongW(client, GwlStyle, style);
            int exStyle = GetWindowLongW(client, GwlExStyle);
            SetWindowLongW(client, GwlExStyle, (exStyle & ~WsExAppWindow) | WsExToolWindow);
            SetWindowPos(client, 0, 0, 0, 0, 0, SwpFrameChanged | SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate);
            FitClient();
            ShowWindow(client, SwShow);
            _focusTarget = LargestChild(client) is var child && child != 0 ? child : client;
            return true;
        }

        void FitClient()
        {
            if (_client == 0)
                return;
            if (_placement is { } placement)
            {
                // Contenu dont seule une partie est visible : l'accueil le coupe. Déplacement sans
                // attendre l'autre processus (appelé à chaque défilement de la page).
                SetWindowPos(_client, 0, placement.X, placement.Y, Math.Max(1, placement.Width), Math.Max(1, placement.Height),
                    SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos);
            }
            else if (GetClientRect(Host, out Rect rect))
            {
                MoveWindow(_client, 0, 0, Math.Max(1, rect.Right - rect.Left), Math.Max(1, rect.Bottom - rect.Top), true);
            }
        }

        /// <summary>
        /// Position et taille de la fenêtre logée dans l'accueil (pixels), ou null pour qu'elle le
        /// remplisse. Gardées si l'accueil est recréé.
        /// </summary>
        public void SetClientPlacement((int X, int Y, int Width, int Height)? placement)
        {
            if (_placement == placement)
                return;
            _placement = placement;
            FitClient();
        }

        /// <summary>
        /// L'accueil passe devant les autres vues natives de la fenêtre (la page web), qu'il
        /// recouvre : sa fenêtre la plus haute sous celle de PommeBrowser remonte en tête.
        /// </summary>
        public void BringToFront()
        {
            if (Host == 0)
                return;
            nint root = GetAncestor(Host, GaRoot);
            nint window = Host;
            while (GetAncestor(window, GaParent) is var parent && parent != 0 && parent != root)
                window = parent;
            SetWindowPos(window, HwndTop, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }

        /// <summary>La fenêtre de Basilisk redevient une fenêtre à part, cachée (sinon elle serait détruite avec l'accueil).</summary>
        public void Undock()
        {
            nint client = _client;
            _client = 0;
            _focusTarget = 0;
            if (client == 0 || !IsWindow(client))
                return;
            ShowWindow(client, SwHide);
            SetParent(client, 0);
            int style = GetWindowLongW(client, GwlStyle);
            SetWindowLongW(client, GwlStyle, (style & ~WsChild) | WsPopup | WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox);
            int exStyle = GetWindowLongW(client, GwlExStyle);
            SetWindowLongW(client, GwlExStyle, (exStyle & ~WsExToolWindow) | WsExAppWindow);
        }

        public void Destroy()
        {
            Undock();
            if (Host == 0)
                return;
            Docks.Remove(Host);
            DestroyWindow(Host);
            Host = 0;
            if (Docks.Count == 0)
                RemoveHooks();
        }

        /// <summary>Clavier à Basilisk (la vue a le focus d'Avalonia) ou rendu à la fenêtre de PommeBrowser.</summary>
        public void SetKeyboard(bool page, nint topLevel)
        {
            if (_client == 0 || !IsWindow(_client))
                return;
            nint focus = GetFocus();
            bool inClient = focus != 0 && (focus == _client || IsChild(_client, focus));
            if (!page)
            {
                if (inClient && topLevel != 0)
                    SetFocus(topLevel);
                return;
            }
            if (inClient || GetForegroundWindow() != topLevel)
                return;

            // Fil différent : les files de saisie sont reliées le temps de donner le focus.
            uint thread = GetCurrentThreadId();
            uint target = GetWindowThreadProcessId(_focusTarget, out _);
            bool attached = target != thread && AttachThreadInput(thread, target, true);
            try
            {
                SetFocus(_focusTarget != 0 && IsWindow(_focusTarget) ? _focusTarget : _client);
            }
            finally
            {
                if (attached)
                    AttachThreadInput(thread, target, false);
            }
        }

        bool Contains(nint window) => _client != 0 && window != 0 && (window == _client || IsChild(_client, window));

        // ---------------------------------------------------------------
        // Fenêtre d'accueil
        // ---------------------------------------------------------------

        static void RegisterClass()
        {
            if (_registered)
                return;
            fixed (char* name = ClassName)
            {
                var windowClass = new WndClassEx
                {
                    Size = (uint)sizeof(WndClassEx),
                    WndProc = &WndProc,
                    Instance = GetModuleHandleW(null),
                    ClassName = name
                };
                if (RegisterClassExW(&windowClass) == 0)
                    throw new InvalidOperationException("RegisterClassEx: " + Marshal.GetLastPInvokeError());
            }
            _registered = true;
        }

        [UnmanagedCallersOnly]
        static nint WndProc(nint window, uint message, nint wParam, nint lParam)
        {
            // Avalonia redimensionne l'accueil : Basilisk suit.
            if (message == WmSize && Docks.TryGetValue(window, out Win32Dock? dock))
                dock.FitClient();
            return DefWindowProcW(window, message, wParam, lParam);
        }

        // ---------------------------------------------------------------
        // Crochets : focus pris par Basilisk, raccourcis de la fenêtre
        // ---------------------------------------------------------------

        static void InstallHooks()
        {
            if (_focusHook == 0)
                _focusHook = SetWinEventHook(EventObjectFocus, EventObjectFocus, 0, &OnFocusEvent, 0, 0, WinEventOutOfContext | WinEventSkipOwnProcess);
            if (_keyboardHook == 0)
                _keyboardHook = SetWindowsHookExW(WhKeyboardLl, &OnKeyboard, GetModuleHandleW(null), 0);
        }

        static void RemoveHooks()
        {
            if (_focusHook != 0)
                UnhookWinEvent(_focusHook);
            if (_keyboardHook != 0)
                UnhookWindowsHookEx(_keyboardHook);
            _focusHook = 0;
            _keyboardHook = 0;
        }

        [UnmanagedCallersOnly]
        static void OnFocusEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
        {
            foreach (Win32Dock dock in Docks.Values)
            {
                if (dock.Contains(window))
                {
                    Action? clicked = dock.Clicked;
                    Dispatcher.UIThread.Post(() => clicked?.Invoke());
                    return;
                }
            }
        }

        [UnmanagedCallersOnly]
        static nint OnKeyboard(int code, nint wParam, nint lParam)
        {
            if (code >= 0 && (wParam == WmKeyDown || wParam == WmSysKeyDown))
            {
                var info = (KbdLlHookStruct*)lParam;
                Key key = VirtualKeys.ToKey(info->VkCode);
                if (key != Key.None && GetFocus() is var focus && focus != 0)
                {
                    KeyModifiers modifiers = (IsDown(VirtualKeys.Control) ? KeyModifiers.Control : 0) |
                                             (IsDown(VirtualKeys.Shift) ? KeyModifiers.Shift : 0) |
                                             ((info->Flags & LlkhfAltDown) != 0 ? KeyModifiers.Alt : 0);
                    if (BrowserShortcuts.IsLegacyWindowShortcut(key, modifiers))
                    {
                        nint foreground = GetForegroundWindow();
                        foreach (Win32Dock dock in Docks.Values)
                        {
                            if (dock.Contains(focus) && GetAncestor(dock.Host, GaRoot) == foreground)
                            {
                                Action<Key, KeyModifiers>? shortcut = dock.Shortcut;
                                Dispatcher.UIThread.Post(() => shortcut?.Invoke(key, modifiers));
                                return 1;
                            }
                        }
                    }
                }
            }
            return CallNextHookEx(0, code, wParam, lParam);
        }

        static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

        /// <summary>Plus grande fenêtre enfant visible (la page de Basilisk) : c'est elle qui reçoit le clavier.</summary>
        static nint LargestChild(nint top)
        {
            nint best = 0;
            long bestArea = 0;
            foreach (nint child in ChildWindows(top))
            {
                if (!IsWindowVisible(child) || !GetWindowRect(child, out Rect rect))
                    continue;
                long area = (long)(rect.Right - rect.Left) * (rect.Bottom - rect.Top);
                if (area > bestArea)
                {
                    best = child;
                    bestArea = area;
                }
            }
            return best;
        }

        static List<nint> ChildWindows(nint parent)
        {
            var children = new List<nint>();
            GCHandle handle = GCHandle.Alloc(children);
            try
            {
                EnumChildWindows(parent, &CollectChild, GCHandle.ToIntPtr(handle));
            }
            finally
            {
                handle.Free();
            }
            return children;
        }

        [UnmanagedCallersOnly]
        static int CollectChild(nint window, nint state)
        {
            ((List<nint>)GCHandle.FromIntPtr(state).Target!).Add(window);
            return 1;
        }

        // ---------------------------------------------------------------
        // Interop
        // ---------------------------------------------------------------

        const int GwlStyle = -16;
        const int GwlExStyle = -20;
        const int WsChild = 0x40000000;
        const int WsVisible = 0x10000000;
        const int WsClipChildren = 0x02000000;
        const int WsPopup = unchecked((int)0x80000000);
        const int WsCaption = 0x00C00000;
        const int WsThickFrame = 0x00040000;
        const int WsSysMenu = 0x00080000;
        const int WsMinimizeBox = 0x00020000;
        const int WsMaximizeBox = 0x00010000;
        const int WsExAppWindow = 0x00040000;
        const int WsExToolWindow = 0x00000080;
        const uint SwpNoSize = 0x0001;
        const uint SwpNoMove = 0x0002;
        const uint SwpNoZOrder = 0x0004;
        const uint SwpNoActivate = 0x0010;
        const uint SwpFrameChanged = 0x0020;
        const uint SwpAsyncWindowPos = 0x4000;
        const nint HwndTop = 0;
        const int SwHide = 0;
        const int SwShow = 5;
        const uint WmSize = 0x0005;
        const nint WmKeyDown = 0x0100;
        const nint WmSysKeyDown = 0x0104;
        const int WhKeyboardLl = 13;
        const uint LlkhfAltDown = 0x20;
        const uint EventObjectFocus = 0x8005;
        const uint GaParent = 1;
        const uint GaRoot = 2;
        const uint WinEventOutOfContext = 0x0000;
        const uint WinEventSkipOwnProcess = 0x0002;

        [StructLayout(LayoutKind.Sequential)]
        struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct WndClassEx
        {
            public uint Size;
            public uint Style;
            public delegate* unmanaged<nint, uint, nint, nint, nint> WndProc;
            public int ClassExtra;
            public int WindowExtra;
            public nint Instance;
            public nint Icon;
            public nint Cursor;
            public nint Background;
            public char* MenuName;
            public char* ClassName;
            public nint SmallIcon;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct KbdLlHookStruct
        {
            public uint VkCode;
            public uint ScanCode;
            public uint Flags;
            public uint Time;
            public nint ExtraInfo;
        }

        [DllImport("user32.dll", SetLastError = true)] static extern ushort RegisterClassExW(WndClassEx* windowClass);
        [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] static extern nint CreateWindowExW(int exStyle, string className, string? windowName, int style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
        [DllImport("user32.dll")] static extern nint DefWindowProcW(nint window, uint message, nint wParam, nint lParam);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool DestroyWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindow(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsWindowVisible(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsChild(nint parent, nint window);
        [DllImport("user32.dll")] static extern nint SetParent(nint child, nint parent);
        [DllImport("user32.dll")] static extern int GetWindowLongW(nint window, int index);
        [DllImport("user32.dll")] static extern int SetWindowLongW(nint window, int index, int value);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool MoveWindow(nint window, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetClientRect(nint window, out Rect rect);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetWindowRect(nint window, out Rect rect);
        [DllImport("user32.dll")] static extern int EnumChildWindows(nint parent, delegate* unmanaged<nint, nint, int> callback, nint state);
        [DllImport("user32.dll")] static extern nint GetFocus();
        [DllImport("user32.dll")] static extern nint SetFocus(nint window);
        [DllImport("user32.dll")] static extern nint GetForegroundWindow();
        [DllImport("user32.dll")] static extern nint GetAncestor(nint window, uint flags);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);
        [DllImport("user32.dll")] static extern short GetAsyncKeyState(int virtualKey);
        [DllImport("user32.dll")] static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback, uint processId, uint threadId, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool UnhookWinEvent(nint hook);
        [DllImport("user32.dll", SetLastError = true)] static extern nint SetWindowsHookExW(int hookId, delegate* unmanaged<int, nint, nint, nint> callback, nint module, uint threadId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool UnhookWindowsHookEx(nint hook);
        [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);
    }
}
