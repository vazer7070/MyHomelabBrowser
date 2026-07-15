using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes.AdBlock.Models;
using MyHomelabBrowser.classes.AdBlock.Services;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MyHomelabBrowser.classes.AdBlock.Integration
{
    public sealed class AdBlockTabSession : IDisposable
    {
        private const string CosmeticStyleAttribute = "data-pomme-adblock";
        private const int MaximumRememberedBlockedUrls = 320;

        private readonly WebView2 _webView;
        private readonly AdBlockModuleService _module;
        private readonly object _blockedUrlsLock = new();
        private readonly HashSet<string> _blockedResourceUrls = new(StringComparer.OrdinalIgnoreCase);

        private string _documentHost = string.Empty;
        private int _blockedCount;
        private long _lastUpdateNotificationTick;
        private DispatcherTimer? _updateNotificationTimer;
        private DispatcherTimer? _cosmeticRefreshTimer;
        private bool _attached;
        private bool _disposed;

        public event Action<AdBlockTabSession>? Updated;

        public WebView2 WebView => _webView;
        public bool IsPrivate { get; }
        public int BlockedCount => Volatile.Read(ref _blockedCount);
        public string CurrentHost => _documentHost;
        public bool IsProtectionActive => _module.IsFilteringEnabledForHost(_documentHost);

        public AdBlockTabSession(WebView2 webView, AdBlockModuleService module, bool isPrivate)
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
            UpdateDocumentHost(_webView.Source?.AbsoluteUri);

            _webView.CoreWebView2.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            _webView.CoreWebView2.WebResourceRequested += CoreWebView2_WebResourceRequested;
            _webView.CoreWebView2.NavigationStarting += CoreWebView2_NavigationStarting;
            _webView.NavigationCompleted += WebView_NavigationCompleted;

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
                _blockedResourceUrls.Clear();

            _cosmeticRefreshTimer?.Stop();
            Updated?.Invoke(this);
        }

        private async void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            UpdateDocumentHost(_webView.Source?.AbsoluteUri);
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
            if (documentHost.Length == 0 && _webView.Source != null)
                documentHost = AdBlockDomain.NormalizeHost(_webView.Source.Host);

            AdBlockResourceType resourceType = MapResourceType(e.ResourceContext);
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

                _blockedResourceUrls.Add(RemoveFragment(absoluteUrl));
            }
        }

        private void ScheduleCosmeticRefresh()
        {
            if (_disposed)
                return;

            _ = _webView.Dispatcher.BeginInvoke(
                DispatcherPriority.Background,
                new Action(() =>
                {
                    if (_disposed)
                        return;

                    _cosmeticRefreshTimer ??= CreateCosmeticRefreshTimer();
                    _cosmeticRefreshTimer.Stop();
                    _cosmeticRefreshTimer.Start();
                }));
        }

        private DispatcherTimer CreateCosmeticRefreshTimer()
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background, _webView.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(220)
            };

            timer.Tick += async (_, _) =>
            {
                timer.Stop();
                if (_disposed)
                    return;

                try
                {
                    await ApplyCosmeticFilteringAsync().ConfigureAwait(true);
                }
                catch
                {
                    // Une page qui change pendant l'exécution du script peut invalider l'appel.
                }
            };

            return timer;
        }

        private async Task ApplyCosmeticFilteringAsync()
        {
            if (_webView.CoreWebView2 == null)
                return;

            bool enabled = _module.Settings.CosmeticFiltering
                && _module.IsFilteringEnabledForHost(_documentHost);

            IReadOnlyList<string> selectors = enabled
                ? _module.GetCosmeticSelectors(_documentHost)
                : Array.Empty<string>();

            IReadOnlyList<string> selectorBlocks = BuildSelectorBlocks(selectors);
            string[] blockedUrls;

            lock (_blockedUrlsLock)
                blockedUrls = _blockedResourceUrls.ToArray();

            string jsonSelectorBlocks = JsonSerializer.Serialize(selectorBlocks);
            string jsonBlockedUrls = JsonSerializer.Serialize(blockedUrls);
            string enabledJson = enabled ? "true" : "false";

            string script = $$"""
(() => {
    'use strict';

    const enabled = {{enabledJson}};
    const selectorBlocks = {{jsonSelectorBlocks}};
    const blockedUrls = {{jsonBlockedUrls}};
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

    const commonAdSelectors = [
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
    ].join(',');

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

        const meaningfulSelector = 'button,input,textarea,select,video:not([data-pomme-adblock-collapsed]),canvas,svg,a[href]';
        try {
            for (const node of element.querySelectorAll(meaningfulSelector)) {
                if (!isAlreadyInvisible(node) && visibleText(node).length > 0)
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
        if (protectedTags.has(element.tagName))
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
            if (!parent || protectedTags.has(parent.tagName))
                break;

            const strongHint = hasAdHint(parent);
            const emptyAfterChild = isEffectivelyEmpty(parent);
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
            for (const element of queryWithin(root, selectorBlock))
                collapseAdElement(element, 'filter-list');
        }
    }

    function scanCommonAdSlots(root) {
        for (const element of queryWithin(root, commonAdSelectors)) {
            if (element.matches('ins.adsbygoogle,[data-ad],[data-ad-slot],[data-ad-unit],[id^="google_ads_"],[id*="div-gpt-ad" i]')
                || isEffectivelyEmpty(element)
                || isBlockedResourceElement(element)) {
                collapseAdElement(element, 'ad-slot');
            }
        }
    }

    function scanBlockedResources(root) {
        const selector = 'img[src],img[data-src],iframe[src],video[src],audio[src],source[src],embed[src],object[data],ins iframe[src]';
        for (const element of queryWithin(root, selector)) {
            if (isBlockedResourceElement(element))
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
            attributeFilter: ['src', 'srcset', 'data-src', 'data-ad', 'data-ad-slot', 'data-ad-unit', 'class', 'id', 'style', 'hidden']
        });
    }

    const errorHandler = event => {
        const target = event.target;
        if (!(target instanceof Element) || !visualResourceTags.has(target.tagName))
            return;

        if (isBlockedResourceElement(target) || hasAdHint(target) || hasAdHint(target.parentElement))
            collapseAdElement(target, 'failed-ad-resource');
    };

    document.addEventListener('error', errorHandler, true);

    window[stateKey] = {
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

            _updateNotificationTimer ??= CreateUpdateNotificationTimer();
            _updateNotificationTimer.Stop();
            _updateNotificationTimer.Start();
        }

        private DispatcherTimer CreateUpdateNotificationTimer()
        {
            var timer = new DispatcherTimer(DispatcherPriority.Background, _webView.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(180)
            };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Interlocked.Exchange(ref _lastUpdateNotificationTick, Environment.TickCount64);
                Updated?.Invoke(this);
            };
            return timer;
        }

        private void Module_RulesChanged()
        {
            _ = _webView.Dispatcher.InvokeAsync(async () =>
            {
                try { await RefreshFilteringAsync(); } catch { }
            });
        }

        private void Module_StateChanged()
        {
            _ = _webView.Dispatcher.InvokeAsync(async () =>
            {
                try { await RefreshFilteringAsync(); } catch { }
            });
        }

        private void UpdateDocumentHost(string? url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out Uri? uri))
                _documentHost = AdBlockDomain.NormalizeHost(uri.Host);
            else
                _documentHost = string.Empty;
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
            _updateNotificationTimer?.Stop();
            _cosmeticRefreshTimer?.Stop();
            _module.RulesChanged -= Module_RulesChanged;
            _module.StateChanged -= Module_StateChanged;

            if (_webView.CoreWebView2 != null)
            {
                _webView.CoreWebView2.WebResourceRequested -= CoreWebView2_WebResourceRequested;
                _webView.CoreWebView2.NavigationStarting -= CoreWebView2_NavigationStarting;
            }

            _webView.NavigationCompleted -= WebView_NavigationCompleted;
        }
    }
}
