namespace MyHomelabBrowser.classes.Flash
{
    /// <summary>
    /// Sonde injectée avant les scripts du site. Elle mémorise les intégrations Flash
    /// dynamiques, notamment les appels swfobject.embedSWF, sans dépendre d'un domaine.
    /// </summary>
    public static class FlashDocumentProbe
    {
        public const string Script = """
(() => {
    if (window.__pommeFlashProbe) return;

    const state = window.__pommeFlashProbe = {
        calls: [],
        lastCall: null,
        createdAt: Date.now()
    };

    const asString = value => value == null ? null : String(value);
    const asFlashVars = value => {
        if (value == null) return null;
        if (typeof value === 'string') return value;
        if (typeof value !== 'object') return String(value);
        try {
            const params = new URLSearchParams();
            for (const [key, item] of Object.entries(value)) {
                if (item != null) params.set(key, String(item));
            }
            return params.toString();
        } catch { return null; }
    };

    const record = value => {
        try {
            const normalized = Object.assign({ recordedAt: Date.now() }, value || {});
            state.lastCall = normalized;
            state.calls.push(normalized);
            if (state.calls.length > 20) state.calls.shift();
        } catch { }
    };

    const wrapSwfObject = () => {
        try {
            const swfobject = window.swfobject;
            const original = swfobject?.embedSWF;
            if (typeof original !== 'function' || original.__pommeWrapped) return;

            const wrapped = function(...args) {
                const params = args[7] && typeof args[7] === 'object' ? args[7] : {};
                record({
                    kind: 'swfobject',
                    sourceUrl: asString(args[0]),
                    targetElement: asString(args[1]),
                    width: Number.parseFloat(args[2] || '0') || 0,
                    height: Number.parseFloat(args[3] || '0') || 0,
                    flashVars: asFlashVars(args[6]),
                    allowScriptAccess: asString(params.allowScriptAccess || params.allowscriptaccess),
                    allowFullscreen: /^(?:1|true|yes)$/i.test(asString(params.allowFullScreen || params.allowfullscreen) || ''),
                    windowMode: asString(params.wmode),
                    quality: asString(params.quality),
                    scale: asString(params.scale),
                    align: asString(params.salign || params.align),
                    baseUrl: asString(params.base)
                });
                return original.apply(this, args);
            };

            Object.defineProperty(wrapped, '__pommeWrapped', { value: true });
            swfobject.embedSWF = wrapped;
        } catch { }
    };

    const recordElement = element => {
        try {
            if (!(element instanceof Element)) return;
            const tag = element.tagName.toLowerCase();
            if (tag !== 'object' && tag !== 'embed') return;

            const source = element.getAttribute(tag === 'embed' ? 'src' : 'data');
            const type = element.getAttribute('type') || '';
            if (!/\.swf(?:[?#].*)?$/i.test(source || '') &&
                !/application\/(?:x-shockwave-flash|futuresplash)/i.test(type)) return;

            record({
                kind: tag,
                sourceUrl: source,
                targetElement: element.id || null,
                width: Number.parseFloat(element.getAttribute('width') || '0') || 0,
                height: Number.parseFloat(element.getAttribute('height') || '0') || 0
            });
        } catch { }
    };

    const observer = new MutationObserver(mutations => {
        wrapSwfObject();
        for (const mutation of mutations) {
            for (const node of mutation.addedNodes) {
                if (!(node instanceof Element)) continue;
                recordElement(node);
                for (const element of node.querySelectorAll?.('object, embed') || [])
                    recordElement(element);
            }
        }
    });

    observer.observe(document, { childList: true, subtree: true });
    state.observer = observer;

    let attempts = 0;
    const timer = setInterval(() => {
        wrapSwfObject();
        attempts++;
        if (attempts >= 120) clearInterval(timer);
    }, 250);
    state.timer = timer;
    wrapSwfObject();
})()
""";
    }
}
