using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using MyHomelabBrowser.classes.Localization;
using PommeBrowser.Linux.Core;

namespace PommeBrowser.Linux.Web
{
    /// <summary>
    /// Moteur partagé par toutes les fenêtres : contexte WebKit, session réseau persistante,
    /// réglages des pages, Ruffle, bloqueur et téléchargements. Chaque fenêtre privée reçoit
    /// sa propre session éphémère (rien n'est écrit sur le disque).
    /// </summary>
    sealed class WebEngine : IDisposable
    {
        readonly Func<LinuxSettings> _settings;
        readonly List<WeakReference<WebKit.NetworkSession>> _privateSessions = new();

        public WebEngine(Func<LinuxSettings> settings)
        {
            _settings = settings;

            Context = WebKit.WebContext.GetDefault();
            Context.SetCacheModel(WebKit.CacheModel.WebBrowser);
            Context.SetPreferredLanguages(Loc.Language == "en"
                ? new[] { "en-US", "en", "fr-FR", "fr" }
                : new[] { "fr-FR", "fr", "en-US", "en" });
            Context.SetSpellCheckingEnabled(true);
            Context.SetSpellCheckingLanguages(Loc.Language == "en" ? new[] { "en_US" } : new[] { "fr_FR" });

            Ruffle.Register(Context);

            WebSettings = CreateWebSettings();
            Downloads = new DownloadManager(() => _settings().DownloadDirectory);

            Session = WebKit.NetworkSession.New(LinuxPaths.Data("webkit"), LinuxPaths.Cache("webkit"));
            Session.GetCookieManager().SetPersistentStorage(LinuxPaths.Data("cookies.sqlite"), WebKit.CookiePersistentStorage.Sqlite);
            Configure(Session);
        }

        public WebKit.WebContext Context { get; }
        public WebKit.NetworkSession Session { get; }
        public WebKit.Settings WebSettings { get; }
        public RuffleSupport Ruffle { get; } = new();
        public AdBlocker AdBlocker { get; } = new();
        public DownloadManager Downloads { get; }

        public WebKit.NetworkSession CreatePrivateSession()
        {
            WebKit.NetworkSession session = WebKit.NetworkSession.NewEphemeral();
            Configure(session);
            _privateSessions.Add(new WeakReference<WebKit.NetworkSession>(session));
            return session;
        }

        /// <summary>Applique les réglages de confidentialité à toutes les sessions ouvertes.</summary>
        public void ApplyPrivacySettings()
        {
            Configure(Session);
            _privateSessions.RemoveAll(r => !r.TryGetTarget(out _));
            foreach (WeakReference<WebKit.NetworkSession> reference in _privateSessions)
            {
                if (reference.TryGetTarget(out WebKit.NetworkSession? session))
                    Configure(session);
            }
        }

        void Configure(WebKit.NetworkSession session)
        {
            LinuxSettings settings = _settings();
            session.SetItpEnabled(settings.TrackingPrevention);
            session.SetTlsErrorsPolicy(WebKit.TLSErrorsPolicy.Fail);
            session.GetCookieManager().SetAcceptPolicy(settings.BlockThirdPartyCookies
                ? WebKit.CookieAcceptPolicy.NoThirdParty
                : WebKit.CookieAcceptPolicy.Always);
            session.GetWebsiteDataManager().SetFaviconsEnabled(true);
            Downloads.Attach(session);
        }

        static WebKit.Settings CreateWebSettings()
        {
            var settings = WebKit.Settings.New();
            settings.SetEnableDeveloperExtras(true);
            settings.SetEnableBackForwardNavigationGestures(true);
            settings.SetEnableSmoothScrolling(true);
            // Fenêtres surgissantes : seulement à la suite d'un clic.
            settings.SetJavascriptCanOpenWindowsAutomatically(false);
            // Pas de lecture automatique avec le son (les « ping » de suivi sont déjà ignorés par WebKit).
            settings.SetMediaPlaybackRequiresUserGesture(true);
            settings.SetDefaultCharset("utf-8");
            return settings;
        }

        /// <summary>Efface les données des sites (cookies, cache, stockage…) depuis <paramref name="since"/>.</summary>
        public Task ClearBrowsingDataAsync(TimeSpan? since)
        {
            WebKit.WebsiteDataManager manager = Session.GetWebsiteDataManager();
            long microseconds = since is { } span ? (long)span.TotalMilliseconds * 1000 : 0;
            return Native.RunAsync(
                callback => Native.webkit_website_data_manager_clear(Native.Pointer(manager), Native.WebsiteDataAll, microseconds, IntPtr.Zero, callback, IntPtr.Zero),
                result =>
                {
                    Native.webkit_website_data_manager_clear_finish(Native.Pointer(manager), result, out IntPtr error);
                    Native.ThrowIfError(error);
                    return true;
                });
        }

        public void Dispose() => AdBlocker.Dispose();
    }
}
