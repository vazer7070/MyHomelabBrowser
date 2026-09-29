using System;
using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Platform;

namespace PommeBrowser.Engine.Gtk
{
    /// <summary>
    /// Sous X11, la vue WebKitGTK est une fenêtre enfant qui garde le clavier après un clic dans la
    /// page. Pour saisir dans la barre d'adresse (Ctrl+L, Ctrl+F…), le focus clavier est rendu à la
    /// fenêtre Avalonia.
    /// </summary>
    static class X11Focus
    {
        const string X11 = "libX11.so.6";
        const int RevertToParent = 2;

        [DllImport(X11)] static extern nint XOpenDisplay(nint name);
        [DllImport(X11)] static extern int XSetInputFocus(nint display, nint window, int revertTo, nint time);
        [DllImport(X11)] static extern int XFlush(nint display);

        static nint _display;

        public static void TakeFocus(TopLevel topLevel)
        {
            if (topLevel.TryGetPlatformHandle() is not { HandleDescriptor: "XID" } handle)
                return;
            try
            {
                if (_display == 0)
                    _display = XOpenDisplay(0);
                if (_display == 0)
                    return;
                XSetInputFocus(_display, handle.Handle, RevertToParent, 0);
                XFlush(_display);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // Pas de X11 (Wayland pur) : rien à faire.
            }
        }
    }
}
