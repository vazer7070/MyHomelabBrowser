using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Input;

namespace PommeBrowser.Engine.WebView2
{
    /// <summary>Touches virtuelles de Windows (raccourcis tapés dans la page) et focus des fenêtres.</summary>
    [SupportedOSPlatform("windows")]
    static class VirtualKeys
    {
        public const int Shift = 0x10;
        public const int Control = 0x11;
        public const int Menu = 0x12;
        public const int MiddleButton = 0x04;

        [DllImport("user32.dll")] static extern short GetKeyState(int virtualKey);
        [DllImport("user32.dll")] static extern nint GetFocus();
        [DllImport("user32.dll")] static extern nint SetFocus(nint window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool IsChild(nint parent, nint window);

        public static bool IsDown(int virtualKey) => (GetKeyState(virtualKey) & 0x8000) != 0;

        public static KeyModifiers Modifiers()
        {
            KeyModifiers modifiers = KeyModifiers.None;
            if (IsDown(Control))
                modifiers |= KeyModifiers.Control;
            if (IsDown(Shift))
                modifiers |= KeyModifiers.Shift;
            if (IsDown(Menu))
                modifiers |= KeyModifiers.Alt;
            return modifiers;
        }

        /// <summary>Touches utilisées par les raccourcis du navigateur (voir BrowserShortcuts).</summary>
        public static Key ToKey(uint virtualKey) => virtualKey switch
        {
            >= 0x41 and <= 0x5A => Key.A + (int)(virtualKey - 0x41),
            >= 0x30 and <= 0x39 => Key.D0 + (int)(virtualKey - 0x30),
            >= 0x60 and <= 0x69 => Key.NumPad0 + (int)(virtualKey - 0x60),
            >= 0x70 and <= 0x7B => Key.F1 + (int)(virtualKey - 0x70),
            0x09 => Key.Tab,
            0x21 => Key.PageUp,
            0x22 => Key.PageDown,
            0x24 => Key.Home,
            0x25 => Key.Left,
            0x27 => Key.Right,
            0x2E => Key.Delete,
            0x6B => Key.Add,
            0x6D => Key.Subtract,
            0xBB => Key.OemPlus,
            0xBC => Key.OemComma,
            0xBD => Key.OemMinus,
            0xA6 => Key.BrowserBack,
            0xA7 => Key.BrowserForward,
            0xA8 => Key.BrowserRefresh,
            _ => Key.None
        };

        /// <summary>
        /// Rend le clavier à la fenêtre <paramref name="window"/> quand la vue <paramref name="view"/>
        /// (fenêtre de WebView2 dans l'onglet) ou une de ses fenêtres enfants l'a gardé.
        /// </summary>
        public static void TakeFocusFromChild(nint window, nint view)
        {
            nint focus = GetFocus();
            if (focus != 0 && (focus == view || IsChild(view, focus)))
                SetFocus(window);
        }
    }
}
