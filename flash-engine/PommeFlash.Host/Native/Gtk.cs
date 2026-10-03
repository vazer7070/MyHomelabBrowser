using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace PommeFlash.Host.Native
{
    /// <summary>
    /// GTK 2, GLib et Xlib, utilisés par l'hôte sous Linux : le module Flash de Linux est écrit
    /// pour GTK 2 (il loge sa fenêtre dans celle du navigateur par XEmbed). Les bibliothèques sont
    /// celles du système ; le module charge les mêmes.
    /// </summary>
    [SupportedOSPlatform("linux")]
    static unsafe partial class Gtk
    {
        const string LibGtk = "libgtk-x11-2.0.so.0";
        const string LibGdk = "libgdk-x11-2.0.so.0";
        const string LibGObject = "libgobject-2.0.so.0";
        const string LibGLib = "libglib-2.0.so.0";
        const string LibX11 = "libX11.so.6";
        const string LibC = "libc.so.6";

        public const int WindowToplevel = 0;
        public const int StateNormal = 0;
        public const int PriorityDefault = 0;
        public const int LcNumeric = 1;
        public const int PrSetPdeathsig = 1;
        public const int SigKill = 9;

        [StructLayout(LayoutKind.Sequential)]
        public struct GdkColor
        {
            public uint pixel;
            public ushort red;
            public ushort green;
            public ushort blue;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GtkAllocation
        {
            public int x;
            public int y;
            public int width;
            public int height;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct XErrorEvent
        {
            public int type;
            public nint display;
            public nuint resourceid;
            public nuint serial;
            public byte error_code;
            public byte request_code;
            public byte minor_code;
        }

        // --- Xlib ---

        [LibraryImport(LibX11)]
        public static partial int XInitThreads();

        [LibraryImport(LibX11)]
        public static partial int XDefaultScreen(nint display);

        [LibraryImport(LibX11)]
        public static partial nint XDefaultVisual(nint display, int screen);

        [LibraryImport(LibX11)]
        public static partial nuint XDefaultColormap(nint display, int screen);

        [LibraryImport(LibX11)]
        public static partial int XDefaultDepth(nint display, int screen);

        [LibraryImport(LibX11)]
        public static partial nint XSetErrorHandler(nint handler);

        [LibraryImport(LibX11)]
        public static partial int XGetErrorText(nint display, int code, byte* buffer, int length);

        // --- GTK 2 et GDK ---

        [LibraryImport(LibGtk)]
        public static partial int gtk_init_check(int* argc, nint* argv);

        [LibraryImport(LibGtk)]
        public static partial nint gtk_window_new(int type);

        [LibraryImport(LibGtk, StringMarshalling = StringMarshalling.Utf8)]
        public static partial void gtk_window_set_title(nint window, string title);

        [LibraryImport(LibGtk)]
        public static partial void gtk_window_set_default_size(nint window, int width, int height);

        [LibraryImport(LibGtk)]
        public static partial nint gtk_socket_new();

        [LibraryImport(LibGtk)]
        public static partial uint gtk_socket_get_id(nint socket);

        [LibraryImport(LibGtk)]
        public static partial void gtk_container_add(nint container, nint widget);

        [LibraryImport(LibGtk)]
        public static partial void gtk_widget_show(nint widget);

        [LibraryImport(LibGtk)]
        public static partial void gtk_widget_realize(nint widget);

        [LibraryImport(LibGtk)]
        public static partial void gtk_widget_size_allocate(nint widget, GtkAllocation* allocation);

        [LibraryImport(LibGtk)]
        public static partial void gtk_widget_get_allocation(nint widget, GtkAllocation* allocation);

        [LibraryImport(LibGtk)]
        public static partial void gtk_widget_destroy(nint widget);

        [LibraryImport(LibGtk)]
        public static partial nint gtk_widget_get_window(nint widget);

        [LibraryImport(LibGtk)]
        public static partial void gtk_widget_modify_bg(nint widget, int state, GdkColor* color);

        [LibraryImport(LibGtk)]
        public static partial void gtk_main();

        [LibraryImport(LibGtk)]
        public static partial void gtk_main_quit();

        [LibraryImport(LibGdk)]
        public static partial nuint gdk_x11_drawable_get_xid(nint drawable);

        [LibraryImport(LibGdk)]
        public static partial nint gdk_x11_get_default_xdisplay();

        [LibraryImport(LibGdk)]
        public static partial void gdk_window_resize(nint window, int width, int height);

        // --- GObject et GLib ---

        [LibraryImport(LibGObject, StringMarshalling = StringMarshalling.Utf8)]
        public static partial nuint g_signal_connect_data(nint instance, string signal, nint handler, nint data, nint destroyData, int flags);

        [LibraryImport(LibGLib)]
        public static partial uint g_idle_add_full(int priority, nint function, nint data, nint notify);

        [LibraryImport(LibGLib)]
        public static partial uint g_timeout_add(uint interval, nint function, nint data);

        [LibraryImport(LibGLib)]
        [return: MarshalAs(UnmanagedType.I4)]
        public static partial bool g_source_remove(uint id);

        // --- libc ---

        [LibraryImport(LibC, StringMarshalling = StringMarshalling.Utf8)]
        public static partial nint setlocale(int category, string locale);

        [LibraryImport(LibC)]
        public static partial int prctl(int option, nuint arg2, nuint arg3, nuint arg4, nuint arg5);
    }
}
