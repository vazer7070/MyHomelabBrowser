using System;
using System.Collections.Generic;
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
        /// sur une erreur), « content: » suivi de la description du contenu principal (FlashContent),
        /// et « contents: » suivi de la liste de tous les contenus du document.
        /// <paramref name="integratedSites"/> : sites (domaine enregistrable de la page principale,
        /// sous-domaines compris) dont le Flash est lu par le moteur intégré : leurs contenus sont
        /// seulement décrits, Ruffle ne les lance pas (un jeu ne doit pas se connecter deux fois). Un message { __pommeStopRuffle: true } du
        /// document parent (ou du document lui-même) arrête les lecteurs Ruffle du document et de ses
        /// cadres : moteur intégré à la place de la page.
        /// </summary>
        public static string ProbeScript(string baseUrl, string post, IEnumerable<string>? integratedSites = null) => """
            (() => {
              if (window.__pommeRuffleProbe) return;
              window.__pommeRuffleProbe = true;
              const FLASH_TYPES = ['application/x-shockwave-flash', 'application/futuresplash', 'application/vnd.adobe.flash.movie'];
              const CLSID = 'clsid:d27cdb6e-ae6d-11cf-96b8-444553540000';
              const PLAYERS = 'ruffle-object, ruffle-embed, ruffle-player';
              // Site de la page principale (celle de l'onglet), vu aussi depuis un cadre d'un autre site.
              const INTEGRATED = new Set(__INTEGRATED__);
              const topHost = (() => {
                try { const a = location.ancestorOrigins; if (a && a.length) return new URL(a[a.length - 1]).hostname; } catch (e) { }
                try { return window.top.location.hostname; } catch (e) { return location.hostname; }
              })().toLowerCase();
              const detectOnly = [...INTEGRATED].some(site => topHost === site || topHost.endsWith('.' + site));
              // Lecteurs Ruffle du document mis en pause et muets (l'élément reste : la page peut s'y
              // adresser), puis ceux des cadres ; plus de nouveau lecteur Ruffle ensuite.
              const stopRuffle = () => {
                window.__pommeRuffleStopped = true;
                try { window.RufflePlayer = window.RufflePlayer || {}; window.RufflePlayer.config = Object.assign(window.RufflePlayer.config || {}, { polyfills: false, autoplay: 'off' }); } catch (e) { }
                for (const player of document.querySelectorAll(PLAYERS)) {
                  try { player.volume = 0; } catch (e) { }
                  try { if (typeof player.pause === 'function') player.pause(); } catch (e) { }
                }
                for (let i = 0; i < window.length; i++) { try { window[i].postMessage({ __pommeStopRuffle: true }, '*'); } catch (e) { } }
              };
              addEventListener('message', (event) => {
                if (event.data && event.data.__pommeStopRuffle === true && (event.source === window.parent || event.source === window)) stopRuffle();
              });
              const direct = (status) => { try { __POST__; } catch (e) { } };
              // Dans un cadre (jeu dans une iframe) : message envoyé directement, et aussi relayé par le
              // document principal, qui le transmet à PommeBrowser (le moteur ne remet pas toujours
              // ceux d'un cadre). Doublons sans effet.
              const post = (status) => {
                direct(status);
                if (window !== window.top) {
                  try { window.top.postMessage({ __pommeRuffle: String(status) }, '*'); } catch (e) { }
                }
              };
              // Document principal : relais des messages de ses cadres. Une description de contenu n'est
              // acceptée que de la page qu'elle décrit (même origine que le cadre qui l'envoie) ; jamais
              // de position de suivi.
              if (window === window.top) {
                addEventListener('message', (event) => {
                  const data = event.data;
                  if (!data || typeof data.__pommeRuffle !== 'string' || event.source === window) return;
                  const status = data.__pommeRuffle;
                  if (status.startsWith('__RECT__')) return;
                  if (status.startsWith('__CONTENT__')) {
                    try {
                      if (new URL(JSON.parse(status.slice('__CONTENT__'.length)).page).origin !== event.origin) return;
                    } catch (e) { return; }
                  }
                  if (status.startsWith('__CONTENTS__')) {
                    try {
                      const list = JSON.parse(status.slice('__CONTENTS__'.length));
                      if (!Array.isArray(list) || list.some(item => new URL(item.page).origin !== event.origin)) return;
                    } catch (e) { return; }
                  }
                  direct(status);
                });
              }
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
              // Contenus du document, décrits pour le moteur Flash intégré : tous (un jeu peut charger
              // son client à part, caché ou minuscule, pendant que la page montre un logo), et le
              // principal : le plus grand, mais un format publicitaire courant passe après tout autre
              // contenu, et un contenu déclaré minuscule (pixel de suivi, lecteur audio invisible :
              // width="1") ne compte pas.
              const AD_SIZES = new Set(__AD_SIZES__);
              // Objet ActiveX (classid, sans type Flash) : Internet Explorer seulement. Un navigateur à
              // greffons (Firefox, Basilisk) ne le lit pas et lit le contenu Flash qu'il contient
              // (<embed> ou <object type=…>), avec ses propres flashvars et paramètres, qui peuvent
              // différer de ceux de l'objet : c'est celui-là qui est décrit.
              const activeX = (el) => kind(el) === 'object' && (el.getAttribute('classid') || '').toLowerCase() === CLSID &&
                !FLASH_TYPES.includes((el.getAttribute('type') || '').toLowerCase());
              const nestedFlash = (el) => [...el.querySelectorAll('object, embed, ruffle-object, ruffle-embed')].some(isFlash);
              const describeAll = () => {
                const found = [];
                for (const el of document.querySelectorAll('object, embed, ruffle-object, ruffle-embed')) {
                  if (!isFlash(el)) continue;
                  if (activeX(el) && nestedFlash(el)) continue;
                  const parent = el.parentElement;
                  if (kind(el) === 'embed' && parent && kind(parent) === 'object' && isFlash(parent) && !activeX(parent)) continue;
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
                  const tiny = (declaredWidth !== null && declaredWidth < 16) || (declaredHeight !== null && declaredHeight < 16);
                  const rect = el.getBoundingClientRect();
                  const width = declaredWidth ?? Math.round(rect.width);
                  const height = declaredHeight ?? Math.round(rect.height);
                  found.push({ el, width, height, tiny, ad: AD_SIZES.has(width + 'x' + height), swf, params });
                }
                return found;
              };
              const toContent = (item) => {
                const el = item.el;
                const params = Object.assign({}, item.params);
                const flashvars = params.flashvars || el.getAttribute('flashvars') || null;
                for (const name of ['movie', 'src', 'data', 'flashvars', 'width', 'height', 'type', 'id', 'name', 'classid', 'codebase', 'pluginspage', 'style', 'class']) delete params[name];
                return { swf: item.swf, page: location.href, flashvars, width: item.width, height: item.height, id: el.id || el.getAttribute('name') || null, params };
              };
              const describe = (found) => {
                let best = null;
                for (const item of found) {
                  if (item.tiny) continue;
                  if (!best || (item.ad !== best.ad ? !item.ad : item.width * item.height > best.width * best.height)) best = item;
                }
                return best ? toContent(best) : null;
              };
              // Contenus décrits au début, puis de nouveau à chaque lecteur Ruffle créé : un contenu
              // ajouté plus tard, ou qui n'avait pas encore de taille, est pris en compte.
              let reported = '', listed = '';
              const report = () => {
                try {
                  const found = describeAll();
                  const content = describe(found);
                  if (content) {
                    const json = JSON.stringify(content);
                    if (json !== reported) { reported = json; post('__CONTENT__' + json); }
                  }
                  const list = JSON.stringify(found.slice(0, 16).map(toContent));
                  if (found.length && list !== listed) { listed = list; post('__CONTENTS__' + list); }
                } catch (e) { }
              };
              let observer = null;
              const inject = () => {
                if (observer) observer.disconnect();
                if (window.__pommeRuffleInjected) return;
                window.__pommeRuffleInjected = true;
                report();
                post('detected');
                if (detectOnly || window.__pommeRuffleStopped) {
                  // Moteur intégré : contenus ajoutés ensuite décrits aussi (aucun lecteur Ruffle ne le fera).
                  let pending = 0;
                  const later = new MutationObserver(() => { clearTimeout(pending); pending = setTimeout(report, 200); });
                  later.observe(document.documentElement, { childList: true, subtree: true });
                  setTimeout(() => later.disconnect(), 60000);
                  return;
                }
                load('__BASE__pomme-config.js')
                  .then(() => load('__BASE__ruffle.js'))
                  .then(() => {
                    post('loaded');
                    // Arrêté pendant le chargement : les lecteurs que Ruffle vient de créer aussi.
                    if (window.__pommeRuffleStopped) { setTimeout(stopRuffle, 0); return; }
                    watch();
                  }, () => post('blocked'));
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
               .Replace("__CONTENTS__", FlashContent.ListPrefix, StringComparison.Ordinal)
               .Replace("__CONTENT__", FlashContent.MessagePrefix, StringComparison.Ordinal)
               .Replace("__RECT__", RectPrefix, StringComparison.Ordinal)
               .Replace("__AD_SIZES__", JsonSerializer.Serialize(FlashContent.AdSizes.Select(s => s.Width + "x" + s.Height)), StringComparison.Ordinal)
               .Replace("__INTEGRATED__", JsonSerializer.Serialize((integratedSites ?? Array.Empty<string>()).Select(host => host.ToLowerInvariant()).Distinct().ToArray()), StringComparison.Ordinal);

        /// <summary>
        /// Lecteurs Ruffle de la page (document principal et cadres, même d'un autre site) mis en
        /// pause : le moteur intégré lit son contenu à la place de la page.
        /// </summary>
        public const string StopRuffleScript = "window.postMessage({ __pommeStopRuffle: true }, '*')";

        /// <summary>Préfixe des messages de position du contenu lu par le moteur intégré.</summary>
        public const string RectPrefix = "rect:";

        /// <summary>
        /// Expression JavaScript : la fenêtre de <paramref name="page"/>, document principal ou cadre
        /// de même origine (à toute profondeur) ; à défaut, le premier cadre de même origine qui
        /// contient un contenu Flash (ou l'emplacement du moteur intégré) ; null sinon (cadre d'un autre site).
        /// </summary>
        public static string WindowOf(Uri page) => """
            ((page) => {
              const strip = (url) => String(url).split('#')[0];
              let fallback = null;
              const visit = (w) => {
                let doc;
                try { doc = w.document; if (strip(w.location.href) === strip(page)) return w; } catch (e) { return null; }
                if (!fallback && w !== window && doc.querySelector('object, embed, ruffle-object, ruffle-embed, [data-pomme-flash-hole]')) fallback = w;
                for (let i = 0; i < w.frames.length; i++) { const found = visit(w.frames[i]); if (found) return found; }
                return null;
              };
              return visit(window) || fallback;
            })(__PAGE__)
            """.Replace("__PAGE__", JsonSerializer.Serialize(page.AbsoluteUri), StringComparison.Ordinal);

        /// <summary>
        /// Script exécuté dans la fenêtre de <paramref name="page"/> (cadre de même origine que la
        /// page : jeu dans une iframe), depuis le document principal ; son résultat est rendu.
        /// </summary>
        public static string InWindowOf(Uri page, string script) => """
            (() => {
              const w = __WINDOW__;
              return w ? w.eval(__SCRIPT__) : undefined;
            })()
            """.Replace("__WINDOW__", WindowOf(page), StringComparison.Ordinal)
               .Replace("__SCRIPT__", JsonSerializer.Serialize(script), StringComparison.Ordinal);

        /// <summary>
        /// Recherche, dans le document « doc », de l'élément d'un contenu (« wanted » : fichier SWF,
        /// adresse complète, et identifiant de l'élément s'il en a un), tel que le script de
        /// détection l'a décrit (élément d'origine ou celui de Ruffle qui l'a remplacé) ; « find() »
        /// rend l'élément à remplacer, null s'il n'est pas (ou plus) dans le document.
        /// </summary>
        const string FindContentScript = """
            const kind = (el) => el.localName.startsWith('ruffle-') ? el.localName.slice(7) : el.localName;
            const sourceOf = (el) => {
              if (kind(el) !== 'object') return el.getAttribute('src') || '';
              if (el.getAttribute('data')) return el.getAttribute('data');
              const movie = el.querySelector(':scope > param[name="movie" i], :scope > param[name="src" i]');
              return movie ? movie.getAttribute('value') || '' : '';
            };
            const find = () => {
              for (const el of doc.querySelectorAll('object, embed, ruffle-object, ruffle-embed')) {
                const source = sourceOf(el);
                if (!source) continue;
                let href;
                try { href = new URL(source, doc.baseURI).href; } catch (e) { continue; }
                if (href !== wanted.swf) continue;
                if (wanted.id !== null && (el.id || el.getAttribute('name') || null) !== wanted.id) continue;
                // Un embed dans un object (forme courante) : l'object entier.
                const parent = el.parentElement;
                return kind(el) === 'embed' && parent && kind(parent) === 'object' ? parent : el;
              }
              return null;
            };
            """;

        /// <summary>Contenu recherché par <see cref="FindContentScript"/> : fichier et identifiant, en JSON.</summary>
        static string Wanted(FlashContent content)
            => "{ swf: " + JsonSerializer.Serialize(content.Swf.OriginalString) + ", id: " + (content.Id is { } id ? JsonSerializer.Serialize(id) : "null") + " }";

        /// <summary>
        /// Moteur Flash intégré dans la page : l'élément du contenu (<paramref name="content"/>,
        /// retrouvé par son fichier et son identifiant) est remplacé par un emplacement vide de même
        /// taille, ce qui arrête Ruffle, et sa position dans la fenêtre est envoyée à PommeBrowser à
        /// chaque changement (défilement, taille, mise en page) : « rect:clé: » (clé de l'emplacement,
        /// <paramref name="slot"/> : un lecteur par contenu) suivi de x, y, largeur, hauteur (pixels
        /// CSS), du rapport pixels CSS / pixels de l'écran et de sa visibilité ; « null » s'il est
        /// introuvable, « gone » quand la page le retire (contenu remplacé, cadre rechargé).
        /// Le contenu peut être dans un cadre de même origine que la page (<paramref name="framePage"/>,
        /// jeu dans une iframe) : sa position tient compte de celle des cadres, et la zone du cadre
        /// où il est visible est jointe (clip). Relancé, il reprend le même emplacement. Les tailles
        /// que la page donne ensuite à l'élément (attributs width et height) passent à l'emplacement.
        /// </summary>
        public static string FlashTrackerScript(string post, string slot, FlashContent content, Uri? framePage = null) => """
            (() => {
              const post = (status) => { try { __POST__; } catch (e) { } };
              const slot = __SLOT__;
              const say = (payload) => post('__RECT__' + slot + ':' + payload);
              // Fenêtre du contenu : document principal, ou cadre de même origine.
              const root = __ROOT__;
              if (!root) { say('null'); return; }
              const doc = root.document;
              const wanted = __WANTED__;
              __FIND__
              const trackers = window.__pommeFlashTrackers || (window.__pommeFlashTrackers = {});
              const previous = trackers[slot];
              const target = find();
              if (previous && previous.doc === doc && previous.hole.isConnected && !target) { previous.send(true); return; }
              if (previous) previous.stop();
              if (!target) { say('null'); return; }
              const box = target.getBoundingClientRect();
              const style = root.getComputedStyle(target);
              // Taille d'un attribut width/height : pixels, pourcentage, sinon la taille affichée.
              const length = (value, measured) => {
                value = String(value == null ? '' : value).trim();
                if (/^\d+(px)?$/i.test(value)) return parseInt(value, 10) + 'px';
                if (/^\d+(\.\d+)?%$/.test(value)) return value;
                return measured === null ? null : Math.round(measured) + 'px';
              };
              const hole = doc.createElement('div');
              for (const name of ['id', 'name', 'class', 'style']) {
                if (target.hasAttribute(name)) hole.setAttribute(name, target.getAttribute(name));
              }
              hole.setAttribute('data-pomme-flash-hole', slot);
              hole.style.width = length(target.getAttribute('width'), box.width);
              hole.style.height = length(target.getAttribute('height'), box.height);
              hole.style.display = style.display === 'inline' ? 'inline-block' : style.display;
              hole.style.background = '#000';
              // Comme sur l'élément d'origine : width et height (propriétés et attributs) font sa taille.
              for (const name of ['width', 'height']) {
                if (target.hasAttribute(name)) hole.setAttribute(name, target.getAttribute(name));
                try {
                  Object.defineProperty(hole, name, {
                    configurable: true,
                    get: () => hole.getAttribute(name) || '',
                    set: (value) => hole.setAttribute(name, String(value))
                  });
                } catch (e) { }
              }
              new root.MutationObserver(() => {
                for (const name of ['width', 'height']) {
                  const size = length(hole.getAttribute(name), null);
                  if (size && hole.style[name] !== size) hole.style[name] = size;
                }
              }).observe(hole, { attributes: true, attributeFilter: ['width', 'height'] });
              // Méthodes du contenu (pont de PommeBrowser, ExternalInterface.addCallback) : gardées.
              const equip = root.__pommeFlashEquips && root.__pommeFlashEquips[slot];
              if (equip) {
                equip(target);
                equip(hole);
              }
              for (const name of Object.keys(target)) {
                if (typeof target[name] === 'function' && !(name in hole)) hole[name] = target[name];
              }
              target.replaceWith(hole);
              // Élément désigné par son nom (document.nom, ancienne façon de joindre un contenu) : l'emplacement.
              const named = target.getAttribute('name');
              if (named && doc[named] === undefined) {
                try { Object.defineProperty(doc, named, { configurable: true, writable: true, value: hole }); } catch (e) { }
              }
              // Cadres qui contiennent le contenu, du plus proche au document principal.
              const frames = [];
              for (let w = root; w !== window && w.frameElement; w = w.parent) frames.push(w.frameElement);
              // Contenu retiré par la page (remplacé par un autre, logo puis jeu) ou cadre rechargé,
              // retiré : « gone », le lecteur s'arrête. Un emplacement déplacé (retiré puis remis
              // aussitôt) ne compte pas.
              let detached = 0;
              const isGone = () => {
                try {
                  if (root.document !== doc || frames.some(f => !f.isConnected)) return true;
                } catch (e) { return true; }
                if (hole.isConnected) { detached = 0; return false; }
                if (!detached) detached = Date.now();
                return Date.now() - detached > 1500;
              };
              let last = '';
              let stopped = false;
              const send = (force) => {
                if (stopped) return;
                if (isGone()) {
                  tracker.stop();
                  if (trackers[slot] === tracker) delete trackers[slot];
                  say('gone');
                  return;
                }
                const area = hole.getBoundingClientRect();
                let x = area.left, y = area.top;
                let visible = hole.isConnected && area.width > 0 && area.height > 0 && root.getComputedStyle(hole).visibility !== 'hidden';
                // Zone visible du cadre, ramenée de cadre en cadre dans la fenêtre principale.
                let clip = null;
                for (const frame of frames) {
                  const owner = frame.ownerDocument.defaultView;
                  const bounds = frame.getBoundingClientRect();
                  const frameStyle = owner.getComputedStyle(frame);
                  const left = parseFloat(frameStyle.paddingLeft) || 0, top = parseFloat(frameStyle.paddingTop) || 0;
                  const dx = bounds.left + frame.clientLeft + left;
                  const dy = bounds.top + frame.clientTop + top;
                  const view = {
                    x: dx, y: dy,
                    w: Math.max(0, frame.clientWidth - left - (parseFloat(frameStyle.paddingRight) || 0)),
                    h: Math.max(0, frame.clientHeight - top - (parseFloat(frameStyle.paddingBottom) || 0))
                  };
                  if (clip) {
                    const cx = Math.max(view.x, clip.x + dx), cy = Math.max(view.y, clip.y + dy);
                    const cr = Math.min(view.x + view.w, clip.x + dx + clip.w), cb = Math.min(view.y + view.h, clip.y + dy + clip.h);
                    clip = { x: cx, y: cy, w: Math.max(0, cr - cx), h: Math.max(0, cb - cy) };
                  } else {
                    clip = view;
                  }
                  x += dx;
                  y += dy;
                  if (bounds.width <= 0 || bounds.height <= 0 || frameStyle.visibility === 'hidden') visible = false;
                }
                const message = JSON.stringify(clip
                  ? { x, y, w: area.width, h: area.height, dpr: window.devicePixelRatio || 1, visible, clip }
                  : { x, y, w: area.width, h: area.height, dpr: window.devicePixelRatio || 1, visible });
                if (force || message !== last) { last = message; say(message); }
              };
              let queued = false;
              const schedule = () => {
                if (queued) return;
                queued = true;
                requestAnimationFrame(() => { queued = false; send(false); });
              };
              const undo = [];
              for (const w of new Set([window, root, ...frames.map(f => f.ownerDocument.defaultView)])) {
                w.addEventListener('scroll', schedule, { capture: true, passive: true });
                w.addEventListener('resize', schedule);
                undo.push(() => { w.removeEventListener('scroll', schedule, { capture: true }); w.removeEventListener('resize', schedule); });
              }
              const resize = new root.ResizeObserver(schedule);
              resize.observe(hole);
              const observers = [new MutationObserver(schedule)];
              observers[0].observe(document.documentElement, { attributes: true, childList: true, subtree: true });
              if (root !== window) {
                const inner = new root.MutationObserver(schedule);
                inner.observe(doc.documentElement, { attributes: true, childList: true, subtree: true });
                observers.push(inner);
              }
              // Animations et transformations CSS ne se signalent pas : vérification régulière.
              const timer = setInterval(() => send(false), 400);
              const tracker = {
                doc, hole, send,
                stop: () => {
                  stopped = true;
                  clearInterval(timer);
                  for (const f of [...undo, () => resize.disconnect(), ...observers.map(o => () => o.disconnect())]) {
                    try { f(); } catch (e) { }
                  }
                }
              };
              trackers[slot] = tracker;
              send(true);
            })();
            """.Replace("__POST__", post, StringComparison.Ordinal).Replace("__RECT__", RectPrefix, StringComparison.Ordinal)
               .Replace("__SLOT__", JsonSerializer.Serialize(SlotKey(slot)), StringComparison.Ordinal)
               .Replace("__WANTED__", Wanted(content), StringComparison.Ordinal)
               .Replace("__FIND__", FindContentScript, StringComparison.Ordinal)
               .Replace("__ROOT__", framePage != null ? WindowOf(framePage) : "window", StringComparison.Ordinal);

        /// <summary>Clé d'emplacement sûre : lettres et chiffres, 16 au plus.</summary>
        static string SlotKey(string slot)
            => slot is { Length: > 0 and <= 16 } && slot.All(char.IsAsciiLetterOrDigit)
                ? slot
                : throw new ArgumentException("Clé d'emplacement invalide.", nameof(slot));

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
        /// Méthodes de Flash que la page appelle sur l'élément du contenu (API JavaScript de Flash
        /// Player : avancement du chargement, variables, lecture) ; les mêmes que l'hôte accepte
        /// (PluginInstance.PageMethods).
        /// </summary>
        /// <summary>Requête du pont : la page a donné le focus à l'élément du contenu (élément.focus()).</summary>
        public const string FocusRequest = "focus";

        public static readonly IReadOnlyList<string> FlashMethods = new[]
        {
            "PercentLoaded", "GetVariable", "SetVariable", "IsPlaying", "Play", "StopPlay", "Rewind",
            "Back", "Forward", "GotoFrame", "CurrentFrame", "TotalFrames", "LoadMovie", "Zoom", "Pan",
            "SetZoomRect", "TCallFrame", "TCallLabel", "TCurrentFrame", "TCurrentLabel", "TGetProperty",
            "TGetPropertyAsNumber", "TGotoFrame", "TGotoLabel", "TPlay", "TSetProperty", "TStopPlay"
        };

        /// <summary>
        /// Appels de la page vers un contenu lu par le moteur intégré, comme vers un greffon :
        /// fonctions déclarées par ExternalInterface.addCallback (Flash les déclare par
        /// __flash__addCallback(élément, nom) ; elles appellent élément.CallFunction(requête XML) et
        /// évaluent la réponse) et méthodes de Flash (<see cref="FlashMethods"/>, PercentLoaded…,
        /// requête {"method":…,"args":[…]} dont la réponse est la valeur en JSON). L'élément du
        /// contenu (retrouvé par son fichier et son identifiant, puis l'emplacement qui le remplace)
        /// reçoit ces fonctions ; chaque requête, précédée de la clé de l'emplacement
        /// (<paramref name="slot"/>, « clé|requête »), passe par l'objet <see cref="FlashBridgeName"/>
        /// de PommeBrowser (WebView2) ou par une requête synchrone au schéma
        /// <see cref="FlashBridgeScheme"/> (WebKitGTK), avec le jeton du pont
        /// (<see cref="NewFlashBridgeToken"/>). Sans réponse, l'appel rend undefined.
        /// </summary>
        public static string FlashBridgeScript(string slot, FlashContent content, string token) => """
            (() => {
              const slot = __SLOT__;
              const doc = document;
              const wanted = __WANTED__;
              __FIND__
              const hostObject = (w) => {
                try { return w.chrome && w.chrome.webview && w.chrome.webview.hostObjects && w.chrome.webview.hostObjects.sync.__BRIDGE__; }
                catch (e) { return null; }
              };
              const send = (request) => {
                try {
                  const message = slot + '|' + String(request);
                  // Dans un cadre de même origine, l'objet de PommeBrowser est celui du document
                  // principal (WebView2 ne l'offre qu'à lui).
                  const bridge = (window !== window.top && hostObject(window.top)) || hostObject(window);
                  if (bridge) return bridge.CallFunction(message);
                  const xhr = new XMLHttpRequest();
                  xhr.open('GET', '__SCHEME__://call/?t=__TOKEN__&r=' + encodeURIComponent(message), false);
                  xhr.send();
                  return xhr.status === 200 ? xhr.responseText : undefined;
                } catch (e) { return undefined; }
              };
              const call = (request) => send(request);
              const method = (name) => function () {
                const args = Array.prototype.slice.call(arguments, 0, 8)
                  .map(v => typeof v === 'number' || typeof v === 'boolean' || v === null ? v : String(v));
                const answer = send(JSON.stringify({ method: name, args }));
                if (answer === undefined || answer === null || answer === '') return undefined;
                try { const value = JSON.parse(answer); return value === null ? undefined : value; } catch (e) { return undefined; }
              };
              const methods = {};
              for (const name of __METHODS__) methods[name] = method(name);
              // Comme pour un greffon : élément.focus() donne le clavier au lecteur.
              methods.focus = function () { send('__FOCUS__'); };
              const equip = (element) => {
                if (!element || element.CallFunction === call) return;
                try {
                  Object.defineProperty(element, 'CallFunction', { value: call, configurable: true, writable: true });
                  for (const name of Object.keys(methods)) Object.defineProperty(element, name, { value: methods[name], configurable: true, writable: true });
                } catch (e) { }
              };
              const equips = window.__pommeFlashEquips || (window.__pommeFlashEquips = {});
              equips[slot] = equip;
              equip(find());
              document.querySelectorAll('[data-pomme-flash-hole="' + slot + '"]').forEach(equip);
            })();
            """.Replace("__BRIDGE__", FlashBridgeName, StringComparison.Ordinal)
               .Replace("__SCHEME__", FlashBridgeScheme, StringComparison.Ordinal)
               .Replace("__TOKEN__", Convert.ToHexString(Convert.FromHexString(token)).ToLowerInvariant(), StringComparison.Ordinal)
               .Replace("__SLOT__", JsonSerializer.Serialize(SlotKey(slot)), StringComparison.Ordinal)
               .Replace("__WANTED__", Wanted(content), StringComparison.Ordinal)
               .Replace("__FIND__", FindContentScript, StringComparison.Ordinal)
               .Replace("__METHODS__", JsonSerializer.Serialize(FlashMethods), StringComparison.Ordinal)
               .Replace("__FOCUS__", FocusRequest, StringComparison.Ordinal);

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
