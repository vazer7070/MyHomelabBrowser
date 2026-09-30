using System;
using System.IO;
using System.Text;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using PommeBrowser.Linux.Core;

namespace PommeBrowser.Linux.Web
{
    /// <summary>
    /// Lecture des contenus Flash avec Ruffle. Les fichiers de Ruffle (vérifiés par SHA-256 à la
    /// compilation) sont servis par le schéma pomme-ruffle:// : aucun CDN, aucun script tiers.
    ///
    /// Un petit script, exécuté dans un monde isolé (invisible pour la page), cherche les
    /// contenus Flash ; Ruffle n'est chargé que sur les pages qui en contiennent.
    /// </summary>
    sealed class RuffleSupport
    {
        public const string Scheme = "pomme-ruffle";
        public const string World = "pommebrowser";
        public const string MessageHandler = "pommeRuffle";
        const string BaseUrl = Scheme + "://ruffle/";

        // Réglages du lecteur : liens ouverts après confirmation, pas d'accès aux scripts de la page.
        const string ConfigScript = """
            window.RufflePlayer = window.RufflePlayer || {};
            window.RufflePlayer.config = Object.assign({
                publicPath: "pomme-ruffle://ruffle/",
                autoplay: "auto",
                unmuteOverlay: "visible",
                letterbox: "on",
                openUrlMode: "confirm",
                allowScriptAccess: false,
                warnOnUnsupportedContent: false,
                showSwfDownload: false,
                logLevel: "error"
            }, window.RufflePlayer.config || {});
            """;

        const string ProbeScript = """
            (() => {
              if (window.__pommeRuffleProbe) return;
              window.__pommeRuffleProbe = true;
              const FLASH_TYPES = ['application/x-shockwave-flash', 'application/futuresplash', 'application/vnd.adobe.flash.movie'];
              const CLSID = 'clsid:d27cdb6e-ae6d-11cf-96b8-444553540000';
              const post = (status) => { try { window.webkit.messageHandlers.pommeRuffle.postMessage(status); } catch (e) { } };
              const isSwf = (value) => {
                try { const path = new URL(value, document.baseURI).pathname.toLowerCase(); return path.endsWith('.swf') || path.endsWith('.spl'); }
                catch (e) { return false; }
              };
              const isFlash = (el) => {
                const type = (el.getAttribute('type') || '').toLowerCase();
                if (FLASH_TYPES.includes(type)) return true;
                if (el.localName === 'object') {
                  if ((el.getAttribute('classid') || '').toLowerCase() === CLSID) return true;
                  if (isSwf(el.getAttribute('data') || '')) return true;
                  const movie = el.querySelector('param[name="movie" i], param[name="src" i]');
                  return !!movie && isSwf(movie.getAttribute('value') || '');
                }
                return el.localName === 'embed' && isSwf(el.getAttribute('src') || '');
              };
              const contains = (root) => {
                if (!root || root.nodeType !== 1) return false;
                if ((root.localName === 'object' || root.localName === 'embed') && isFlash(root)) return true;
                for (const el of root.querySelectorAll('object, embed')) if (isFlash(el)) return true;
                return false;
              };
              const load = (src) => new Promise((resolve, reject) => {
                const script = document.createElement('script');
                script.src = src;
                script.onload = resolve;
                script.onerror = reject;
                (document.head || document.documentElement).appendChild(script);
              });
              let observer = null;
              const inject = () => {
                if (observer) observer.disconnect();
                if (window.__pommeRuffleInjected) return;
                window.__pommeRuffleInjected = true;
                post('detected');
                load('pomme-ruffle://ruffle/pomme-config.js')
                  .then(() => load('pomme-ruffle://ruffle/ruffle.js'))
                  .then(() => post('loaded'), () => post('blocked'));
              };
              if (contains(document.documentElement)) { inject(); return; }
              // Les contenus ajoutés après coup (SWFObject…) sont aussi détectés, pendant 30 secondes.
              observer = new MutationObserver((mutations) => {
                for (const m of mutations) for (const node of m.addedNodes) if (contains(node)) { inject(); return; }
              });
              observer.observe(document.documentElement, { childList: true, subtree: true });
              setTimeout(() => observer.disconnect(), 30000);
            })();
            """;

        WebKit.UserScript? _probe;
        WebKit.UserScript? _plugin;

        public static string AssetDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "Ruffle");

        public bool IsAvailable => File.Exists(Path.Combine(AssetDirectory, "ruffle.js"));

        public string? InstalledVersion
        {
            get
            {
                try
                {
                    string file = Path.Combine(AssetDirectory, "VERSION.txt");
                    return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
                }
                catch (IOException)
                {
                    return null;
                }
            }
        }

        public void Register(WebKit.WebContext context)
        {
            WebKit.SecurityManager security = context.GetSecurityManager();
            // Sécurisé : chargeable depuis une page HTTPS ; CORS : Ruffle y télécharge son module WebAssembly.
            security.RegisterUriSchemeAsSecure(Scheme);
            security.RegisterUriSchemeAsCorsEnabled(Scheme);
            context.RegisterUriScheme(Scheme, Serve);
        }

        /// <summary>Ajoute la détection de Flash aux scripts d'un onglet.</summary>
        public void Attach(WebKit.UserContentManager manager)
        {
            if (!IsAvailable)
                return;

            _probe ??= WebKit.UserScript.NewForWorld(
                ProbeScript,
                WebKit.UserContentInjectedFrames.AllFrames,
                WebKit.UserScriptInjectionTime.End,
                World,
                null,
                null);
            // Flash annoncé dans le monde de la page, avant ses scripts : beaucoup de sites
            // n'ajoutent leur contenu Flash qu'à cette condition (voir RufflePluginScript).
            _plugin ??= WebKit.UserScript.New(
                RufflePluginScript.Source,
                WebKit.UserContentInjectedFrames.AllFrames,
                WebKit.UserScriptInjectionTime.Start,
                null,
                null);

            manager.AddScript(_plugin);
            manager.AddScript(_probe);
            manager.RegisterScriptMessageHandler(MessageHandler, World);
        }

        public void Detach(WebKit.UserContentManager manager)
        {
            if (_probe == null)
                return;

            if (_plugin != null)
                manager.RemoveScript(_plugin);
            manager.RemoveScript(_probe);
            manager.UnregisterScriptMessageHandler(MessageHandler, World);
        }

        void Serve(WebKit.URISchemeRequest request)
        {
            string name = (request.GetPath() ?? string.Empty).TrimStart('/');
            byte[]? data = null;
            string type = "application/octet-stream";

            if (name == "pomme-config.js")
            {
                data = Encoding.UTF8.GetBytes(ConfigScript);
                type = "text/javascript";
            }
            else if (RuffleAssets.IsAssetName(name))
            {
                string path = Path.Combine(AssetDirectory, name);
                try
                {
                    if (File.Exists(path))
                        data = File.ReadAllBytes(path);
                }
                catch (IOException ex)
                {
                    RuntimeLogBuffer.Append("[Ruffle] " + ex.Message);
                }

                type = name.EndsWith(".wasm", StringComparison.Ordinal) ? "application/wasm" : "text/javascript";
            }

            if (data == null)
            {
                request.FinishError(GLib.Error.NewLiteral(Gio.Functions.IoErrorQuark(), (int)Gio.IOErrorEnum.NotFound, name));
                return;
            }

            var response = WebKit.URISchemeResponse.New(Gio.MemoryInputStream.NewFromBytes(GLib.Bytes.New(data)), data.Length);
            response.SetStatus(200, null);
            response.SetContentType(type);

            var headers = Soup.MessageHeaders.New(Soup.MessageHeadersType.Response);
            headers.Append("Content-Type", type);
            headers.Append("Access-Control-Allow-Origin", "*");
            headers.Append("X-Content-Type-Options", "nosniff");
            headers.Append("Cache-Control", "max-age=604800, immutable");
            response.SetHttpHeaders(headers);

            request.FinishWithResponse(response);
        }

    }
}
