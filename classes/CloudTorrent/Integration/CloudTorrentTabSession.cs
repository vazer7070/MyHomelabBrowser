using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes.CloudTorrent.Models;
using MyHomelabBrowser.classes.CloudTorrent.Services;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace MyHomelabBrowser.classes.CloudTorrent.Integration
{
    public sealed class CloudTorrentTabSession : IDisposable
    {
        private const string ObserverScript = """
            (() => {
                if (window.__cloudTorrentBrowserObserverInstalled) return;
                window.__cloudTorrentBrowserObserverInstalled = true;
                let timer = 0;
                const signal = () => {
                    window.clearTimeout(timer);
                    timer = window.setTimeout(() => {
                        try {
                            window.chrome.webview.postMessage({
                                source: 'MyHomelabBrowser.CloudTorrent',
                                kind: 'pageChanged',
                                url: location.href
                            });
                        } catch (_) { }
                    }, 900);
                };
                new MutationObserver(signal).observe(document.documentElement || document, {
                    childList: true,
                    subtree: true,
                    attributes: true,
                    attributeFilter: ['src', 'href']
                });
                document.addEventListener('play', signal, true);
                window.addEventListener('popstate', signal, true);
                window.addEventListener('hashchange', signal, true);
                const pushState = history.pushState;
                history.pushState = function(...args) { const value = pushState.apply(this, args); signal(); return value; };
                const replaceState = history.replaceState;
                history.replaceState = function(...args) { const value = replaceState.apply(this, args); signal(); return value; };
            })();
            """;

        private readonly WebView2 _webView;
        private readonly CloudTorrentModuleService _module;
        private readonly CloudTorrentPageAnalyzer _analyzer;
        private readonly bool _isPrivate;
        private readonly DispatcherTimer _analysisDebounce;
        private readonly HashSet<string> _observedNetworkUrls = new(StringComparer.OrdinalIgnoreCase);
        private CancellationTokenSource? _analysisCts;
        private string? _observerScriptId;
        private bool _attached;
        private bool _disposed;

        public CloudTorrentTabSession(
            WebView2 webView,
            CloudTorrentModuleService module,
            CloudTorrentPageAnalyzer analyzer,
            bool isPrivate)
        {
            _webView = webView ?? throw new ArgumentNullException(nameof(webView));
            _module = module ?? throw new ArgumentNullException(nameof(module));
            _analyzer = analyzer ?? throw new ArgumentNullException(nameof(analyzer));
            _isPrivate = isPrivate;

            _analysisDebounce = new DispatcherTimer(DispatcherPriority.Background, _webView.Dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(1250)
            };
            _analysisDebounce.Tick += AnalysisDebounce_Tick;
        }

        public event Action<CloudTorrentTabSession>? Changed;
        public event Action<CloudTorrentTabSession>? OpenPanelRequested;
        public event Action<string, string>? NotificationRequested;

        public WebView2 WebView => _webView;
        public bool IsPrivate => _isPrivate;
        public bool IsBusy { get; private set; }
        public string StatusMessage { get; private set; } = "Prêt.";
        public CloudTorrentPageAnalysis Analysis { get; private set; } = CloudTorrentPageAnalysis.Empty();
        public CloudTorrentMediaAnalysis? ServerAnalysis { get; private set; }
        public string ServerAnalysisSourceUrl { get; private set; } = string.Empty;

        public bool CanUseModule => !_isPrivate && _module.Snapshot.IsActive;

        public async Task AttachAsync()
        {
            ThrowIfDisposed();
            if (_attached || _webView.CoreWebView2 == null)
                return;

            _attached = true;
            CoreWebView2 core = _webView.CoreWebView2;
            core.ContextMenuRequested += Core_ContextMenuRequested;
            core.WebResourceResponseReceived += Core_WebResourceResponseReceived;
            core.WebMessageReceived += Core_WebMessageReceived;
            _webView.NavigationStarting += WebView_NavigationStarting;
            _webView.NavigationCompleted += WebView_NavigationCompleted;
            _module.StateChanged += Module_StateChanged;

            core.Settings.IsWebMessageEnabled = true;
            _observerScriptId = await core.AddScriptToExecuteOnDocumentCreatedAsync(ObserverScript);

            if (CanAutoAnalyzeCurrentPage())
                ScheduleAnalysis(TimeSpan.FromMilliseconds(350));
        }

        public async Task<CloudTorrentPageAnalysis> AnalyzeNowAsync(bool userInitiated = true)
        {
            ThrowIfDisposed();

            if (_isPrivate)
                throw new InvalidOperationException("CloudTorrent est désactivé dans les onglets privés.");
            if (!_module.Snapshot.IsActive)
                throw new InvalidOperationException("Configurez CloudTorrent dans les paramètres du navigateur.");

            _analysisDebounce.Stop();
            _analysisCts?.Cancel();
            _analysisCts?.Dispose();
            _analysisCts = new CancellationTokenSource();

            SetBusy(true, userInitiated ? "Analyse de la page…" : "Mise à jour de la détection…");
            try
            {
                CloudTorrentPageAnalysis result = await _analyzer.AnalyzeAsync(
                    _webView,
                    _observedNetworkUrls.ToArray(),
                    _analysisCts.Token);

                Analysis = result;

                if (!string.IsNullOrWhiteSpace(ServerAnalysisSourceUrl) &&
                    !result.Items.Any(item => string.Equals(
                        item.Url,
                        ServerAnalysisSourceUrl,
                        StringComparison.OrdinalIgnoreCase)))
                {
                    // Une nouvelle détection ne doit jamais conserver le résultat d’une
                    // ancienne URL erronée, par exemple une adresse générée en /null.
                    ServerAnalysis = null;
                    ServerAnalysisSourceUrl = string.Empty;
                }

                StatusMessage = result.Summary;
                Changed?.Invoke(this);
                return result;
            }
            catch (OperationCanceledException) when (_analysisCts.IsCancellationRequested)
            {
                return Analysis;
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
                Changed?.Invoke(this);
                if (userInitiated)
                    NotificationRequested?.Invoke("Analyse CloudTorrent impossible", ex.Message);
                throw;
            }
            finally
            {
                SetBusy(false, StatusMessage);
            }
        }

        public async Task<CloudTorrentMediaAnalysis> AnalyzeOnServerAsync(string? sourceUrl = null)
        {
            ThrowIfDisposed();
            string url = ResolveAnalysisUrl(sourceUrl);
            string? referer = Analysis.PageUrl;

            SetBusy(true, "Analyse par CloudTorrent…");
            try
            {
                CloudTorrentMediaAnalysis result = await _module.AnalyzeMediaAsync(url, referer);
                ServerAnalysis = result;
                ServerAnalysisSourceUrl = url;
                StatusMessage = $"Analyse terminée : {result.Title}";
                Changed?.Invoke(this);
                return result;
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
                Changed?.Invoke(this);
                NotificationRequested?.Invoke("Analyse CloudTorrent impossible", ex.Message);
                throw;
            }
            finally
            {
                SetBusy(false, StatusMessage);
            }
        }

        public async Task<IReadOnlyList<CloudTorrentQueueResult>> QueueSelectedAsync(
            CloudTorrentQualityChoice? qualityChoice = null)
        {
            IReadOnlyList<CloudTorrentDetectedItem> selected = Analysis.Items.Where(item => item.IsSelected).ToArray();
            if (selected.Count == 0)
                throw new InvalidOperationException("Sélectionnez au moins un élément.");

            var results = new List<CloudTorrentQueueResult>();
            SetBusy(true, "Envoi vers CloudTorrent…");
            try
            {
                foreach (CloudTorrentDetectedItem item in selected)
                {
                    CloudTorrentQueueResult result = await _module.QueueItemAsync(
                        item,
                        Analysis.PageUrl,
                        item.Type is CloudTorrentDetectedItemType.Media or CloudTorrentDetectedItemType.Page ? qualityChoice : null,
                        item.Url == ServerAnalysisSourceUrl ? ServerAnalysis : null);
                    results.Add(result);
                }

                int added = results.Count(result => !result.Duplicate);
                int duplicates = results.Count - added;
                string message = $"{added} élément{(added > 1 ? "s" : string.Empty)} ajouté{(added > 1 ? "s" : string.Empty)}";
                if (duplicates > 0)
                    message += $" · {duplicates} déjà présent{(duplicates > 1 ? "s" : string.Empty)}";

                StatusMessage = message;
                NotificationRequested?.Invoke("CloudTorrent", message);
                Changed?.Invoke(this);
                _ = RefreshAccountQuietlyAsync();
                return results;
            }
            finally
            {
                SetBusy(false, StatusMessage);
            }
        }

        public async Task<CloudTorrentQueueResult> QueueSourceAsync(
            string source,
            string label,
            CloudTorrentQualityChoice? qualityChoice = null)
        {
            if (string.IsNullOrWhiteSpace(source))
                throw new ArgumentException("Aucune adresse exploitable n’a été trouvée.", nameof(source));

            CloudTorrentDetectedItem item = CloudTorrentPageAnalyzer.CreateContextItem(source, label);
            SetBusy(true, "Envoi vers CloudTorrent…");
            try
            {
                CloudTorrentQueueResult result = await _module.QueueItemAsync(
                    item,
                    CurrentPageUrl(),
                    qualityChoice,
                    source == ServerAnalysisSourceUrl ? ServerAnalysis : null);
                StatusMessage = result.Message;
                NotificationRequested?.Invoke("CloudTorrent", result.Message);
                Changed?.Invoke(this);
                _ = RefreshAccountQuietlyAsync();
                return result;
            }
            catch (Exception ex)
            {
                StatusMessage = ex.Message;
                Changed?.Invoke(this);
                NotificationRequested?.Invoke("CloudTorrent", ex.Message);
                throw;
            }
            finally
            {
                SetBusy(false, StatusMessage);
            }
        }

        private void Core_ContextMenuRequested(object? sender, CoreWebView2ContextMenuRequestedEventArgs e)
        {
            if (!CanUseModule || _webView.CoreWebView2 == null)
                return;

            CoreWebView2ContextMenuTarget target = e.ContextMenuTarget;
            string pageUrl = target.PageUri ?? CurrentPageUrl();
            string linkUrl = target.HasLinkUri ? target.LinkUri ?? string.Empty : string.Empty;
            string sourceUrl = target.HasSourceUri ? target.SourceUri ?? string.Empty : string.Empty;
            string selection = target.HasSelection ? (target.SelectionText ?? string.Empty).Trim() : string.Empty;
            bool targetIsMedia = target.Kind is CoreWebView2ContextMenuTargetKind.Audio or CoreWebView2ContextMenuTargetKind.Video;

            CoreWebView2Environment environment = _webView.CoreWebView2.Environment;
            e.MenuItems.Add(environment.CreateContextMenuItem(
                string.Empty, null, CoreWebView2ContextMenuItemKind.Separator));

            if (LooksLikeTransferableSource(linkUrl))
            {
                CoreWebView2ContextMenuItem linkItem = environment.CreateContextMenuItem(
                    "Télécharger ce lien avec CloudTorrent", null, CoreWebView2ContextMenuItemKind.Command);
                linkItem.CustomItemSelected += (_, _) => _ = ExecuteContextActionAsync(
                    () => QueueSourceAsync(linkUrl, "Lien de la page"), openPanel: false);
                e.MenuItems.Add(linkItem);
            }

            if (LooksLikeTransferableSource(sourceUrl))
            {
                CoreWebView2ContextMenuItem mediaItem = environment.CreateContextMenuItem(
                    "Télécharger ce média avec CloudTorrent", null, CoreWebView2ContextMenuItemKind.Command);
                mediaItem.CustomItemSelected += (_, _) => _ = ExecuteContextActionAsync(
                    () => QueueSourceAsync(sourceUrl, "Média de la page"), openPanel: false);
                e.MenuItems.Add(mediaItem);
            }

            if (LooksLikeTransferableSource(selection))
            {
                CoreWebView2ContextMenuItem selectionItem = environment.CreateContextMenuItem(
                    "Envoyer la sélection vers CloudTorrent", null, CoreWebView2ContextMenuItemKind.Command);
                selectionItem.CustomItemSelected += (_, _) => _ = ExecuteContextActionAsync(
                    () => QueueSourceAsync(selection, "Sélection de la page"), openPanel: false);
                e.MenuItems.Add(selectionItem);
            }

            CoreWebView2ContextMenuItem analyzeItem = environment.CreateContextMenuItem(
                "Analyser cette page avec CloudTorrent", null, CoreWebView2ContextMenuItemKind.Command);
            analyzeItem.CustomItemSelected += (_, _) => _ = ExecuteContextActionAsync(async () =>
            {
                await AnalyzeNowAsync();
                string analysisTarget = targetIsMedia && LooksLikeHttp(sourceUrl) ? sourceUrl : pageUrl;
                await AnalyzeOnServerAsync(analysisTarget);
                return true;
            }, openPanel: true);
            e.MenuItems.Add(analyzeItem);

        }

        private void Core_WebResourceResponseReceived(object? sender, CoreWebView2WebResourceResponseReceivedEventArgs e)
        {
            string uri = e.Request?.Uri ?? string.Empty;
            if (!CloudTorrentPageAnalyzer.IsNetworkCandidate(uri))
                return;

            if (_observedNetworkUrls.Count >= 200)
                _observedNetworkUrls.Remove(_observedNetworkUrls.First());

            if (_observedNetworkUrls.Add(uri) && CanAutoAnalyzeCurrentPage())
                ScheduleAnalysis(TimeSpan.FromMilliseconds(1100));
        }

        private void Core_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            if (!CanAutoAnalyzeCurrentPage())
                return;

            try
            {
                using JsonDocument document = JsonDocument.Parse(e.WebMessageAsJson);
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                    return;

                if (!root.TryGetProperty("source", out JsonElement source) ||
                    !string.Equals(source.GetString(), "MyHomelabBrowser.CloudTorrent", StringComparison.Ordinal))
                    return;

                ScheduleAnalysis(TimeSpan.FromMilliseconds(1200));
            }
            catch (JsonException)
            {
            }
        }

        private void WebView_NavigationStarting(object? sender, CoreWebView2NavigationStartingEventArgs e)
        {
            _analysisDebounce.Stop();
            _analysisCts?.Cancel();
            _observedNetworkUrls.Clear();
            ServerAnalysis = null;
            ServerAnalysisSourceUrl = string.Empty;
            Analysis = CloudTorrentPageAnalysis.Empty("Navigation en cours…");
            StatusMessage = "Navigation en cours…";
            Changed?.Invoke(this);
        }

        private void WebView_NavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (CanAutoAnalyzeCurrentPage())
                ScheduleAnalysis(TimeSpan.FromMilliseconds(700));
            else
                Changed?.Invoke(this);
        }

        private void Module_StateChanged(CloudTorrentModuleSnapshot snapshot)
        {
            if (_disposed)
                return;

            _webView.Dispatcher.BeginInvoke(() =>
            {
                if (!snapshot.IsActive)
                {
                    _analysisDebounce.Stop();
                    _analysisCts?.Cancel();
                    Analysis = CloudTorrentPageAnalysis.Empty("CloudTorrent n’est pas connecté.");
                    ServerAnalysis = null;
                    StatusMessage = snapshot.Message;
                }
                else if (snapshot.AutoAnalyzePages && !_isPrivate)
                {
                    ScheduleAnalysis(TimeSpan.FromMilliseconds(500));
                }

                Changed?.Invoke(this);
            });
        }

        private async void AnalysisDebounce_Tick(object? sender, EventArgs e)
        {
            _analysisDebounce.Stop();
            if (!CanAutoAnalyzeCurrentPage())
                return;

            try
            {
                await AnalyzeNowAsync(userInitiated: false);
            }
            catch
            {
            }
        }

        private void ScheduleAnalysis(TimeSpan delay)
        {
            if (!CanAutoAnalyzeCurrentPage())
                return;

            _analysisDebounce.Stop();
            _analysisDebounce.Interval = delay;
            _analysisDebounce.Start();
        }

        private bool CanAutoAnalyzeCurrentPage()
            => CanUseModule && _module.Snapshot.AutoAnalyzePages && IsHttpPage();

        private bool IsHttpPage()
            => Uri.TryCreate(CurrentPageUrl(), UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        private string CurrentPageUrl()
            => _webView.Source?.AbsoluteUri ?? _webView.CoreWebView2?.Source ?? string.Empty;

        private string ResolveAnalysisUrl(string? requested)
        {
            string value = (requested ?? string.Empty).Trim();
            if (LooksLikeHttp(value))
                return value;

            CloudTorrentDetectedItem? preferred = Analysis.Items.FirstOrDefault(item =>
                item.IsSelected && item.Type is CloudTorrentDetectedItemType.Media or CloudTorrentDetectedItemType.Page);
            if (preferred != null)
                return preferred.Url;

            string page = CurrentPageUrl();
            if (LooksLikeHttp(page))
                return page;

            throw new InvalidOperationException("Aucune page HTTP ou HTTPS ne peut être analysée.");
        }

        private static bool LooksLikeTransferableSource(string value)
            => value.StartsWith("magnet:?", StringComparison.OrdinalIgnoreCase) || LooksLikeHttp(value);

        private static bool LooksLikeHttp(string value)
            => Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

        private async Task ExecuteContextActionAsync(Func<Task> action, bool openPanel)
        {
            try
            {
                if (openPanel)
                    OpenPanelRequested?.Invoke(this);
                await action();
            }
            catch
            {
            }
        }

        private async Task ExecuteContextActionAsync(Func<Task<bool>> action, bool openPanel)
        {
            try
            {
                if (openPanel)
                    OpenPanelRequested?.Invoke(this);
                await action();
            }
            catch
            {
            }
        }

        private async Task RefreshAccountQuietlyAsync()
        {
            try
            {
                await _module.RefreshAccountAsync();
            }
            catch
            {
            }
        }

        private void SetBusy(bool busy, string message)
        {
            IsBusy = busy;
            StatusMessage = message;
            Changed?.Invoke(this);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
                throw new ObjectDisposedException(nameof(CloudTorrentTabSession));
        }

        public void Dispose()
        {
            if (_disposed)
                return;

            _disposed = true;
            _analysisDebounce.Stop();
            _analysisDebounce.Tick -= AnalysisDebounce_Tick;
            _analysisCts?.Cancel();
            _analysisCts?.Dispose();
            _module.StateChanged -= Module_StateChanged;

            _webView.NavigationStarting -= WebView_NavigationStarting;
            _webView.NavigationCompleted -= WebView_NavigationCompleted;

            if (_webView.CoreWebView2 != null)
            {
                CoreWebView2 core = _webView.CoreWebView2;
                core.ContextMenuRequested -= Core_ContextMenuRequested;
                core.WebResourceResponseReceived -= Core_WebResourceResponseReceived;
                core.WebMessageReceived -= Core_WebMessageReceived;

                if (!string.IsNullOrWhiteSpace(_observerScriptId))
                {
                    try { core.RemoveScriptToExecuteOnDocumentCreated(_observerScriptId); }
                    catch { }
                }
            }
        }
    }
}
