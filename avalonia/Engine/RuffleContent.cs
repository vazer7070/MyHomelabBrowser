using System;
using System.IO;
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
              // Lecteurs Ruffle : « playing » dès qu'un contenu démarre, « failed » s'il s'arrête sur
              // son écran d'erreur (#panic, dans son shadow root ouvert), à tout moment de la lecture.
              const watch = () => {
                const seen = new WeakSet();
                let playing = false, failed = false;
                const fail = () => { if (!failed) { failed = true; post('failed'); } };
                const attach = (player) => {
                  if (seen.has(player)) return;
                  seen.add(player);
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
              // Contenu principal (le plus grand) : décrit pour le moteur Flash intégré, avant
              // que Ruffle ne remplace les éléments.
              const describe = () => {
                let best = null, bestArea = -1;
                for (const el of document.querySelectorAll('object, embed')) {
                  if (!isFlash(el)) continue;
                  if (el.localName === 'embed' && el.parentElement && el.parentElement.localName === 'object' && isFlash(el.parentElement)) continue;
                  // Taille donnée en pixels par l'élément, sinon celle affichée (un <object> sans
                  // lecteur n'affiche que son contenu de repli).
                  const rect = el.getBoundingClientRect();
                  const size = (name, shown) => {
                    const value = (el.getAttribute(name) || '').trim();
                    return /^\d+(px)?$/i.test(value) ? parseInt(value, 10) : Math.round(shown);
                  };
                  const width = size('width', rect.width);
                  const height = size('height', rect.height);
                  if (width * height > bestArea) { best = { el, width, height }; bestArea = width * height; }
                }
                if (!best) return null;
                const el = best.el;
                // Repère gardé par Ruffle quand il remplace l'élément : le moteur intégré le retrouve.
                el.setAttribute('data-pomme-flash', '');
                const params = {};
                if (el.localName === 'object') {
                  for (const p of el.querySelectorAll(':scope > param')) {
                    const name = (p.getAttribute('name') || '').toLowerCase();
                    if (name) params[name] = p.getAttribute('value') || '';
                  }
                } else {
                  for (const a of el.attributes) params[a.name.toLowerCase()] = a.value;
                }
                const source = el.localName === 'object' ? (el.getAttribute('data') || params.movie || params.src || '') : (el.getAttribute('src') || '');
                let swf;
                try { swf = new URL(source, document.baseURI).href; } catch (e) { return null; }
                const flashvars = params.flashvars || el.getAttribute('flashvars') || null;
                for (const name of ['movie', 'src', 'data', 'flashvars', 'width', 'height', 'type', 'id', 'name', 'classid', 'codebase', 'pluginspage', 'style', 'class']) delete params[name];
                return { swf, page: location.href, flashvars, width: best.width, height: best.height, id: el.id || el.getAttribute('name') || null, params };
              };
              let observer = null;
              const inject = () => {
                if (observer) observer.disconnect();
                if (window.__pommeRuffleInjected) return;
                window.__pommeRuffleInjected = true;
                try { const content = describe(); if (content) post('__CONTENT__' + JSON.stringify(content)); } catch (e) { }
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
               .Replace("__CONTENT__", FlashContent.MessagePrefix, StringComparison.Ordinal);

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
        /// synchrone « pomme-flash://call/?r=requête » (comme un greffon, elle attend la réponse).
        /// </summary>
        public const string FlashBridgeScheme = "pomme-flash";

        /// <summary>
        /// Appels de la page vers le contenu lu par le moteur intégré (ExternalInterface.addCallback).
        /// Flash déclare ses fonctions par __flash__addCallback(élément, nom) : elles appellent
        /// élément.CallFunction(requête XML) et évaluent la réponse. L'élément du contenu (repéré par
        /// data-pomme-flash ou son identifiant) reçoit ce CallFunction, qui passe par l'objet
        /// <see cref="FlashBridgeName"/> de PommeBrowser (WebView2) ou par une requête synchrone au
        /// schéma <see cref="FlashBridgeScheme"/> (WebKitGTK), appelé de façon synchrone comme un greffon.
        /// Sans réponse, l'appel rend undefined.
        /// </summary>
        public static string FlashBridgeScript(string? elementId) => """
            (() => {
              const call = (request) => {
                try {
                  const bridge = window.chrome && chrome.webview && chrome.webview.hostObjects && chrome.webview.hostObjects.sync.__BRIDGE__;
                  if (bridge) return bridge.CallFunction(String(request));
                  const xhr = new XMLHttpRequest();
                  xhr.open('GET', '__SCHEME__://call/?r=' + encodeURIComponent(String(request)), false);
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
