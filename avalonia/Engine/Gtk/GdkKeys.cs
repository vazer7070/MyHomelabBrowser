using Avalonia.Input;

namespace PommeBrowser.Engine.Gtk
{
    /// <summary>Touches GDK (keyval, état des modificateurs) en touches Avalonia, pour les raccourcis du navigateur.</summary>
    static class GdkKeys
    {
        const uint ShiftMask = 1;
        const uint ControlMask = 4;
        const uint AltMask = 8;

        public static KeyModifiers Modifiers(uint state)
            => ((state & ShiftMask) != 0 ? KeyModifiers.Shift : 0) |
               ((state & ControlMask) != 0 ? KeyModifiers.Control : 0) |
               ((state & AltMask) != 0 ? KeyModifiers.Alt : 0);

        public static Key ToKey(uint keyval) => keyval switch
        {
            >= 0x61 and <= 0x7a => Key.A + (int)(keyval - 0x61),
            >= 0x41 and <= 0x5a => Key.A + (int)(keyval - 0x41),
            >= 0x30 and <= 0x39 => Key.D0 + (int)(keyval - 0x30),
            // Chiffres de la rangée du haut sur un clavier AZERTY (& é " ' ( - è _ ç à).
            0x26 => Key.D1,
            0xe9 => Key.D2,
            0x22 => Key.D3,
            0x27 => Key.D4,
            0x28 => Key.D5,
            0xe8 => Key.D7,
            0x5f => Key.D8,
            0xe7 => Key.D9,
            0xe0 => Key.D0,
            >= 0xffbe and <= 0xffc9 => Key.F1 + (int)(keyval - 0xffbe),
            0xff09 or 0xfe20 => Key.Tab,
            0xff1b => Key.Escape,
            0xff50 => Key.Home,
            0xff51 => Key.Left,
            0xff52 => Key.Up,
            0xff53 => Key.Right,
            0xff54 => Key.Down,
            0xff55 => Key.PageUp,
            0xff56 => Key.PageDown,
            0xffff => Key.Delete,
            0x2b or 0x3d => Key.OemPlus,
            0x2d => Key.OemMinus,
            0x2c => Key.OemComma,
            0xffab => Key.Add,
            0xffad => Key.Subtract,
            0xffb0 => Key.NumPad0,
            0x1008ff26 => Key.BrowserBack,
            0x1008ff27 => Key.BrowserForward,
            0x1008ff29 => Key.BrowserRefresh,
            _ => Key.None
        };
    }
}
