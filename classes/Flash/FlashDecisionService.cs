using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes;
using System;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.Flash
{
    public class FlashDecisionService
    {
        private readonly WebView2 _web;
        private readonly SettingsService _settings;
        private readonly LegacyLauncher _legacy;

        public FlashDecisionService(
            WebView2 web,
            SettingsService settings,
            LegacyLauncher legacyLauncher)
        {
            _web = web;
            _settings = settings;
            _legacy = legacyLauncher;
        }

        // ======================================================
        // DÉCISION AVANT NAVIGATION (RÈGLES + SETTINGS)
        // ======================================================
        public FlashMode DecideInitialMode(Uri uri)
        {
            var s = _settings.Settings;

            if (!s.EnableFlashSupport)
                return FlashMode.None;

            var rule = FlashDomainRules.GetRule(uri);

            if (rule == FlashRuleMode.Disabled)
                return FlashMode.None;

            if (rule == FlashRuleMode.Legacy)
                return _legacy.CanLaunch()
                    ? FlashMode.Legacy
                    : FlashMode.None;

            if (rule == FlashRuleMode.Ruffle)
                return FlashMode.None;

            // Auto
            if (!s.PreferRuffle && _legacy.CanLaunch())
                return FlashMode.Legacy;

            return FlashMode.None;
        }

        // ======================================================
        // DÉTECTION DOM FLASH (PRÉCISE, UN PEU PLUS LENTE)
        // ======================================================
        public async Task<bool> DetectFlashDomAsync()
        {
            if (!_settings.Settings.EnableFlashSupport)
                return false;

            if (_web.CoreWebView2 == null)
                return false;

            const string script = @"
(() => {
    // embed / object classiques
    if (document.querySelector('embed[type=""application/x-shockwave-flash""]'))
        return true;

    if (document.querySelector('object[data$="".swf""]'))
        return true;

    if (document.querySelector('param[name=""movie""][value$="".swf""]'))
        return true;

    // flashContent / swfobject container (cas Ministry of War)
    if (document.getElementById('flashContent'))
        return true;

    return false;
})();
";

            try
            {
                var result = await _web.ExecuteScriptAsync(script);
                return result.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        // ======================================================
        // DÉTECTION RAPIDE / HEURISTIQUE (UX IMMÉDIATE)
        // 👉 C’est CELLE-CI qu’on appelle en premier
        // ======================================================
        public async Task<bool> DetectFlashRequirementAsync()
        {
            if (_web.CoreWebView2 == null)
                return false;

            const string script = @"
(() => {
    // 1️⃣ swfobject = Flash legacy quasi certain
    if (typeof window.swfobject !== 'undefined')
        return true;

    // 2️⃣ embedSWF explicit
    if (typeof window.swfobject !== 'undefined' &&
        typeof window.swfobject.embedSWF === 'function')
        return true;

    // 3️⃣ Recherche SWF dans HTML / JS inline
    const html = document.documentElement?.innerHTML || '';
    if (/\.swf(\?|\""|'|$)/i.test(html))
        return true;

    // 4️⃣ Indices texte (EN / CN)
    const text = document.body?.innerText?.toLowerCase() || '';
    if (
        text.includes('flash') ||
        text.includes('shockwave') ||
        text.includes('adobe flash') ||
        text.includes('flash player') ||
        text.includes('播放器') ||      // lecteur
        text.includes('需要') ||        // nécessite
        text.includes('安装')           // installer
    )
        return true;

    return false;
})();
";

            try
            {
                var result = await _web.ExecuteScriptAsync(script);
                return result.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
