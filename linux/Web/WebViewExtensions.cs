using System;
using System.Runtime.InteropServices;

namespace PommeBrowser.Linux.Web
{
    /// <summary>
    /// Lectures qui peuvent renvoyer NULL (page sans titre, vue encore vide…) : les liaisons
    /// Gir.Core les déclarent non nulles et lèvent une exception. Appel direct de WebKitGTK.
    /// </summary>
    static class WebViewExtensions
    {
        [DllImport(Native.WebKitLibrary)]
        static extern IntPtr webkit_web_view_get_title(IntPtr webView);

        [DllImport(Native.WebKitLibrary)]
        static extern IntPtr webkit_web_view_get_uri(IntPtr webView);

        [DllImport(Native.WebKitLibrary)]
        static extern IntPtr webkit_hit_test_result_get_link_uri(IntPtr result);

        [DllImport(Native.WebKitLibrary)]
        static extern IntPtr webkit_uri_request_get_uri(IntPtr request);

        [DllImport(Native.WebKitLibrary)]
        static extern IntPtr webkit_uri_for_display([MarshalAs(UnmanagedType.LPUTF8Str)] string uri);

        [DllImport(Native.GLibLibrary)]
        static extern void g_free(IntPtr memory);

        public static string? Title(this WebKit.WebView view)
            => Marshal.PtrToStringUTF8(webkit_web_view_get_title(Native.Pointer(view)));

        public static string? Url(this WebKit.WebView view)
            => Marshal.PtrToStringUTF8(webkit_web_view_get_uri(Native.Pointer(view)));

        public static string? LinkUrl(this WebKit.HitTestResult result)
            => result.ContextIsLink() ? Marshal.PtrToStringUTF8(webkit_hit_test_result_get_link_uri(Native.Pointer(result))) : null;

        public static string? Url(this WebKit.URIRequest request)
            => Marshal.PtrToStringUTF8(webkit_uri_request_get_uri(Native.Pointer(request)));

        /// <summary>Adresse lisible (domaines internationalisés décodés), ou l'adresse telle quelle.</summary>
        public static string ForDisplay(string uri)
        {
            IntPtr display = webkit_uri_for_display(uri);
            if (display == IntPtr.Zero)
                return uri;
            try
            {
                return Marshal.PtrToStringUTF8(display) ?? uri;
            }
            finally
            {
                g_free(display);
            }
        }
    }
}
