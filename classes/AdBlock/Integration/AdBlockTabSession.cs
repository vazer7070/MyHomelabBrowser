using Microsoft.Web.WebView2.Core;
using MyHomelabBrowser.classes.AdBlock.Models;
using MyHomelabBrowser.classes.AdBlock.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.AdBlock.Integration
{
    public sealed class AdBlockTabSession : IDisposable
    {
        private const string CosmeticStyleAttribute = "data-pomme-adblock";
        private const int MaximumRememberedBlockedUrls = 320;

        private readonly IAdBlockWebView _webView;
        private readonly AdBlockModuleService _module;
        private readonly object _blockedUrlsLock = new();
        private readonly HashSet<string> _blockedResourceUrls = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _pendingBlockedUrls = new();

        // Sélecteurs sérialisés, partagés entre onglets : le tableau renvoyé par le moteur
        // est mis en cache par hôte, sa sérialisation JSON (souvent plusieurs centaines de Ko) aussi.
        private static readonly ConditionalWeakTable<object, string> SelectorJsonCache = new();

        private string _documentHost = string.Empty;
        private int _blockedCount;
        private long _lastUpdateNotificationTick;
        private DeferredAction? _updateNotificationTimer;
        private DeferredAction? _cosmeticRefreshTimer;
        private bool _attached;
        private bool _disposed;

        // Script complet présent dans le document courant (et s'il était actif).
        private bool _cosmeticInjected;
        private bool _cosmeticInjectedEnabled;

        public event Action<AdBlockTabSession>? Updated;

        public IAdBlockWebView WebView => _webView;
        public bool IsPrivate { get; }
        public int BlockedCount => Volatile.Read(ref _blockedCount);
        public string CurrentHost => _documentHost;
        public bool IsProtectionActive => _module.IsFilteringEnabledForHost(_documentHost);

        public AdBlockTabSession(IAdBlockWebView webView, AdBlockModuleService module, bool isPrivate)
        {
            _webView = webView;
            _module = module;
            IsPrivate = isPrivate;
        }

        public Task AttachAsync()
        {
            if (_attached || _webView.CoreWebView2 == null)
                return Task.CompletedTask;

            _attached = true;
            UpdateDocumentHost(_webView.CoreWebView2.Source);

            _webView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            _webView.CoreWebView2.WebResourceRequested += CoreWebView2_WebResourceRequested;
            _webView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            _webView.CoreWebView2.NavigationCompleted += WebView_NavigationCompleted;

            _module.RulesChanged += Module_RulesChanged;
            _module.StateChanged += Module_StateChanged;

            return Task.CompletedTask;
        }

        public async Task RefreshFilteringAsync()
        {
            if (_disposed || _webView.CoreWebView2 == null)
                return;

            await ApplyCosmeticFilteringAsync().ConfigureAwait(true);
            Updated?.Invoke(this);
        }

        private void CoreWebView2_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            UpdateDocumentHost(e.Uri);
            Interlocked.Exchange(ref _blockedCount, 0);

            lock (_blockedUrlsLock)
            {
                _blockedResourceUrls.Clear();
                _pendingBlockedUrls.Clear();
            }

            _cosmeticInjected = false;
            _cosmeticRefreshTimer?.Stop();
            Updated?.Invoke(this);
        }

        private async void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            UpdateDocumentHost(_webView.CoreWebView2?.Source);
            try
            {
                await ApplyCosmeticFilteringAsync().ConfigureAwait(true);
            }
            catch
            {
                // Le filtrage visuel ne doit jamais empêcher l'affichage de la page.
            }

            Updated?.Invoke(this);
        }

        private void CoreWebView2_WebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
        {
            if (_disposed || _webView.CoreWebView2 == null)
                return;

            if (!Uri.TryCreate(e.Request.Uri, UriKind.Absolute, out Uri? requestUri))
                return;

            string documentHost = _documentHost;
            if (documentHost.Length == 0 && Uri.TryCreate(_webView.CoreWebView2.Source, UriKind.Absolute, out Uri? source))
                documentHost = AdBlockDomain.NormalizeHost(source.Host);

            AdBlockResourceType resourceType = MapResourceType(e.ResourceContext);

            // Les flux audio et vidéo YouTube utilisent souvent des requêtes séparées.
            // Bloquer l'une d'elles peut laisser l'audio jouer avec une zone vidéo vide.
            // Ces flux de lecture sont donc toujours préservés ; les publicités YouTube
            // restent traitées par les règles visuelles sûres ci-dessous.
            if (IsProtectedPlaybackRequest(requestUri, documentHost, resourceType))
                return;

            if (!_module.ShouldBlock(requestUri, documentHost, resourceType))
                return;

            try
            {
                e.Response = _webView.CoreWebView2.Environment.CreateWebResourceResponse(
                    new MemoryStream(Array.Empty<byte>()),
                    204,
                    "Bloqué par PommeBrowser",
                    "Content-Type: text/plain\r\nCache-Control: no-store\r\n");

                RememberBlockedResource(requestUri.AbsoluteUri);
                ScheduleCosmeticRefresh();

                int count = Interlocked.Increment(ref _blockedCount);
                _module.RecordBlockedRequest();
                NotifyUpdatedForBlockedRequest(count);
            }
            catch
            {
                // Une requête ne doit jamais faire tomber l'onglet si WebView2 refuse la réponse synthétique.
            }
        }

        private void RememberBlockedResource(string absoluteUrl)
        {
            if (string.IsNullOrWhiteSpace(absoluteUrl))
                return;

            lock (_blockedUrlsLock)
            {
                if (_blockedResourceUrls.Count >= MaximumRememberedBlockedUrls)
                {
                    string? oldest = _blockedResourceUrls.FirstOrDefault();
                    if (oldest != null)
                        _blockedResourceUrls.Remove(oldest);
                }

                string url = RemoveFragment(absoluteUrl);
                if (_blockedResourceUrls.Add(url) && _pendingBlockedUrls.Count < MaximumRememberedBlockedUrls)
                    _pendingBlockedUrls.Add(url);
            }
        }

        private void ScheduleCosmeticRefresh()
        {
            if (_disposed)
                return;

            _webView.Post(() =>
            {
                if (_disposed)
                    return;

                _cosmeticRefreshTimer ??= new DeferredAction(_webView, TimeSpan.FromMilliseconds(220), async () =>
                {
                    if (_disposed)
                        return;

                    try
                    {
                        await ApplyCosmeticFilteringAsync(incremental: true).ConfigureAwait(true);
                    }
                    catch
                    {
                        // Une page qui change pendant l'exécution du script peut invalider l'appel.
                    }
                });
                _cosmeticRefreshTimer.Restart();
            });
        }

        /// <param name="incremental">
        /// Vrai pour un simple ajout de ressources bloquées : si le script est déjà en place
        /// dans le document, seules les nouvelles URL lui sont transmises, au lieu de tout
        /// réinjecter (sélecteurs compris) à chaque requête bloquée.
        /// </param>
        private async Task ApplyCosmeticFilteringAsync(bool incremental = false)
        {
            if (_webView.CoreWebView2 == null)
                return;

            if (incremental && _cosmeticInjected)
            {
                string[] pending;
                lock (_blockedUrlsLock)
                {
                    pending = _pendingBlockedUrls.ToArray();
                    _pendingBlockedUrls.Clear();
                }

                // Filtrage visuel désactivé : il n'y a rien à replier.
                if (pending.Length == 0 || !_cosmeticInjectedEnabled)
                    return;

                string update = $$"""
(() => {
    const state = window.__pommeBrowserAdBlockCosmetic;
    if (!state || typeof state.addBlocked !== 'function')
        return false;
    state.addBlocked({{JsonSerializer.Serialize(pending)}});
    return true;
})();
""";

                string result = await _webView.CoreWebView2.ExecuteScriptAsync(update).ConfigureAwait(true);
                if (result == "true")
                    return;

                // Nouveau document sans le script : réinjection complète ci-dessous.
            }

            bool enabled = _module.Settings.CosmeticFiltering
                && _module.IsFilteringEnabledForHost(_documentHost);

            IReadOnlyList<string> selectors = enabled
                ? _module.GetCosmeticSelectors(_documentHost)
                : Array.Empty<string>();

            bool isYouTubeDocument = IsYouTubeHost(_documentHost);

            // Les sélecteurs génériques d'EasyList peuvent devenir trop agressifs sur
            // le lecteur YouTube, dont la structure change fréquemment. Sur YouTube,
            // on utilise uniquement une liste cosmétique ciblée et sûre dans le script.
            string jsonSelectorBlocks = isYouTubeDocument || selectors.Count == 0
                ? "[]"
                : SelectorJsonCache.GetValue(selectors, static key => JsonSerializer.Serialize(BuildSelectorBlocks((IReadOnlyList<string>)key)));

            string[] blockedUrls;

            lock (_blockedUrlsLock)
            {
                blockedUrls = _blockedResourceUrls.ToArray();
                _pendingBlockedUrls.Clear();
            }

            string jsonBlockedUrls = JsonSerializer.Serialize(blockedUrls);
            string enabledJson = enabled ? "true" : "false";

            string script = $$"""
(() => {
    'use strict';

    const enabled = {{enabledJson}};
    const selectorBlocks = {{jsonSelectorBlocks}};
    const blockedUrls = {{jsonBlockedUrls}};
    const currentHost = (location.hostname || '').toLowerCase().replace(/^www\./, '');
    const isYouTube = currentHost === 'youtube.com'
        || currentHost.endsWith('.youtube.com')
        || currentHost === 'youtu.be';
    const stateKey = '__pommeBrowserAdBlockCosmetic';
    const styleAttribute = '{{CosmeticStyleAttribute}}';
    const collapsedAttribute = 'data-pomme-adblock-collapsed';

    const previous = window[stateKey];
    if (previous && typeof previous.destroy === 'function') {
        try { previous.destroy(!enabled); } catch (_) { }
    }

    document.querySelectorAll(`style[${styleAttribute}]`).forEach(node => node.remove());

    if (!enabled) {
        document.querySelectorAll(`[${collapsedAttribute}]`).forEach(node => {
            node.removeAttribute(collapsedAttribute);
            node.removeAttribute('data-pomme-adblock-reason');
        });
        delete window[stateKey];
        return;
    }

    const blocked = new Set();
    for (const rawUrl of blockedUrls) {
        try {
            const parsed = new URL(rawUrl, document.baseURI);
            parsed.hash = '';
            blocked.add(parsed.href);
        } catch (_) { }
    }

    const collapseCss = `
[${collapsedAttribute}="1"] {
    display: none !important;
    visibility: hidden !important;
    opacity: 0 !important;
    width: 0 !important;
    min-width: 0 !important;
    max-width: 0 !important;
    height: 0 !important;
    min-height: 0 !important;
    max-height: 0 !important;
    margin: 0 !important;
    padding: 0 !important;
    border: 0 !important;
    overflow: hidden !important;
    pointer-events: none !important;
}
`;

    for (const selectorBlock of selectorBlocks) {
        if (!selectorBlock)
            continue;

        const style = document.createElement('style');
        style.setAttribute(styleAttribute, 'selectors');
        style.textContent = `${selectorBlock}{display:none!important;visibility:hidden!important;opacity:0!important;width:0!important;min-width:0!important;max-width:0!important;height:0!important;min-height:0!important;max-height:0!important;margin:0!important;padding:0!important;border:0!important;overflow:hidden!important;pointer-events:none!important;}`;
        (document.head || document.documentElement).appendChild(style);
    }

    const collapseStyle = document.createElement('style');
    collapseStyle.setAttribute(styleAttribute, 'collapse');
    collapseStyle.textContent = collapseCss;
    (document.head || document.documentElement).appendChild(collapseStyle);

    const protectedTags = new Set(['HTML', 'BODY', 'MAIN', 'ARTICLE']);
    const visualResourceTags = new Set(['IMG', 'IFRAME', 'VIDEO', 'AUDIO', 'SOURCE', 'EMBED', 'OBJECT', 'INS']);
    const ignoredEmptyTags = new Set(['SCRIPT', 'STYLE', 'LINK', 'META', 'NOSCRIPT', 'TEMPLATE', 'BR']);
    const adHintPattern = /(?:^|[\s_\-.:/])(ad(?:s|vert(?:isement|ising)?)?|banner|sponsor(?:ed|isé|ise)?|promo(?:tion)?|publicit(?:é|e)|dfp|gpt)(?:$|[\s_\-.:/\d])/i;
    const harmlessTextPattern = /^(?:publicit(?:é|e)|advertisement|sponsor(?:ed|isé|ise)?|annonce|ad|ads|fermer|close|x|×|—|-)$/i;

    const genericAdSelectors = [
        'ins.adsbygoogle',
        '[data-ad]',
        '[data-ads]',
        '[data-ad-slot]',
        '[data-ad-unit]',
        '[data-ad-client]',
        '[data-ad-container]',
        '[data-testid*="advert" i]',
        '[data-testid*="sponsor" i]',
        '[aria-label*="advertisement" i]',
        '[aria-label*="publicité" i]',
        '[aria-label*="publicite" i]',
        '[id^="google_ads_"]',
        '[id*="div-gpt-ad" i]',
        '[id^="ad_"]',
        '[id^="ad-"]',
        '[id*="_ad_" i]',
        '[id*="-ad-" i]',
        '[id*="advert" i]',
        '[class~="ad"]',
        '[class~="ads"]',
        '[class~="advert"]',
        '[class~="advertisement"]',
        '[class*="ad-container" i]',
        '[class*="ad_container" i]',
        '[class*="ad-wrapper" i]',
        '[class*="ad_wrapper" i]',
        '[class*="advertisement" i]',
        '[class*="sponsored" i]',
        '[class*="sponsorisé" i]',
        '[class*="publicite" i]',
        '[class*="publicité" i]'
    ];

    const youtubeAdSelectors = [
        '#masthead-ad',
        '#player-ads',
        'ytd-ad-slot-renderer',
        'ytd-display-ad-renderer',
        'ytd-promoted-video-renderer',
        'ytd-promoted-sparkles-web-renderer',
        'ytd-in-feed-ad-layout-renderer',
        'ytd-companion-slot-renderer',
        '.ytp-ad-module',
        '.ytp-ad-overlay-container',
        '.video-ads.ytp-ad-module'
    ];

    const commonAdSelectors = (isYouTube ? youtubeAdSelectors : genericAdSelectors).join(',');
    const protectedPlayerSelectors = isYouTube
        ? '#movie_player,#player,#player-container,#player-container-inner,#ytd-player,ytd-player,.html5-video-player,.html5-main-video,video.video-stream,#primary-inner,#columns'
        : 'video,audio';

    function isProtectedPlayerElement(element) {
        if (!(element instanceof Element))
            return false;

        try {
            if (element.matches(protectedPlayerSelectors))
                return true;

            const protectedAncestor = element.closest(protectedPlayerSelectors);
            if (protectedAncestor) {
                // Sur YouTube, on ne protège que le lecteur lui-même et ses descendants.
                // Hors YouTube, un élément média principal ne doit jamais être replié.
                return true;
            }
        } catch (_) { }

        return false;
    }

    function normalizeUrl(value) {
        if (!value)
            return '';

        try {
            const parsed = new URL(value, document.baseURI);
            parsed.hash = '';
            return parsed.href;
        } catch (_) {
            return '';
        }
    }

    function getElementUrls(element) {
        const urls = [];
        const attributes = ['src', 'data-src', 'poster', 'href', 'data-url'];
        for (const attribute of attributes) {
            const value = element.getAttribute && element.getAttribute(attribute);
            const normalized = normalizeUrl(value);
            if (normalized)
                urls.push(normalized);
        }

        const srcset = element.getAttribute && element.getAttribute('srcset');
        if (srcset) {
            for (const entry of srcset.split(',')) {
                const value = entry.trim().split(/\s+/)[0];
                const normalized = normalizeUrl(value);
                if (normalized)
                    urls.push(normalized);
            }
        }

        return urls;
    }

    function isBlockedResourceElement(element) {
        if (!(element instanceof Element) || !visualResourceTags.has(element.tagName))
            return false;

        for (const url of getElementUrls(element)) {
            if (blocked.has(url))
                return true;
        }

        return false;
    }

    function hasAdHint(element) {
        if (!(element instanceof Element))
            return false;

        const values = [
            element.id,
            element.className && typeof element.className === 'string' ? element.className : '',
            element.getAttribute('name'),
            element.getAttribute('title'),
            element.getAttribute('aria-label'),
            element.getAttribute('data-testid'),
            element.getAttribute('data-ad'),
            element.getAttribute('data-ad-slot'),
            element.getAttribute('data-ad-unit')
        ].filter(Boolean).join(' ');

        return adHintPattern.test(values);
    }

    function visibleText(element) {
        if (!(element instanceof Element))
            return '';

        const text = (element.innerText || element.textContent || '')
            .replace(/\s+/g, ' ')
            .trim();

        return harmlessTextPattern.test(text) ? '' : text;
    }

    function isAlreadyInvisible(element) {
        if (!(element instanceof Element))
            return true;

        if (element.hasAttribute(collapsedAttribute))
            return true;

        try {
            const style = getComputedStyle(element);
            return style.display === 'none'
                || style.visibility === 'hidden'
                || style.opacity === '0';
        } catch (_) {
            return false;
        }
    }

    function hasMeaningfulContent(element) {
        if (!(element instanceof Element))
            return false;

        if (visibleText(element).length > 32)
            return true;

        const meaningfulSelector = 'button,input,textarea,select,video:not([data-pomme-adblock-collapsed]),audio:not([data-pomme-adblock-collapsed]),iframe:not([data-pomme-adblock-collapsed]),canvas,svg,a[href]';
        try {
            for (const node of element.querySelectorAll(meaningfulSelector)) {
                if (isAlreadyInvisible(node))
                    continue;

                const tag = node.tagName;
                const rect = node.getBoundingClientRect();
                const isVisualMedia = tag === 'VIDEO'
                    || tag === 'AUDIO'
                    || tag === 'IFRAME'
                    || tag === 'CANVAS'
                    || tag === 'SVG';

                if (isVisualMedia && rect.width > 24 && rect.height > 18)
                    return true;

                if (visibleText(node).length > 0)
                    return true;
            }
        } catch (_) { }

        for (const child of element.children) {
            if (ignoredEmptyTags.has(child.tagName))
                continue;
            if (child.hasAttribute(collapsedAttribute) || isAlreadyInvisible(child))
                continue;
            if (isBlockedResourceElement(child))
                continue;

            const rect = child.getBoundingClientRect();
            if (rect.width > 8 && rect.height > 8)
                return true;
        }

        return false;
    }

    function isEffectivelyEmpty(element) {
        if (!(element instanceof Element))
            return false;

        if (visibleText(element).length > 0)
            return false;

        return !hasMeaningfulContent(element);
    }

    function collapse(element, reason) {
        if (!(element instanceof HTMLElement) && !(element instanceof SVGElement))
            return false;
        if (protectedTags.has(element.tagName) || isProtectedPlayerElement(element))
            return false;

        if (element.getAttribute(collapsedAttribute) === '1')
            return false;

        element.setAttribute(collapsedAttribute, '1');
        if (reason)
            element.setAttribute('data-pomme-adblock-reason', reason);
        return true;
    }

    function collapseParents(startElement) {
        let current = startElement;

        for (let depth = 0; depth < 4; depth++) {
            const parent = current && current.parentElement;
            if (!parent || protectedTags.has(parent.tagName) || isProtectedPlayerElement(parent))
                break;

            const strongHint = hasAdHint(parent);
            const emptyAfterChild = isEffectivelyEmpty(parent);

            // Sur YouTube, le repli par simple détection de vide peut remonter jusqu'au
            // lecteur. On ne replie donc un parent que s'il porte un indice publicitaire.
            if (isYouTube && !strongHint)
                break;
            const childCount = Array.from(parent.children)
                .filter(child => !ignoredEmptyTags.has(child.tagName))
                .length;

            if (!strongHint && !(emptyAfterChild && childCount <= 3))
                break;

            const rect = parent.getBoundingClientRect();
            const style = getComputedStyle(parent);
            const reservesSpace = rect.height >= 18
                || rect.width >= 80
                || parseFloat(style.minHeight || '0') >= 18
                || parseFloat(style.height || '0') >= 18;

            if (!strongHint && !reservesSpace)
                break;

            collapse(parent, strongHint ? 'ad-parent' : 'empty-parent');
            current = parent;
        }
    }

    function collapseAdElement(element, reason) {
        if (!(element instanceof Element))
            return;

        collapse(element, reason);
        collapseParents(element);
    }

    function queryWithin(root, selector) {
        const result = [];
        if (!root || !selector)
            return result;

        try {
            if (root instanceof Element && root.matches(selector))
                result.push(root);

            if (typeof root.querySelectorAll === 'function')
                result.push(...root.querySelectorAll(selector));
        } catch (_) { }

        return result;
    }

    function scanSelectorMatches(root) {
        for (const selectorBlock of selectorBlocks) {
            for (const element of queryWithin(root, selectorBlock)) {
                if (!isProtectedPlayerElement(element))
                    collapseAdElement(element, 'filter-list');
            }
        }
    }

    function scanCommonAdSlots(root) {
        for (const element of queryWithin(root, commonAdSelectors)) {
            if (isProtectedPlayerElement(element))
                continue;

            if (element.matches('ins.adsbygoogle,[data-ad],[data-ad-slot],[data-ad-unit],[id^="google_ads_"],[id*="div-gpt-ad" i],#masthead-ad,#player-ads,ytd-ad-slot-renderer,ytd-display-ad-renderer,ytd-promoted-video-renderer,ytd-promoted-sparkles-web-renderer,ytd-in-feed-ad-layout-renderer,ytd-companion-slot-renderer,.ytp-ad-module,.ytp-ad-overlay-container')
                || isEffectivelyEmpty(element)
                || isBlockedResourceElement(element)) {
                collapseAdElement(element, 'ad-slot');
            }
        }
    }

    function scanBlockedResources(root) {
        const selector = 'img[src],img[data-src],iframe[src],video[src],audio[src],source[src],embed[src],object[data],ins iframe[src]';
        for (const element of queryWithin(root, selector)) {
            if (!isProtectedPlayerElement(element) && isBlockedResourceElement(element))
                collapseAdElement(element, 'blocked-resource');
        }
    }

    function scanShadowRoots(root) {
        const elements = [];
        if (root instanceof Element)
            elements.push(root);
        if (root && typeof root.querySelectorAll === 'function')
            elements.push(...root.querySelectorAll('*'));

        for (const element of elements) {
            if (element.shadowRoot)
                scan(element.shadowRoot);
        }
    }

    function scan(root) {
        if (!root || !document.documentElement.contains(root instanceof DocumentFragment ? root.host : root)) {
            if (!(root instanceof Document) && !(root instanceof DocumentFragment))
                return;
        }

        scanSelectorMatches(root);
        scanCommonAdSlots(root);
        scanBlockedResources(root);
        scanShadowRoots(root);
    }

    let pendingRoots = new Set();
    let scanTimer = 0;

    function scheduleScan(root) {
        if (root)
            pendingRoots.add(root);

        if (scanTimer)
            return;

        scanTimer = window.setTimeout(() => {
            scanTimer = 0;
            const roots = Array.from(pendingRoots);
            pendingRoots.clear();

            if (roots.length === 0 || roots.length > 24) {
                scan(document);
                return;
            }

            for (const scanRoot of roots)
                scan(scanRoot);
        }, 90);
    }

    const observer = new MutationObserver(mutations => {
        for (const mutation of mutations) {
            if (mutation.type === 'childList') {
                for (const node of mutation.addedNodes) {
                    if (node instanceof Element || node instanceof DocumentFragment)
                        scheduleScan(node);
                }
            } else if (mutation.target instanceof Element) {
                scheduleScan(mutation.target);
            }
        }
    });

    if (document.documentElement) {
        observer.observe(document.documentElement, {
            childList: true,
            subtree: true,
            attributes: true,
            // Pas de « style » : les animations le modifient à chaque image et
            // déclenchaient un nouveau balayage en continu.
            attributeFilter: ['src', 'srcset', 'data-src', 'data-ad', 'data-ad-slot', 'data-ad-unit', 'class', 'id', 'hidden']
        });
    }

    const errorHandler = event => {
        const target = event.target;
        if (!(target instanceof Element)
            || !visualResourceTags.has(target.tagName)
            || isProtectedPlayerElement(target))
            return;

        if (isBlockedResourceElement(target) || hasAdHint(target) || hasAdHint(target.parentElement))
            collapseAdElement(target, 'failed-ad-resource');
    };

    document.addEventListener('error', errorHandler, true);

    window[stateKey] = {
        addBlocked(urls) {
            let added = false;
            for (const rawUrl of urls) {
                const normalized = normalizeUrl(rawUrl);
                if (normalized && !blocked.has(normalized)) {
                    blocked.add(normalized);
                    added = true;
                }
            }

            if (added) {
                scanBlockedResources(document);
                scanCommonAdSlots(document);
            }
        },
        destroy(restoreCollapsed) {
            observer.disconnect();
            document.removeEventListener('error', errorHandler, true);
            if (scanTimer)
                window.clearTimeout(scanTimer);

            if (restoreCollapsed) {
                document.querySelectorAll(`[${collapsedAttribute}]`).forEach(node => {
                    node.removeAttribute(collapsedAttribute);
                    node.removeAttribute('data-pomme-adblock-reason');
                });
            }
        }
    };

    scan(document);
})();
""";

            await _webView.CoreWebView2.ExecuteScriptAsync(script).ConfigureAwait(true);
            _cosmeticInjected = true;
            _cosmeticInjectedEnabled = enabled;
        }

        private static IReadOnlyList<string> BuildSelectorBlocks(IReadOnlyList<string> selectors)
        {
            if (selectors.Count == 0)
                return Array.Empty<string>();

            var blocks = new List<string>();
            var currentSelectors = new List<string>();
            int currentLength = 0;

            foreach (string selector in selectors)
            {
                if (string.IsNullOrWhiteSpace(selector))
                    continue;

                int additionalLength = selector.Length + 1;
                if (currentSelectors.Count >= 550 || currentLength + additionalLength > 42_000)
                {
                    AddSelectorBlock(blocks, currentSelectors);
                    currentSelectors.Clear();
                    currentLength = 0;
                }

                currentSelectors.Add(selector);
                currentLength += additionalLength;
            }

            AddSelectorBlock(blocks, currentSelectors);
            return blocks;
        }

        private static void AddSelectorBlock(List<string> blocks, List<string> selectors)
        {
            if (selectors.Count == 0)
                return;

            var builder = new StringBuilder();
            builder.AppendJoin(',', selectors);
            blocks.Add(builder.ToString());
        }

        private static string RemoveFragment(string url)
        {
            int hashIndex = url.IndexOf('#');
            return hashIndex >= 0 ? url[..hashIndex] : url;
        }

        private void NotifyUpdatedForBlockedRequest(int count)
        {
            long now = Environment.TickCount64;
            long previous = Interlocked.Read(ref _lastUpdateNotificationTick);
            if (count == 1 || count % 10 == 0 || now - previous >= 150)
            {
                Interlocked.Exchange(ref _lastUpdateNotificationTick, now);
                _updateNotificationTimer?.Stop();
                Updated?.Invoke(this);
                return;
            }

            _updateNotificationTimer ??= new DeferredAction(_webView, TimeSpan.FromMilliseconds(180), () =>
            {
                if (_disposed)
                    return;
                Interlocked.Exchange(ref _lastUpdateNotificationTick, Environment.TickCount64);
                Updated?.Invoke(this);
            });
            _updateNotificationTimer.Restart();
        }

        private void Module_RulesChanged() => _webView.Post(() => _ = RefreshFilteringSafeAsync());

        private void Module_StateChanged() => _webView.Post(() => _ = RefreshFilteringSafeAsync());

        private async Task RefreshFilteringSafeAsync()
        {
            try { await RefreshFilteringAsync().ConfigureAwait(true); } catch { }
        }

        private void UpdateDocumentHost(string? url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                _documentHost = AdBlockDomain.NormalizeHost(uri.Host);
            else
                _documentHost = string.Empty;
        }

        private static bool IsYouTubeHost(string? host)
        {
            string normalized = AdBlockDomain.NormalizeHost(host ?? string.Empty);
            return normalized.Equals("youtube.com", StringComparison.OrdinalIgnoreCase)
                || normalized.EndsWith(".youtube.com", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("youtu.be", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsProtectedPlaybackRequest(
            Uri requestUri,
            string documentHost,
            AdBlockResourceType resourceType)
        {
            if (!IsYouTubeHost(documentHost))
                return false;

            string requestHost = AdBlockDomain.NormalizeHost(requestUri.Host);
            bool isGoogleVideo = requestHost.Equals("googlevideo.com", StringComparison.OrdinalIgnoreCase)
                || requestHost.EndsWith(".googlevideo.com", StringComparison.OrdinalIgnoreCase);

            if (isGoogleVideo
                && requestUri.AbsolutePath.Contains("/videoplayback", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // L'API player fournit les informations nécessaires au démarrage et au
            // changement de qualité. La bloquer peut produire un lecteur vide.
            if (requestHost.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase)
                && requestUri.AbsolutePath.Contains("/youtubei/v1/player", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return resourceType == AdBlockResourceType.Media
                && (isGoogleVideo || requestHost.EndsWith("youtube.com", StringComparison.OrdinalIgnoreCase));
        }

        private static AdBlockResourceType MapResourceType(CoreWebView2WebResourceContext context)
            => context switch
            {
                CoreWebView2WebResourceContext.Document => AdBlockResourceType.Document,
                CoreWebView2WebResourceContext.Stylesheet => AdBlockResourceType.Stylesheet,
                CoreWebView2WebResourceContext.Image => AdBlockResourceType.Image,
                CoreWebView2WebResourceContext.Media => AdBlockResourceType.Media,
                CoreWebView2WebResourceContext.Font => AdBlockResourceType.Font,
                CoreWebView2WebResourceContext.Script => AdBlockResourceType.Script,
                CoreWebView2WebResourceContext.XmlHttpRequest => AdBlockResourceType.XmlHttpRequest,
                CoreWebView2WebResourceContext.Fetch => AdBlockResourceType.Fetch,
                CoreWebView2WebResourceContext.Ping => AdBlockResourceType.Ping,
                CoreWebView2WebResourceContext.Websocket => AdBlockResourceType.WebSocket,
                _ => AdBlockResourceType.Other
            };

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _updateNotificationTimer?.Dispose();
            _cosmeticRefreshTimer?.Dispose();
            _module.RulesChanged -= Module_RulesChanged;
            _module.StateChanged -= Module_StateChanged;

            try
            {
                if (_webView.CoreWebView2 is { } core)
                {
                    core.WebResourceRequested -= CoreWebView2_WebResourceRequested;
                    core.NavigationStarting -= CoreWebView2_NavigationStarting;
                    core.NavigationCompleted -= WebView_NavigationCompleted;
                }
            }
            catch (InvalidOperationException)
            {
                // Vue déjà fermée : ses événements sont partis avec elle.
            }
        }

        /// <summary>
        /// Action différée d'un délai, repoussée à chaque nouvel appel, exécutée sur le fil de
        /// l'interface de la vue (remplace le minuteur propre à WPF).
        /// </summary>
        private sealed class DeferredAction : IDisposable
        {
            private readonly Timer _timer;
            private readonly TimeSpan _delay;
            private int _generation;
            private bool _disposed;

            public DeferredAction(IAdBlockWebView view, TimeSpan delay, Action action)
            {
                _delay = delay;
                _timer = new Timer(state =>
                {
                    int generation = Volatile.Read(ref _generation);
                    view.Post(() =>
                    {
                        // Arrêté ou relancé entre-temps : ce déclenchement ne compte plus.
                        if (!_disposed && generation == Volatile.Read(ref _generation))
                            action();
                    });
                }, null, Timeout.Infinite, Timeout.Infinite);
            }

            public void Restart()
            {
                if (_disposed)
                    return;
                Interlocked.Increment(ref _generation);
                _timer.Change(_delay, Timeout.InfiniteTimeSpan);
            }

            public void Stop()
            {
                Interlocked.Increment(ref _generation);
                if (!_disposed)
                    _timer.Change(Timeout.Infinite, Timeout.Infinite);
            }

            public void Dispose()
            {
                _disposed = true;
                _timer.Dispose();
            }
        }
    }
}
