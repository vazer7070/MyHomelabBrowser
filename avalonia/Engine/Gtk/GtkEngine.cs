using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using MyHomelabBrowser.classes;
using PommeBrowser.Linux.Core;
using static PommeBrowser.Engine.Gtk.WebKitGtk;

namespace PommeBrowser.Engine.Gtk
{
    /// <summary>
    /// Sessions WebKitGTK : contexte du profil (partagé par tous ses onglets) et contextes
    /// éphémères de la navigation privée (un par onglet privé). Chaque contexte est réglé une
    /// fois : Ruffle, cookies, protection contre le pistage, langues, téléchargements.
    /// Tout se passe sur le fil GLib.
    /// </summary>
    static unsafe class GtkEngine
    {
        public const string RuffleScheme = "pomme-ruffle";
        public const string RuffleBaseUrl = RuffleScheme + "://ruffle/";

        /// <summary>Monde isolé des scripts de PommeBrowser (invisible pour la page).</summary>
        public const string World = "pommebrowser";

        const int CacheModelWebBrowser = 1;
        const int TlsErrorsPolicyFail = 1;
        const int SoupHeadersResponse = 1;

        static readonly nint ServeRuffleCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&ServeRuffle;
        static readonly nint ServeFlashBridgeCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&ServeFlashBridge;
        static readonly nint ContextGoneCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnContextGone;
        static readonly nint DownloadStartedCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnDownloadStarted;
        static readonly nint ClearedCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnCleared;

        /// <summary>Contextes vivants (retirés à leur destruction).</summary>
        static readonly HashSet<nint> Contexts = new();

        /// <summary>Signaux des contextes (retenus tant que le contexte vit).</summary>
        static readonly Dictionary<nint, GSignal> ContextSignals = new();

        /// <summary>Vues ouvertes : origine des téléchargements.</summary>
        static readonly Dictionary<nint, GtkEngineTab> Tabs = new();

        /// <summary>Filtre anti-pub compilé (WebKitUserContentFilter), 0 s'il n'est pas prêt.</summary>
        public static nint ContentFilter { get; private set; }

        public static void Register(nint view, GtkEngineTab tab) => Tabs[view] = tab;

        public static void Unregister(nint view) => Tabs.Remove(view);

        /// <summary>Premier onglet d'un contexte : réglages de la session (une fois par contexte).</summary>
        public static void ConfigureContext(nint context)
        {
            if (!Contexts.Add(context))
                return;

            g_object_weak_ref(context, ContextGoneCallback, 0);

            nint security = webkit_web_context_get_security_manager(context);
            // Sécurisé : chargeable depuis une page HTTPS ; CORS : Ruffle y télécharge son module WebAssembly.
            webkit_security_manager_register_uri_scheme_as_secure(security, RuffleScheme);
            webkit_security_manager_register_uri_scheme_as_cors_enabled(security, RuffleScheme);
            webkit_web_context_register_uri_scheme(context, RuffleScheme, ServeRuffleCallback, 0, 0);
            // Pont de la page vers le contenu Flash du moteur intégré (voir ServeFlashBridge).
            webkit_security_manager_register_uri_scheme_as_secure(security, RuffleContent.FlashBridgeScheme);
            webkit_security_manager_register_uri_scheme_as_cors_enabled(security, RuffleContent.FlashBridgeScheme);
            webkit_web_context_register_uri_scheme(context, RuffleContent.FlashBridgeScheme, ServeFlashBridgeCallback, 0, 0);

            webkit_web_context_set_cache_model(context, CacheModelWebBrowser);

            bool ephemeral = webkit_web_context_is_ephemeral(context) != 0;
            if (!ephemeral)
            {
                // Icônes des sites, dans le dossier de données du profil.
                webkit_web_context_set_favicon_database_directory(context, null);
                if (EngineHost.Settings.CookieDatabase is { Length: > 0 } cookies)
                    webkit_cookie_manager_set_persistent_storage(webkit_web_context_get_cookie_manager(context), cookies, CookieStorageSqlite);
            }

            webkit_website_data_manager_set_tls_errors_policy(webkit_web_context_get_website_data_manager(context), TlsErrorsPolicyFail);
            ContextSignals[context] = new GSignal(context, "download-started", DownloadStartedCallback, new object());
            ApplySettings(context);
        }

        /// <summary>Réglages modifiés : appliqués à toutes les sessions ouvertes.</summary>
        public static void ApplySettings()
            => Glib.Post(() =>
            {
                foreach (nint context in Contexts)
                    ApplySettings(context);
            });

        static void ApplySettings(nint context)
        {
            EngineSettings settings = EngineHost.Settings;

            webkit_website_data_manager_set_itp_enabled(webkit_web_context_get_website_data_manager(context), settings.TrackingPrevention ? 1 : 0);
            webkit_cookie_manager_set_accept_policy(webkit_web_context_get_cookie_manager(context),
                settings.BlockThirdPartyCookies ? CookiesNoThirdParty : CookiesAlways);

            nint languages = Strv(settings.Languages);
            webkit_web_context_set_preferred_languages(context, languages);
            FreeStrv(languages, settings.Languages.Count);

            webkit_web_context_set_spell_checking_enabled(context, settings.SpellCheckingLanguages.Count > 0 ? 1 : 0);
            if (settings.SpellCheckingLanguages.Count > 0)
            {
                nint spelling = Strv(settings.SpellCheckingLanguages);
                webkit_web_context_set_spell_checking_languages(context, spelling);
                FreeStrv(spelling, settings.SpellCheckingLanguages.Count);
            }
        }

        /// <summary>Réglages d'une vue (chaque vue a les siens).</summary>
        public static void ConfigureView(nint view)
        {
            nint settings = webkit_web_view_get_settings(view);
            webkit_settings_set_enable_developer_extras(settings, 1);
            webkit_settings_set_enable_back_forward_navigation_gestures(settings, 1);
            webkit_settings_set_enable_smooth_scrolling(settings, 1);
            webkit_settings_set_enable_fullscreen(settings, 1);
            webkit_settings_set_enable_media_stream(settings, 1);
            webkit_settings_set_enable_webrtc(settings, 1);
            // Fenêtres surgissantes : seulement à la suite d'un clic.
            webkit_settings_set_javascript_can_open_windows_automatically(settings, 0);
            // Pas de lecture automatique avec le son.
            webkit_settings_set_media_playback_requires_user_gesture(settings, 1);
            webkit_settings_set_default_charset(settings, "utf-8");
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnContextGone(nint data, nint context)
        {
            Contexts.Remove(context);
            ContextSignals.Remove(context);
        }

        // ---------------------------------------------------------------
        // Ruffle : pomme-ruffle://ruffle/<fichier>
        // ---------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void ServeRuffle(nint request, nint data)
        {
            try
            {
                string name = (String(webkit_uri_scheme_request_get_path(request)) ?? string.Empty).TrimStart('/');
                (byte[] Data, string ContentType)? file = RuffleContent.Read(name, RuffleBaseUrl);
                if (file is not { } content)
                {
                    nint error = g_error_new_literal(g_io_error_quark(), GIoErrorNotFound, name);
                    webkit_uri_scheme_request_finish_error(request, error);
                    g_error_free(error);
                    return;
                }

                nint bytes = Bytes(content.Data);
                nint stream = g_memory_input_stream_new_from_bytes(bytes);
                nint response = webkit_uri_scheme_response_new(stream, content.Data.Length);
                webkit_uri_scheme_response_set_status(response, 200, null);
                webkit_uri_scheme_response_set_content_type(response, content.ContentType);

                nint headers = soup_message_headers_new(SoupHeadersResponse);
                soup_message_headers_append(headers, "Content-Type", content.ContentType);
                soup_message_headers_append(headers, "Access-Control-Allow-Origin", "*");
                soup_message_headers_append(headers, "X-Content-Type-Options", "nosniff");
                soup_message_headers_append(headers, "Cache-Control", "max-age=604800, immutable");
                webkit_uri_scheme_response_set_http_headers(response, headers);

                webkit_uri_scheme_request_finish_with_response(request, response);
                g_object_unref(response);
                g_object_unref(stream);
                g_bytes_unref(bytes);
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[Ruffle] " + ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // Pont page → Flash : pomme-flash://call/?t=<jeton>&r=<requête>
        // ---------------------------------------------------------------

        /// <summary>Réponse de la page que l'onglet attend d'un appel vers le contenu (fil de l'interface).</summary>
        static readonly TimeSpan FlashBridgeTimeout = TimeSpan.FromSeconds(9);

        /// <summary>
        /// Appel de la page vers le contenu Flash (CallFunction d'une fonction déclarée par
        /// ExternalInterface.addCallback) : la page fait une requête synchrone, et attend ; l'onglet
        /// répond sur le fil de l'interface. 200 avec la réponse, 204 si l'appel est refusé ou sans
        /// réponse, 403 sans le jeton du lecteur (requête venue d'ailleurs que du script du pont,
        /// par exemple d'un cadre d'un autre site) ou trop longue. Seul l'onglet de la vue qui
        /// demande peut répondre (voir GtkEngineTab.SetFlashBridge).
        /// </summary>
        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void ServeFlashBridge(nint request, nint data)
        {
            try
            {
                string? result = null;
                bool refused = false;
                nint view = webkit_uri_scheme_request_get_web_view(request);
                if (view != 0 && Tabs.TryGetValue(view, out GtkEngineTab? tab) && tab.FlashBridge is { } bridge)
                {
                    if (RuffleContent.ParseFlashBridgeRequest(String(webkit_uri_scheme_request_get_uri(request)), bridge.Token) is { } call)
                    {
                        // La page attend : l'onglet répond sur son fil, sans attente sans fin si l'interface est prise.
                        Task<string?> answer = Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => bridge.Call(call)).GetTask();
                        if (answer.Wait(FlashBridgeTimeout))
                            result = answer.Result;
                        else
                            RuntimeLogBuffer.Append("[Flash] Appel de la page vers le contenu sans réponse de l'interface.");
                    }
                    else
                    {
                        refused = true;
                        RuntimeLogBuffer.Append("[Flash] Appel du contenu refusé : sans le jeton du pont (cadre d'un autre site ?) ou trop long.");
                    }
                }

                byte[] body = System.Text.Encoding.UTF8.GetBytes(result ?? string.Empty);
                nint bytes = Bytes(body);
                nint stream = g_memory_input_stream_new_from_bytes(bytes);
                nint response = webkit_uri_scheme_response_new(stream, body.Length);
                webkit_uri_scheme_response_set_status(response, refused ? 403u : result != null ? 200u : 204u, null);
                webkit_uri_scheme_response_set_content_type(response, "text/plain; charset=utf-8");
                nint headers = soup_message_headers_new(SoupHeadersResponse);
                soup_message_headers_append(headers, "Content-Type", "text/plain; charset=utf-8");
                soup_message_headers_append(headers, "Access-Control-Allow-Origin", "*");
                soup_message_headers_append(headers, "Cache-Control", "no-store");
                webkit_uri_scheme_response_set_http_headers(response, headers);
                webkit_uri_scheme_request_finish_with_response(request, response);
                g_object_unref(response);
                g_object_unref(stream);
                g_bytes_unref(bytes);
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[Flash] Pont de la page : " + ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // Anti-pub : filtre compilé par WebKit (UserContentFilterStore)
        // ---------------------------------------------------------------

        static nint _filterStore;

        static nint FilterStore(string directory)
        {
            if (_filterStore == 0)
            {
                Directory.CreateDirectory(directory);
                _filterStore = webkit_user_content_filter_store_new(directory);
            }
            return _filterStore;
        }

        /// <summary>Charge un filtre déjà compilé (0 s'il n'existe pas).</summary>
        public static Task<nint> LoadFilterAsync(string storeDirectory, string id)
        {
            var completion = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
            GCHandle handle = GCHandle.Alloc(completion);
            Glib.Post(() => webkit_user_content_filter_store_load(FilterStore(storeDirectory), id, 0,
                (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnFilterLoaded, GCHandle.ToIntPtr(handle)));
            return completion.Task;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnFilterLoaded(nint store, nint result, nint data)
        {
            GCHandle handle = GCHandle.FromIntPtr(data);
            var completion = (TaskCompletionSource<nint>)handle.Target!;
            handle.Free();
            nint filter = webkit_user_content_filter_store_load_finish(store, result, out nint error);
            if (error != 0)
                g_error_free(error);
            completion.TrySetResult(filter);
        }

        /// <summary>Compile des règles (JSON WebKit) et garde le résultat dans le magasin.</summary>
        public static Task<nint> CompileFilterAsync(string storeDirectory, string id, byte[] json)
        {
            var completion = new TaskCompletionSource<nint>(TaskCreationOptions.RunContinuationsAsynchronously);
            GCHandle handle = GCHandle.Alloc(completion);
            Glib.Post(() =>
            {
                nint bytes = Bytes(json);
                webkit_user_content_filter_store_save(FilterStore(storeDirectory), id, bytes, 0,
                    (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnFilterSaved, GCHandle.ToIntPtr(handle));
                g_bytes_unref(bytes);
            });
            return completion.Task;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnFilterSaved(nint store, nint result, nint data)
        {
            GCHandle handle = GCHandle.FromIntPtr(data);
            var completion = (TaskCompletionSource<nint>)handle.Target!;
            handle.Free();
            nint filter = webkit_user_content_filter_store_save_finish(store, result, out nint error);
            if (error != 0)
                completion.TrySetException(new InvalidOperationException(TakeError(error)));
            else
                completion.TrySetResult(filter);
        }

        /// <summary>Nouveau filtre actif (0 : aucun) ; les onglets le reprennent, l'ancien est libéré.</summary>
        public static void SetContentFilter(nint filter)
            => Glib.Post(() =>
            {
                nint old = ContentFilter;
                ContentFilter = filter;
                foreach (GtkEngineTab tab in Tabs.Values)
                    tab.ApplyContentFilterOnGlib(force: true);
                if (old != 0 && old != filter)
                    webkit_user_content_filter_unref(old);
            });

        // ---------------------------------------------------------------
        // Téléchargements
        // ---------------------------------------------------------------

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnDownloadStarted(nint context, nint download, nint data)
        {
            try
            {
                nint view = webkit_download_get_web_view(download);
                Tabs.TryGetValue(view, out GtkEngineTab? source);
                GtkDownload.Track(download, source);
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[Téléchargements] " + ex.Message);
            }
        }

        // ---------------------------------------------------------------
        // Effacement des données
        // ---------------------------------------------------------------

        /// <summary>Données des sites du profil (cookies, cache, stockage…) depuis <paramref name="since"/>.</summary>
        public static Task ClearDataAsync(TimeSpan? since, bool cookiesAndSiteData, bool cache)
        {
            // Cache : mémoire, disque, cache hors ligne, cache DOM ; le reste : cookies et stockage des sites.
            const int CacheTypes = 1 | 2 | 4 | 8192;
            int types = (cache ? CacheTypes : 0) | (cookiesAndSiteData ? WebsiteDataAll & ~CacheTypes : 0);
            if (types == 0)
                return Task.CompletedTask;

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            GCHandle handle = GCHandle.Alloc(completion);
            long microseconds = since is { } span ? (long)span.TotalMilliseconds * 1000 : 0;
            Glib.Post(() =>
            {
                nint manager = webkit_web_context_get_website_data_manager(DefaultContext());
                webkit_website_data_manager_clear(manager, types, microseconds, 0, ClearedCallback, GCHandle.ToIntPtr(handle));
            });
            return completion.Task;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnCleared(nint manager, nint result, nint data)
        {
            GCHandle handle = GCHandle.FromIntPtr(data);
            var completion = (TaskCompletionSource<bool>)handle.Target!;
            handle.Free();
            webkit_website_data_manager_clear_finish(manager, result, out nint error);
            if (error != 0)
                completion.TrySetException(new InvalidOperationException(TakeError(error)));
            else
                completion.TrySetResult(true);
        }

        [DllImport("libwebkit2gtk-4.1.so.0")] static extern nint webkit_web_context_get_default();
        [DllImport("libwebkit2gtk-4.1.so.0")] static extern uint webkit_get_major_version();
        [DllImport("libwebkit2gtk-4.1.so.0")] static extern uint webkit_get_minor_version();
        [DllImport("libwebkit2gtk-4.1.so.0")] static extern uint webkit_get_micro_version();

        public static string? Version()
        {
            try
            {
                return $"{webkit_get_major_version()}.{webkit_get_minor_version()}.{webkit_get_micro_version()}";
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                return null;
            }
        }

        /// <summary>Processus de WebKit (pages, réseau, GPU) lancés par PommeBrowser, d'après /proc.</summary>
        public static IReadOnlyList<int> ChildProcesses()
        {
            var result = new List<int>();
            int self = Environment.ProcessId;
            try
            {
                foreach (string directory in Directory.EnumerateDirectories("/proc"))
                {
                    if (!int.TryParse(Path.GetFileName(directory), out int pid))
                        continue;
                    try
                    {
                        string stat = File.ReadAllText(Path.Combine(directory, "stat"));
                        int close = stat.LastIndexOf(')');
                        string name = stat[(stat.IndexOf('(') + 1)..close];
                        string[] fields = stat[(close + 2)..].Split(' ');
                        if (name.StartsWith("WebKit", StringComparison.Ordinal) && int.TryParse(fields[1], out int parent) && parent == self)
                            result.Add(pid);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or IndexOutOfRangeException or ArgumentOutOfRangeException)
                    {
                        // Processus terminé entre-temps.
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            return result;
        }

        static nint DefaultContext() => webkit_web_context_get_default();
    }

    /// <summary>Suivi d'un téléchargement WebKit ; les changements remontent au fil de l'interface.</summary>
    sealed unsafe class GtkDownload
    {
        static readonly nint DecideDestinationCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, int>)&OnDecideDestination;
        static readonly nint ReceivedDataCallback = (nint)(delegate* unmanaged[Cdecl]<nint, ulong, nint, void>)&OnReceivedData;
        static readonly nint FailedCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, nint, void>)&OnFailed;
        static readonly nint FinishedCallback = (nint)(delegate* unmanaged[Cdecl]<nint, nint, void>)&OnFinished;

        readonly nint _download;
        readonly string _uri;
        readonly bool _isPrivate;
        readonly List<GSignal> _signals = new();
        EngineDownload? _public;
        DateTime _lastNotified;

        GtkDownload(nint download, string uri, bool isPrivate)
        {
            _download = download;
            _uri = uri;
            _isPrivate = isPrivate;
        }

        public static void Track(nint download, GtkEngineTab? source)
        {
            g_object_ref(download);
            string uri = String(webkit_uri_request_get_uri(webkit_download_get_request(download))) ?? string.Empty;
            var state = new GtkDownload(download, uri, source?.IsPrivate ?? false);
            state._signals.Add(new GSignal(download, "decide-destination", DecideDestinationCallback, state));
            state._signals.Add(new GSignal(download, "received-data", ReceivedDataCallback, state));
            state._signals.Add(new GSignal(download, "failed", FailedCallback, state));
            state._signals.Add(new GSignal(download, "finished", FinishedCallback, state));
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static int OnDecideDestination(nint download, nint suggested, nint data)
        {
            GtkDownload? state = GSignal.State<GtkDownload>(data);
            if (state == null)
                return 0;

            string directory = EngineHost.Settings.DownloadDirectory;
            try
            {
                Directory.CreateDirectory(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RuntimeLogBuffer.Append("[Téléchargements] " + ex.Message);
            }

            // Jamais par-dessus un fichier existant.
            string path = DownloadNames.UniquePath(directory, String(suggested));
            webkit_download_set_allow_overwrite(download, 0);
            webkit_download_set_destination(download, new Uri(path).AbsoluteUri);

            var info = new EngineDownload(state._uri, Path.GetFileName(path), () => Glib.Post(() => webkit_download_cancel(download)))
            {
                Destination = path,
                IsPrivate = state._isPrivate
            };
            state._public = info;
            EngineHost.RaiseDownloadStarted(info);
            return 1;
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnReceivedData(nint download, ulong length, nint data)
        {
            GtkDownload? state = GSignal.State<GtkDownload>(data);
            if (state?._public is not { } info)
                return;

            long received = (long)webkit_download_get_received_data_length(download);
            double progress = webkit_download_get_estimated_progress(download);
            long total = progress > 0 ? (long)(received / progress) : 0;

            // Au plus quatre mises à jour par seconde.
            if (DateTime.UtcNow - state._lastNotified < TimeSpan.FromMilliseconds(250))
                return;
            state._lastNotified = DateTime.UtcNow;
            Post(() =>
            {
                info.ReceivedBytes = received;
                info.TotalBytes = total;
                info.RaiseChanged();
            });
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnFailed(nint download, nint error, nint data)
        {
            GtkDownload? state = GSignal.State<GtkDownload>(data);
            if (state?._public is not { } info)
                return;

            (uint domain, int code) = ErrorCode(error);
            bool cancelled = domain == webkit_download_error_quark() && code == DownloadErrorCancelledByUser;
            string message = ErrorMessage(error);
            Post(() =>
            {
                info.IsCancelled = cancelled;
                info.Error = cancelled ? null : message;
            });
        }

        [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
        static void OnFinished(nint download, nint data)
        {
            GtkDownload? state = GSignal.State<GtkDownload>(data);
            if (state == null)
                return;

            long received = (long)webkit_download_get_received_data_length(download);
            foreach (GSignal signal in state._signals)
                signal.Dispose();
            state._signals.Clear();
            g_object_unref(download);

            if (state._public is { } info)
            {
                Post(() =>
                {
                    info.ReceivedBytes = received;
                    if (info.Error == null && !info.IsCancelled)
                        info.TotalBytes = received;
                    info.IsFinished = true;
                    info.RaiseChanged();
                });
            }
        }

        static void Post(Action action) => Avalonia.Threading.Dispatcher.UIThread.Post(action);
    }
}
