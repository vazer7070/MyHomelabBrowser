using Microsoft.Web.WebView2.Core;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Security;
using MyHomelabBrowser.controles;
using System.Net;
using System.Text.Json;
using System.Windows.Controls;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // ---------------------------
        // Sécurité de la navigation : HTTPS automatique, pistage, autorisations des sites
        // ---------------------------

        /// <summary>Tentative en HTTPS d'une adresse demandée en HTTP.</summary>
        internal sealed class HttpsUpgradeAttempt(Uri original, Uri target, HashSet<string> hosts)
        {
            public Uri Original { get; } = original;
            public Uri Target { get; } = target;
            public ulong? NavigationId { get; set; }

            /// <summary>Hôtes déjà passés en HTTPS dans cette chaîne de redirections.</summary>
            public HashSet<string> Hosts { get; } = hosts;

            /// <summary>Même adresse, quelle que soit la façon dont WebView2 la réécrit.</summary>
            public bool IsTarget(Uri uri)
                => string.Equals(uri.Scheme, Target.Scheme, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(uri.IdnHost, Target.IdnHost, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(uri.PathAndQuery, Target.PathAndQuery, StringComparison.Ordinal);
        }

        /// <summary>Au-delà, une chaîne de redirections HTTP est considérée comme une boucle.</summary>
        const int MaxHttpsUpgradesPerNavigation = 5;

        // Onglets privés : les choix valent pour la session seulement, rien n'est enregistré.
        readonly HashSet<string> _privateHttpAllowed = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<(string Site, string Kind), bool> _privatePermissions = new();

        // Échecs HTTPS qui justifient de proposer HTTP (le site ne sait pas répondre en HTTPS).
        static readonly HashSet<CoreWebView2WebErrorStatus> HttpsFailures = new()
        {
            CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect,
            CoreWebView2WebErrorStatus.CertificateExpired,
            CoreWebView2WebErrorStatus.ClientCertificateContainsErrors,
            CoreWebView2WebErrorStatus.CertificateRevoked,
            CoreWebView2WebErrorStatus.CertificateIsInvalid,
            CoreWebView2WebErrorStatus.ServerUnreachable,
            CoreWebView2WebErrorStatus.Timeout,
            CoreWebView2WebErrorStatus.ErrorHttpInvalidServerResponse,
            CoreWebView2WebErrorStatus.ConnectionAborted,
            CoreWebView2WebErrorStatus.ConnectionReset,
            CoreWebView2WebErrorStatus.Disconnected,
            CoreWebView2WebErrorStatus.CannotConnect,
            CoreWebView2WebErrorStatus.RedirectFailed,
            CoreWebView2WebErrorStatus.UnexpectedError
        };

        void AttachNavigationSecurity(WebTabContent content, CoreWebView2 core, bool isPrivate)
        {
            ApplyTrackingPrevention(core);

            core.NavigationStarting += (_, e) => OnSecureNavigationStarting(content, core, e, isPrivate);
            core.NavigationCompleted += (_, e) => OnSecureNavigationCompleted(content, core, e);
            core.WebMessageReceived += (_, e) => OnHttpsInterstitialMessage(content, core, e, isPrivate);
            core.PermissionRequested += (_, e) => OnPermissionRequested(e, isPrivate);
        }

        void ApplyTrackingPrevention(CoreWebView2 core)
        {
            try
            {
                core.Profile.PreferredTrackingPreventionLevel = _settings.Settings.TrackingPrevention switch
                {
                    BrowserSettings.TrackingProtection.Off => CoreWebView2TrackingPreventionLevel.None,
                    BrowserSettings.TrackingProtection.Basic => CoreWebView2TrackingPreventionLevel.Basic,
                    BrowserSettings.TrackingProtection.Strict => CoreWebView2TrackingPreventionLevel.Strict,
                    _ => CoreWebView2TrackingPreventionLevel.Balanced
                };
            }
            catch
            {
                // Runtime WebView2 trop ancien : son niveau par défaut reste appliqué.
            }
        }

        /// <summary>Nouveau niveau de protection appliqué aux onglets déjà ouverts.</summary>
        void ApplyTrackingPreventionToOpenTabs()
        {
            foreach (TabItem tab in Tabs.Items.OfType<TabItem>())
            {
                if (tab.Tag is WebTabContent { Web.CoreWebView2: { } core })
                    ApplyTrackingPrevention(core);
            }
        }

        bool IsHttpAllowed(string host, bool isPrivate)
            => SiteSecurityStore.Current.IsHttpAllowed(host) || (isPrivate && _privateHttpAllowed.Contains(host));

        void OnSecureNavigationStarting(WebTabContent content, CoreWebView2 core, CoreWebView2NavigationStartingEventArgs e, bool isPrivate)
        {
            if (!Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return;
            }

            // Une vraie page remplace l'avertissement éventuel.
            content.HttpsInterstitialUrl = null;
            content.HttpsInterstitialNonce = null;

            HttpsUpgradeAttempt? attempt = content.HttpsUpgrade;
            if (attempt != null)
            {
                // Début de la navigation HTTPS lancée par nous : on retient son identifiant.
                if (attempt.NavigationId == null && attempt.IsTarget(uri))
                {
                    attempt.NavigationId = e.NavigationId;
                    return;
                }

                // La version HTTPS renvoie vers HTTP : le site n'a pas de HTTPS utilisable.
                if (e.IsRedirected && uri.Scheme == Uri.UriSchemeHttp &&
                    string.Equals(uri.Host, attempt.Original.Host, StringComparison.OrdinalIgnoreCase))
                {
                    e.Cancel = true;
                    content.HttpsUpgrade = null;
                    ShowHttpsInterstitial(content, core, uri);
                    return;
                }

                if (!e.IsRedirected)
                    content.HttpsUpgrade = null;
            }

            if (!_settings.Settings.HttpsUpgrade ||
                !HttpsUpgradePolicy.ShouldUpgrade(uri, host => IsHttpAllowed(host, isPrivate)))
            {
                return;
            }

            e.Cancel = true;

            // Redirections qui renvoient sans fin vers HTTP (a → b → a…) : avertissement.
            HashSet<string> hosts = attempt != null && e.IsRedirected
                ? attempt.Hosts
                : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!hosts.Add(uri.IdnHost) || hosts.Count > MaxHttpsUpgradesPerNavigation)
            {
                content.HttpsUpgrade = null;
                ShowHttpsInterstitial(content, core, uri);
                return;
            }

            Uri target = HttpsUpgradePolicy.Upgrade(uri);
            content.HttpsUpgrade = new HttpsUpgradeAttempt(uri, target, hosts);

            Dispatcher.BeginInvoke(() =>
            {
                if (!content.IsClosed)
                    core.Navigate(target.AbsoluteUri);
            });
        }

        void OnSecureNavigationCompleted(WebTabContent content, CoreWebView2 core, CoreWebView2NavigationCompletedEventArgs e)
        {
            HttpsUpgradeAttempt? attempt = content.HttpsUpgrade;
            if (attempt?.NavigationId == null || attempt.NavigationId != e.NavigationId)
                return;

            content.HttpsUpgrade = null;

            // Un 404 ou une page d'erreur du site prouve que HTTPS fonctionne : seul un
            // échec de connexion ou de certificat justifie de proposer HTTP.
            if (e.IsSuccess || !HttpsFailures.Contains(e.WebErrorStatus))
                return;

            ShowHttpsInterstitial(content, core, attempt.Original);
        }

        /// <summary>
        /// Avertissement affiché dans l'onglet : le site ne répond pas en HTTPS.
        /// L'utilisateur choisit de continuer en HTTP (mémorisé pour ce site) ou de revenir.
        /// </summary>
        void ShowHttpsInterstitial(WebTabContent content, CoreWebView2 core, Uri httpUri)
        {
            string nonce = Guid.NewGuid().ToString("N");

            Dispatcher.BeginInvoke(() =>
            {
                if (content.IsClosed)
                    return;

                content.HttpsInterstitialNonce = nonce;
                content.HttpsInterstitialUrl = httpUri.AbsoluteUri;

                try
                {
                    core.NavigateToString(BuildHttpsInterstitialHtml(httpUri, nonce, ThemeManager.IsDark));
                }
                catch
                {
                    return;
                }

                if (IsActiveTab(content))
                    UpdateAddressBarFromTab();
            });
        }

        void OnHttpsInterstitialMessage(WebTabContent content, CoreWebView2 core, CoreWebView2WebMessageReceivedEventArgs e, bool isPrivate)
        {
            // Seule la page d'avertissement (sans origine web) connaît le jeton de l'onglet.
            string? nonce = content.HttpsInterstitialNonce;
            if (nonce == null || content.HttpsInterstitialUrl is not { } url ||
                e.Source.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            string? action;
            try
            {
                using JsonDocument message = JsonDocument.Parse(e.WebMessageAsJson);
                if (message.RootElement.ValueKind != JsonValueKind.Object ||
                    !message.RootElement.TryGetProperty("nonce", out JsonElement messageNonce) ||
                    messageNonce.GetString() != nonce)
                {
                    return;
                }

                action = message.RootElement.TryGetProperty("action", out JsonElement value) ? value.GetString() : null;
            }
            catch (JsonException)
            {
                return;
            }

            content.HttpsInterstitialNonce = null;
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? httpUri))
                return;

            if (action == "proceed")
            {
                if (isPrivate)
                    _privateHttpAllowed.Add(httpUri.Host);
                else
                    SiteSecurityStore.Current.Set(httpUri.Host, SiteSecurityStore.InsecureHttp, true);

                core.Navigate(httpUri.AbsoluteUri);
            }
            else if (core.CanGoBack)
            {
                core.GoBack();
            }
            else
            {
                content.HttpsInterstitialUrl = null;
                core.Navigate("about:blank");
                if (IsActiveTab(content))
                    UpdateAddressBarFromTab();
            }
        }

        static string BuildHttpsInterstitialHtml(Uri httpUri, string nonce, bool dark)
        {
            string E(string text) => WebUtility.HtmlEncode(text);
            string background = dark ? "#16181d" : "#f5f6f8";
            string card = dark ? "#1f2229" : "#ffffff";
            string text = dark ? "#e8eaef" : "#1b1d22";
            string muted = dark ? "#a3a8b4" : "#5b606b";
            string border = dark ? "#2e323b" : "#dcdfe5";

            return $$"""
<!doctype html>
<html lang="{{MyHomelabBrowser.classes.Localization.Loc.Language}}">
<head>
<meta charset="utf-8">
<meta name="color-scheme" content="{{(dark ? "dark" : "light")}}">
<title>{{E(Tr("Connexion non sécurisée"))}}</title>
<style>
  body { margin: 0; min-height: 100vh; display: flex; align-items: center; justify-content: center;
         background: {{background}}; color: {{text}}; font: 14px/1.5 "Segoe UI Variable Text", "Segoe UI", sans-serif; }
  .card { max-width: 560px; margin: 24px; padding: 28px 32px; background: {{card}}; border: 1px solid {{border}}; border-radius: 12px; }
  .icon { width: 44px; height: 44px; border-radius: 22px; background: rgba(245, 166, 35, .16); color: #f5a623;
          display: flex; align-items: center; justify-content: center; font-size: 22px; margin-bottom: 14px; }
  h1 { font-size: 20px; font-weight: 600; margin: 0 0 8px; }
  p { margin: 0 0 10px; color: {{muted}}; }
  .host { color: {{text}}; font-weight: 600; word-break: break-all; }
  .actions { display: flex; gap: 8px; justify-content: flex-end; margin-top: 22px; }
  button { font: inherit; padding: 7px 16px; border-radius: 8px; cursor: pointer; border: 1px solid {{border}};
           background: transparent; color: {{text}}; }
  button.primary { background: #3b82f6; border-color: #3b82f6; color: #fff; }
</style>
</head>
<body>
<div class="card">
  <div class="icon">⚠</div>
  <h1>{{E(Tr("Ce site ne propose pas de connexion sécurisée"))}}</h1>
  <p>{{E(Tr("PommeBrowser a essayé d’ouvrir {0} en HTTPS, sans succès.", httpUri.Host))}}</p>
  <p>{{E(Tr("En HTTP, ce que vous envoyez et recevez (mots de passe, formulaires) circule en clair et peut être lu ou modifié sur le réseau."))}}</p>
  <p class="host">{{E(httpUri.AbsoluteUri)}}</p>
  <div class="actions">
    <button id="proceed">{{E(Tr("Continuer en HTTP"))}}</button>
    <button id="back" class="primary">{{E(Tr("Revenir en arrière"))}}</button>
  </div>
</div>
<script>
  const send = action => window.chrome?.webview?.postMessage({ nonce: {{JsonSerializer.Serialize(nonce)}}, action });
  document.getElementById('proceed').onclick = () => send('proceed');
  document.getElementById('back').onclick = () => send('back');
  document.getElementById('back').focus();
</script>
</body>
</html>
""";
        }

        // ---------------------------
        // Autorisations demandées par les sites
        // ---------------------------

        static string? PermissionLabel(CoreWebView2PermissionKind kind) => kind switch
        {
            CoreWebView2PermissionKind.Camera => Tr("utiliser votre caméra"),
            CoreWebView2PermissionKind.Microphone => Tr("utiliser votre micro"),
            CoreWebView2PermissionKind.Geolocation => Tr("connaître votre position"),
            CoreWebView2PermissionKind.Notifications => Tr("afficher des notifications"),
            CoreWebView2PermissionKind.OtherSensors => Tr("utiliser les capteurs de l’appareil"),
            CoreWebView2PermissionKind.ClipboardRead => Tr("lire le contenu du presse-papiers"),
            CoreWebView2PermissionKind.MultipleAutomaticDownloads => Tr("télécharger plusieurs fichiers automatiquement"),
            CoreWebView2PermissionKind.FileReadWrite => Tr("modifier des fichiers de cet ordinateur"),
            CoreWebView2PermissionKind.LocalFonts => Tr("voir les polices installées"),
            CoreWebView2PermissionKind.MidiSystemExclusiveMessages => Tr("contrôler vos appareils MIDI"),
            CoreWebView2PermissionKind.WindowManagement => Tr("placer des fenêtres sur tous vos écrans"),
            _ => null
        };

        /// <summary>Nom affiché d'une autorisation mémorisée (paramètres).</summary>
        public static string DescribeSiteDecisionKind(string kind)
        {
            if (kind == SiteSecurityStore.InsecureHttp)
                return Tr("Connexion non sécurisée (HTTP)");

            return Enum.TryParse(kind, out CoreWebView2PermissionKind permission) && PermissionLabel(permission) is { } label
                ? char.ToUpper(label[0]) + label[1..]
                : kind;
        }

        async void OnPermissionRequested(CoreWebView2PermissionRequestedEventArgs e, bool isPrivate)
        {
            // Gestionnaire async void : aucune exception ne doit en sortir.
            CoreWebView2Deferral? deferral = null;
            try
            {
                string? label = PermissionLabel(e.PermissionKind);
                if (label == null || !Uri.TryCreate(e.Uri, UriKind.Absolute, out Uri? origin))
                    return; // Choix par défaut de WebView2 (lecture automatique…).

                string site = origin.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
                string kind = e.PermissionKind.ToString();

                bool? remembered = isPrivate
                    ? _privatePermissions.TryGetValue((site, kind), out bool privateChoice) ? privateChoice : null
                    : SiteSecurityStore.Current.Get(site, kind);

                if (remembered is bool known)
                {
                    e.State = known ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                    e.Handled = true;
                    return;
                }

                deferral = e.GetDeferral();

                // Laisser la navigation en cours se terminer avant d'ouvrir la boîte.
                await Dispatcher.InvokeAsync(() => { });

                var dialog = new PermissionDialog(origin.Host, label, isPrivate);
                bool allowed = dialog.ShowFor(this) && dialog.Allowed;

                e.State = allowed ? CoreWebView2PermissionState.Allow : CoreWebView2PermissionState.Deny;
                e.Handled = true;

                if (dialog.Remember)
                {
                    if (isPrivate)
                        _privatePermissions[(site, kind)] = allowed;
                    else
                        SiteSecurityStore.Current.Set(site, kind, allowed);
                }
            }
            catch
            {
                try { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; } catch { }
            }
            finally
            {
                try { deferral?.Complete(); } catch { }
            }
        }
    }
}
