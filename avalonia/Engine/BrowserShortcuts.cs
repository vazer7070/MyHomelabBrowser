using System;
using Avalonia.Input;

namespace PommeBrowser.Engine
{
    /// <summary>
    /// Raccourcis du navigateur. Quand la page a le focus, les touches vont directement au moteur :
    /// celui-ci les intercepte (sans les donner à la page) si elles figurent ici, et les renvoie à
    /// la fenêtre. Lu depuis le fil du moteur : table fixe, sans état.
    /// </summary>
    public static class BrowserShortcuts
    {
        public static bool IsShortcut(Key key, KeyModifiers modifiers)
        {
            bool ctrl = modifiers.HasFlag(KeyModifiers.Control);
            bool shift = modifiers.HasFlag(KeyModifiers.Shift);
            bool alt = modifiers.HasFlag(KeyModifiers.Alt);

            if (ctrl && !alt)
            {
                return key switch
                {
                    Key.T or Key.N or Key.W or Key.F4 or Key.Tab or Key.PageDown or Key.PageUp => true,
                    >= Key.D1 and <= Key.D9 => !shift,
                    Key.L or Key.R or Key.F5 or Key.H or Key.J or Key.D or Key.OemComma or Key.F or Key.G or Key.P => true,
                    Key.Delete => shift,
                    Key.I => shift,
                    Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract or Key.D0 or Key.NumPad0 => !shift || key == Key.OemPlus,
                    _ => false
                };
            }

            if (alt && !ctrl)
                return key is Key.Left or Key.Right or Key.Home or Key.D;

            return !ctrl && !alt && key is Key.F3 or Key.F5 or Key.F6 or Key.F11 or Key.F12 or Key.BrowserBack or Key.BrowserForward or Key.BrowserRefresh;
        }
    }
}
