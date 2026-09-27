using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.classes.Profiles;
using MyHomelabBrowser.classes.Profiles.Credentials;
using MyHomelabBrowser.controles;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // Profil WebView2 InPrivate : cookies, cache et stockage restent en mémoire
        // et disparaissent à la fermeture du dernier onglet privé.
        private const string PrivateProfileName = "PommeInPrivate";

        private static readonly Lazy<ImageSource> StartTabIcon = new(() =>
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri("pack://application:,,,/Assets/logo.png", UriKind.Absolute);
            image.DecodePixelWidth = 32;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        });

        // ---------------------------
        // Environnements WebView2
        // ---------------------------
        async Task InitWebViewEnvironmentsAsync()
        {
            if (_privateEnvironment != null && _normalEnvironment != null)
                return;

            var baseDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyHomelabBrowser"
            );

            var opts = CreateWebViewEnvironmentOptions();

            _normalEnvironment ??= await CoreWebView2Environment.CreateAsync(
                userDataFolder: Path.Combine(baseDir, "Default"),
                options: opts
            );

            if (_privateEnvironment == null)
            {
                string privateFolder = Path.Combine(baseDir, "Private");

                // Les versions précédentes écrivaient les onglets privés sur le disque.
                // Ces restes (cookies, cache) sont effacés avant d'ouvrir l'environnement.
                PurgeLegacyPrivateData(privateFolder);

                _privateEnvironment = await CoreWebView2Environment.CreateAsync(
                    userDataFolder: privateFolder,
                    options: opts
                );
            }
        }

        static void PurgeLegacyPrivateData(string privateFolder)
        {
            try
            {
                string legacyProfile = Path.Combine(privateFolder, "EBWebView", "Default");
                if (Directory.Exists(legacyProfile))
                    Directory.Delete(legacyProfile, recursive: true);
            }
            catch
            {
                // Dossier encore utilisé par une autre instance : nouvel essai au prochain démarrage.
            }
        }

        async Task<CoreWebView2Environment> GetEnvironmentForCurrentProfileAsync()
        {
            var profileId = WebViewProfileData.NormalizeId(_profileService.Current?.Username);

            if (_envByProfile.TryGetValue(profileId, out var cached))
                return cached;

            string userData = WebViewProfileData.GetUserDataFolder(profileId);

            Directory.CreateDirectory(userData);

            var env = await CoreWebView2Environment.CreateAsync(
                null,
                userData,
                CreateWebViewEnvironmentOptions()
            );

            _envByProfile[profileId] = env;
            return env;
        }

        CoreWebView2ControllerOptions CreatePrivateControllerOptions(CoreWebView2Environment environment)
        {
            var options = environment.CreateCoreWebView2ControllerOptions();
            options.IsInPrivateModeEnabled = true;
            options.ProfileName = PrivateProfileName;
            return options;
        }

        // ---------------------------
        // Création des onglets
        // ---------------------------
        void CreateTab(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                CreateEmptyStartTab();
                return;
            }

            _ = CreateTabInternal(url);
        }

        Task CreateTabInternal(string url)
            => CreateWebTabAsync(url, isPrivate: false);

        void CreatePrivateTab(string url)
            => _ = CreateWebTabAsync(url, isPrivate: true);

        private void NewPrivateTab_Click(object sender, RoutedEventArgs e)
        {
            string url = GetNewTabUrl();
            CreatePrivateTab(url == "about:blank" ? string.Empty : url);
        }

        void CreateEmptyStartTab(int insertIndex = -1)
        {
            var header = new BrowserTabHeader();
            header.SetTitle("Accueil");
            header.SetIcon(StartTabIcon.Value);

            var content = new WebTabContent
            {
                IsCustomView = true,
                IsPinned = false,
                IsSuspended = false,
                LastActivated = DateTime.Now
            };

            var tab = new TabItem
            {
                Header = header,
                Tag = content
            };

            content.OwnerTab = tab;
            content.CreateView = () => CreateStartPageView(tab);
            content.HostGrid.Children.Add(content.CreateView());

            WireTabHeader(tab, header, content);
            AddTab(tab, insertIndex, select: true);
        }

        EmptyStartPage CreateStartPageView(TabItem? owningTab)
        {
            var view = new EmptyStartPage
            {
                SearchEngine = _settings.Settings.Search
            };
            view.SetHistory(_history);
            view.SetFavorites(_favorites);
            view.NavigateRequested += url =>
            {
                // Une recherche depuis la page d'accueil remplace l'onglet d'accueil,
                // au lieu d'ouvrir un onglet supplémentaire à côté.
                if (owningTab != null && Tabs.Items.Contains(owningTab))
                    ReplaceTabWithWeb(owningTab, url);
                else
                    _ = CreateTabInternal(url);
            };
            return view;
        }

        void ReplaceTabWithWeb(TabItem tab, string url)
        {
            int index = Tabs.Items.IndexOf(tab);
            bool isPrivate = tab.Tag is WebTabContent { IsPrivate: true };

            _ = CreateWebTabAsync(url, isPrivate, insertIndex: index < 0 ? -1 : index);

            if (index >= 0)
                Tabs.Items.Remove(tab);
        }

        void AddTab(TabItem tab, int insertIndex, bool select)
        {
            if (insertIndex >= 0 && insertIndex <= Tabs.Items.Count)
                Tabs.Items.Insert(insertIndex, tab);
            else
                Tabs.Items.Add(tab);

            if (select)
            {
                Tabs.SelectedItem = tab;
                SyncWebHostWithSelection();
            }
        }

        void WireTabHeader(TabItem tab, BrowserTabHeader header, WebTabContent content)
        {
            header.CloseRequested += () => CloseTab(tab);

            header.DetachRequested += () =>
            {
                if (_isDocking)
                {
                    header.ResetVisualState();
                    return;
                }

                DetachTab(tab);
            };

            header.PinRequested += () =>
            {
                content.IsPinned = !content.IsPinned;
                ApplyPinState(tab, header, content.IsPinned);
            };

            header.ReorderRequested += dir => ReorderTab(tab, dir);
        }

        /// <summary>
        /// Crée un onglet web (normal ou privé). Retourne après l'initialisation du moteur,
        /// ce qui permet de l'utiliser comme cible de window.open.
        /// </summary>
        async Task<WebTabContent?> CreateWebTabAsync(
            string? url,
            bool isPrivate,
            bool select = true,
            int insertIndex = -1,
            string? pendingTitle = null,
            bool deferNavigation = false,
            CoreWebView2Environment? environmentOverride = null)
        {
            var web = new WebView2();
            DownloadHook.Attach(web, isPrivate);

            var header = new BrowserTabHeader();
            header.SetTitle(!string.IsNullOrWhiteSpace(pendingTitle)
                ? pendingTitle
                : isPrivate ? "Onglet privé" : "Nouvel onglet");
            header.SetPrivate(isPrivate);
            header.SetLoading(!deferNavigation && !string.IsNullOrWhiteSpace(url));

            var overlay = new FlashUxOverlay(this);
            var flashService = new FlashDecisionService(web, _settings, _legacyLauncher);

            var content = new WebTabContent
            {
                Web = web,
                IsPrivate = isPrivate,
                IsPinned = false,
                IsSuspended = false,
                LastActivated = DateTime.Now,
                FlashService = flashService,
                FlashMode = FlashMode.None,
                FlashOverlay = overlay,
                PendingUrl = deferNavigation ? url : null
            };

            content.HostGrid.Children.Add(web);

            var tab = new TabItem
            {
                Header = header,
                Tag = content
            };

            content.OwnerTab = tab;
            WireTabHeader(tab, header, content);

            if (!isPrivate)
                AttachHistoryRecording(web, content);

            web.SourceChanged += (_, _) =>
            {
                if (!IsActiveTab(content))
                    return;

                Dispatcher.BeginInvoke(UpdateAddressBarFromTab);
                Dispatcher.BeginInvoke(UpdateFillCredentialButtonState);
            };

            AddTab(tab, insertIndex, select);

            // ===============================
            // Moteur WebView2
            // ===============================
            try
            {
                CoreWebView2Environment environment;

                if (isPrivate)
                {
                    await InitWebViewEnvironmentsAsync();
                    environment = environmentOverride ?? _privateEnvironment!;
                    await web.EnsureCoreWebView2Async(environment, CreatePrivateControllerOptions(environment));
                }
                else
                {
                    environment = environmentOverride ?? await GetEnvironmentForCurrentProfileAsync();
                    await web.EnsureCoreWebView2Async(environment);
                }
            }
            catch (Exception ex)
            {
                // Onglet fermé pendant l'initialisation, ou runtime WebView2 absent/cassé.
                if (!content.IsClosed)
                {
                    header.SetLoading(false);
                    ShowTabError(content, "Impossible de démarrer le moteur web de cet onglet.", ex.Message);
                }

                FlashDbg("[CreateWebTabAsync] " + ex);
                return content;
            }

            CoreWebView2? core = web.CoreWebView2;
            if (content.IsClosed || core == null)
                return content;

            InitializeFlashRuntimeForCore(content);
            AttachSiteZoom(web, content);

            core.NavigationStarting += (_, e) =>
            {
                ResetFlashForNavigation(content);
                content.IsLoading = true;
                header.SetLoading(true);

                if (IsActiveTab(content))
                {
                    UpdateNavButtonsFast();

                    // Nouvelle page : les résultats de recherche ne s'appliquent plus.
                    if (!e.IsRedirected && _findBarOpen)
                        CloseFindBar(focusPage: false);
                }
            };

            web.NavigationCompleted += async (_, e) =>
            {
                content.IsLoading = false;
                header.SetLoading(false);

                if (IsActiveTab(content))
                {
                    UpdateAddressBarFromTab();
                    UpdateFavoriteButton();
                    UpdateManualLegacyButton();
                    UpdateNavButtons();
                }

                try
                {
                    await HandleFlashAsync(web, content, header, overlay, forceRecheck: true);
                    _ = ScheduleFlashRechecksAsync(web, content, header, overlay);
                }
                catch { }

                if (!isPrivate && !content.IsClosed)
                {
                    AttachPreview(tab, web);

                    // Miniature pour l'aperçu au survol, uniquement si l'onglet est visible.
                    await Task.Delay(50);
                    await CaptureAndCachePreviewAsync(tab, web);
                }
            };

            core.DocumentTitleChanged += (_, _) =>
            {
                string title = core.DocumentTitle;
                header.SetTitle(string.IsNullOrWhiteSpace(title)
                    ? isPrivate ? "Onglet privé" : "Nouvel onglet"
                    : title);

                if (!isPrivate)
                    UpdateHistoryTitle(content, web.Source?.AbsoluteUri, title);
            };

            core.FaviconChanged += async (_, _) => await RefreshTabFaviconAsync(content, header);

            core.ContainsFullScreenElementChanged += (_, _) =>
                SetHtmlFullscreen(core.ContainsFullScreenElement && IsActiveTab(content));

            core.NewWindowRequested += (_, ev) => OnNewWindowRequested(web, content, ev);

            // Autorise la capture d'identifiants (JS -> C#) dans les onglets normaux.
            core.Settings.IsWebMessageEnabled = true;

            if (!isPrivate)
            {
                await CredentialInjector.Attach(
                    web,
                    isPrivateTab: () => content.IsPrivate,
                    isVaultUnlocked: () => _vault.IsUnlocked,
                    getCredentialForOrigin: uri => _vault.FindForOrigin(uri),
                    onCredentialCaptured: async candidate =>
                    {
                        await HandleCredentialCandidateAsync(candidate);
                        await Dispatcher.InvokeAsync(UpdateFillCredentialButtonState);
                    });
            }

            await AttachAdBlockToWebViewAsync(web, isPrivate);

            if (IsActiveTab(content))
                UpdateFillCredentialButtonState();

            if (!deferNavigation && !string.IsNullOrWhiteSpace(url))
                NavigateWebView(content, url);

            return content;
        }

        void NavigateWebView(WebTabContent content, string url)
        {
            if (content.Web?.CoreWebView2 == null)
            {
                content.PendingUrl = url;
                return;
            }

            try
            {
                content.Web.CoreWebView2.Navigate(url);
            }
            catch (ArgumentException)
            {
                // URL refusée par WebView2 : on bascule sur une recherche.
                content.Web.CoreWebView2.Navigate(UrlResolver.BuildSearchUrl(url, _settings.Settings.Search));
            }
        }

        /// <summary>
        /// Navigation différée (onglets restaurés en arrière-plan) : l'onglet ne charge
        /// sa page qu'à sa première sélection, ce qui allège fortement le démarrage.
        /// </summary>
        void EnsurePendingNavigation(WebTabContent content)
        {
            if (string.IsNullOrWhiteSpace(content.PendingUrl) || content.Web?.CoreWebView2 == null)
                return;

            string url = content.PendingUrl;
            content.PendingUrl = null;
            NavigateWebView(content, url);
        }

        void ShowTabError(WebTabContent content, string title, string detail)
        {
            var panel = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                MaxWidth = 520
            };

            var titleBlock = new TextBlock
            {
                Text = title,
                FontSize = 18,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            };
            titleBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

            var detailBlock = new TextBlock
            {
                Text = detail,
                Margin = new Thickness(0, 10, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center
            };
            detailBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextSecondaryBrush");

            panel.Children.Add(titleBlock);
            panel.Children.Add(detailBlock);

            content.HostGrid.Children.Clear();
            content.HostGrid.Children.Add(panel);
        }

        // ---------------------------
        // Historique
        // ---------------------------
        void AttachHistoryRecording(WebView2 web, WebTabContent content)
        {
            // URL stabilisée : on attend la fin des redirections avant d'écrire.
            var historyDebounce = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };

            string? pendingUrl = null;

            historyDebounce.Tick += (_, _) =>
            {
                historyDebounce.Stop();

                if (pendingUrl == null || content.IsClosed)
                    return;

                AddHistoryEntryFinal(web, content, pendingUrl);
                pendingUrl = null;
            };

            web.SourceChanged += (_, _) =>
            {
                if (web.Source == null)
                    return;

                pendingUrl = web.Source.AbsoluteUri;
                historyDebounce.Stop();
                historyDebounce.Start();
            };
        }

        void AddHistoryEntryFinal(WebView2 web, WebTabContent content, string url)
        {
            var now = DateTime.Now;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var u))
                return;

            if (u.Scheme != Uri.UriSchemeHttp &&
                u.Scheme != Uri.UriSchemeHttps)
                return;

            // Anti-duplication rapide (rechargements, redirections internes).
            if (string.Equals(url, _lastHistoryUrl, StringComparison.OrdinalIgnoreCase) &&
                (now - _lastHistoryAt) < TimeSpan.FromSeconds(3))
                return;

            _lastHistoryUrl = url;
            _lastHistoryAt = now;

            string? title = null;
            try { title = web.CoreWebView2?.DocumentTitle; } catch { }

            var entry = new HistoryEntry
            {
                Title = string.IsNullOrWhiteSpace(title) ? u.Host : title,
                Url = url,
                VisitedAt = now
            };

            _history.Add(entry);
            content.LastHistoryEntry = entry;

            const int max = 5000;
            if (_history.Count > max)
                _history.RemoveRange(0, _history.Count - max);

            ScheduleHistorySave();
        }

        /// <summary>
        /// Le titre n'est souvent pas encore connu quand l'entrée d'historique est créée :
        /// on le complète dès que la page le publie.
        /// </summary>
        void UpdateHistoryTitle(WebTabContent content, string? url, string? title)
        {
            HistoryEntry? entry = content.LastHistoryEntry;
            if (entry == null || string.IsNullOrWhiteSpace(title) || url == null)
                return;

            if (!string.Equals(entry.Url, url, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Title, title, StringComparison.Ordinal))
                return;

            entry.Title = title;
            ScheduleHistorySave();
        }

        // ---------------------------
        // Favicons
        // ---------------------------
        async Task RefreshTabFaviconAsync(WebTabContent content, BrowserTabHeader header)
        {
            var core = content.Web?.CoreWebView2;
            if (core == null || content.IsClosed)
                return;

            try
            {
                if (string.IsNullOrEmpty(core.FaviconUri))
                {
                    header.SetIcon(null);
                    return;
                }

                using Stream stream = await core.GetFaviconAsync(CoreWebView2FaviconImageFormat.Png);
                if (stream == null || content.IsClosed)
                    return;

                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer);
                byte[] bytes = buffer.ToArray();

                ImageSource? icon = FaviconStore.Decode(bytes);
                header.SetIcon(icon);

                // Le cache local ne reçoit jamais rien des onglets privés.
                if (!content.IsPrivate && icon != null)
                {
                    using var copy = new MemoryStream(bytes, writable: false);
                    await FaviconStore.SaveAsync(content.Web?.Source?.AbsoluteUri, copy);
                }
            }
            catch
            {
                // Favicon indisponible : l'onglet garde l'icône par défaut.
            }
        }

        void OnFaviconStored(string host)
        {
            foreach (FavoriteItem favorite in _favorites)
            {
                if (string.Equals(FaviconStore.HostFromUrl(favorite.Url), host, StringComparison.OrdinalIgnoreCase))
                {
                    RefreshFavoritesBar();
                    return;
                }
            }
        }

        // ---------------------------
        // Nouvelles fenêtres (window.open, target=_blank)
        // ---------------------------
        static bool IsAuthenticationUrl(string uri)
        {
            return uri.Contains("accounts.google.com", StringComparison.OrdinalIgnoreCase)
                || uri.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)
                || uri.Contains("appleid.apple.com", StringComparison.OrdinalIgnoreCase)
                || uri.Contains("gameforge.com/service/external-auth", StringComparison.OrdinalIgnoreCase)
                || uri.Contains("oauth", StringComparison.OrdinalIgnoreCase)
                || uri.Contains("openid", StringComparison.OrdinalIgnoreCase)
                || uri.Contains("/authorize", StringComparison.OrdinalIgnoreCase);
        }

        async void OnNewWindowRequested(WebView2 opener, WebTabContent openerContent, CoreWebView2NewWindowRequestedEventArgs ev)
        {
            var deferral = ev.GetDeferral();

            try
            {
                string uri = ev.Uri ?? string.Empty;
                bool isBlank = string.IsNullOrWhiteSpace(uri) ||
                               uri.Equals("about:blank", StringComparison.OrdinalIgnoreCase);

                // Page de retour Gameforge : WebView2 la gère lui-même.
                bool isMessageCallback =
                    uri.Contains("gameforge.com/service/external-auth", StringComparison.OrdinalIgnoreCase) &&
                    uri.EndsWith("/message", StringComparison.OrdinalIgnoreCase);

                if (isMessageCallback)
                {
                    ev.Handled = false;
                    return;
                }

                bool requestedAsPopup =
                    isBlank ||
                    IsAuthenticationUrl(uri) ||
                    ev.WindowFeatures?.HasSize == true ||
                    ev.WindowFeatures?.HasPosition == true;

                if (requestedAsPopup)
                {
                    if (!await TryRouteToPopupAsync(opener, ev))
                        ev.Handled = false;
                    return;
                }

                // Fenêtre ouverte sans action de l'utilisateur : bloquée (popunders publicitaires).
                if (!ev.IsUserInitiated)
                {
                    ev.Handled = true;
                    NotifyPopupBlocked(openerContent, uri);
                    return;
                }

                // Lien target=_blank : nouvel onglet du même type, relié à l'opener.
                var environment = opener.CoreWebView2?.Environment;
                var created = await CreateWebTabAsync(
                    url: null,
                    isPrivate: openerContent.IsPrivate,
                    select: true,
                    insertIndex: Tabs.Items.Count,
                    environmentOverride: environment);

                if (created?.Web?.CoreWebView2 != null)
                {
                    ev.NewWindow = created.Web.CoreWebView2;
                    ev.Handled = true;
                    return;
                }

                // Repli : nouvel onglet classique sur l'URL.
                ev.Handled = true;
                if (openerContent.IsPrivate)
                    CreatePrivateTab(uri);
                else
                    CreateTab(uri);
            }
            catch (Exception ex)
            {
                FlashDbg("[NewWindowRequested] " + ex);
                try { ev.Handled = false; } catch { }
            }
            finally
            {
                deferral.Complete();
            }
        }

        void NotifyPopupBlocked(WebTabContent content, string uri)
        {
            if (string.IsNullOrWhiteSpace(uri) || !IsActiveTab(content))
                return;

            // Une seule notification par onglet toutes les 15 secondes.
            if (DateTime.UtcNow - content.LastPopupNoticeUtc < TimeSpan.FromSeconds(15))
                return;

            content.LastPopupNoticeUtc = DateTime.UtcNow;

            ShowToast(
                "Fenêtre surgissante bloquée",
                uri,
                actionLabel: "Ouvrir",
                action: () =>
                {
                    if (content.IsPrivate)
                        CreatePrivateTab(uri);
                    else
                        CreateTab(uri);
                });
        }

        // ---------------------------
        // Plein écran HTML (vidéos)
        // ---------------------------
        bool _htmlFullscreen;
        WindowState _stateBeforeFullscreen;
        WindowStyle _styleBeforeFullscreen;
        ResizeMode _resizeBeforeFullscreen;

        void SetHtmlFullscreen(bool enabled)
        {
            if (_htmlFullscreen == enabled)
                return;

            _htmlFullscreen = enabled;

            if (enabled)
            {
                _stateBeforeFullscreen = WindowState;
                _styleBeforeFullscreen = WindowStyle;
                _resizeBeforeFullscreen = ResizeMode;

                BrowserChrome.Visibility = Visibility.Collapsed;

                // Passer par Normal permet à Maximized de couvrir aussi la barre des tâches.
                WindowStyle = WindowStyle.None;
                ResizeMode = ResizeMode.NoResize;
                WindowState = WindowState.Normal;
                WindowState = WindowState.Maximized;
            }
            else
            {
                BrowserChrome.Visibility = Visibility.Visible;

                WindowStyle = _styleBeforeFullscreen;
                ResizeMode = _resizeBeforeFullscreen;
                WindowState = _stateBeforeFullscreen;
            }
        }

        void ExitHtmlFullscreenIfNeeded()
        {
            if (!_htmlFullscreen)
                return;

            if (Tabs.SelectedItem is TabItem { Tag: WebTabContent content } &&
                content.Web?.CoreWebView2?.ContainsFullScreenElement == true)
            {
                _ = content.Web.CoreWebView2.ExecuteScriptAsync("document.exitFullscreen && document.exitFullscreen()");
            }

            SetHtmlFullscreen(false);
        }
    }
}
