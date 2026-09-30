using System;
using System.IO;
using System.Text;
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
