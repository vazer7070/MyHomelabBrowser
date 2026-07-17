using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes;
using System;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.Flash
{
    public sealed class FlashDecisionService
    {
        private readonly WebView2 _web;
        private readonly SettingsService _settings;
        private readonly LegacyLauncher _legacy;
        private readonly object _networkSync = new();
        private string? _networkSwfUrl;

        public FlashDecisionService(
            WebView2 web,
            SettingsService settings,
            LegacyLauncher legacyLauncher)
        {
            _web = web ?? throw new ArgumentNullException(nameof(web));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _legacy = legacyLauncher ?? throw new ArgumentNullException(nameof(legacyLauncher));
        }

        public FlashMode DecideInitialMode(
            Uri uri,
            bool forceLegacyOnce,
            FlashDetectionResult detection)
        {
            if (uri == null || !_settings.Settings.EnableFlashSupport)
                return FlashMode.None;

            if (forceLegacyOnce)
                return _legacy.CanLaunch() ? FlashMode.Legacy : FlashMode.None;

            FlashRuleMode rule = GetRuleWithSubdomainFallback(uri);

            if (rule == FlashRuleMode.Disabled)
                return FlashMode.None;

            if (rule == FlashRuleMode.Legacy)
                return _legacy.CanLaunch() ? FlashMode.Legacy : FlashMode.None;

            if (rule == FlashRuleMode.Ruffle)
                return FlashMode.Ruffle;

            // En mode automatique, la décision est apprise par contenu SWF et jamais
            // par nom de domaine. Un contenu ne passe automatiquement en Legacy qu'après
            // plusieurs échecs Ruffle et un lancement Legacy réellement réussi.
            return FlashCompatibilityMemory.GetRecommendedMode(uri, detection, _legacy.CanLaunch());
        }

        public FlashMode DecideInitialMode(Uri uri, bool forceLegacyOnce) =>
            DecideInitialMode(uri, forceLegacyOnce, FlashDetectionResult.None);

        public void ResetNavigationEvidence()
        {
            lock (_networkSync)
                _networkSwfUrl = null;
        }

        public void RegisterNetworkResource(string? resourceUrl)
        {
            if (!IsSwfResource(resourceUrl))
                return;

            lock (_networkSync)
                _networkSwfUrl ??= resourceUrl;
        }

        public async Task<FlashDetectionResult> DetectAsync()
        {
            if (!CanRunAutomaticDetection())
                return FlashDetectionResult.None;

            Uri? pageUri = _web.Source;
            if (pageUri != null && IsSwfResource(pageUri.AbsoluteUri))
            {
                return new FlashDetectionResult(
                    true,
                    FlashEvidenceKind.DirectSwfNavigation,
                    pageUri.AbsoluteUri,
                    null,
                    1d,
                    BuildNetworkHints(pageUri, pageUri));
            }

            string? networkUrl;
            lock (_networkSync)
                networkUrl = _networkSwfUrl;

            if (!string.IsNullOrWhiteSpace(networkUrl))
            {
                Uri? swfUri = Uri.TryCreate(networkUrl, UriKind.Absolute, out Uri? parsed) ? parsed : null;
                return new FlashDetectionResult(
                    true,
                    FlashEvidenceKind.NetworkRequest,
                    networkUrl,
                    null,
                    0.96d,
                    BuildNetworkHints(pageUri, swfUri));
            }

            const string script = """
(() => {
    const FLASH_CLASS_ID = 'd27cdb6e-ae6d-11cf-96b8-444553540000';

    const absoluteUrl = value => {
        if (!value || typeof value !== 'string') return null;
        const trimmed = value.trim();
        if (!trimmed || /^(?:javascript:|about:|data:text\/html)/i.test(trimmed)) return null;
        try { return new URL(trimmed, document.baseURI).href; }
        catch { return trimmed; }
    };

    const isSwfUrl = value => {
        const absolute = absoluteUrl(value);
        if (!absolute) return false;
        try { return /\.swf$/i.test(new URL(absolute, document.baseURI).pathname); }
        catch { return /\.swf(?:[?#].*)?$/i.test(absolute); }
    };

    const isFlashMime = value =>
        typeof value === 'string' &&
        /^(?:application\/(?:x-shockwave-flash|futuresplash)|application\/vnd\.adobe\.flash\.movie)$/i.test(value.trim());

    const isInsideInactiveMarkup = element => !!element?.closest?.('head, template, noscript');

    const isAdContainer = element => {
        for (let current = element; current && current !== document.documentElement; current = current.parentElement) {
            const marker = [
                current.id || '',
                typeof current.className === 'string' ? current.className : '',
                current.getAttribute?.('data-ad-slot') || '',
                current.getAttribute?.('data-ad-unit') || '',
                current.getAttribute?.('aria-label') || ''
            ].join(' ').toLowerCase();

            if (/(^|[\s_-])(ad|ads|advert|advertisement|banner-ad|sponsor|sponsored|dfp|gpt)([\s_-]|$)/i.test(marker))
                return true;
        }
        return false;
    };

    const isVisibleContent = element => {
        if (!element || !element.isConnected || isInsideInactiveMarkup(element)) return false;
        const style = getComputedStyle(element);
        if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse') return false;
        if (Number.parseFloat(style.opacity || '1') <= 0.01) return false;
        if (element.closest('[hidden], [aria-hidden="true"]')) return false;

        const rect = element.getBoundingClientRect();
        const attrWidth = Number.parseFloat(element.getAttribute?.('width') || '0');
        const attrHeight = Number.parseFloat(element.getAttribute?.('height') || '0');
        return Math.max(rect.width, attrWidth) >= 24 && Math.max(rect.height, attrHeight) >= 24;
    };

    const getObjectParam = (element, name) => {
        if (!element || element.tagName?.toLowerCase() !== 'object') return null;
        for (const param of element.querySelectorAll(':scope > param')) {
            if ((param.getAttribute('name') || '').trim().toLowerCase() === name.toLowerCase())
                return param.getAttribute('value');
        }
        return null;
    };

    const readValue = (element, names) => {
        for (const name of names) {
            const attr = element?.getAttribute?.(name);
            if (attr != null && String(attr).trim()) return String(attr).trim();
            const param = getObjectParam(element, name);
            if (param != null && String(param).trim()) return String(param).trim();
        }
        return null;
    };

    const parseBool = value => /^(?:1|true|yes)$/i.test(String(value || '').trim());

    const collectHints = (element, sourceUrl, dynamic = false) => {
        const absolute = absoluteUrl(sourceUrl);
        let pageOrigin = null;
        let swfOrigin = null;
        try { pageOrigin = location.origin; } catch { }
        try { swfOrigin = absolute ? new URL(absolute, document.baseURI).origin : null; } catch { }

        const rect = element?.getBoundingClientRect?.();
        return {
            flashVars: readValue(element, ['flashvars']),
            baseUrl: absoluteUrl(readValue(element, ['base'])),
            allowScriptAccess: readValue(element, ['allowscriptaccess']),
            allowFullscreen: parseBool(readValue(element, ['allowfullscreen', 'allowFullScreenInteractive'])),
            windowMode: readValue(element, ['wmode']),
            quality: readValue(element, ['quality']),
            scale: readValue(element, ['scale']),
            align: readValue(element, ['salign', 'align']),
            width: Math.max(Number(rect?.width || 0), Number.parseFloat(element?.getAttribute?.('width') || '0')),
            height: Math.max(Number(rect?.height || 0), Number.parseFloat(element?.getAttribute?.('height') || '0')),
            isDynamicEmbed: !!dynamic,
            isCrossOrigin: !!(pageOrigin && swfOrigin && pageOrigin !== swfOrigin),
            pageOrigin,
            swfOrigin
        };
    };

    const result = (evidence, sourceUrl, targetElement, confidence, element, dynamic = false) => ({
        detected: true,
        evidence,
        sourceUrl: absoluteUrl(sourceUrl),
        targetElement: targetElement || null,
        confidence,
        hints: collectHints(element, sourceUrl, dynamic)
    });

    const existingPlayer = document.querySelector('ruffle-player, ruffle-object, ruffle-embed');
    if (existingPlayer && isVisibleContent(existingPlayer))
        return result('ExistingRufflePlayer', null, existingPlayer.id || existingPlayer.tagName.toLowerCase(), 1.0, existingPlayer);

    const probeCall = window.__pommeFlashProbe?.lastCall;
    if (probeCall && isSwfUrl(probeCall.sourceUrl)) {
        const target = probeCall.targetElement ? document.getElementById(probeCall.targetElement) : null;
        if (!target || (isVisibleContent(target) && !isAdContainer(target))) {
            const absolute = absoluteUrl(probeCall.sourceUrl);
            let pageOrigin = null;
            let swfOrigin = null;
            try { pageOrigin = location.origin; } catch { }
            try { swfOrigin = absolute ? new URL(absolute, document.baseURI).origin : null; } catch { }

            return {
                detected: true,
                evidence: probeCall.kind === 'swfobject' ? 'SwfObjectCall' : (probeCall.kind === 'embed' ? 'Embed' : 'Object'),
                sourceUrl: absolute,
                targetElement: probeCall.targetElement || null,
                confidence: 0.995,
                hints: {
                    flashVars: probeCall.flashVars || null,
                    baseUrl: absoluteUrl(probeCall.baseUrl),
                    allowScriptAccess: probeCall.allowScriptAccess || null,
                    allowFullscreen: !!probeCall.allowFullscreen,
                    windowMode: probeCall.windowMode || null,
                    quality: probeCall.quality || null,
                    scale: probeCall.scale || null,
                    align: probeCall.align || null,
                    width: Number(probeCall.width || 0),
                    height: Number(probeCall.height || 0),
                    isDynamicEmbed: true,
                    isCrossOrigin: !!(pageOrigin && swfOrigin && pageOrigin !== swfOrigin),
                    pageOrigin,
                    swfOrigin
                }
            };
        }
    }

    for (const embed of document.querySelectorAll('embed')) {
        if (!isVisibleContent(embed) || isAdContainer(embed)) continue;
        const src = embed.getAttribute('src');
        if (isFlashMime(embed.getAttribute('type')) || isSwfUrl(src))
            return result('Embed', src, embed.id || null, 0.99, embed);
    }

    for (const object of document.querySelectorAll('object')) {
        if (!isVisibleContent(object) || isAdContainer(object)) continue;

        const data = object.getAttribute('data');
        if (isFlashMime(object.getAttribute('type')) ||
            isSwfUrl(data) ||
            (object.getAttribute('classid') || '').toLowerCase().includes(FLASH_CLASS_ID)) {
            const source = data || getObjectParam(object, 'movie') || getObjectParam(object, 'src');
            return result('Object', source, object.id || null, 0.99, object);
        }

        for (const param of object.querySelectorAll(':scope > param')) {
            const name = (param.getAttribute('name') || '').trim().toLowerCase();
            const value = param.getAttribute('value');
            if ((name === 'movie' || name === 'src') && isSwfUrl(value))
                return result('ObjectParameter', value, object.id || null, 0.98, object);
        }
    }

    const embedCall = /(?:window\.)?swfobject\s*\.\s*embedSWF\s*\(\s*(['"])([^'"]+\.swf(?:\?[^'"]*)?)\1\s*,\s*(['"])([^'"]+)\3/i;
    for (const scriptElement of document.scripts) {
        if (scriptElement.src || isInsideInactiveMarkup(scriptElement)) continue;
        const match = embedCall.exec(scriptElement.textContent || '');
        if (!match || !isSwfUrl(match[2])) continue;

        const target = document.getElementById(match[4]);
        if (!target || !isVisibleContent(target) || isAdContainer(target)) continue;
        if (target.querySelector('video, audio, iframe[src], canvas')) continue;

        return result('SwfObjectCall', match[2], match[4], 0.94, target, true);
    }

    for (const frame of document.querySelectorAll('iframe')) {
        try {
            const doc = frame.contentDocument;
            if (!doc) continue;
            const flash = doc.querySelector('embed[type="application/x-shockwave-flash"], embed[src*=".swf" i], object[type="application/x-shockwave-flash"], object[data*=".swf" i]');
            if (flash && isVisibleContent(frame) && !isAdContainer(frame)) {
                const source = flash.getAttribute('src') || flash.getAttribute('data');
                return result(flash.tagName.toLowerCase() === 'embed' ? 'Embed' : 'Object', source, frame.id || null, 0.92, frame, true);
            }
        } catch { }
    }

    return { detected: false, evidence: 'None', sourceUrl: null, targetElement: null, confidence: 0, hints: null };
})()
""";

            try
            {
                string raw = await _web.ExecuteScriptAsync(script).ConfigureAwait(true);
                return ParseDetectionResult(raw);
            }
            catch
            {
                return FlashDetectionResult.None;
            }
        }

        public Task<bool> DetectFlashRequirementAsync() => Task.FromResult(false);

        public async Task<bool> DetectFlashDomAsync() =>
            (await DetectAsync().ConfigureAwait(true)).Detected;

        private static FlashDetectionResult ParseDetectionResult(string raw)
        {
            try
            {
                using JsonDocument doc = JsonDocument.Parse(raw);
                JsonElement root = doc.RootElement;

                if (root.ValueKind == JsonValueKind.String)
                {
                    string? nested = root.GetString();
                    if (string.IsNullOrWhiteSpace(nested))
                        return FlashDetectionResult.None;
                    using JsonDocument nestedDoc = JsonDocument.Parse(nested);
                    return ParseRoot(nestedDoc.RootElement);
                }

                return ParseRoot(root);
            }
            catch
            {
                return FlashDetectionResult.None;
            }
        }

        private static FlashDetectionResult ParseRoot(JsonElement root)
        {
            bool detected = root.TryGetProperty("detected", out JsonElement detectedElement) &&
                            detectedElement.ValueKind == JsonValueKind.True;
            if (!detected)
                return FlashDetectionResult.None;

            string evidenceText = root.TryGetProperty("evidence", out JsonElement evidenceElement)
                ? evidenceElement.GetString() ?? "None"
                : "None";

            _ = Enum.TryParse(evidenceText, ignoreCase: true, out FlashEvidenceKind evidence);

            string? sourceUrl = GetNullableString(root, "sourceUrl");
            string? target = GetNullableString(root, "targetElement");
            double confidence = root.TryGetProperty("confidence", out JsonElement confidenceElement) &&
                                confidenceElement.TryGetDouble(out double value)
                ? value
                : 0.9d;

            FlashRuntimeHints hints = FlashRuntimeHints.Empty;
            if (root.TryGetProperty("hints", out JsonElement hintsElement) &&
                hintsElement.ValueKind == JsonValueKind.Object)
            {
                hints = new FlashRuntimeHints
                {
                    FlashVars = GetNullableString(hintsElement, "flashVars"),
                    BaseUrl = GetNullableString(hintsElement, "baseUrl"),
                    AllowScriptAccess = GetNullableString(hintsElement, "allowScriptAccess"),
                    AllowFullscreen = GetBool(hintsElement, "allowFullscreen"),
                    WindowMode = GetNullableString(hintsElement, "windowMode"),
                    Quality = GetNullableString(hintsElement, "quality"),
                    Scale = GetNullableString(hintsElement, "scale"),
                    Align = GetNullableString(hintsElement, "align"),
                    Width = GetDouble(hintsElement, "width"),
                    Height = GetDouble(hintsElement, "height"),
                    IsDynamicEmbed = GetBool(hintsElement, "isDynamicEmbed"),
                    IsCrossOrigin = GetBool(hintsElement, "isCrossOrigin"),
                    PageOrigin = GetNullableString(hintsElement, "pageOrigin"),
                    SwfOrigin = GetNullableString(hintsElement, "swfOrigin")
                };
            }

            return new FlashDetectionResult(true, evidence, sourceUrl, target, confidence, hints);
        }

        private static FlashRuntimeHints BuildNetworkHints(Uri? pageUri, Uri? swfUri)
        {
            string? pageOrigin = pageUri?.GetLeftPart(UriPartial.Authority);
            string? swfOrigin = swfUri?.GetLeftPart(UriPartial.Authority);
            return new FlashRuntimeHints
            {
                PageOrigin = pageOrigin,
                SwfOrigin = swfOrigin,
                IsCrossOrigin = !string.IsNullOrWhiteSpace(pageOrigin) &&
                                !string.IsNullOrWhiteSpace(swfOrigin) &&
                                !string.Equals(pageOrigin, swfOrigin, StringComparison.OrdinalIgnoreCase),
                IsDynamicEmbed = true
            };
        }

        private static string? GetNullableString(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;

        private static bool GetBool(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

        private static double GetDouble(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double result)
                ? result
                : 0d;

        private bool CanRunAutomaticDetection() =>
            _settings.Settings.EnableFlashSupport && _web.CoreWebView2 != null;

        private static bool IsSwfResource(string? resourceUrl)
        {
            if (string.IsNullOrWhiteSpace(resourceUrl))
                return false;

            if (!Uri.TryCreate(resourceUrl, UriKind.Absolute, out Uri? uri))
                return resourceUrl.Contains(".swf", StringComparison.OrdinalIgnoreCase);

            return uri.AbsolutePath.EndsWith(".swf", StringComparison.OrdinalIgnoreCase);
        }

        private static FlashRuleMode GetRuleWithSubdomainFallback(Uri uri)
        {
            string host = uri.Host?.Trim().ToLowerInvariant() ?? string.Empty;
            if (host.Length == 0)
                return FlashRuleMode.Auto;

            FlashRuleMode direct = FlashDomainRules.GetRule(new Uri("https://" + host));
            if (direct != FlashRuleMode.Auto)
                return direct;

            string[] parts = host.Split('.');
            if (parts.Length < 2)
                return FlashRuleMode.Auto;

            for (int i = 1; i < parts.Length; i++)
            {
                string parent = string.Join(".", parts.Skip(i));
                FlashRuleMode parentRule = FlashDomainRules.GetRule(new Uri("https://" + parent));
                if (parentRule != FlashRuleMode.Auto)
                    return parentRule;
            }

            return FlashRuleMode.Auto;
        }
    }
}
