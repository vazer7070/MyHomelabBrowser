using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes.CloudTorrent.Models;
using MyHomelabBrowser.classes.CloudTorrent.Services;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.CloudTorrent.Integration
{
    public sealed class CloudTorrentBrowserController : IDisposable
    {
        private readonly CloudTorrentModuleService _module;
        private readonly CloudTorrentPageAnalyzer _analyzer = new();
        private readonly Dictionary<WebView2, CloudTorrentTabSession> _sessions = new();
        private bool _disposed;

        public CloudTorrentBrowserController(CloudTorrentModuleService module)
        {
            _module = module ?? throw new ArgumentNullException(nameof(module));
            _module.StateChanged += Module_StateChanged;
        }

        public event Action<CloudTorrentTabSession?>? ActiveSessionChanged;
        public event Action<CloudTorrentTabSession>? ActiveSessionUpdated;
        public event Action<CloudTorrentTabSession>? OpenPanelRequested;
        public event Action<string, string>? NotificationRequested;
        public event Action? ModuleStateChanged;

        public CloudTorrentTabSession? ActiveSession { get; private set; }
        public bool IsActiveTabPrivate => ActiveSession?.IsPrivate == true;

        public async Task<CloudTorrentTabSession> AttachAsync(WebView2 webView, bool isPrivate)
        {
            ThrowIfDisposed();
            if (_sessions.TryGetValue(webView, out CloudTorrentTabSession? existing))
                return existing;

            var session = new CloudTorrentTabSession(webView, _module, _analyzer, isPrivate);
            session.Changed += Session_Changed;
            session.OpenPanelRequested += Session_OpenPanelRequested;
            session.NotificationRequested += Session_NotificationRequested;
            _sessions.Add(webView, session);

            await session.AttachAsync();
            return session;
        }

        public void SetActiveWebView(WebView2? webView)
        {
            ThrowIfDisposed();
            CloudTorrentTabSession? next = null;
            if (webView != null)
                _sessions.TryGetValue(webView, out next);

            if (ReferenceEquals(ActiveSession, next))
            {
                if (next != null)
                    ActiveSessionUpdated?.Invoke(next);
                return;
            }

            ActiveSession = next;
            ActiveSessionChanged?.Invoke(next);
        }

        public CloudTorrentTabSession? GetSession(WebView2? webView)
        {
            if (webView == null)
                return null;

            _sessions.TryGetValue(webView, out CloudTorrentTabSession? session);
            return session;
        }

        public void Detach(WebView2? webView)
        {
            if (webView == null || !_sessions.Remove(webView, out CloudTorrentTabSession? session))
                return;

            session.Changed -= Session_Changed;
            session.OpenPanelRequested -= Session_OpenPanelRequested;
            session.NotificationRequested -= Session_NotificationRequested;

            if (ReferenceEquals(ActiveSession, session))
            {
                ActiveSession = null;
                ActiveSessionChanged?.Invoke(null);
            }

            session.Dispose();
        }

        private void Session_Changed(CloudTorrentTabSession session)
        {
            if (ReferenceEquals(ActiveSession, session))
                ActiveSessionUpdated?.Invoke(session);
        }

        private void Session_OpenPanelRequested(CloudTorrentTabSession session)
        {
            SetActiveWebView(session.WebView);
            OpenPanelRequested?.Invoke(session);
        }

        private void Session_NotificationRequested(string title, string message)
            => NotificationRequested?.Invoke(title, message);

        private void Module_StateChanged(CloudTorrentModuleSnapshot snapshot)
            => ModuleStateChanged?.Invoke();

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CloudTorrentBrowserController));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _module.StateChanged -= Module_StateChanged;

            foreach (CloudTorrentTabSession session in _sessions.Values)
            {
                session.Changed -= Session_Changed;
                session.OpenPanelRequested -= Session_OpenPanelRequested;
                session.NotificationRequested -= Session_NotificationRequested;
                session.Dispose();
            }

            _sessions.Clear();
            ActiveSession = null;
        }
    }
}
