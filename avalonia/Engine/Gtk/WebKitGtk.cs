using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace PommeBrowser.Engine.Gtk
{
    /// <summary>
    /// Appels directs à WebKitGTK 4.1 (API GTK 3, celle qu'utilise Avalonia.Controls.WebView)
    /// et à GLib. Tout appel doit avoir lieu sur le fil GLib d'Avalonia (voir <see cref="Glib.Run"/>).
    /// </summary>
    static unsafe class WebKitGtk
    {
        const string WebKit = "libwebkit2gtk-4.1.so.0";
        const string JavaScriptCore = "libjavascriptcoregtk-4.1.so.0";
        const string GObject = "libgobject-2.0.so.0";
        const string GLibLib = "libglib-2.0.so.0";
        const string Gio = "libgio-2.0.so.0";
        const string Soup = "libsoup-3.0.so.0";
        const string Cairo = "libcairo.so.2";
        const string Gtk3 = "libgtk-3.so.0";
        const string Gdk3 = "libgdk-3.so.0";
        const string X11 = "libX11.so.6";

        // ---------------------------------------------------------------
        // GLib / GObject
        // ---------------------------------------------------------------

        [DllImport(GLibLib)] public static extern void g_free(nint memory);
        [DllImport(GLibLib)] public static extern void g_error_free(nint error);
        [DllImport(GLibLib)] public static extern void g_list_free(nint list);
        [DllImport(GLibLib)] public static extern nint g_date_time_new_from_unix_utc(long seconds);
        [DllImport(GLibLib)] public static extern void g_date_time_unref(nint dateTime);
        [DllImport(GLibLib)] public static extern nint g_bytes_new(byte* data, nuint size);
        [DllImport(GLibLib)] public static extern void g_bytes_unref(nint bytes);
        [DllImport(GLibLib)] public static extern void g_set_prgname([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        [DllImport(GObject)] public static extern nint g_object_ref(nint instance);
        [DllImport(GObject)] public static extern void g_object_unref(nint instance);
        [DllImport(GObject)] public static extern ulong g_signal_connect_data(nint instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string signal, nint handler, nint data, nint destroy, int flags);
        [DllImport(GObject)] public static extern void g_signal_handler_disconnect(nint instance, ulong handlerId);
        [DllImport(GObject)] public static extern uint g_signal_lookup([MarshalAs(UnmanagedType.LPUTF8Str)] string name, nuint type);
        [DllImport(GObject)] public static extern uint g_signal_handlers_disconnect_matched(nint instance, int mask, uint signalId, uint detail, nint closure, nint func, nint data);
        [DllImport(GObject)] [return: MarshalAs(UnmanagedType.I4)] public static extern int g_type_check_instance_is_a(nint instance, nuint type);
        [DllImport(GObject)] public static extern void g_value_init(GValue* value, nuint type);
        [DllImport(GObject)] public static extern void g_value_unset(GValue* value);
        [DllImport(GObject)] public static extern nint g_value_get_string(GValue* value);
        [DllImport(GObject)] public static extern void g_object_get_property(nint instance, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, GValue* value);
        [DllImport(GObject)] public static extern nint g_param_spec_get_name(nint pspec);
        [DllImport(GObject)] public static extern void g_object_weak_ref(nint instance, nint notify, nint data);

        [DllImport(Gio)] public static extern nint g_memory_input_stream_new_from_bytes(nint bytes);
        [DllImport(Gio)] public static extern uint g_io_error_quark();

        public const int GIoErrorNotFound = 1;
        public const int GIoErrorCancelled = 19;

        public const nuint GTypeString = 16 << 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct GValue
        {
            public nuint Type;
            public long Data0;
            public long Data1;
        }

        // ---------------------------------------------------------------
        // Vue web
        // ---------------------------------------------------------------

        [DllImport(WebKit)] public static extern nuint webkit_web_view_get_type();
        [DllImport(WebKit)] public static extern nint webkit_web_view_get_title(nint view);
        [DllImport(WebKit)] public static extern nint webkit_web_view_get_uri(nint view);
        [DllImport(WebKit)] public static extern double webkit_web_view_get_estimated_load_progress(nint view);
        [DllImport(WebKit)] public static extern int webkit_web_view_is_loading(nint view);
        [DllImport(WebKit)] public static extern int webkit_web_view_can_go_back(nint view);
        [DllImport(WebKit)] public static extern int webkit_web_view_can_go_forward(nint view);
        [DllImport(WebKit)] public static extern void webkit_web_view_go_back(nint view);
        [DllImport(WebKit)] public static extern void webkit_web_view_go_forward(nint view);
        [DllImport(WebKit)] public static extern void webkit_web_view_reload(nint view);
        [DllImport(WebKit)] public static extern void webkit_web_view_reload_bypass_cache(nint view);
        [DllImport(WebKit)] public static extern void webkit_web_view_stop_loading(nint view);
        [DllImport(WebKit)] public static extern void webkit_web_view_load_uri(nint view, [MarshalAs(UnmanagedType.LPUTF8Str)] string uri);
        [DllImport(WebKit)] public static extern double webkit_web_view_get_zoom_level(nint view);
        [DllImport(WebKit)] public static extern void webkit_web_view_set_zoom_level(nint view, double level);
        [DllImport(WebKit)] public static extern nint webkit_web_view_get_favicon(nint view);
        [DllImport(WebKit)] public static extern nint webkit_web_view_get_settings(nint view);
        [DllImport(WebKit)] public static extern nint webkit_web_view_get_context(nint view);
        [DllImport(WebKit)] public static extern nint webkit_web_view_get_user_content_manager(nint view);
        [DllImport(WebKit)] public static extern nint webkit_web_view_get_find_controller(nint view);
        [DllImport(WebKit)] public static extern nint webkit_web_view_get_inspector(nint view);
        [DllImport(WebKit)] public static extern void webkit_web_inspector_show(nint inspector);
        [DllImport(WebKit)] public static extern nint webkit_web_view_new_with_related_view(nint view);
        [DllImport(WebKit)] public static extern nint webkit_web_view_get_back_forward_list(nint view);
        [DllImport(WebKit)] public static extern nint webkit_web_view_get_window_properties(nint view);
        [DllImport(WebKit)] public static extern void webkit_window_properties_get_geometry(nint properties, GdkRectangle* geometry);
        [DllImport(WebKit)] public static extern void webkit_web_view_set_background_color(nint view, GdkRgba* color);
        [DllImport(WebKit)] public static extern int webkit_web_view_get_tls_info(nint view, out nint certificate, out int errors);
        [DllImport(WebKit)] public static extern uint webkit_network_error_quark();
        [DllImport(WebKit)] public static extern uint webkit_policy_error_quark();
        [DllImport(WebKit)] public static extern uint webkit_download_error_quark();

        public const int NetworkErrorCancelled = 302;
        public const int NetworkErrorFileDoesNotExist = 303;
        public const int PolicyErrorFrameLoadInterrupted = 102;
        public const int DownloadErrorCancelledByUser = 400;
        [DllImport(WebKit)] public static extern void webkit_web_view_evaluate_javascript(nint view, [MarshalAs(UnmanagedType.LPUTF8Str)] string script, nint length, [MarshalAs(UnmanagedType.LPUTF8Str)] string? world, [MarshalAs(UnmanagedType.LPUTF8Str)] string? sourceUri, nint cancellable, nint callback, nint data);
        [DllImport(WebKit)] public static extern nint webkit_web_view_evaluate_javascript_finish(nint view, nint result, out nint error);

        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_developer_extras(nint settings, int enabled);
        [DllImport(WebKit)] public static extern void webkit_settings_set_javascript_can_open_windows_automatically(nint settings, int enabled);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_back_forward_navigation_gestures(nint settings, int enabled);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_smooth_scrolling(nint settings, int enabled);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_media_stream(nint settings, int enabled);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_webrtc(nint settings, int enabled);
        [DllImport(WebKit)] public static extern void webkit_settings_set_enable_fullscreen(nint settings, int enabled);
        [DllImport(WebKit)] public static extern void webkit_settings_set_media_playback_requires_user_gesture(nint settings, int enabled);
        [DllImport(WebKit)] public static extern void webkit_settings_set_default_charset(nint settings, [MarshalAs(UnmanagedType.LPUTF8Str)] string charset);
        [DllImport(WebKit)] public static extern void webkit_settings_set_user_agent_with_application_details(nint settings, [MarshalAs(UnmanagedType.LPUTF8Str)] string? name, [MarshalAs(UnmanagedType.LPUTF8Str)] string? version);

        // Recherche dans la page
        [DllImport(WebKit)] public static extern void webkit_find_controller_search(nint finder, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint options, uint maxMatches);
        [DllImport(WebKit)] public static extern void webkit_find_controller_count_matches(nint finder, [MarshalAs(UnmanagedType.LPUTF8Str)] string text, uint options, uint maxMatches);
        [DllImport(WebKit)] public static extern void webkit_find_controller_search_next(nint finder);
        [DllImport(WebKit)] public static extern void webkit_find_controller_search_previous(nint finder);
        [DllImport(WebKit)] public static extern void webkit_find_controller_search_finish(nint finder);

        public const uint FindCaseInsensitive = 1 << 0;
        public const uint FindWrapAround = 1 << 4;

        // Scripts et messages
        [DllImport(WebKit)] public static extern nint webkit_user_script_new([MarshalAs(UnmanagedType.LPUTF8Str)] string source, int frames, int time, nint allowList, nint blockList);
        [DllImport(WebKit)] public static extern nint webkit_user_script_new_for_world([MarshalAs(UnmanagedType.LPUTF8Str)] string source, int frames, int time, [MarshalAs(UnmanagedType.LPUTF8Str)] string world, nint allowList, nint blockList);
        [DllImport(WebKit)] public static extern void webkit_user_script_unref(nint script);
        [DllImport(WebKit)] public static extern void webkit_user_content_manager_add_script(nint manager, nint script);
        [DllImport(WebKit)] public static extern void webkit_user_content_manager_remove_script(nint manager, nint script);
        [DllImport(WebKit)] public static extern int webkit_user_content_manager_register_script_message_handler_in_world(nint manager, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string world);
        [DllImport(WebKit)] public static extern void webkit_user_content_manager_add_filter(nint manager, nint filter);
        [DllImport(WebKit)] public static extern void webkit_user_content_manager_remove_filter(nint manager, nint filter);
        [DllImport(WebKit)] public static extern void webkit_user_content_manager_remove_all_filters(nint manager);
        [DllImport(WebKit)] public static extern nint webkit_javascript_result_get_js_value(nint result);
        [DllImport(JavaScriptCore)] public static extern nint jsc_value_to_string(nint value);
        [DllImport(JavaScriptCore)] public static extern int jsc_value_is_string(nint value);
        [DllImport(JavaScriptCore)] public static extern int jsc_value_is_undefined(nint value);
        [DllImport(JavaScriptCore)] public static extern int jsc_value_is_null(nint value);

        // Filtres de contenu (anti-pub) compilés par WebKit
        [DllImport(WebKit)] public static extern nint webkit_user_content_filter_store_new([MarshalAs(UnmanagedType.LPUTF8Str)] string path);
        [DllImport(WebKit)] public static extern void webkit_user_content_filter_store_save(nint store, [MarshalAs(UnmanagedType.LPUTF8Str)] string identifier, nint source, nint cancellable, nint callback, nint data);
        [DllImport(WebKit)] public static extern nint webkit_user_content_filter_store_save_finish(nint store, nint result, out nint error);
        [DllImport(WebKit)] public static extern void webkit_user_content_filter_store_load(nint store, [MarshalAs(UnmanagedType.LPUTF8Str)] string identifier, nint cancellable, nint callback, nint data);
        [DllImport(WebKit)] public static extern nint webkit_user_content_filter_store_load_finish(nint store, nint result, out nint error);
        [DllImport(WebKit)] public static extern void webkit_user_content_filter_ref(nint filter);
        [DllImport(WebKit)] public static extern void webkit_user_content_filter_unref(nint filter);

        // Décisions de navigation
        [DllImport(WebKit)] public static extern void webkit_policy_decision_use(nint decision);
        [DllImport(WebKit)] public static extern void webkit_policy_decision_ignore(nint decision);
        [DllImport(WebKit)] public static extern void webkit_policy_decision_download(nint decision);
        [DllImport(WebKit)] public static extern nint webkit_navigation_policy_decision_get_navigation_action(nint decision);
        [DllImport(WebKit)] public static extern nint webkit_navigation_action_get_request(nint action);
        [DllImport(WebKit)] public static extern int webkit_navigation_action_get_navigation_type(nint action);
        [DllImport(WebKit)] public static extern uint webkit_navigation_action_get_mouse_button(nint action);
        [DllImport(WebKit)] public static extern uint webkit_navigation_action_get_modifiers(nint action);
        [DllImport(WebKit)] public static extern int webkit_navigation_action_is_user_gesture(nint action);
        [DllImport(WebKit)] public static extern nint webkit_uri_request_get_uri(nint request);
        [DllImport(WebKit)] public static extern nint webkit_uri_request_get_http_method(nint request);
        [DllImport(WebKit)] public static extern nint webkit_response_policy_decision_get_response(nint decision);
        [DllImport(WebKit)] public static extern int webkit_response_policy_decision_is_mime_type_supported(nint decision);
        [DllImport(WebKit)] public static extern int webkit_response_policy_decision_is_main_frame_main_resource(nint decision);
        [DllImport(WebKit)] public static extern nint webkit_uri_response_get_http_headers(nint response);
        [DllImport(WebKit)] public static extern ulong webkit_uri_response_get_content_length(nint response);
        [DllImport(WebKit)] public static extern nint webkit_hit_test_result_get_link_uri(nint hit);
        [DllImport(Soup)] public static extern nint soup_message_headers_get_one(nint headers, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
        [DllImport(Soup)] public static extern nint soup_message_headers_new(int type);
        [DllImport(Soup)] public static extern void soup_message_headers_append(nint headers, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value);
        [DllImport(Soup)] public static extern nint soup_cookie_new([MarshalAs(UnmanagedType.LPUTF8Str)] string name, [MarshalAs(UnmanagedType.LPUTF8Str)] string value, [MarshalAs(UnmanagedType.LPUTF8Str)] string domain, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int maxAge);
        [DllImport(Soup)] public static extern void soup_cookie_free(nint cookie);
        [DllImport(Soup)] public static extern void soup_cookie_set_expires(nint cookie, nint expires);
        [DllImport(Soup)] public static extern void soup_cookie_set_secure(nint cookie, int secure);
        [DllImport(Soup)] public static extern void soup_cookie_set_http_only(nint cookie, int httpOnly);
        [DllImport(Soup)] public static extern void soup_cookie_set_same_site_policy(nint cookie, int policy);
        [DllImport(Soup)] public static extern nint soup_cookie_get_name(nint cookie);
        [DllImport(Soup)] public static extern nint soup_cookie_get_value(nint cookie);
        [DllImport(Soup)] public static extern int soup_cookie_get_http_only(nint cookie);

        public const int PolicyNavigationAction = 0;
        public const int PolicyNewWindowAction = 1;
        public const int PolicyResponse = 2;
        public const int NavigationLinkClicked = 0;
        public const uint GdkControlMask = 1 << 2;

        // Autorisations
        [DllImport(WebKit)] public static extern void webkit_permission_request_allow(nint request);
        [DllImport(WebKit)] public static extern void webkit_permission_request_deny(nint request);
        [DllImport(WebKit)] public static extern nuint webkit_geolocation_permission_request_get_type();
        [DllImport(WebKit)] public static extern nuint webkit_notification_permission_request_get_type();
        [DllImport(WebKit)] public static extern nuint webkit_user_media_permission_request_get_type();
        [DllImport(WebKit)] public static extern int webkit_user_media_permission_is_for_audio_device(nint request);
        [DllImport(WebKit)] public static extern int webkit_user_media_permission_is_for_video_device(nint request);
        [DllImport(WebKit)] public static extern int webkit_user_media_permission_is_for_display_device(nint request);
        [DllImport(WebKit)] public static extern nuint webkit_device_info_permission_request_get_type();
        [DllImport(WebKit)] public static extern nuint webkit_clipboard_permission_request_get_type();
        [DllImport(WebKit)] public static extern nuint webkit_website_data_access_permission_request_get_type();
        [DllImport(WebKit)] public static extern nuint webkit_pointer_lock_permission_request_get_type();

        // Contexte (session de navigation du profil)
        [DllImport(WebKit)] public static extern nint webkit_web_context_get_website_data_manager(nint context);
        [DllImport(WebKit)] public static extern nint webkit_web_context_get_cookie_manager(nint context);
        [DllImport(WebKit)] public static extern nint webkit_web_context_get_security_manager(nint context);
        [DllImport(WebKit)] public static extern void webkit_web_context_set_favicon_database_directory(nint context, [MarshalAs(UnmanagedType.LPUTF8Str)] string? path);
        [DllImport(WebKit)] public static extern void webkit_web_context_set_spell_checking_enabled(nint context, int enabled);
        [DllImport(WebKit)] public static extern void webkit_web_context_set_spell_checking_languages(nint context, nint languages);
        [DllImport(WebKit)] public static extern void webkit_web_context_set_preferred_languages(nint context, nint languages);
        [DllImport(WebKit)] public static extern void webkit_web_context_set_cache_model(nint context, int model);
        [DllImport(WebKit)] public static extern void webkit_web_context_register_uri_scheme(nint context, [MarshalAs(UnmanagedType.LPUTF8Str)] string scheme, nint callback, nint data, nint destroy);
        [DllImport(WebKit)] public static extern void webkit_web_context_allow_tls_certificate_for_host(nint context, nint certificate, [MarshalAs(UnmanagedType.LPUTF8Str)] string host);
        [DllImport(WebKit)] public static extern int webkit_web_context_is_ephemeral(nint context);
        [DllImport(WebKit)] public static extern void webkit_security_manager_register_uri_scheme_as_secure(nint manager, [MarshalAs(UnmanagedType.LPUTF8Str)] string scheme);
        [DllImport(WebKit)] public static extern void webkit_security_manager_register_uri_scheme_as_cors_enabled(nint manager, [MarshalAs(UnmanagedType.LPUTF8Str)] string scheme);
        [DllImport(WebKit)] public static extern void webkit_cookie_manager_set_persistent_storage(nint manager, [MarshalAs(UnmanagedType.LPUTF8Str)] string filename, int storage);
        [DllImport(WebKit)] public static extern void webkit_cookie_manager_set_accept_policy(nint manager, int policy);
        [DllImport(WebKit)] public static extern void webkit_cookie_manager_get_cookies(nint manager, [MarshalAs(UnmanagedType.LPUTF8Str)] string uri, nint cancellable, nint callback, nint data);
        [DllImport(WebKit)] public static extern nint webkit_cookie_manager_get_cookies_finish(nint manager, nint result, out nint error);
        [DllImport(WebKit)] public static extern void webkit_cookie_manager_add_cookie(nint manager, nint cookie, nint cancellable, nint callback, nint data);
        [DllImport(WebKit)] public static extern int webkit_cookie_manager_add_cookie_finish(nint manager, nint result, out nint error);
        [DllImport(WebKit)] public static extern void webkit_cookie_manager_delete_cookie(nint manager, nint cookie, nint cancellable, nint callback, nint data);
        [DllImport(WebKit)] public static extern int webkit_cookie_manager_delete_cookie_finish(nint manager, nint result, out nint error);
        [DllImport(WebKit)] public static extern void webkit_website_data_manager_set_itp_enabled(nint manager, int enabled);
        [DllImport(WebKit)] public static extern void webkit_website_data_manager_set_tls_errors_policy(nint manager, int policy);
        [DllImport(WebKit)] public static extern void webkit_website_data_manager_clear(nint manager, int types, long timeSpan, nint cancellable, nint callback, nint data);
        [DllImport(WebKit)] public static extern int webkit_website_data_manager_clear_finish(nint manager, nint result, out nint error);

        public const int CookieStorageSqlite = 1;
        public const int SoupSameSiteNone = 0;
        public const int SoupSameSiteLax = 1;
        public const int SoupSameSiteStrict = 2;
        public const int CookiesAlways = 0;
        public const int CookiesNoThirdParty = 2;
        public const int WebsiteDataAll = (1 << 14) - 1;

        // Schéma d'adresse propre à PommeBrowser (Ruffle)
        [DllImport(WebKit)] public static extern nint webkit_uri_scheme_request_get_path(nint request);
        [DllImport(WebKit)] public static extern nint webkit_uri_scheme_request_get_uri(nint request);
        [DllImport(WebKit)] public static extern nint webkit_uri_scheme_request_get_web_view(nint request);
        [DllImport(WebKit)] public static extern nint webkit_uri_scheme_response_new(nint stream, long length);
        [DllImport(WebKit)] public static extern void webkit_uri_scheme_response_set_status(nint response, uint status, [MarshalAs(UnmanagedType.LPUTF8Str)] string? reason);
        [DllImport(WebKit)] public static extern void webkit_uri_scheme_response_set_content_type(nint response, [MarshalAs(UnmanagedType.LPUTF8Str)] string type);
        [DllImport(WebKit)] public static extern void webkit_uri_scheme_response_set_http_headers(nint response, nint headers);
        [DllImport(WebKit)] public static extern void webkit_uri_scheme_request_finish_with_response(nint request, nint response);
        [DllImport(WebKit)] public static extern void webkit_uri_scheme_request_finish_error(nint request, nint error);
        [DllImport(GLibLib)] public static extern nint g_error_new_literal(uint domain, int code, [MarshalAs(UnmanagedType.LPUTF8Str)] string message);
        [DllImport(GLibLib)] public static extern uint g_quark_from_string([MarshalAs(UnmanagedType.LPUTF8Str)] string name);

        // Téléchargements
        [DllImport(WebKit)] public static extern nint webkit_download_get_request(nint download);
        [DllImport(WebKit)] public static extern nint webkit_download_get_web_view(nint download);
        [DllImport(WebKit)] public static extern nint webkit_download_get_response(nint download);
        [DllImport(WebKit)] public static extern void webkit_download_set_destination(nint download, [MarshalAs(UnmanagedType.LPUTF8Str)] string uri);
        [DllImport(WebKit)] public static extern ulong webkit_download_get_received_data_length(nint download);
        [DllImport(WebKit)] public static extern void webkit_download_cancel(nint download);
        [DllImport(WebKit)] public static extern double webkit_download_get_estimated_progress(nint download);
        [DllImport(WebKit)] public static extern void webkit_download_set_allow_overwrite(nint download, int allowed);

        // Fenêtres GTK (fenêtres ouvertes par les pages avec window.open)
        [DllImport(Gtk3)] public static extern nint gtk_window_new(int type);
        [DllImport(Gtk3)] public static extern void gtk_container_add(nint container, nint widget);
        [DllImport(Gtk3)] public static extern void gtk_widget_show_all(nint widget);
        [DllImport(Gtk3)] public static extern void gtk_widget_destroy(nint widget);
        [DllImport(Gtk3)] public static extern void gtk_widget_grab_focus(nint widget);
        [DllImport(Gtk3)] public static extern void gtk_window_set_title(nint window, [MarshalAs(UnmanagedType.LPUTF8Str)] string title);
        [DllImport(Gtk3)] public static extern void gtk_window_set_default_size(nint window, int width, int height);
        [DllImport(Gtk3)] public static extern void gtk_window_set_icon_from_file(nint window, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, nint error);
        [DllImport(Gtk3)] public static extern void gtk_window_present(nint window);

        // Clavier de la vue intégrée (fenêtre GTK de l'adaptateur, placée dans la fenêtre X11 d'Avalonia)
        [DllImport(Gtk3)] public static extern nint gtk_widget_get_toplevel(nint widget);
        [DllImport(Gtk3)] public static extern int gtk_widget_is_toplevel(nint widget);
        [DllImport(Gtk3)] public static extern nint gtk_widget_get_window(nint widget);
        [DllImport(Gtk3)] public static extern int gtk_widget_event(nint widget, nint gdkEvent);
        [DllImport(Gtk3)] public static extern int gtk_window_is_active(nint window);
        [DllImport(Gdk3)] public static extern int gdk_window_get_events(nint window);
        [DllImport(Gdk3)] public static extern void gdk_window_set_events(nint window, int mask);
        [DllImport(Gdk3)] public static extern nint gdk_window_get_display(nint window);
        [DllImport(Gdk3)] public static extern nint gdk_x11_window_get_xid(nint window);
        [DllImport(Gdk3)] public static extern nint gdk_x11_display_get_xdisplay(nint display);
        [DllImport(Gdk3)] public static extern void gdk_x11_display_error_trap_push(nint display);
        [DllImport(Gdk3)] public static extern int gdk_x11_display_error_trap_pop(nint display);
        [DllImport(Gdk3)] public static extern nint gdk_event_new(int type);
        [DllImport(Gdk3)] public static extern void gdk_event_free(nint gdkEvent);
        [DllImport(X11)] public static extern int XGetInputFocus(nint display, out nint focus, out int revertTo);
        [DllImport(X11)] public static extern int XSetInputFocus(nint display, nint window, int revertTo, nint time);
        [DllImport(X11)] public static extern int XQueryTree(nint display, nint window, out nint root, out nint parent, out nint children, out uint count);
        [DllImport(X11)] public static extern int XFree(nint data);

        public const int GdkKeyPressMask = 1 << 10;
        public const int GdkKeyReleaseMask = 1 << 11;
        public const int GdkFocusChange = 12;
        public const int XRevertToParent = 2;

        [StructLayout(LayoutKind.Sequential)]
        public struct GdkRectangle
        {
            public int X, Y, Width, Height;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct GdkRgba
        {
            public double Red, Green, Blue, Alpha;
        }

        // Icônes de site (surface cairo → PNG)
        [DllImport(Cairo)] public static extern int cairo_surface_write_to_png_stream(nint surface, nint writeFunc, nint closure);

        // ---------------------------------------------------------------
        // Outils
        // ---------------------------------------------------------------

        public static string? String(nint utf8) => utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);

        /// <summary>Texte alloué par GLib : lu puis libéré.</summary>
        public static string? TakeString(nint utf8)
        {
            if (utf8 == 0)
                return null;
            try
            {
                return Marshal.PtrToStringUTF8(utf8);
            }
            finally
            {
                g_free(utf8);
            }
        }

        /// <summary>Domaine et code d'une GError (non libérée).</summary>
        public static (uint Domain, int Code) ErrorCode(nint error)
            => error == 0 ? (0u, 0) : ((uint)Marshal.ReadInt32(error, 0), Marshal.ReadInt32(error, 4));

        public static string ErrorMessage(nint error)
            => error == 0 ? string.Empty : Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "?";

        /// <summary>Message d'une GError (libérée).</summary>
        public static string TakeError(nint error)
        {
            if (error == 0)
                return string.Empty;
            // struct GError { GQuark domain; gint code; gchar *message; }
            string message = Marshal.PtrToStringUTF8(Marshal.ReadIntPtr(error, 8)) ?? "?";
            g_error_free(error);
            return message;
        }

        public static bool IsA(nint instance, nuint type) => instance != 0 && g_type_check_instance_is_a(instance, type) != 0;

        /// <summary>Tableau de chaînes terminé par NULL, libéré par l'appelant (FreeStrv).</summary>
        public static nint Strv(IReadOnlyList<string> values)
        {
            nint array = Marshal.AllocHGlobal((values.Count + 1) * IntPtr.Size);
            for (int i = 0; i < values.Count; i++)
                Marshal.WriteIntPtr(array, i * IntPtr.Size, Marshal.StringToCoTaskMemUTF8(values[i]));
            Marshal.WriteIntPtr(array, values.Count * IntPtr.Size, 0);
            return array;
        }

        public static void FreeStrv(nint array, int count)
        {
            for (int i = 0; i < count; i++)
                Marshal.ZeroFreeCoTaskMemUTF8(Marshal.ReadIntPtr(array, i * IntPtr.Size));
            Marshal.FreeHGlobal(array);
        }

        /// <summary>Propriété texte d'un objet GObject (ex. « certificate-pem »).</summary>
        public static string? GetStringProperty(nint instance, string name)
        {
            GValue value = default;
            g_value_init(&value, GTypeString);
            try
            {
                g_object_get_property(instance, name, &value);
                return String(g_value_get_string(&value));
            }
            finally
            {
                g_value_unset(&value);
            }
        }

        public static nint Bytes(byte[] data)
        {
            fixed (byte* pointer = data)
                return g_bytes_new(pointer, (nuint)data.Length);
        }

        /// <summary>Icône (surface cairo) en PNG.</summary>
        public static byte[]? SurfaceToPng(nint surface)
        {
            if (surface == 0)
                return null;
            var buffer = new List<byte>();
            GCHandle handle = GCHandle.Alloc(buffer);
            try
            {
                int status = cairo_surface_write_to_png_stream(surface, (nint)(delegate* unmanaged[Cdecl]<nint, byte*, uint, int>)&WritePng, GCHandle.ToIntPtr(handle));
                return status == 0 && buffer.Count > 0 ? buffer.ToArray() : null;
            }
            finally
            {
                handle.Free();
            }
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int WritePng(nint closure, byte* data, uint length)
        {
            if (GCHandle.FromIntPtr(closure).Target is List<byte> buffer)
            {
                for (uint i = 0; i < length; i++)
                    buffer.Add(data[i]);
            }
            return 0;
        }
    }

    /// <summary>
    /// Fil GLib d'Avalonia : GTK et WebKitGTK n'y sont utilisables que là. Les appels depuis
    /// l'interface ne l'attendent jamais de façon bloquante (pas d'interblocage avec les
    /// signaux, qui remontent vers l'interface de façon asynchrone).
    /// </summary>
    static class Glib
    {
        public static Task<T> Run<T>(Func<T> action) => Avalonia.X11.Interop.GtkInteropHelper.RunOnGlibThread(action);

        public static Task Run(Action action) => Run(() =>
        {
            action();
            return true;
        });

        /// <summary>Exécution sans attendre ; une erreur est notée dans le journal.</summary>
        public static void Post(Action action)
        {
            _ = Run(() =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    MyHomelabBrowser.classes.RuntimeLogBuffer.Append("[WebKitGTK] " + ex.Message);
                }
            });
        }
    }

    /// <summary>Connexion à un signal GObject ; l'objet d'état est retenu jusqu'à la déconnexion.</summary>
    sealed unsafe class GSignal : IDisposable
    {
        static readonly nint OnDestroyPointer = (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnDestroy;

        readonly nint _instance;
        ulong _id;

        public GSignal(nint instance, string signal, nint callback, object state)
        {
            _instance = instance;
            GCHandle handle = GCHandle.Alloc(state);
            _id = WebKitGtk.g_signal_connect_data(instance, signal, callback, GCHandle.ToIntPtr(handle), OnDestroyPointer, 0);
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnDestroy(nint data, nint closure) => GCHandle.FromIntPtr(data).Free();

        public static T? State<T>(nint data) where T : class
            => data == 0 ? null : GCHandle.FromIntPtr(data).Target as T;

        /// <summary>À appeler sur le fil GLib.</summary>
        public void Dispose()
        {
            if (_id != 0)
                WebKitGtk.g_signal_handler_disconnect(_instance, _id);
            _id = 0;
        }
    }
}
