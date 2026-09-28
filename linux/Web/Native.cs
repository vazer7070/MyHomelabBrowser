using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace PommeBrowser.Linux.Web
{
    /// <summary>
    /// Fonctions de WebKitGTK et GLib appelées directement : les liaisons Gir.Core ne
    /// fournissent pas encore les versions asynchrones (…_save, …_load, …_clear).
    /// </summary>
    static class Native
    {
        public const string WebKitLibrary = "libwebkitgtk-6.0.so.4";
        public const string GLibLibrary = "libglib-2.0.so.0";
        public const string GtkLibrary = "libgtk-4.so.1";
        public const string AdwaitaLibrary = "libadwaita-1.so.0";
        public const string JavaScriptCoreLibrary = "libjavascriptcoregtk-6.0.so.1";
        public const string GObjectLibrary = "libgobject-2.0.so.0";

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        public delegate void AsyncReadyCallback(IntPtr source, IntPtr result, IntPtr userData);

        [DllImport(WebKitLibrary)]
        public static extern void webkit_user_content_filter_store_save(IntPtr store, [MarshalAs(UnmanagedType.LPUTF8Str)] string identifier, IntPtr source, IntPtr cancellable, AsyncReadyCallback callback, IntPtr userData);

        [DllImport(WebKitLibrary)]
        public static extern IntPtr webkit_user_content_filter_store_save_finish(IntPtr store, IntPtr result, out IntPtr error);

        [DllImport(WebKitLibrary)]
        public static extern void webkit_user_content_filter_store_load(IntPtr store, [MarshalAs(UnmanagedType.LPUTF8Str)] string identifier, IntPtr cancellable, AsyncReadyCallback callback, IntPtr userData);

        [DllImport(WebKitLibrary)]
        public static extern IntPtr webkit_user_content_filter_store_load_finish(IntPtr store, IntPtr result, out IntPtr error);

        [DllImport(WebKitLibrary)]
        public static extern void webkit_user_content_filter_store_remove(IntPtr store, [MarshalAs(UnmanagedType.LPUTF8Str)] string identifier, IntPtr cancellable, AsyncReadyCallback callback, IntPtr userData);

        [DllImport(WebKitLibrary)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool webkit_user_content_filter_store_remove_finish(IntPtr store, IntPtr result, out IntPtr error);

        [DllImport(WebKitLibrary)]
        public static extern void webkit_user_content_filter_unref(IntPtr filter);

        [DllImport(WebKitLibrary)]
        public static extern void webkit_user_content_manager_add_filter(IntPtr manager, IntPtr filter);

        [DllImport(WebKitLibrary)]
        public static extern void webkit_website_data_manager_clear(IntPtr manager, int types, long timeSpan, IntPtr cancellable, AsyncReadyCallback callback, IntPtr userData);

        [DllImport(WebKitLibrary)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool webkit_website_data_manager_clear_finish(IntPtr manager, IntPtr result, out IntPtr error);

        [DllImport(WebKitLibrary)]
        public static extern void webkit_web_view_evaluate_javascript(IntPtr webView, [MarshalAs(UnmanagedType.LPUTF8Str)] string script, IntPtr length, [MarshalAs(UnmanagedType.LPUTF8Str)] string? worldName, [MarshalAs(UnmanagedType.LPUTF8Str)] string? sourceUri, IntPtr cancellable, AsyncReadyCallback callback, IntPtr userData);

        [DllImport(WebKitLibrary)]
        public static extern IntPtr webkit_web_view_evaluate_javascript_finish(IntPtr webView, IntPtr result, out IntPtr error);

        [DllImport(JavaScriptCoreLibrary)]
        public static extern IntPtr jsc_value_to_string(IntPtr value);

        [DllImport(JavaScriptCoreLibrary)]
        [return: MarshalAs(UnmanagedType.I1)]
        public static extern bool jsc_value_is_string(IntPtr value);

        [DllImport(GObjectLibrary)]
        public static extern void g_object_unref(IntPtr instance);

        [DllImport(GLibLibrary)]
        public static extern void g_free(IntPtr memory);

        [DllImport(GLibLibrary)]
        public static extern IntPtr g_bytes_new(byte[] data, UIntPtr size);

        [DllImport(GLibLibrary)]
        public static extern void g_bytes_unref(IntPtr bytes);

        [DllImport(GLibLibrary)]
        public static extern void g_error_free(IntPtr error);

        /// <summary>Tous les types de données des sites (WEBKIT_WEBSITE_DATA_ALL).</summary>
        public const int WebsiteDataAll = (1 << 14) - 1;

        // Délégués en attente de leur rappel : ils ne doivent pas être collectés avant.
        static readonly HashSet<AsyncReadyCallback> Pending = new();

        /// <summary>
        /// Lance une opération GIO asynchrone et renvoie son résultat. Le rappel arrive sur la
        /// boucle principale de GLib, donc sur le fil de l'interface.
        /// </summary>
        public static Task<T> RunAsync<T>(Action<AsyncReadyCallback> begin, Func<IntPtr, T> finish)
        {
            var completion = new TaskCompletionSource<T>();
            AsyncReadyCallback? callback = null;
            callback = (_, result, _) =>
            {
                Pending.Remove(callback!);
                try
                {
                    completion.SetResult(finish(result));
                }
                catch (Exception ex)
                {
                    completion.SetException(ex);
                }
            };

            Pending.Add(callback);
            begin(callback);
            return completion.Task;
        }

        /// <summary>Transforme une GError en exception (et la libère).</summary>
        public static void ThrowIfError(IntPtr error)
        {
            if (error == IntPtr.Zero)
                return;

            // struct GError { GQuark domain; gint code; gchar *message; }
            string message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "?";
            g_error_free(error);
            throw new InvalidOperationException(message);
        }

        /// <summary>
        /// Exécute un script dans la page, dans un monde isolé si <paramref name="world"/> est donné
        /// (la page ne voit ni ses variables ni ses fonctions). Renvoie le résultat s'il s'agit d'un texte.
        /// </summary>
        public static Task<string?> EvaluateAsync(WebKit.WebView webView, string script, string? world)
        {
            IntPtr view = Pointer(webView);
            return RunAsync(
                callback => webkit_web_view_evaluate_javascript(view, script, -1, world, null, IntPtr.Zero, callback, IntPtr.Zero),
                result =>
                {
                    IntPtr value = webkit_web_view_evaluate_javascript_finish(view, result, out IntPtr error);
                    ThrowIfError(error);
                    if (value == IntPtr.Zero)
                        return null;
                    try
                    {
                        if (!jsc_value_is_string(value))
                            return null;
                        IntPtr text = jsc_value_to_string(value);
                        try
                        {
                            return Marshal.PtrToStringUTF8(text);
                        }
                        finally
                        {
                            g_free(text);
                        }
                    }
                    finally
                    {
                        g_object_unref(value);
                    }
                });
        }

        public static IntPtr NewBytes(byte[] data) => g_bytes_new(data, (UIntPtr)data.Length);

        public static IntPtr Pointer(GObject.Object value) => value.Handle.DangerousGetHandle();
    }
}
