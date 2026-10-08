using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PommeBrowser.Legacy
{
    /// <summary>
    /// Fenêtre d'un autre programme logée dans une fenêtre de PommeBrowser (Windows) : styles de
    /// fenêtre enfant, et liaison des files de saisie. Windows relie de lui-même la saisie d'une
    /// fenêtre créée enfant d'une fenêtre d'un autre fil (la page web de WebView2), mais pas celle
    /// d'une fenêtre qui le devient ensuite par SetParent (lecteur Flash, Basilisk). Sans liaison,
    /// un clic dans le lecteur réactive la fenêtre de PommeBrowser et les touches vont à la page :
    /// le lecteur ne les reçoit jamais.
    /// </summary>
    [SupportedOSPlatform("windows")]
    static class Win32Embedding
    {
        /// <summary>La fenêtre devient enfant de <paramref name="parent"/>, sans cadre ni boutons ni bouton de barre des tâches.</summary>
        public static void MakeChild(nint client, nint parent)
        {
            SetParent(client, parent);
            int style = GetWindowLongW(client, GwlStyle);
            style = (style & ~(WsPopup | WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox)) | WsChild | WsVisible;
            SetWindowLongW(client, GwlStyle, style);
            int exStyle = GetWindowLongW(client, GwlExStyle);
            SetWindowLongW(client, GwlExStyle, (exStyle & ~WsExAppWindow) | WsExToolWindow);
            SetWindowPos(client, 0, 0, 0, 0, 0, SwpFrameChanged | SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate);
        }

        /// <summary>La fenêtre redevient une fenêtre à part, cachée.</summary>
        public static void MakeTopLevel(nint client)
        {
            ShowWindow(client, SwHide);
            SetParent(client, 0);
            int style = GetWindowLongW(client, GwlStyle);
            SetWindowLongW(client, GwlStyle, (style & ~WsChild) | WsPopup | WsCaption | WsThickFrame | WsSysMenu | WsMinimizeBox | WsMaximizeBox);
            int exStyle = GetWindowLongW(client, GwlExStyle);
            SetWindowLongW(client, GwlExStyle, (exStyle & ~WsExToolWindow) | WsExAppWindow);
        }

        /// <summary>
        /// Saisie du fil appelant (celui de l'interface de PommeBrowser) reliée à celle du fil de la
        /// fenêtre : un seul focus pour les deux, le clavier va au lecteur quand il l'a. Rend le fil
        /// relié (à délier avec <see cref="UnlinkInput"/>), ou 0 (même fil, ou refus de Windows :
        /// <paramref name="error"/>).
        /// </summary>
        public static uint LinkInput(nint client, out int error)
        {
            error = 0;
            uint current = GetCurrentThreadId();
            uint thread = GetWindowThreadProcessId(client, out _);
            if (thread == 0 || thread == current)
                return 0;
            if (AttachThreadInput(current, thread, true))
                return thread;
            error = Marshal.GetLastPInvokeError();
            return 0;
        }

        /// <summary>Liaison défaite (fenêtre retirée). Un fil terminé entre-temps n'est plus relié : rien à faire.</summary>
        public static void UnlinkInput(uint thread)
        {
            if (thread != 0)
                AttachThreadInput(GetCurrentThreadId(), thread, false);
        }

        const int GwlStyle = -16;
        const int GwlExStyle = -20;
        const int WsChild = 0x40000000;
        const int WsVisible = 0x10000000;
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
        const int SwHide = 0;

        [DllImport("user32.dll")] static extern nint SetParent(nint child, nint parent);
        [DllImport("user32.dll")] static extern int GetWindowLongW(nint window, int index);
        [DllImport("user32.dll")] static extern int SetWindowLongW(nint window, int index, int value);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool ShowWindow(nint window, int command);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(nint window, out uint processId);
        [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);
        [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    }
}
