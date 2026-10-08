using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using PommeFlash.Host.Native;

namespace PommeFlash.Host
{
    /// <summary>
    /// Instance du module Flash (un élément &lt;object&gt; ou &lt;embed&gt;) : création, fenêtre,
    /// adresses demandées par le module, minuteries et questions sur la page.
    /// </summary>
    sealed unsafe partial class PluginInstance
    {
        static nint _userAgent;

        readonly PluginLibrary _library;
        readonly HostOptions _options;
        readonly NPP_t* _npp;
        // NPWindow, ou NPWindowUnix sous Linux (ws_info avant le type).
        readonly nint _window;
        readonly List<nint> _strings = new();
        readonly List<PluginStream> _streams = new();
        readonly Dictionary<uint, (nint Function, bool Repeat)> _timers = new();
        readonly Stack<bool> _popups = new();
        readonly CookieContainer _cookies = new();
        readonly HttpClient _http;
        GCHandle _self;
        // Objet scriptable du module (NPPVpluginScriptableNPObject), lu au premier appel de la page.
        nint _scriptable;
        uint _nextTimer = 1000;
        NpObjectRef _windowObject;
        NpObjectRef _elementObject;

        public PluginInstance(PluginLibrary library, HostOptions options)
        {
            _library = library;
            _options = options;
            _npp = (NPP_t*)NpMemory.AllocZeroed((nuint)sizeof(NPP_t));
            _window = NpMemory.AllocZeroed((nuint)(OperatingSystem.IsWindows() ? sizeof(NPWindow) : sizeof(NPWindowUnix)));
            _self = GCHandle.Alloc(this);
            _npp->ndata = GCHandle.ToIntPtr(_self);
            // Cookies de la page (PommeBrowser) ou propres à l'hôte ; redirections suivies comme un navigateur.
            _http = new HttpClient(new BrowserHttpHandler(options.ShareCookies, _cookies));
            // En-têtes d'un chargement de module dans un navigateur : identité et langues de la page.
            _http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", options.HttpUserAgent ?? options.UserAgent);
            _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept", "*/*");
            if (options.AcceptLanguage != null)
                _http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", options.AcceptLanguage);

            var page = new PageObjects(options);
            _windowObject = page.Window;
            _elementObject = page.Element;
        }

        public static PluginInstance? Current { get; private set; }

        public static nint UserAgentPointer => _userAgent;

        public bool IsAlive { get; private set; }

        internal PluginLibrary Library => _library;

        internal NPP_t* Npp => _npp;

        internal HttpClient Http => _http;

        internal HostOptions Options => _options;

        /// <summary>Flux ouverts et minuteries du module (diagnostic : commande « stats »).</summary>
        internal (int Streams, int Timers) Usage => (_streams.Count, _timers.Count);

        public static void SetUserAgent(string userAgent) => _userAgent = NpMemory.Utf8(userAgent);

        public static PluginInstance? FromNpp(nint npp)
        {
            if (npp == 0)
                return null;
            nint data = ((NPP_t*)npp)->ndata;
            return data == 0 ? null : GCHandle.FromIntPtr(data).Target as PluginInstance;
        }

        nint Keep(string text)
        {
            nint pointer = NpMemory.Utf8(text);
            _strings.Add(pointer);
            return pointer;
        }

        // ---------------------------------------------------------------
        // Cycle de vie
        // ---------------------------------------------------------------

        /// <summary>NPP_New avec les attributs et paramètres de l'élément.</summary>
        public void Create()
        {
            List<KeyValuePair<string, string>> arguments = _options.PluginArguments();
            var names = (nint*)NpMemory.AllocZeroed((nuint)(arguments.Count * sizeof(nint)));
            var values = (nint*)NpMemory.AllocZeroed((nuint)(arguments.Count * sizeof(nint)));
            for (int i = 0; i < arguments.Count; i++)
            {
                names[i] = Keep(arguments[i].Key);
                values[i] = Keep(arguments[i].Value);
            }
            // Gardés avec l'instance : certains modules conservent ces pointeurs.
            _strings.Add((nint)names);
            _strings.Add((nint)values);

            Current = this;
            IsAlive = true;
            short error = _library.New(Keep(HostOptions.FlashMimeType), _npp, Np.ModeEmbed, (short)arguments.Count, names, values);
            if (error != Np.NoError)
            {
                IsAlive = false;
                throw new InvalidOperationException($"NPP_New a échoué ({error}).");
            }
        }

        /// <summary>
        /// Fenêtre donnée au module (il y crée la sienne), à chaque changement de taille. Sous
        /// Linux, <paramref name="info"/> décrit l'affichage X11 (NPSetWindowCallbackStruct).
        /// </summary>
        public void SetWindow(nint window, nint info, int width, int height)
        {
            if (!IsAlive)
                return;
            var clip = new NPRect { top = 0, left = 0, bottom = (ushort)Math.Min(height, ushort.MaxValue), right = (ushort)Math.Min(width, ushort.MaxValue) };
            if (OperatingSystem.IsWindows())
            {
                *(NPWindow*)_window = new NPWindow
                {
                    window = window, width = (uint)Math.Max(1, width), height = (uint)Math.Max(1, height),
                    clipRect = clip, type = Np.WindowTypeWindow
                };
            }
            else
            {
                *(NPWindowUnix*)_window = new NPWindowUnix
                {
                    window = window, width = (uint)Math.Max(1, width), height = (uint)Math.Max(1, height),
                    clipRect = clip, ws_info = info, type = Np.WindowTypeWindow
                };
            }
            short error = _library.SetWindow(_npp, _window);
            if (error != Np.NoError)
                HostChannel.Error($"NPP_SetWindow a échoué ({error}).");
        }

        /// <summary>Le contenu lui-même : premier flux, sans notification.</summary>
        public void StartSource() => RequestUrl(_options.Swf.AbsoluteUri, null, null, notify: false, 0);

        public void Destroy()
        {
            if (!IsAlive)
                return;
            foreach (PluginStream stream in _streams.ToList())
                stream.Close(Np.ReasonUserBreak);
            foreach (uint id in _timers.Keys.ToList())
                UnscheduleTimer(id);
            // Rendu avant NPP_Destroy, comme le fait un navigateur.
            if (_scriptable != 0)
            {
                NpObjects.Release(_scriptable);
                _scriptable = 0;
            }

            _library.Destroy(_npp);
            IsAlive = false;
            AnswerRedirects(0, false);
            NpObjects.Release(_windowObject.Pointer);
            NpObjects.Release(_elementObject.Pointer);
            _http.Dispose();
        }

        // ---------------------------------------------------------------
        // Adresses demandées par le module
        // ---------------------------------------------------------------

        /// <summary>
        /// NPN_GetURL(Notify) et NPN_PostURL(Notify). Sans cible : le contenu est lu et donné au
        /// module. Avec une cible (_blank, _self…) : PommeBrowser ouvre la page. « javascript: » :
        /// transmis à la page.
        /// </summary>
        public short RequestUrl(string? url, string? target, PostData? post, bool notify, nint notifyData)
        {
            if (!IsAlive)
                return Np.InvalidInstanceError;
            if (string.IsNullOrWhiteSpace(url))
                return Np.InvalidUrl;

            if (url.TrimStart().StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
            {
                string code = Uri.UnescapeDataString(url.TrimStart()["javascript:".Length..]);
                HostChannel.Trace("js:" + code, "Adresse javascript: demandée (" + (target ?? "sans cible") + ") : " + HostChannel.Excerpt(code));
                HostChannel.Send("script", ("code", code), ("target", target));
                if (notify)
                    NotifyLater(url, Np.ReasonDone, notifyData);
                return Np.NoError;
            }

            if (!Uri.TryCreate(_options.Page, url, out Uri? uri))
                return Np.InvalidUrl;

            if (!string.IsNullOrEmpty(target))
            {
                HostChannel.Trace("nav:" + target + uri.GetLeftPart(UriPartial.Path),
                    $"Page demandée par le contenu (cible {target}) : {uri.GetLeftPart(UriPartial.Path)}");
                HostChannel.Send("navigate", ("url", uri.AbsoluteUri), ("target", target), ("popups", _popups.Count == 0 || _popups.Peek()));
                if (notify)
                    NotifyLater(url, Np.ReasonDone, notifyData);
                return Np.NoError;
            }

            if (!IsAllowed(uri))
            {
                HostChannel.Log("Adresse refusée : " + uri.Scheme + ":");
                if (notify)
                    NotifyLater(url, Np.ReasonNetworkError, notifyData);
                return Np.NoError;
            }

            HostChannel.Trace("url:" + (post != null ? "POST " : "GET ") + uri.GetLeftPart(UriPartial.Path),
                "Chargement demandé : " + (post != null ? "POST " : "GET ") + uri.GetLeftPart(UriPartial.Path));
            if (post != null)
                HostChannel.Trace("post:" + uri.GetLeftPart(UriPartial.Path), ResponseShape.DescribePost(uri, post.Body, post.Headers.Select(header => header.Key), post.Dropped));
            var stream = new PluginStream(this, url, uri, post, notify, notifyData);
            _streams.Add(stream);
            stream.Start();
            return Np.NoError;
        }

        /// <summary>Fichiers locaux : seulement pour un contenu lui-même ouvert depuis le disque.</summary>
        bool IsAllowed(Uri uri) => uri.Scheme switch
        {
            "http" or "https" or "data" => true,
            "file" => _options.Page.IsFile || _options.Swf.IsFile,
            _ => false
        };

        void NotifyLater(string url, short reason, nint notifyData)
            => UiThread.Post(() =>
            {
                if (!IsAlive)
                    return;
                nint pointer = NpMemory.Utf8(url);
                _library.UrlNotify(_npp, pointer, reason, notifyData);
                NpMemory.Free(pointer);
            });

        internal void Forget(PluginStream stream) => _streams.Remove(stream);

        /// <summary>NPN_DestroyStream : le module abandonne un flux (traité après son appel).</summary>
        public short CancelStream(nint stream, short reason)
        {
            PluginStream? found = _streams.FirstOrDefault(s => s.Native == (NPStream*)stream);
            if (found == null)
                return Np.InvalidParam;
            found.Stop();
            UiThread.Post(() => found.Close(reason));
            return Np.NoError;
        }

        // ---------------------------------------------------------------
        // Valeurs échangées avec le module
        // ---------------------------------------------------------------

        /// <summary>NPN_GetValue : ce que le module demande au navigateur.</summary>
        public static short GetBrowserValue(PluginInstance? instance, NPNVariable variable, nint value)
        {
            if (value == 0)
                return Np.InvalidParam;

            switch (variable)
            {
                case NPNVariable.NetscapeWindow:
                    *(nint*)value = UiThread.Display.NetscapeWindow;
                    return Np.NoError;
                // Linux : affichage X11, GTK 2 et XEmbed, comme Firefox (le module branche sa fenêtre dans la prise).
                case NPNVariable.XDisplay when !OperatingSystem.IsWindows():
                    *(nint*)value = UiThread.Display.XDisplay;
                    return Np.NoError;
                case NPNVariable.Toolkit when !OperatingSystem.IsWindows():
                    *(int*)value = Np.ToolkitGtk2;
                    return Np.NoError;
                case NPNVariable.SupportsXEmbedBool when !OperatingSystem.IsWindows():
                    *(byte*)value = 1;
                    return Np.NoError;
                case NPNVariable.JavascriptEnabledBool:
                    *(byte*)value = 1;
                    return Np.NoError;
                case NPNVariable.AsdEnabledBool:
                case NPNVariable.IsOfflineBool:
                case NPNVariable.SupportsWindowless:
                case NPNVariable.SupportsAdvancedKeyHandling:
                case NPNVariable.SupportsAsyncBitmapSurfaceBool:
                case NPNVariable.SupportsAsyncWindowsDXGISurfaceBool:
                case NPNVariable.MuteAudioBool:
                    *(byte*)value = 0;
                    return Np.NoError;
                case NPNVariable.PrivateModeBool:
                    *(byte*)value = instance?._options.IsPrivate == true ? (byte)1 : (byte)0;
                    return Np.NoError;
                case NPNVariable.CSSZoomFactor:
                    *(double*)value = 1.0;
                    return Np.NoError;
                case NPNVariable.ContentsScaleFactor:
                    // Comme Basilisk sous Windows. L'hôte n'est pas adapté aux DPI : Windows met
                    // ses fenêtres à l'échelle, le contenu reste à l'échelle 1.
                    *(double*)value = 1.0;
                    return Np.NoError;
                case NPNVariable.WindowNPObject when instance != null:
                    *(nint*)value = NpObjects.Retain(instance._windowObject.Pointer);
                    return Np.NoError;
                case NPNVariable.PluginElementNPObject when instance != null:
                    *(nint*)value = NpObjects.Retain(instance._elementObject.Pointer);
                    return Np.NoError;
                case NPNVariable.DocumentOrigin when instance != null:
                    // Libérée par le module (NPN_MemFree).
                    *(nint*)value = NpMemory.Utf8(Origin(instance._options.Page));
                    return Np.NoError;
                default:
                    HostChannel.Trace("value:" + (int)variable, $"NPN_GetValue({variable}) : valeur non fournie.");
                    return Np.GenericError;
            }
        }

        static string Origin(Uri page)
            => page.IsFile ? "file://" : page.GetLeftPart(UriPartial.Authority).ToLowerInvariant();

        /// <summary>NPN_SetValue : réglages annoncés par le module.</summary>
        public short SetPluginValue(NPPVariable variable, nint value)
        {
            switch (variable)
            {
                case NPPVariable.PluginWindowBool:
                    if (value == 0)
                        HostChannel.Log("Le module demande le mode sans fenêtre (non pris en charge : mode fenêtré gardé).");
                    return Np.NoError;
                case NPPVariable.PluginIsPlayingAudio:
                    HostChannel.Send("audio", ("playing", value != 0));
                    return Np.NoError;
                default:
                    return Np.NoError;
            }
        }

        /// <summary>NPN_GetValueForURL : cookies envoyés à une adresse, mandataire.</summary>
        public short GetValueForUrl(NPNURLVariable variable, string? url, nint* value, uint* length)
        {
            if (value == null || length == null || url == null || !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                return Np.InvalidParam;

            string? text = variable switch
            {
                // Ceux qu'un script de la page verrait (sans HttpOnly), comme dans un navigateur.
                NPNURLVariable.Cookie when uri.Scheme is "http" or "https" =>
                    _options.ShareCookies ? HostChannel.PageCookies(uri) : _cookies.GetCookieHeader(uri),
                NPNURLVariable.Proxy => "DIRECT",
                _ => null
            };
            if (text == null)
                return Np.GenericError;
            *value = NpMemory.Utf8(text, out uint count);
            *length = count;
            return Np.NoError;
        }

        /// <summary>NPN_SetValueForURL : cookie posé par le module, comme par un script de la page.</summary>
        public short SetValueForUrl(NPNURLVariable variable, string? url, string? value)
        {
            if (variable != NPNURLVariable.Cookie || string.IsNullOrEmpty(value) || url == null ||
                !Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) || uri.Scheme is not ("http" or "https"))
            {
                return Np.InvalidParam;
            }
            if (_options.ShareCookies)
            {
                HostChannel.SetPageCookie(uri, value, fromHttp: false);
                return Np.NoError;
            }
            try
            {
                _cookies.SetCookies(uri, value);
                return Np.NoError;
            }
            catch (CookieException)
            {
                return Np.InvalidParam;
            }
        }

        /// <summary>
        /// Méthodes de Flash que la page peut appeler sur l'élément du contenu, comme dans un
        /// navigateur (API JavaScript de Flash Player) : avancement du chargement, variables,
        /// lecture et images. Les fonctions déclarées par ExternalInterface.addCallback passent,
        /// elles, par CallFunction.
        /// </summary>
        public static readonly IReadOnlySet<string> PageMethods = new HashSet<string>(StringComparer.Ordinal)
        {
            "PercentLoaded", "GetVariable", "SetVariable", "IsPlaying", "Play", "StopPlay", "Rewind",
            "Back", "Forward", "GotoFrame", "CurrentFrame", "TotalFrames", "LoadMovie", "Zoom", "Pan",
            "SetZoomRect", "TCallFrame", "TCallLabel", "TCurrentFrame", "TCurrentLabel", "TGetProperty",
            "TGetPropertyAsNumber", "TGotoFrame", "TGotoLabel", "TPlay", "TSetProperty", "TStopPlay"
        };

        /// <summary>Arguments d'une méthode : au plus 8, nombres, textes, booléens ou null.</summary>
        const int MaxMethodArguments = 8;

        /// <summary>
        /// Appel de la page vers le contenu (fonction déclarée par ExternalInterface.addCallback) :
        /// la page appelle CallFunction avec la requête XML (&lt;invoke name="…"&gt;…) sur l'objet
        /// scriptable du module, qui répond par du code JavaScript à évaluer dans la page. Une
        /// requête JSON ({"method":"PercentLoaded","args":[]}) appelle une méthode de Flash
        /// (<see cref="PageMethods"/>) : la réponse est sa valeur, en JSON.
        /// </summary>
        public (bool Ok, string? Value) CallFromPage(string request)
        {
            if (!IsAlive || Scriptable() is not { } scriptable)
                return (false, null);
            if (request.StartsWith('{'))
                return CallMethodFromPage(scriptable, request);

            HostChannel.Trace("call:" + HostChannel.Excerpt(request, 80), "Appel de la page vers le contenu : " + HostChannel.Excerpt(request, 160));
            NPVariant argument;
            NpVariants.Write(&argument, request);
            NPVariant result = default;
            try
            {
                if (!NpObjects.Invoke(scriptable, NpIdentifiers.FromString("CallFunction"), &argument, 1, &result))
                    return (false, null);
                object? value = NpVariants.Read(&result);
                return (true, value switch
                {
                    string text => text,
                    bool flag => flag ? "true" : "false",
                    int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    double number => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    _ => null
                });
            }
            finally
            {
                NpVariants.Release(&argument);
                NpVariants.Release(&result);
            }
        }

        /// <summary>Objet scriptable du module (NPPVpluginScriptableNPObject), demandé une fois.</summary>
        nint? Scriptable()
        {
            if (_scriptable == 0)
            {
                nint scriptable = 0;
                if (_library.GetValue(_npp, NPPVariable.PluginScriptableNPObject, &scriptable) != Np.NoError || scriptable == 0)
                {
                    HostChannel.Trace("call:none", "Le contenu n'offre pas d'objet scriptable : appels de la page refusés.");
                    return null;
                }
                _scriptable = scriptable;
            }
            return _scriptable;
        }

        /// <summary>Méthode de Flash appelée par la page : {"method":"…","args":[…]} ; valeur rendue en JSON.</summary>
        (bool Ok, string? Value) CallMethodFromPage(nint scriptable, string request)
        {
            string? method = null;
            var args = new List<object?>();
            try
            {
                using var document = System.Text.Json.JsonDocument.Parse(request);
                System.Text.Json.JsonElement root = document.RootElement;
                if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                    !root.TryGetProperty("method", out System.Text.Json.JsonElement name) || name.ValueKind != System.Text.Json.JsonValueKind.String)
                    return (false, null);
                method = name.GetString();
                if (root.TryGetProperty("args", out System.Text.Json.JsonElement list))
                {
                    if (list.ValueKind != System.Text.Json.JsonValueKind.Array || list.GetArrayLength() > MaxMethodArguments)
                        return (false, null);
                    foreach (System.Text.Json.JsonElement item in list.EnumerateArray())
                    {
                        switch (item.ValueKind)
                        {
                            case System.Text.Json.JsonValueKind.String:
                                args.Add(item.GetString());
                                break;
                            case System.Text.Json.JsonValueKind.Number:
                                args.Add(item.TryGetInt32(out int whole) ? whole : item.GetDouble());
                                break;
                            case System.Text.Json.JsonValueKind.True:
                            case System.Text.Json.JsonValueKind.False:
                                args.Add(item.GetBoolean());
                                break;
                            case System.Text.Json.JsonValueKind.Null:
                                args.Add(NpVariants.JsNull);
                                break;
                            default:
                                return (false, null);
                        }
                    }
                }
            }
            catch (System.Text.Json.JsonException)
            {
                return (false, null);
            }
            if (method == null || !PageMethods.Contains(method))
            {
                HostChannel.Trace("method:" + HostChannel.Excerpt(method, 40), "Méthode de Flash inconnue demandée par la page : " + HostChannel.Excerpt(method, 40));
                return (false, null);
            }

            HostChannel.Trace("method:" + method, "Méthode de Flash appelée par la page : " + method + "()");
            NPVariant* arguments = stackalloc NPVariant[Math.Max(1, args.Count)];
            for (int i = 0; i < args.Count; i++)
                NpVariants.Write(&arguments[i], args[i]);
            NPVariant result = default;
            try
            {
                if (!NpObjects.Invoke(scriptable, NpIdentifiers.FromString(method), arguments, (uint)args.Count, &result))
                    return (false, null);
                object? value = NpVariants.Read(&result);
                return (true, value switch
                {
                    string text => "\"" + System.Text.Json.JsonEncodedText.Encode(text).ToString() + "\"",
                    bool flag => flag ? "true" : "false",
                    int number => number.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    double number when double.IsFinite(number) => number.ToString("R", System.Globalization.CultureInfo.InvariantCulture),
                    _ => "null"
                });
            }
            finally
            {
                for (int i = 0; i < args.Count; i++)
                    NpVariants.Release(&arguments[i]);
                NpVariants.Release(&result);
            }
        }

        /// <summary>NPP_URLRedirectNotify, sur le fil du module (voir PluginInstance.Redirects.cs).</summary>
        void NotifyRedirect(Uri next, int status, nint notifyData)
        {
            nint url = NpMemory.Utf8(next.AbsoluteUri);
            try
            {
                _library.UrlRedirectNotify(_npp, url, status, notifyData);
            }
            finally
            {
                NpMemory.Free(url);
            }
        }

        public void PushPopups(bool enabled) => _popups.Push(enabled);

        public void PopPopups()
        {
            if (_popups.Count > 0)
                _popups.Pop();
        }

        // ---------------------------------------------------------------
        // Scripts
        // ---------------------------------------------------------------

        /// <summary>Attente maximale d'un script de la page (une boîte alert() le bloque jusqu'à sa fermeture).</summary>
        static readonly TimeSpan ScriptTimeout = TimeSpan.FromSeconds(20);

        [GeneratedRegex("""^\s*(?:(?:window|top|self|parent)\s*\.\s*)*(?:document\s*\.\s*)?location(?:\s*\.\s*href)?\s*(?:\+\s*(["'])(?<suffix>.*)\1)?\s*;?\s*$""", RegexOptions.CultureInvariant)]
        private static partial Regex LocationScript();

        /// <summary>
        /// NPN_Evaluate. Le module demande l'adresse de la page (« top.location + … ») pour ses
        /// règles de sécurité : l'hôte répond lui-même. Le reste (ExternalInterface.call…) est
        /// exécuté dans la page par PommeBrowser, dont la réponse est attendue.
        /// </summary>
        public bool Evaluate(string code, out object? value)
        {
            Match location = LocationScript().Match(code);
            if (location.Success)
            {
                value = _options.Page.AbsoluteUri + location.Groups["suffix"].Value;
                return true;
            }

            HostChannel.Trace("eval:" + code, "Script de la page demandé (NPN_Evaluate) : " + HostChannel.Excerpt(code));
            return HostChannel.RunInPage(code, ScriptTimeout, out value);
        }

        // ---------------------------------------------------------------
        // Minuteries du module
        // ---------------------------------------------------------------

        public uint ScheduleTimer(uint interval, bool repeat, nint function)
        {
            if (function == 0)
                return 0;
            uint id = _nextTimer++;
            _timers[id] = (function, repeat);
            UiThread.Display.StartTimer(id, interval);
            return id;
        }

        public void UnscheduleTimer(uint id)
        {
            if (_timers.Remove(id))
                UiThread.Display.StopTimer(id);
        }

        /// <summary>Échéance d'une minuterie du module.</summary>
        public void OnTimer(uint id)
        {
            if (!IsAlive || !_timers.TryGetValue(id, out (nint Function, bool Repeat) timer))
                return;
            if (!timer.Repeat)
                UnscheduleTimer(id);
            ((delegate* unmanaged[Cdecl]<NPP_t*, uint, void>)timer.Function)(_npp, id);
        }
    }
}
