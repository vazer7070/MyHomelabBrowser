using System;
using System.Collections.Generic;
using System.Linq;
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
        uint _attachedThread;
        (int X, int Y, int Width, int Height)? _placement;

        public nint Host { get; private set; }

        /// <summary>La fenêtre logée a reçu le clavier (clic dedans, ou rendu par le gardien du clavier).</summary>
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
            PommeBrowser.Core.CrashWatch.Activity = "fenêtre d'un autre processus logée dans l'onglet";

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
            PommeBrowser.Core.CrashWatch.Activity = null;
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
            if (_keeper == this)
                _keeper = null;
            if (_requested == this)
                _requested = null;
            DetachInput();
            if (client == 0 || !IsWindow(client))
                return;
            PommeBrowser.Core.CrashWatch.Activity = "fenêtre d'un autre processus retirée de l'onglet";
            ShowWindow(client, SwHide);
            SetParent(client, 0);
            int style = GetWindowLongW(client, GwlStyle);
            SetWindowLongW(client, GwlStyle, (style & ~WsChild) | WsPopup | WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox);
            int exStyle = GetWindowLongW(client, GwlExStyle);
            SetWindowLongW(client, GwlExStyle, (exStyle & ~WsExToolWindow) | WsExAppWindow);
            PommeBrowser.Core.CrashWatch.Activity = null;
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

        /// <summary>Nom de la fenêtre logée dans le journal du clavier (« jeu f2 : EvonyClient.swf »…).</summary>
        public string Name { get; set; } = "fenêtre logée";

        /// <summary>
        /// La fenêtre logée peut garder le clavier : sa vue est affichée (onglet actif) et aucun
        /// élément de PommeBrowser (champ, bouton) n'a pris le focus d'Avalonia.
        /// </summary>
        public Func<bool>? CanKeepKeyboard { get; set; }

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
                {
                    if (_keeper == this)
                        _keeper = null;
                    NoteKeyboard("PommeBrowser (repris à " + Name + ")");
                    SetFocus(topLevel);
                }
                return;
            }
            if (inClient || GetForegroundWindow() != topLevel)
                return;
            NoteKeyboard(Name + " (donné par PommeBrowser)");
            FocusClient();
        }

        /// <summary>
        /// Focus à la fenêtre logée. Les files de saisie de PommeBrowser et de l'autre processus
        /// sont déjà reliées par Windows (fenêtre enfant d'un autre fil) : le focus est donné tel
        /// quel. Si cela échoue, elles sont reliées à la main, et le restent tant que la fenêtre
        /// est logée : les délier aussitôt après pourrait couper aussi le lien établi par Windows,
        /// et le clavier n'arriverait plus au lecteur.
        /// </summary>
        void FocusClient()
        {
            nint target = _focusTarget != 0 && IsWindow(_focusTarget) ? _focusTarget : _client;
            SetFocus(target);
            if (Contains(GetFocus()))
                return;
            uint thread = GetCurrentThreadId();
            uint targetThread = GetWindowThreadProcessId(target, out _);
            if (_attachedThread == 0 && targetThread != 0 && targetThread != thread && AttachThreadInput(thread, targetThread, true))
            {
                _attachedThread = targetThread;
                LogKeyboard("Files de saisie reliées à la main pour " + Name + ".");
            }
            SetFocus(target);
        }

        /// <summary>Fenêtre retirée : le lien posé par FocusClient est défait.</summary>
        void DetachInput()
        {
            if (_attachedThread == 0)
                return;
            AttachThreadInput(GetCurrentThreadId(), _attachedThread, false);
            _attachedThread = 0;
        }

        /// <summary>
        /// La page donne le focus à l'élément du contenu (élément.focus()) : le prochain passage du
        /// clavier à cette fenêtre est voulu, même si une autre le gardait.
        /// </summary>
        public void RequestKeyboard() => _requested = this;

        bool Contains(nint window) => _client != 0 && window != 0 && (window == _client || IsChild(_client, window));

        /// <summary>Le pointeur est au-dessus de la partie affichée de la fenêtre logée.</summary>
        bool IsUnder(Point cursor)
            => Host != 0 && GetWindowRect(Host, out Rect rect) &&
               cursor.X >= rect.Left && cursor.X < rect.Right && cursor.Y >= rect.Top && cursor.Y < rect.Bottom;

        bool CanKeep() => _client != 0 && Host != 0 && IsWindow(_client) && (CanKeepKeyboard?.Invoke() ?? true);

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

        // Fonctions appelées par Windows ([UnmanagedCallersOnly]) : une exception ne peut pas
        // remonter jusqu'à Windows, .NET arrêterait PommeBrowser sur-le-champ, sans rien consigner.
        // Elle est donc consignée (errors.log), et Windows reçoit la réponse par défaut.
        static void Report(string where, Exception exception)
        {
            try
            {
                PommeBrowser.Core.ErrorLog.Write("Fenêtre logée (" + where + ")", exception);
            }
            catch
            {
                // Rien ne doit sortir d'ici.
            }
        }

        [UnmanagedCallersOnly]
        static nint WndProc(nint window, uint message, nint wParam, nint lParam)
        {
            try
            {
                // Avalonia redimensionne l'accueil : Basilisk suit.
                if (message == WmSize && Docks.TryGetValue(window, out Win32Dock? dock))
                    dock.FitClient();
            }
            catch (Exception ex)
            {
                Report("taille", ex);
            }
            return DefWindowProcW(window, message, wParam, lParam);
        }

        // ---------------------------------------------------------------
        // Crochets : focus pris par Basilisk, raccourcis de la fenêtre
        // ---------------------------------------------------------------

        static void InstallHooks()
        {
            // Tous les processus, PommeBrowser compris : sa propre fenêtre peut aussi prendre le
            // clavier au lecteur (voir OnFocusChanged).
            if (_focusHook == 0)
                _focusHook = SetWinEventHook(EventObjectFocus, EventObjectFocus, 0, &OnFocusEvent, 0, 0, WinEventOutOfContext);
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

        // Journal du clavier (rapports) : à chaque changement de détenteur, 300 lignes au plus.
        static string? _keyboardOwner;
        static int _keyboardNotes;
        static readonly Dictionary<uint, string> ProcessNames = new();

        // Gardien du clavier : fenêtre logée qui l'a reçu en dernier (clic dedans, ou élément.focus()
        // de la page). Une autre fenêtre de PommeBrowser (page web, fenêtre elle-même, autre
        // lecteur) qui le lui prend sans que l'utilisateur ait cliqué ailleurs le lui rend.
        static Win32Dock? _keeper;
        static Win32Dock? _requested;
        static nint _lastFocus;
        static long _givebackStart;
        static int _givebacks;
        const int MaxGivebacks = 10;
        const long GivebackPeriod = 5000;

        /// <summary>Le clavier change de détenteur dans la fenêtre de PommeBrowser : noté au journal.</summary>
        static void NoteKeyboard(string owner)
        {
            if (owner == _keyboardOwner)
                return;
            _keyboardOwner = owner;
            LogKeyboard("→ " + owner);
        }

        static void LogKeyboard(string text)
        {
            if (_keyboardNotes >= 300)
                return;
            _keyboardNotes++;
            MyHomelabBrowser.classes.RuntimeLogBuffer.Append("[Clavier] " + text);
        }

        /// <summary>Programme d'une fenêtre (journal du clavier).</summary>
        static string ProcessOf(nint window)
        {
            GetWindowThreadProcessId(window, out uint process);
            if (ProcessNames.TryGetValue(process, out string? known))
                return known;
            string name;
            try
            {
                using var running = System.Diagnostics.Process.GetProcessById((int)process);
                name = running.ProcessName;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                name = "processus " + process;
            }
            if (ProcessNames.Count > 64)
                ProcessNames.Clear();
            ProcessNames[process] = name;
            return name;
        }

        /// <summary>Fenêtre qui a le clavier, pour le journal : programme et classe de fenêtre.</summary>
        static string Describe(nint window)
        {
            char* name = stackalloc char[64];
            int length = GetClassNameW(window, name, 64);
            string windowClass = length > 0 ? new string(name, 0, length) : "?";
            return GetWindowThreadProcessId(window, out uint process) != 0 && process == (uint)Environment.ProcessId
                ? "PommeBrowser (" + windowClass + ")"
                : ProcessOf(window) + " (" + windowClass + ")";
        }

        static Win32Dock? DockOf(nint window)
        {
            foreach (Win32Dock dock in Docks.Values)
            {
                if (dock.Contains(window))
                    return dock;
            }
            return null;
        }

        [UnmanagedCallersOnly]
        static void OnFocusEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time)
        {
            try
            {
                if (window == 0 || Docks.Count == 0)
                    return;
                // Seules comptent les fenêtres de PommeBrowser où un lecteur est logé (pas les autres applications).
                nint root = GetAncestor(window, GaRoot);
                bool ours = false;
                foreach (Win32Dock dock in Docks.Values)
                    ours |= GetAncestor(dock.Host, GaRoot) == root;
                if (!ours)
                    return;
                // Geste de l'utilisateur au moment du changement : bouton de la souris enfoncé, et où.
                bool pressed = IsDown(VkLButton) || IsDown(VkRButton) || IsDown(VkMButton);
                GetCursorPos(out Point cursor);
                Dispatcher.UIThread.Post(() => OnFocusChanged(window, pressed, cursor));
            }
            catch (Exception ex)
            {
                Report("focus", ex);
            }
        }

        /// <summary>
        /// Le clavier a changé de fenêtre. Dans une fenêtre logée : elle le garde désormais (et sa
        /// vue prend le focus d'Avalonia). Ailleurs dans la fenêtre de PommeBrowser, alors qu'une
        /// fenêtre logée le gardait : s'il n'y a pas eu de clic hors d'elle (la page web le reprend
        /// d'elle-même, une autre fenêtre se l'attribue…), il lui est rendu.
        /// </summary>
        static void OnFocusChanged(nint window, bool pressed, Point cursor)
        {
            try
            {
                // État actuel (des changements ont pu se suivre) ; rien si le clavier a quitté PommeBrowser.
                nint focus = GetFocus();
                if (focus == 0)
                {
                    // Le clavier est dans une autre application : au retour, tout changement compte.
                    _lastFocus = 0;
                    return;
                }
                if (focus == _lastFocus)
                    return;
                _lastFocus = focus;
                nint root = GetAncestor(focus, GaRoot);

                Win32Dock? keeper = _keeper;
                if (keeper != null && !keeper.CanKeep())
                    keeper = _keeper = null;
                bool keeperHere = keeper != null && GetAncestor(keeper.Host, GaRoot) == root;
                // Clic hors du lecteur qui avait le clavier : l'utilisateur va ailleurs.
                bool userMove = keeper == null || pressed && !keeper.IsUnder(cursor);

                if (DockOf(focus) is { } owner)
                {
                    if (keeperHere && owner != keeper && !userMove && owner != _requested)
                    {
                        GiveBack(keeper!, owner.Name);
                        return;
                    }
                    if (owner != keeper)
                        _givebacks = 0;
                    _keeper = owner;
                    _requested = null;
                    NoteKeyboard(owner.Name + " (" + ProcessOf(focus) + ")");
                    owner.Clicked?.Invoke();
                    return;
                }

                if (!keeperHere)
                {
                    // Autre fenêtre de PommeBrowser (moteur web…) alors qu'un lecteur y est logé : noté.
                    if (Docks.Values.Any(dock => GetAncestor(dock.Host, GaRoot) == root))
                        NoteKeyboard(Describe(focus));
                    return;
                }
                if (userMove)
                {
                    _keeper = null;
                    NoteKeyboard(Describe(focus) + " (clic hors de " + keeper!.Name + ")");
                    return;
                }
                GiveBack(keeper!, Describe(focus));
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or ObjectDisposedException)
            {
                PommeBrowser.Core.ErrorLog.Write("Clavier de la fenêtre logée", ex);
            }
        }

        /// <summary>
        /// Clavier pris à la fenêtre logée sans geste de l'utilisateur : il lui est rendu. Une
        /// fenêtre qui le reprend sans cesse finit par le garder (pas de lutte sans fin) ; un
        /// nouveau clic dans le lecteur le lui redonne.
        /// </summary>
        static void GiveBack(Win32Dock keeper, string thief)
        {
            long now = Environment.TickCount64;
            if (now - _givebackStart > GivebackPeriod)
            {
                _givebackStart = now;
                _givebacks = 0;
            }
            if (++_givebacks > MaxGivebacks)
            {
                _keeper = null;
                LogKeyboard($"{thief} reprend sans cesse le clavier à {keeper.Name} : il le garde (cliquer dans le jeu pour le lui redonner).");
                return;
            }
            if (GetForegroundWindow() != GetAncestor(keeper.Host, GaRoot))
                return;
            LogKeyboard($"Pris par {thief} sans clic de l'utilisateur : rendu à {keeper.Name}.");
            _keyboardOwner = null;
            keeper.FocusClient();
        }

        [UnmanagedCallersOnly]
        static nint OnKeyboard(int code, nint wParam, nint lParam)
        {
            try
            {
                if (TakeShortcut(code, wParam, lParam))
                    return 1;
            }
            catch (Exception ex)
            {
                Report("clavier", ex);
            }
            return CallNextHookEx(0, code, wParam, lParam);
        }

        /// <summary>Raccourci de la fenêtre tapé dans une fenêtre logée : transmis à PommeBrowser, et gardé.</summary>
        static bool TakeShortcut(int code, nint wParam, nint lParam)
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
                        foreach (Win32Dock dock in Docks.Values.ToArray())
                        {
                            if (dock.Contains(focus) && GetAncestor(dock.Host, GaRoot) == foreground)
                            {
                                Action<Key, KeyModifiers>? shortcut = dock.Shortcut;
                                Dispatcher.UIThread.Post(() => shortcut?.Invoke(key, modifiers));
                                return true;
                            }
                        }
                    }
                }
            }
            return false;
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
            try
            {
                ((List<nint>)GCHandle.FromIntPtr(state).Target!).Add(window);
                return 1;
            }
            catch (Exception ex)
            {
                Report("fenêtres enfants", ex);
                return 0;
            }
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
        const int VkLButton = 0x01;
        const int VkRButton = 0x02;
        const int VkMButton = 0x04;

        [StructLayout(LayoutKind.Sequential)]
        struct Rect
        {
            public int Left, Top, Right, Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct Point
        {
            public int X, Y;
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
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool GetCursorPos(out Point point);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(nint window, char* name, int length);
        [DllImport("user32.dll")] static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback, uint processId, uint threadId, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool UnhookWinEvent(nint hook);
        [DllImport("user32.dll", SetLastError = true)] static extern nint SetWindowsHookExW(int hookId, delegate* unmanaged<int, nint, nint, nint> callback, nint module, uint threadId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool UnhookWindowsHookEx(nint hook);
        [DllImport("user32.dll")] static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);
    }
}
