using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using PommeBrowser.Linux.Core;

namespace PommeBrowser.Engine
{
    /// <summary>
    /// Lecture des contenus Flash avec Ruffle. Les fichiers de Ruffle (vérifiés par SHA-256 à la
    /// compilation) sont servis par PommeBrowser lui-même : aucun CDN, aucun script tiers.
    /// Un petit script, exécuté dans le monde isolé de PommeBrowser, cherche les contenus Flash ;
    /// Ruffle n'est chargé que sur les pages qui en contiennent.
    /// </summary>
    public static class RuffleContent
    {
        public const string MessageHandler = "pommeRuffle";

        /// <summary>
        /// Message accepté d'un cadre (iframe) de la page : ceux de Ruffle sur le contenu Flash
        /// (description, lecture, échec), pas la position de suivi (document principal seulement)
        /// ni les autres canaux.
        /// </summary>
        public static bool IsFrameMessage(string channel, string body)
            => channel == MessageHandler && !body.StartsWith(RectPrefix, StringComparison.Ordinal);
        public const string ScriptId = "ruffle-probe";
        public const string PluginScriptId = "ruffle-plugin";

        public static string AssetDirectory => Path.Combine(AppContext.BaseDirectory, "Assets", "Ruffle");

        public static bool IsAvailable => File.Exists(Path.Combine(AssetDirectory, "ruffle.js"));

        public static string? InstalledVersion
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

        /// <summary>Réglages du lecteur : liens ouverts après confirmation, pas d'accès aux scripts de la page.</summary>
        public static string ConfigScript(string baseUrl) => """
            window.RufflePlayer = window.RufflePlayer || {};
            window.RufflePlayer.config = Object.assign({
                publicPath: "__BASE__",
                autoplay: "auto",
                unmuteOverlay: "visible",
                letterbox: "on",
                openUrlMode: "confirm",
                allowScriptAccess: false,
                warnOnUnsupportedContent: false,
                showSwfDownload: false,
                logLevel: "error"
            }, window.RufflePlayer.config || {});
            """.Replace("__BASE__", baseUrl, StringComparison.Ordinal);

        /// <summary>Lecteur Flash annoncé à la page avant ses scripts (voir RufflePluginScript).</summary>
        public const string PluginScript = RufflePluginScript.Source;

        /// <summary>
        /// Détection des contenus Flash, puis suivi des lecteurs. <paramref name="post"/> : expression
        /// qui envoie « status » à PommeBrowser (propre au moteur) : detected, loaded, blocked
        /// (Ruffle refusé par la page), playing (un contenu a démarré), failed (Ruffle s'est arrêté
        /// sur une erreur), et « content: » suivi de la description du contenu principal (FlashContent).
        /// </summary>
        public static string ProbeScript(string baseUrl, string post) => """
            (() => {
              if (window.__pommeRuffleProbe) return;
              window.__pommeRuffleProbe = true;
              const FLASH_TYPES = ['application/x-shockwave-flash', 'application/futuresplash', 'application/vnd.adobe.flash.movie'];
              const CLSID = 'clsid:d27cdb6e-ae6d-11cf-96b8-444553540000';
              const PLAYERS = 'ruffle-object, ruffle-embed, ruffle-player';
              const post = (status) => { try { __POST__; } catch (e) { } };
              const isSwf = (value) => {
                try { const path = new URL(value, document.baseURI).pathname.toLowerCase(); return path.endsWith('.swf') || path.endsWith('.spl'); }
                catch (e) { return false; }
              };
              // Élément d'origine, ou celui de Ruffle qui l'a remplacé (attributs et paramètres recopiés).
              const kind = (el) => el.localName.startsWith('ruffle-') ? el.localName.slice(7) : el.localName;
              const isFlash = (el) => {
                const type = (el.getAttribute('type') || '').toLowerCase();
                if (FLASH_TYPES.includes(type)) return true;
                if (kind(el) === 'object') {
                  if ((el.getAttribute('classid') || '').toLowerCase() === CLSID) return true;
                  if (isSwf(el.getAttribute('data') || '')) return true;
                  const movie = el.querySelector('param[name="movie" i], param[name="src" i]');
                  return !!movie && isSwf(movie.getAttribute('value') || '');
                }
                return kind(el) === 'embed' && isSwf(el.getAttribute('src') || '');
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
              // Lecteurs Ruffle : « playing » dès qu'un contenu démarre, « failed » s'il s'arrête sur
              // son écran d'erreur (#panic, dans son shadow root ouvert), à tout moment de la lecture.
              const watch = () => {
                const seen = new WeakSet();
                let playing = false, failed = false;
                const fail = () => { if (!failed) { failed = true; post('failed'); } };
                const attach = (player) => {
                  if (seen.has(player)) return;
                  seen.add(player);
                  report();
                  player.addEventListener('loadedmetadata', () => { if (!playing) { playing = true; post('playing'); } });
                  const box = player.shadowRoot && player.shadowRoot.getElementById('container');
                  if (!box) return;
                  if (box.querySelector('#panic')) { fail(); return; }
                  new MutationObserver(() => { if (box.querySelector('#panic')) fail(); }).observe(box, { childList: true });
                };
                const scan = (node) => {
                  if (!node || node.nodeType !== 1) return;
                  if (node.localName.startsWith('ruffle-')) { attach(node); return; }
                  if (node.firstElementChild) for (const el of node.querySelectorAll(PLAYERS)) attach(el);
                };
                scan(document.documentElement);
                new MutationObserver((mutations) => {
                  for (const m of mutations) for (const node of m.addedNodes) scan(node);
                }).observe(document.documentElement, { childList: true, subtree: true });
              };
              // Contenu principal : décrit pour le moteur Flash intégré. Le plus grand, mais un format
              // publicitaire courant passe après tout autre contenu, et un contenu déclaré minuscule
              // (pixel de suivi, lecteur audio invisible : width="1") ne compte pas.
              const AD_SIZES = new Set(__AD_SIZES__);
              const describe = () => {
                let best = null;
                for (const el of document.querySelectorAll('object, embed, ruffle-object, ruffle-embed')) {
                  if (!isFlash(el)) continue;
                  const parent = el.parentElement;
                  if (kind(el) === 'embed' && parent && kind(parent) === 'object' && isFlash(parent)) continue;
                  const params = {};
                  if (kind(el) === 'object') {
                    for (const p of el.querySelectorAll(':scope > param')) {
                      const name = (p.getAttribute('name') || '').toLowerCase();
                      if (name) params[name] = p.getAttribute('value') || '';
                    }
                  } else {
                    for (const a of el.attributes) params[a.name.toLowerCase()] = a.value;
                  }
                  const source = kind(el) === 'object' ? (el.getAttribute('data') || params.movie || params.src || '') : (el.getAttribute('src') || '');
                  if (!source) continue;
                  let swf;
                  try { swf = new URL(source, document.baseURI).href; } catch (e) { continue; }
                  // Taille déclarée en pixels, sinon celle affichée : 0 pour un contenu encore caché ou
                  // en pourcentage d'un conteneur sans taille, qui compte quand même (le plus petit).
                  const declared = (name) => {
                    const value = (el.getAttribute(name) || '').trim();
                    return /^\d+(px)?$/i.test(value) ? parseInt(value, 10) : null;
                  };
                  const declaredWidth = declared('width'), declaredHeight = declared('height');
                  if ((declaredWidth !== null && declaredWidth < 16) || (declaredHeight !== null && declaredHeight < 16)) continue;
                  const rect = el.getBoundingClientRect();
                  const width = declaredWidth ?? Math.round(rect.width);
                  const height = declaredHeight ?? Math.round(rect.height);
                  const ad = AD_SIZES.has(width + 'x' + height);
                  if (!best || (ad !== best.ad ? !ad : width * height > best.width * best.height)) best = { el, width, height, ad, swf, params };
                }
                if (!best) return null;
                const el = best.el;
                // Repère gardé par Ruffle quand il remplace l'élément : le moteur intégré le retrouve.
                el.setAttribute('data-pomme-flash', '');
                const params = best.params;
                const flashvars = params.flashvars || el.getAttribute('flashvars') || null;
                for (const name of ['movie', 'src', 'data', 'flashvars', 'width', 'height', 'type', 'id', 'name', 'classid', 'codebase', 'pluginspage', 'style', 'class']) delete params[name];
                return { swf: best.swf, page: location.href, flashvars, width: best.width, height: best.height, id: el.id || el.getAttribute('name') || null, params };
              };
              // Contenu décrit au début, puis de nouveau à chaque lecteur Ruffle créé : un contenu
              // ajouté plus tard, ou qui n'avait pas encore de taille, est pris en compte.
              let reported = '';
              const report = () => {
                try {
                  const content = describe();
                  if (!content) return;
                  const json = JSON.stringify(content);
                  if (json !== reported) { reported = json; post('__CONTENT__' + json); }
                } catch (e) { }
              };
              let observer = null;
              const inject = () => {
                if (observer) observer.disconnect();
                if (window.__pommeRuffleInjected) return;
                window.__pommeRuffleInjected = true;
                report();
                post('detected');
                load('__BASE__pomme-config.js')
                  .then(() => load('__BASE__ruffle.js'))
                  .then(() => { post('loaded'); watch(); }, () => post('blocked'));
              };
              if (contains(document.documentElement)) { inject(); return; }
              // Les contenus ajoutés après coup (SWFObject…) sont aussi détectés, pendant 30 secondes.
              observer = new MutationObserver((mutations) => {
                for (const m of mutations) for (const node of m.addedNodes) if (contains(node)) { inject(); return; }
              });
              observer.observe(document.documentElement, { childList: true, subtree: true });
              setTimeout(() => observer.disconnect(), 30000);
            })();
            """.Replace("__BASE__", baseUrl, StringComparison.Ordinal).Replace("__POST__", post, StringComparison.Ordinal)
               .Replace("__CONTENT__", FlashContent.MessagePrefix, StringComparison.Ordinal)
               .Replace("__AD_SIZES__", JsonSerializer.Serialize(FlashContent.AdSizes.Select(s => s.Width + "x" + s.Height)), StringComparison.Ordinal);

        /// <summary>Préfixe des messages de position du contenu lu par le moteur intégré.</summary>
        public const string RectPrefix = "rect:";

        /// <summary>
        /// Moteur Flash intégré dans la page : le contenu repéré (data-pomme-flash, posé par le script
        /// de détection) est remplacé par un emplacement vide de même taille, ce qui arrête Ruffle, et
        /// sa position dans la fenêtre est envoyée à PommeBrowser à chaque changement (défilement,
        /// taille, mise en page) : « rect: » suivi de x, y, largeur, hauteur (pixels CSS), du rapport
        /// pixels CSS / pixels de l'écran et de sa visibilité ; « rect:null » s'il est introuvable.
        /// Relancé, il reprend le même emplacement.
        /// </summary>
        public static string FlashTrackerScript(string post) => """
            (() => {
              const post = (status) => { try { __POST__; } catch (e) { } };
              if (window.__pommeFlashSend) { window.__pommeFlashSend(true); return; }
              const target = document.querySelector('[data-pomme-flash]');
              if (!target) { post('__RECT__null'); return; }
              let hole = target;
              if (!target.hasAttribute('data-pomme-flash-hole')) {
                const box = target.getBoundingClientRect();
                const style = getComputedStyle(target);
                const length = (name, measured) => {
                  const value = (target.getAttribute(name) || '').trim();
                  if (/^\d+(px)?$/i.test(value)) return parseInt(value, 10) + 'px';
                  if (/^\d+(\.\d+)?%$/.test(value)) return value;
                  return Math.round(measured) + 'px';
                };
                hole = document.createElement('div');
                for (const name of ['id', 'class', 'style']) {
                  if (target.hasAttribute(name)) hole.setAttribute(name, target.getAttribute(name));
                }
                hole.setAttribute('data-pomme-flash', '');
                hole.setAttribute('data-pomme-flash-hole', '');
                hole.style.width = length('width', box.width);
                hole.style.height = length('height', box.height);
                hole.style.display = style.display === 'inline' ? 'inline-block' : style.display;
                hole.style.background = '#000';
                // Fonctions déclarées par le contenu (ExternalInterface.addCallback) : gardées.
                if (window.__pommeFlashEquip) {
                  window.__pommeFlashEquip(target);
                  window.__pommeFlashEquip(hole);
                  for (const name of Object.keys(target)) {
                    if (typeof target[name] === 'function' && !(name in hole)) hole[name] = target[name];
                  }
                }
                target.replaceWith(hole);
              }
              let last = '';
              const send = (force) => {
                const box = hole.getBoundingClientRect();
                const visible = hole.isConnected && box.width > 0 && box.height > 0 && getComputedStyle(hole).visibility !== 'hidden';
                const message = JSON.stringify({ x: box.left, y: box.top, w: box.width, h: box.height, dpr: window.devicePixelRatio || 1, visible });
                if (force || message !== last) { last = message; post('__RECT__' + message); }
              };
              let queued = false;
              const schedule = () => {
                if (queued) return;
                queued = true;
                requestAnimationFrame(() => { queued = false; send(false); });
              };
              window.__pommeFlashSend = send;
              addEventListener('scroll', schedule, { capture: true, passive: true });
              addEventListener('resize', schedule);
              new ResizeObserver(schedule).observe(hole);
              new MutationObserver(schedule).observe(document.documentElement, { attributes: true, childList: true, subtree: true });
              // Animations et transformations CSS ne se signalent pas : vérification régulière.
              setInterval(() => send(false), 400);
              send(true);
            })();
            """.Replace("__POST__", post, StringComparison.Ordinal).Replace("__RECT__", RectPrefix, StringComparison.Ordinal);

        /// <summary>Nom de l'objet de PommeBrowser par lequel la page appelle le contenu du moteur intégré.</summary>
        public const string FlashBridgeName = "pommeFlash";

        /// <summary>
        /// Schéma d'adresse du pont sous WebKitGTK : la page appelle le contenu par une requête
        /// synchrone « pomme-flash://call/?t=jeton&amp;r=requête » (comme un greffon, elle attend la réponse).
        /// </summary>
        public const string FlashBridgeScheme = "pomme-flash";

        /// <summary>Requête la plus longue transmise au contenu (caractères) : bien au-delà des appels réels.</summary>
        public const int MaxFlashCallLength = 4 * 1024 * 1024;

        /// <summary>
        /// Jeton du pont, propre à chaque lecteur : seul le script du pont, injecté dans le
        /// document principal, le connaît. Un cadre d'un autre site (publicité) ne peut donc pas
        /// appeler les fonctions du contenu par le schéma du pont.
        /// </summary>
        public static string NewFlashBridgeToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

        /// <summary>
        /// Requête reçue par le schéma du pont (« pomme-flash://call/?t=jeton&amp;r=requête ») : la
        /// requête si le jeton est celui du lecteur et sa taille raisonnable, null sinon.
        /// </summary>
        public static string? ParseFlashBridgeRequest(string? url, string? token)
        {
            string prefix = FlashBridgeScheme + "://call/?t=";
            if (url == null || string.IsNullOrEmpty(token) || !url.StartsWith(prefix, StringComparison.Ordinal))
                return null;
            int separator = url.IndexOf("&r=", prefix.Length, StringComparison.Ordinal);
            if (separator < 0)
                return null;
            ReadOnlySpan<char> given = url.AsSpan(prefix.Length, separator - prefix.Length);
            if (!CryptographicOperations.FixedTimeEquals(MemoryMarshal.AsBytes(given), MemoryMarshal.AsBytes(token.AsSpan())))
                return null;
            // Chaque caractère encodé en prend au plus 9 (%XX par octet UTF-8, 3 octets).
            ReadOnlySpan<char> encoded = url.AsSpan(separator + 3);
            if (encoded.Length > (long)MaxFlashCallLength * 9)
                return null;
            try
            {
                string request = Uri.UnescapeDataString(encoded.ToString());
                return request.Length <= MaxFlashCallLength ? request : null;
            }
            catch (UriFormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// Appels de la page vers le contenu lu par le moteur intégré (ExternalInterface.addCallback).
        /// Flash déclare ses fonctions par __flash__addCallback(élément, nom) : elles appellent
        /// élément.CallFunction(requête XML) et évaluent la réponse. L'élément du contenu (repéré par
        /// data-pomme-flash ou son identifiant) reçoit ce CallFunction, qui passe par l'objet
        /// <see cref="FlashBridgeName"/> de PommeBrowser (WebView2) ou par une requête synchrone au
        /// schéma <see cref="FlashBridgeScheme"/> (WebKitGTK), appelé de façon synchrone comme un greffon.
        /// Sans réponse, l'appel rend undefined. Le jeton (<see cref="NewFlashBridgeToken"/>)
        /// accompagne chaque requête au schéma.
        /// </summary>
        public static string FlashBridgeScript(string? elementId, string token) => """
            (() => {
              const call = (request) => {
                try {
                  const bridge = window.chrome && chrome.webview && chrome.webview.hostObjects && chrome.webview.hostObjects.sync.__BRIDGE__;
                  if (bridge) return bridge.CallFunction(String(request));
                  const xhr = new XMLHttpRequest();
                  xhr.open('GET', '__SCHEME__://call/?t=__TOKEN__&r=' + encodeURIComponent(String(request)), false);
                  xhr.send();
                  return xhr.status === 200 ? xhr.responseText : undefined;
                } catch (e) { return undefined; }
              };
              const equip = (element) => {
                if (!element || element.CallFunction === call) return;
                try { Object.defineProperty(element, 'CallFunction', { value: call, configurable: true, writable: true }); } catch (e) { }
              };
              window.__pommeFlashEquip = equip;
              document.querySelectorAll('[data-pomme-flash]').forEach(equip);
              const id = __ID__;
              if (id) equip(document.getElementById(id));
            })();
            """.Replace("__BRIDGE__", FlashBridgeName, StringComparison.Ordinal)
               .Replace("__SCHEME__", FlashBridgeScheme, StringComparison.Ordinal)
               .Replace("__TOKEN__", Convert.ToHexString(Convert.FromHexString(token)).ToLowerInvariant(), StringComparison.Ordinal)
               .Replace("__ID__", JsonSerializer.Serialize(elementId ?? string.Empty), StringComparison.Ordinal);

        /// <summary>Fichier demandé par la page (nom seul) : contenu et type, ou null s'il n'existe pas.</summary>
        public static (byte[] Data, string ContentType)? Read(string name, string baseUrl)
        {
            if (name == "pomme-config.js")
                return (Encoding.UTF8.GetBytes(ConfigScript(baseUrl)), "text/javascript");

            if (!RuffleAssets.IsAssetName(name))
                return null;

            try
            {
                string path = Path.Combine(AssetDirectory, name);
                if (!File.Exists(path))
                    return null;
                return (File.ReadAllBytes(path), name.EndsWith(".wasm", StringComparison.Ordinal) ? "application/wasm" : "text/javascript");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RuntimeLogBuffer.Append("[Ruffle] " + ex.Message);
                return null;
            }
        }
    }
}
