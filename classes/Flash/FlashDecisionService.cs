using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes;
using System;
using System.Linq;
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
            if (uri == null)
                return FlashMode.None;

            // ✅ 1) règle exacte OU parent-domain (suffix match)
            var rule = GetRuleWithSubdomainFallback(uri);

            // ✅ 2) règles explicites (prioritaires)
            if (rule == FlashRuleMode.Disabled)
                return FlashMode.None;

            if (rule == FlashRuleMode.Legacy)
                return _legacy.CanLaunch()
                    ? FlashMode.Legacy
                    : FlashMode.None;

            if (rule == FlashRuleMode.Ruffle)
                return FlashMode.None; // Ruffle-only -> donc pas de legacy

            // ✅ 3) AUTO
            var s = _settings.Settings;

            // si l'utilisateur ne préfère pas Ruffle -> Legacy direct si dispo
            if (!s.PreferRuffle && _legacy.CanLaunch())
                return FlashMode.Legacy;

            return FlashMode.None;
        }

        // ===================================================
        // ✅ helper: applique rule pour sous-domaines aussi
        // ===================================================
        private static FlashRuleMode GetRuleWithSubdomainFallback(Uri uri)
        {
            var host = uri.Host?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(host))
                return FlashRuleMode.Auto;

            // ✅ 1) règle exacte
            var direct = FlashDomainRules.GetRule(new Uri("https://" + host));
            if (direct != FlashRuleMode.Auto)
                return direct;

            // ✅ 2) fallback suffix : play13.ministryofwar.com -> ministryofwar.com
            var parts = host.Split('.');
            if (parts.Length < 2)
                return FlashRuleMode.Auto;

            // ex: parts = [play13, ministryofwar, com]
            // i=1 -> ministryofwar.com
            for (int i = 1; i < parts.Length; i++)
            {
                var parent = string.Join(".", parts.Skip(i));
                var parentRule = FlashDomainRules.GetRule(new Uri("https://" + parent));

                if (parentRule != FlashRuleMode.Auto)
                    return parentRule;
            }

            return FlashRuleMode.Auto;
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
        // ======================================================
        public async Task<bool> DetectFlashRequirementAsync()
        {
            if (!_settings.Settings.EnableFlashSupport)
                return false;

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
        text.includes('播放器') ||
        text.includes('需要') ||
        text.includes('安装')
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
