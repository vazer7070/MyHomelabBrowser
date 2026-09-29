using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes.AdBlock.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MyHomelabBrowser.classes.AdBlock.Integration
{
    public sealed class AdBlockBrowserController : IDisposable
    {
        private readonly AdBlockModuleService _module;
        private readonly Dictionary<WebView2, AdBlockTabSession> _sessions = new();
        private AdBlockTabSession? _activeSession;
        private bool _disposed;

        public event Action<AdBlockTabSession?>? ActiveSessionChanged;
        public event Action<AdBlockTabSession>? ActiveSessionUpdated;
        public event Action? ModuleStateChanged;

        public AdBlockTabSession? ActiveSession => _activeSession;

        public AdBlockBrowserController(AdBlockModuleService module)
        {
            _module = module ?? throw new ArgumentNullException(nameof(module));
            _module.StateChanged += Module_StateChanged;
            _module.RulesChanged += Module_StateChanged;
            _module.StatisticsChanged += Module_StateChanged;
        }

        public async Task<AdBlockTabSession> AttachAsync(WebView2 webView, bool isPrivate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_sessions.TryGetValue(webView, out AdBlockTabSession? existing))
                return existing;

            var session = new AdBlockTabSession(new WpfAdBlockWebView(webView), _module, isPrivate);
            session.Updated += Session_Updated;
            _sessions[webView] = session;
            await session.AttachAsync().ConfigureAwait(true);
            return session;
        }

        /// <summary>
        /// Détache et détruit la session liée à un onglet fermé. Sans cela, le
        /// contrôleur conserve une référence au WebView2 supprimé.
        /// </summary>
        public void Detach(WebView2? webView)
        {
            if (webView == null || !_sessions.Remove(webView, out AdBlockTabSession? session))
                return;

            session.Updated -= Session_Updated;

            if (ReferenceEquals(_activeSession, session))
            {
                _activeSession = null;
                ActiveSessionChanged?.Invoke(null);
            }

            session.Dispose();
        }

        public void SetActiveWebView(WebView2? webView)
        {
            AdBlockTabSession? next = null;
            if (webView != null)
                _sessions.TryGetValue(webView, out next);

            if (ReferenceEquals(_activeSession, next))
                return;

            _activeSession = next;
            ActiveSessionChanged?.Invoke(next);
        }

        public async Task RefreshAllAsync()
        {
            foreach (AdBlockTabSession session in _sessions.Values.ToArray())
            {
                try { await session.RefreshFilteringAsync().ConfigureAwait(true); }
                catch { }
            }
        }

        private void Session_Updated(AdBlockTabSession session)
        {
            if (ReferenceEquals(session, _activeSession))
                ActiveSessionUpdated?.Invoke(session);
        }

        private void Module_StateChanged()
        {
            ModuleStateChanged?.Invoke();
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _module.StateChanged -= Module_StateChanged;
            _module.RulesChanged -= Module_StateChanged;
            _module.StatisticsChanged -= Module_StateChanged;

            foreach (AdBlockTabSession session in _sessions.Values)
            {
                session.Updated -= Session_Updated;
                session.Dispose();
            }

            _sessions.Clear();
            _activeSession = null;
        }

        /// <summary>Contrôle WebView2 de WPF vu par le bloqueur.</summary>
        private sealed class WpfAdBlockWebView : IAdBlockWebView
        {
            private readonly WebView2 _webView;

            public WpfAdBlockWebView(WebView2 webView) => _webView = webView;

            public CoreWebView2? CoreWebView2 => _webView.CoreWebView2;

            public void Post(Action action)
                => _webView.Dispatcher.BeginInvoke(DispatcherPriority.Background, action);
        }
    }
}
