using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.Flash
{
    public sealed class FlashDecisionService
    {
        private readonly WebView2 _web;
        private readonly SettingsService _settings;
        private readonly LegacyLauncher _legacy;

        public FlashDecisionService(
            WebView2 web,
            SettingsService settings,
            LegacyLauncher legacyLauncher)
        {
            _web = web ?? throw new ArgumentNullException(nameof(web));
            _settings = settings ?? throw new ArgumentNullException(nameof(settings));
            _legacy = legacyLauncher ?? throw new ArgumentNullException(nameof(legacyLauncher));
        }

        public FlashMode DecideInitialMode(Uri uri, bool forceLegacyOnce)
        {
            if (uri == null)
                return FlashMode.None;

            if (forceLegacyOnce)
                return _legacy.CanLaunch() ? FlashMode.Legacy : FlashMode.None;

            FlashRuleMode rule = GetRuleWithSubdomainFallback(uri);

            if (rule == FlashRuleMode.Disabled)
                return FlashMode.None;

            if (rule == FlashRuleMode.Legacy)
                return _legacy.CanLaunch() ? FlashMode.Legacy : FlashMode.None;

            if (rule == FlashRuleMode.Ruffle)
                return FlashMode.None;

            BrowserSettings settings = _settings.Settings;
            if (!settings.PreferRuffle && _legacy.CanLaunch())
                return FlashMode.Legacy;

            return FlashMode.None;
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

        /// <summary>
        /// L'ancienne heuristique rapide analysait le texte et le HTML brut de toute la page.
        /// Elle provoquait des bascules Legacy sur des sites modernes contenant seulement une
        /// ancienne référence Flash, une bibliothèque SWF ou un emplacement publicitaire caché.
        ///
        /// Le pipeline existant appelle immédiatement DetectFlashDomAsync après ce résultat.
        /// On désactive donc volontairement l'heuristique approximative et on conserve uniquement
        /// la détection DOM stricte ci-dessous.
        /// </summary>
        public Task<bool> DetectFlashRequirementAsync()
        {
            return Task.FromResult(false);
        }

        /// <summary>
        /// Détecte uniquement un contenu Flash réellement exploitable dans la page courante :
        /// - object/embed Flash visible ;
        /// - param movie/src pointant vers un SWF ;
        /// - appel swfobject.embedSWF avec cible visible existante.
        ///
        /// Les scripts, modèles cachés, références historiques et emplacements publicitaires
        /// ne déclenchent pas le basculement de tout l'onglet en Legacy.
        /// </summary>
        public async Task<bool> DetectFlashDomAsync()
        {
            if (!CanRunAutomaticDetection())
                return false;

            const string script = """
(() => {
    const FLASH_CLASS_ID = 'd27cdb6e-ae6d-11cf-96b8-444553540000';

    const isSwfUrl = value => {
        if (!value || typeof value !== 'string') return false;

        const trimmed = value.trim();
        if (!trimmed || /^(?:javascript:|about:|data:text\/html)/i.test(trimmed))
            return false;

        try {
            const absolute = new URL(trimmed, document.baseURI);
            return /\.swf$/i.test(absolute.pathname);
        } catch {
            const clean = trimmed.split('#')[0].split('?')[0];
            return /\.swf$/i.test(clean);
        }
    };

    const isFlashMime = value =>
        typeof value === 'string' &&
        /^(?:application\/(?:x-shockwave-flash|futuresplash)|application\/vnd\.adobe\.flash\.movie)$/i.test(value.trim());

    const isInsideInactiveMarkup = element =>
        !!element.closest('head, template, noscript');

    const isAdContainer = element => {
        for (let current = element; current && current !== document.documentElement; current = current.parentElement) {
            const marker = [
                current.id || '',
                current.className || '',
                current.getAttribute?.('data-ad-slot') || '',
                current.getAttribute?.('data-ad-unit') || '',
                current.getAttribute?.('aria-label') || ''
            ].join(' ').toLowerCase();

            // Une ancienne publicité Flash ne doit pas forcer tout le site en Legacy.
            if (/(^|[\s_-])(ad|ads|advert|advertisement|banner-ad|sponsor|sponsored|dfp|gpt)([\s_-]|$)/i.test(marker))
                return true;
        }

        return false;
    };

    const isVisibleContent = element => {
        if (!element || !element.isConnected || isInsideInactiveMarkup(element))
            return false;

        const style = getComputedStyle(element);
        if (style.display === 'none' || style.visibility === 'hidden' || style.visibility === 'collapse')
            return false;

        if (Number.parseFloat(style.opacity || '1') <= 0.01)
            return false;

        const hiddenAncestor = element.closest('[hidden], [aria-hidden="true"]');
        if (hiddenAncestor)
            return false;

        const rect = element.getBoundingClientRect();
        const attrWidth = Number.parseFloat(element.getAttribute?.('width') || '0');
        const attrHeight = Number.parseFloat(element.getAttribute?.('height') || '0');
        const width = Math.max(rect.width, attrWidth);
        const height = Math.max(rect.height, attrHeight);

        // Ignore les pixels de suivi, objets techniques et marqueurs invisibles.
        return width >= 24 && height >= 24;
    };

    const objectHasFlashSource = object => {
        if (isFlashMime(object.getAttribute('type')) ||
            isSwfUrl(object.getAttribute('data')) ||
            (object.getAttribute('classid') || '').toLowerCase().includes(FLASH_CLASS_ID)) {
            return true;
        }

        for (const param of object.querySelectorAll(':scope > param')) {
            const name = (param.getAttribute('name') || '').trim().toLowerCase();
            const value = param.getAttribute('value');
            if ((name === 'movie' || name === 'src') && isSwfUrl(value))
                return true;
        }

        return false;
    };

    for (const embed of document.querySelectorAll('embed')) {
        if (!isVisibleContent(embed) || isAdContainer(embed))
            continue;

        if (isFlashMime(embed.getAttribute('type')) || isSwfUrl(embed.getAttribute('src')))
            return true;
    }

    for (const object of document.querySelectorAll('object')) {
        if (!isVisibleContent(object) || isAdContainer(object))
            continue;

        if (objectHasFlashSource(object))
            return true;
    }

    // Cas des anciens sites qui appellent swfobject.embedSWF mais dont l'objet n'est pas
    // créé dans Chromium faute de plug-in. On exige une URL SWF littérale, une cible DOM
    // existante et visible, et on refuse les emplacements publicitaires.
    const embedCall = /(?:window\.)?swfobject\s*\.\s*embedSWF\s*\(\s*(['"])([^'"]+\.swf(?:\?[^'"]*)?)\1\s*,\s*(['"])([^'"]+)\3/i;

    for (const scriptElement of document.scripts) {
        if (scriptElement.src || isInsideInactiveMarkup(scriptElement))
            continue;

        const code = scriptElement.textContent || '';
        const match = embedCall.exec(code);
        if (!match || !isSwfUrl(match[2]))
            continue;

        const target = document.getElementById(match[4]);
        if (!target || !isVisibleContent(target) || isAdContainer(target))
            continue;

        // Un lecteur HTML5 moderne présent dans la même cible est prioritaire et ne doit
        // pas être pris pour une dépendance Flash.
        if (target.querySelector('video, audio, iframe[src], canvas'))
            continue;

        return true;
    }

    return false;
})()
""";

            return await ExecuteBooleanScriptAsync(script).ConfigureAwait(true);
        }

        private bool CanRunAutomaticDetection()
        {
            return _settings.Settings.EnableFlashSupport && _web.CoreWebView2 != null;
        }

        private async Task<bool> ExecuteBooleanScriptAsync(string script)
        {
            try
            {
                string result = await _web.ExecuteScriptAsync(script).ConfigureAwait(true);
                return result.Trim().Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }
    }
}
