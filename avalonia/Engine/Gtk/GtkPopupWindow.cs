using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using static PommeBrowser.Engine.Gtk.WebKitGtk;

namespace PommeBrowser.Engine.Gtk
{
    /// <summary>
    /// Fenêtre ouverte par une page avec window.open (connexion OAuth, partage…) : fenêtre GTK
    /// séparée, dont la vue est liée à celle de la page (même session, window.opener conservé).
    /// Elle se ferme d'elle-même quand la page le demande (window.close). Fil GLib uniquement.
    /// </summary>
    sealed unsafe class GtkPopupWindow
    {
        const int GtkWindowToplevel = 0;
        const int DefaultWidth = 1000;
        const int DefaultHeight = 720;

        static readonly nint ReadyToShowCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnReadyToShow;
        static readonly nint CloseCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnClose;
        static readonly nint TitleCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnTitle;
        static readonly nint DestroyCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnDestroy;

        /// <summary>Fenêtres ouvertes (retenues jusqu'à leur destruction).</summary>
        static readonly HashSet<GtkPopupWindow> Open = new();

        readonly List<GSignal> _signals = new();
        readonly nint _view;
        nint _window;

        GtkPopupWindow(nint view)
        {
            _view = view;
        }

        /// <summary>Crée la vue liée à <paramref name="opener"/> ; la fenêtre apparaît quand la page est prête.</summary>
        public static nint Create(nint opener)
        {
            nint view = webkit_web_view_new_with_related_view(opener);
            if (view == 0)
                return 0;

            var popup = new GtkPopupWindow(view);
            Open.Add(popup);
            GtkEngine.ConfigureView(view);
            popup._signals.Add(new GSignal(view, "ready-to-show", ReadyToShowCallback, popup));
            popup._signals.Add(new GSignal(view, "close", CloseCallback, popup));
            popup._signals.Add(new GSignal(view, "notify::title", TitleCallback, popup));
            return view;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnReadyToShow(nint view, nint data)
        {
            if (GSignal.State<GtkPopupWindow>(data) is not { } popup || popup._window != 0)
                return;

            GdkRectangle geometry;
            webkit_window_properties_get_geometry(webkit_web_view_get_window_properties(view), &geometry);

            nint window = gtk_window_new(GtkWindowToplevel);
            popup._window = window;
            // Taille demandée par la page (window.open(…, "width=…,height=…")), sinon une taille confortable.
            gtk_window_set_default_size(window,
                geometry.Width >= 200 ? Math.Min(geometry.Width, 1400) : DefaultWidth,
                geometry.Height >= 150 ? Math.Min(geometry.Height, 1000) : DefaultHeight);
            gtk_window_set_title(window, String(webkit_web_view_get_title(view)) ?? "PommeBrowser");

            string icon = Path.Combine(AppContext.BaseDirectory, "icons", "pommebrowser.png");
            if (File.Exists(icon))
                gtk_window_set_icon_from_file(window, icon, 0);

            popup._signals.Add(new GSignal(window, "destroy", DestroyCallback, popup));
            gtk_container_add(window, view);
            gtk_widget_show_all(window);
            gtk_window_present(window);
            gtk_widget_grab_focus(view);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnTitle(nint view, nint pspec, nint data)
        {
            if (GSignal.State<GtkPopupWindow>(data) is { _window: not 0 } popup)
                gtk_window_set_title(popup._window, String(webkit_web_view_get_title(view)) ?? "PommeBrowser");
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnClose(nint view, nint data)
        {
            if (GSignal.State<GtkPopupWindow>(data) is not { } popup)
                return;

            if (popup._window != 0)
                gtk_widget_destroy(popup._window);
            else
                popup.Release();
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnDestroy(nint window, nint data)
            => GSignal.State<GtkPopupWindow>(data)?.Release();

        void Release()
        {
            if (!Open.Remove(this))
                return;
            foreach (GSignal signal in _signals)
                signal.Dispose();
            _signals.Clear();
            _window = 0;
        }
    }
}
