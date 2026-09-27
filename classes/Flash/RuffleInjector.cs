using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using WpfWebView2 = Microsoft.Web.WebView2.Wpf.WebView2;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.classes.Flash
{
    public static class RuffleInjector
    {
        public static async Task<RuffleInjectionResult> InjectAsync(
            WpfWebView2 webView,
            FlashDetectionResult detection)
        {
            if (webView.CoreWebView2 == null)
                return RuffleInjectionResult.Failed("core-unavailable", false, Tr("WebView2 n'est pas initialisé."));

            // Ruffle vient uniquement des fichiers de l'application : pas de repli vers un CDN.
            bool localAssets = RuffleAssetService.Configure(webView.CoreWebView2);
            if (!localAssets)
            {
                return RuffleInjectionResult.Failed("ruffle-missing", false,
                    Tr("Les fichiers de Ruffle sont absents de cette installation de PommeBrowser."));
            }

            string scriptUrl = RuffleAssetService.LocalScriptUrl;
            string publicPath = RuffleAssetService.LocalBaseUrl;

            string bootstrap = BuildBootstrapScript(scriptUrl, publicPath, detection);
            string rawResult;

            try
            {
                rawResult = await webView.ExecuteScriptAsync(bootstrap).ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                return RuffleInjectionResult.Failed("execute-failed", localAssets, ex.Message);
            }

            string status = DecodeScriptString(rawResult);
            bool success = status is "ok" or "already-present" or "existing-player";

            if (!success && localAssets &&
                (status is "load-failed" or "load-timeout") &&
                File.Exists(RuffleAssetService.MainScriptPath))
            {
                try
                {
                    string source = await File.ReadAllTextAsync(RuffleAssetService.MainScriptPath).ConfigureAwait(false);
                    await webView.ExecuteScriptAsync(source + "\n//# sourceURL=pomme-ruffle.js").ConfigureAwait(true);
                    rawResult = await webView.ExecuteScriptAsync(BuildFinalizeScript(detection)).ConfigureAwait(true);
                    status = DecodeScriptString(rawResult);
                    success = status is "ok" or "already-present" or "existing-player";
                }
                catch (Exception ex)
                {
                    return RuffleInjectionResult.Failed("inline-load-failed", true, ex.Message);
                }
            }

            return success
                ? RuffleInjectionResult.Ok(status, localAssets)
                : RuffleInjectionResult.Failed(status, localAssets, Tr("Ruffle n'a pas créé de lecteur utilisable."));
        }

        public static async Task<RuffleStatus> GetStatusAsync(WpfWebView2 webView)
        {
            const string js = """
(() => {
    const state = window.__pommeRuffle;
    const players = Array.from(document.querySelectorAll('ruffle-player, ruffle-object, ruffle-embed'));

    const visiblePlayers = players.filter(player => {
        try {
            const style = getComputedStyle(player);
            const rect = player.getBoundingClientRect();
            return style.display !== 'none' &&
                   style.visibility !== 'hidden' &&
                   Number.parseFloat(style.opacity || '1') > 0.01 &&
                   rect.width >= 20 && rect.height >= 20;
        } catch { return false; }
    });

    let maxReadyState = 0;
    let loadedPlayerCount = 0;
    let metadata = null;
    let suspendedPlayerCount = 0;

    for (const player of players) {
        try {
            const api = typeof player.ruffle === 'function' ? player.ruffle(1) : player;
            const readyState = Number(api?.readyState ?? player.readyState ?? 0);
            if (Number.isFinite(readyState)) maxReadyState = Math.max(maxReadyState, readyState);

            const playerMetadata = api?.metadata ?? player.metadata ?? null;
            if (playerMetadata) {
                loadedPlayerCount++;
                metadata ||= {
                    width: Number(playerMetadata.width || 0),
                    height: Number(playerMetadata.height || 0),
                    frameRate: Number(playerMetadata.frameRate || 0),
                    numFrames: Number(playerMetadata.numFrames || 0),
                    swfVersion: Number(playerMetadata.swfVersion || 0),
                    isActionScript3: !!playerMetadata.isActionScript3,
                    uncompressedLength: Number(playerMetadata.uncompressedLength || 0)
                };
            }

            if (api?.suspended === true)
                suspendedPlayerCount++;
        } catch (error) {
            state?.errors?.push?.('status-api:' + String(error));
        }
    }

    if (state) {
        const signature = JSON.stringify({ maxReadyState, loadedPlayerCount, suspendedPlayerCount, metadata });
        if (state.lastRuntimeSignature !== signature) {
            state.lastRuntimeSignature = signature;
            state.lastActivityAt = Date.now();
        }
    }

    return {
        exists: !!state,
        scriptLoaded: !!window.RufflePlayer,
        injectionCompleted: !!state?.injectionCompleted,
        playerCount: players.length,
        visiblePlayerCount: visiblePlayers.length,
        loadedPlayerCount,
        maxReadyState,
        metadataLoaded: !!metadata,
        suspendedPlayerCount,
        injectedAt: state?.injectedAt || 0,
        lastPlayerMutationAt: state?.lastPlayerMutationAt || 0,
        lastActivityAt: state?.lastActivityAt || 0,
        lastStatus: state?.lastStatus || '',
        metadata,
        errors: Array.isArray(state?.errors) ? state.errors.slice(-30) : []
    };
})()
""";

            string json = await webView.ExecuteScriptAsync(js).ConfigureAwait(true);
            return RuffleStatus.FromWebViewJson(json);
        }

        public static async Task ResetAsync(WpfWebView2 webView)
        {
            if (webView.CoreWebView2 == null)
                return;

            try
            {
                await webView.ExecuteScriptAsync("""
(() => {
    try { window.__pommeRuffle?.observer?.disconnect?.(); } catch { }
    try { delete window.__pommeRuffle; } catch { window.__pommeRuffle = undefined; }
    return true;
})()
""").ConfigureAwait(true);
            }
            catch { }
        }

        private static string BuildBootstrapScript(
            string scriptUrl,
            string publicPath,
            FlashDetectionResult detection)
        {
            string scriptUrlJson = JsonSerializer.Serialize(scriptUrl);
            string publicPathJson = JsonSerializer.Serialize(publicPath);
            string sourceUrlJson = JsonSerializer.Serialize(detection.SourceUrl);
            string targetJson = JsonSerializer.Serialize(detection.TargetElement);
            string flashVarsJson = JsonSerializer.Serialize(detection.Hints.FlashVars);
            string baseUrlJson = JsonSerializer.Serialize(detection.Hints.BaseUrl);
            string wmodeJson = JsonSerializer.Serialize(NormalizeWmode(detection.Hints.WindowMode));
            string qualityJson = JsonSerializer.Serialize(detection.Hints.Quality);
            string scaleJson = JsonSerializer.Serialize(detection.Hints.Scale);
            string alignJson = JsonSerializer.Serialize(detection.Hints.Align);
            string credentialsJson = JsonSerializer.Serialize(BuildCredentialAllowList(detection.Hints));
            string allowScriptAccess = detection.Hints.CanGrantScriptAccess ? "true" : "false";
            string allowFullscreen = detection.Hints.AllowFullscreen ? "true" : "false";
            double width = detection.Hints.Width > 0 ? detection.Hints.Width : 0;
            double height = detection.Hints.Height > 0 ? detection.Hints.Height : 0;

            return $$"""
(async () => {
    const SCRIPT_URL = {{scriptUrlJson}};
    const PUBLIC_PATH = {{publicPathJson}};
    const DETECTED_SWF = {{sourceUrlJson}};
    const TARGET_ID = {{targetJson}};
    const FLASH_VARS = {{flashVarsJson}};
    const BASE_URL = {{baseUrlJson}};
    const WMODE = {{wmodeJson}};
    const QUALITY = {{qualityJson}};
    const SCALE = {{scaleJson}};
    const SALIGN = {{alignJson}};
    const CREDENTIAL_ORIGINS = {{credentialsJson}};
    const ALLOW_SCRIPT_ACCESS = {{allowScriptAccess}};
    const ALLOW_FULLSCREEN = {{allowFullscreen}};
    const ORIGINAL_WIDTH = {{width.ToString(System.Globalization.CultureInfo.InvariantCulture)}};
    const ORIGINAL_HEIGHT = {{height.ToString(System.Globalization.CultureInfo.InvariantCulture)}};

    const state = window.__pommeRuffle = window.__pommeRuffle || {
        injectedAt: Date.now(),
        injectionCompleted: false,
        lastPlayerMutationAt: Date.now(),
        lastActivityAt: Date.now(),
        lastStatus: 'initializing',
        errors: []
    };

    const addError = value => {
        const text = String(value || 'unknown-error');
        if (!state.errors.includes(text)) state.errors.push(text);
        if (state.errors.length > 30) state.errors.shift();
        state.lastActivityAt = Date.now();
    };

    if (!state.errorHookInstalled) {
        state.errorHookInstalled = true;
        window.addEventListener('error', event => {
            const message = String(event?.message || '');
            const filename = String(event?.filename || '');
            if (/ruffle|\.wasm|pomme\.internal/i.test(message + ' ' + filename))
                addError(message || filename);
        }, true);
        window.addEventListener('unhandledrejection', event => {
            const message = String(event?.reason || '');
            if (/ruffle|wasm|flash|swf/i.test(message))
                addError('promise:' + message);
        }, true);
    }

    const adaptiveConfig = {
        autoplay: 'on',
        unmuteOverlay: 'visible',
        publicPath: PUBLIC_PATH,
        allowScriptAccess: ALLOW_SCRIPT_ACCESS,
        allowFullscreen: ALLOW_FULLSCREEN,
        allowNetworking: 'all',
        openUrlMode: 'confirm',
        compatibilityRules: true,
        favorFlash: false,
        credentialAllowList: CREDENTIAL_ORIGINS
    };

    if (FLASH_VARS) adaptiveConfig.parameters = FLASH_VARS;
    if (BASE_URL) adaptiveConfig.base = BASE_URL;
    if (WMODE) adaptiveConfig.wmode = WMODE;
    if (QUALITY) adaptiveConfig.quality = QUALITY;
    if (SCALE) adaptiveConfig.scale = SCALE;
    if (SALIGN) adaptiveConfig.salign = SALIGN;

    window.RufflePlayer = window.RufflePlayer || {};
    window.RufflePlayer.config = Object.assign({}, window.RufflePlayer.config || {}, adaptiveConfig);

    const existing = document.querySelector('ruffle-player, ruffle-object, ruffle-embed');
    if (existing) {
        state.injectionCompleted = true;
        state.lastStatus = 'existing-player';
        state.lastActivityAt = Date.now();
        return state.lastStatus;
    }

    if (!window.RufflePlayer.newest) {
        const loaded = await new Promise(resolve => {
            const previous = document.querySelector('script[data-pomme-ruffle="true"]');
            if (previous) {
                previous.addEventListener('load', () => resolve(true), { once: true });
                previous.addEventListener('error', () => resolve(false), { once: true });
                setTimeout(() => resolve(!!window.RufflePlayer?.newest), 15000);
                return;
            }

            const script = document.createElement('script');
            script.dataset.pommeRuffle = 'true';
            script.src = SCRIPT_URL;
            script.async = true;
            script.onload = () => resolve(true);
            script.onerror = () => resolve(false);
            (document.head || document.documentElement).appendChild(script);
            setTimeout(() => resolve(!!window.RufflePlayer?.newest), 15000);
        });

        if (!loaded || !window.RufflePlayer?.newest) {
            state.lastStatus = loaded ? 'load-timeout' : 'load-failed';
            addError(state.lastStatus);
            return state.lastStatus;
        }
    }

    return await ({{BuildFinalizeScriptBody("DETECTED_SWF", "TARGET_ID", "FLASH_VARS", "BASE_URL", "WMODE", "QUALITY", "SCALE", "SALIGN", "CREDENTIAL_ORIGINS", "ALLOW_SCRIPT_ACCESS", "ALLOW_FULLSCREEN", "ORIGINAL_WIDTH", "ORIGINAL_HEIGHT")}});
})()
""";
        }

        private static string BuildFinalizeScript(FlashDetectionResult detection)
        {
            string sourceUrlJson = JsonSerializer.Serialize(detection.SourceUrl);
            string targetJson = JsonSerializer.Serialize(detection.TargetElement);
            string flashVarsJson = JsonSerializer.Serialize(detection.Hints.FlashVars);
            string baseUrlJson = JsonSerializer.Serialize(detection.Hints.BaseUrl);
            string wmodeJson = JsonSerializer.Serialize(NormalizeWmode(detection.Hints.WindowMode));
            string qualityJson = JsonSerializer.Serialize(detection.Hints.Quality);
            string scaleJson = JsonSerializer.Serialize(detection.Hints.Scale);
            string alignJson = JsonSerializer.Serialize(detection.Hints.Align);
            string credentialsJson = JsonSerializer.Serialize(BuildCredentialAllowList(detection.Hints));
            string allowScriptAccess = detection.Hints.CanGrantScriptAccess ? "true" : "false";
            string allowFullscreen = detection.Hints.AllowFullscreen ? "true" : "false";
            double width = detection.Hints.Width > 0 ? detection.Hints.Width : 0;
            double height = detection.Hints.Height > 0 ? detection.Hints.Height : 0;

            return $$"""
(async () => {
    const DETECTED_SWF = {{sourceUrlJson}};
    const TARGET_ID = {{targetJson}};
    const FLASH_VARS = {{flashVarsJson}};
    const BASE_URL = {{baseUrlJson}};
    const WMODE = {{wmodeJson}};
    const QUALITY = {{qualityJson}};
    const SCALE = {{scaleJson}};
    const SALIGN = {{alignJson}};
    const CREDENTIAL_ORIGINS = {{credentialsJson}};
    const ALLOW_SCRIPT_ACCESS = {{allowScriptAccess}};
    const ALLOW_FULLSCREEN = {{allowFullscreen}};
    const ORIGINAL_WIDTH = {{width.ToString(System.Globalization.CultureInfo.InvariantCulture)}};
    const ORIGINAL_HEIGHT = {{height.ToString(System.Globalization.CultureInfo.InvariantCulture)}};
    return await ({{BuildFinalizeScriptBody("DETECTED_SWF", "TARGET_ID", "FLASH_VARS", "BASE_URL", "WMODE", "QUALITY", "SCALE", "SALIGN", "CREDENTIAL_ORIGINS", "ALLOW_SCRIPT_ACCESS", "ALLOW_FULLSCREEN", "ORIGINAL_WIDTH", "ORIGINAL_HEIGHT")}});
})()
""";
        }

        private static string BuildFinalizeScriptBody(
            string swfVariable,
            string targetVariable,
            string flashVarsVariable,
            string baseVariable,
            string wmodeVariable,
            string qualityVariable,
            string scaleVariable,
            string alignVariable,
            string credentialsVariable,
            string allowScriptVariable,
            string allowFullscreenVariable,
            string widthVariable,
            string heightVariable) => $$"""
(async () => {
    const state = window.__pommeRuffle = window.__pommeRuffle || {
        injectedAt: Date.now(), injectionCompleted: false, lastPlayerMutationAt: Date.now(), lastActivityAt: Date.now(), lastStatus: '', errors: []
    };

    const markMutation = () => {
        state.lastPlayerMutationAt = Date.now();
        state.lastActivityAt = Date.now();
    };

    try { state.observer?.disconnect?.(); } catch { }
    state.observer = new MutationObserver(markMutation);
    state.observer.observe(document.documentElement, { childList: true, subtree: true, attributes: true });

    await new Promise(resolve => setTimeout(resolve, 400));
    let players = document.querySelectorAll('ruffle-player, ruffle-object, ruffle-embed');

    if (players.length === 0 && {{swfVariable}} && window.RufflePlayer?.newest) {
        try {
            const target = ({{targetVariable}} && document.getElementById({{targetVariable}})) ||
                document.querySelector('object[data*=".swf" i], embed[src*=".swf" i], object[type="application/x-shockwave-flash"], embed[type="application/x-shockwave-flash"]');

            const container = document.createElement('div');
            container.dataset.pommeRuffleFallback = 'true';
            container.style.width = {{widthVariable}} > 0 ? `${Math.round({{widthVariable}})}px` : '100%';
            container.style.height = {{heightVariable}} > 0 ? `${Math.round({{heightVariable}})}px` : 'min(75vh, 720px)';
            container.style.minHeight = {{heightVariable}} > 0 ? '0' : '360px';
            container.style.maxWidth = '100%';

            const player = window.RufflePlayer.newest().createPlayer();
            player.style.width = '100%';
            player.style.height = '100%';
            container.appendChild(player);

            if (target?.parentNode) target.parentNode.replaceChild(container, target);
            else document.body.appendChild(container);

            const options = {
                url: {{swfVariable}},
                autoplay: 'on',
                unmuteOverlay: 'visible',
                allowScriptAccess: {{allowScriptVariable}},
                allowFullscreen: {{allowFullscreenVariable}},
                allowNetworking: 'all',
                openUrlMode: 'confirm',
                compatibilityRules: true,
                favorFlash: false,
                credentialAllowList: {{credentialsVariable}}
            };

            if ({{flashVarsVariable}}) options.parameters = {{flashVarsVariable}};
            if ({{baseVariable}}) options.base = {{baseVariable}};
            if ({{wmodeVariable}}) options.wmode = {{wmodeVariable}};
            if ({{qualityVariable}}) options.quality = {{qualityVariable}};
            if ({{scaleVariable}}) options.scale = {{scaleVariable}};
            if ({{alignVariable}}) options.salign = {{alignVariable}};

            const api = typeof player.ruffle === 'function' ? player.ruffle(1) : player;
            await api.load(options);
            players = document.querySelectorAll('ruffle-player, ruffle-object, ruffle-embed');
        } catch (error) {
            state.errors.push('player-load:' + String(error));
        }
    }

    state.injectionCompleted = true;
    state.lastStatus = players.length > 0 ? 'ok' : 'no-player';
    markMutation();
    return state.lastStatus;
})()
""";

        private static string? NormalizeWmode(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            string normalized = value.Trim().ToLowerInvariant();
            return normalized is "window" or "opaque" or "transparent" or "direct" or "gpu"
                ? normalized
                : null;
        }

        private static string[] BuildCredentialAllowList(FlashRuntimeHints hints)
        {
            var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(hints.PageOrigin))
                origins.Add(hints.PageOrigin!);
            if (!string.IsNullOrWhiteSpace(hints.SwfOrigin))
                origins.Add(hints.SwfOrigin!);
            return origins.ToArray();
        }

        private static string DecodeScriptString(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return string.Empty;

            try
            {
                return JsonSerializer.Deserialize<string>(raw) ?? raw.Trim('"');
            }
            catch
            {
                return raw.Trim('"');
            }
        }
    }
}
