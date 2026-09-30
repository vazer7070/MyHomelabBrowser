using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.controles;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        private void InitializeFlashRuntimeForCore(WebTabContent content)
        {
            if (content.FlashNetworkHookAttached || content.Web.CoreWebView2 == null)
                return;

            content.FlashNetworkHookAttached = true;
            RuffleAssetService.Configure(content.Web.CoreWebView2);
            _ = InstallFlashDocumentProbeAsync(content.Web.CoreWebView2);
            if (_settings.Settings.EnableFlashSupport)
                _ = InstallRufflePluginAsync(content.Web.CoreWebView2);

            try
            {
                content.Web.CoreWebView2.AddWebResourceRequestedFilter(
                    "*://*/*.swf*",
                    CoreWebView2WebResourceContext.All);

                content.Web.CoreWebView2.WebResourceRequested += (_, args) =>
                {
                    content.FlashService?.RegisterNetworkResource(args.Request.Uri);
                };
            }
            catch (Exception ex)
            {
                FlashDbg("[FlashRuntime] network hook: " + ex.Message);
            }
        }


        private static async Task InstallFlashDocumentProbeAsync(CoreWebView2 core)
        {
            try
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(FlashDocumentProbe.Script)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                FlashDebugConsole.Log("Flash document probe: " + ex.Message);
            }
        }

        /// <summary>
        /// Flash annoncé à la page avant ses scripts (voir RufflePluginScript) : les sites qui
        /// vérifient sa présence (détection d'Adobe, SWFObject…) ajoutent alors leur contenu.
        /// </summary>
        private static async Task InstallRufflePluginAsync(CoreWebView2 core)
        {
            try
            {
                await core.AddScriptToExecuteOnDocumentCreatedAsync(RufflePluginScript.Source)
                    .ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                FlashDebugConsole.Log("Ruffle plugin: " + ex.Message);
            }
        }

        private void ResetFlashForNavigation(WebTabContent content)
        {
            content.FlashNavigationGeneration++;

            content.FlashNavigationCts?.Cancel();
            content.FlashNavigationCts?.Dispose();
            content.FlashNavigationCts = new CancellationTokenSource();

            content.FlashChecked = false;
            content.FlashRequired = false;
            content.LastFlashDetection = FlashDetectionResult.None;
            content.RuffleFailureReason = null;
            content.RuffleFailureStatus = null;
            content.FlashService?.ResetNavigationEvidence();

            try
            {
                content.RuffleMonitor?.Dispose();
                content.RuffleMonitor = null;
            }
            catch { }

            try { _ = RuffleInjector.ResetAsync(content.Web); } catch { }
            try { content.FlashOverlay?.Hide(); } catch { }

            if (!content.IsLegacyExternal && !content.IsLegacyLaunching)
                content.FlashMode = FlashMode.None;
        }

        private async Task ScheduleFlashRechecksAsync(
            WebView2 web,
            WebTabContent content,
            BrowserTabHeader header,
            FlashUxOverlay overlay)
        {
            int generation = content.FlashNavigationGeneration;
            CancellationToken token = content.FlashNavigationCts?.Token ?? CancellationToken.None;
            int[] delays = { 1800, 5000, 10000 };

            foreach (int delay in delays)
            {
                try
                {
                    await Task.Delay(delay, token).ConfigureAwait(true);
                }
                catch (OperationCanceledException)
                {
                    return;
                }

                if (token.IsCancellationRequested ||
                    generation != content.FlashNavigationGeneration ||
                    content.FlashRequired ||
                    content.IsLegacyExternal ||
                    content.IsLegacyLaunching ||
                    web.CoreWebView2 == null)
                {
                    return;
                }

                try
                {
                    await HandleFlashAsync(web, content, header, overlay, forceRecheck: true)
                        .ConfigureAwait(true);
                }
                catch (Exception ex)
                {
                    FlashDbg("[FlashRuntime] delayed scan: " + ex.Message);
                }
            }
        }

        private void StopRuffleMonitoring(WebTabContent content)
        {
            try
            {
                content.RuffleMonitor?.Dispose();
                content.RuffleMonitor = null;
            }
            catch { }
        }


        private void RestoreFlashOverlayForSelectedTab(TabItem tab, WebTabContent content)
        {
            FlashUxOverlay? overlay = content.FlashOverlay;
            if (overlay == null || content.Web.Source == null)
                return;

            overlay.BindHost(content.HostGrid);

            if (!string.IsNullOrWhiteSpace(content.RuffleFailureReason))
            {
                Uri uri = content.Web.Source;
                string reason = content.RuffleFailureReason;
                overlay.ShowBlocked(FlashCompatibilityPolicy.BuildRuffleFailureMessage(
                    content.LastFlashDetection, reason, content.RuffleFailureStatus));

                Action retryRuffle = () =>
                {
                    content.RuffleFailureReason = null;
                    content.RuffleFailureStatus = null;
                    if (tab.Header is BrowserTabHeader header)
                        _ = HandleFlashAsync(content.Web, content, header, overlay, forceRecheck: true);
                };

                if (_legacyLauncher.CanLaunch() && tab.Header is BrowserTabHeader legacyHeader)
                {
                    overlay.SetActions(
                        Tr("Ouvrir avec le moteur Legacy"),
                        () =>
                        {
                            content.FlashMode = FlashMode.Legacy;
                            _ = LaunchLegacyIntoInternalTabAsync(content, uri, legacyHeader);
                        },
                        Tr("Réessayer Ruffle"),
                        retryRuffle);
                }
                else
                {
                    overlay.SetActions(
                        Tr("Réessayer Ruffle"),
                        retryRuffle,
                        Tr("Configurer Basilisk"),
                        OpenSettings);
                }

                return;
            }

            if (content.FlashRequired &&
                content.FlashMode == FlashMode.Ruffle &&
                content.RuffleMonitor != null)
            {
                overlay.TryShow(Tr("Démarrage du moteur Ruffle intégré…"));
                return;
            }

            overlay.Hide();
        }

        internal Task ShutdownDetachedTabAsync(WebTabContent content) =>
            ShutdownWebTabAsync(content);

        public bool CanUseFlashOrLegacy(Uri uri, bool flashDetected, out string reason)
        {
            reason = "";

            // Pas de Flash → aucune contrainte
            if (!flashDetected)
                return true;

            var s = _settings.Settings;

            if (!s.EnableFlashSupport)
            {
                reason = Tr("Le support Flash est désactivé dans les paramètres.");
                return false;
            }

            var rule = FlashDomainRules.GetRule(uri);

            bool legacyRequired = rule == FlashRuleMode.Legacy;

            if (legacyRequired && !_legacyLauncher.CanLaunch())
            {
                reason = Tr("Ce site nécessite Flash réel, mais Basilisk n’est pas configuré.");
                return false;
            }

            return true;
        }

        private async Task<bool> TryLaunchLegacy(
     WebTabContent content,
     Uri uri,
     BrowserTabHeader header)
        {
            try
            {
                if (!_legacyLauncher.CanLaunch())
                {
                    content.LegacyLastError = Tr("Basilisk n’est pas configuré ou chemin invalide.");
                    return false;
                }

                return await LaunchLegacyIntoInternalTabAsync(content, uri, header);
            }
            catch (Exception ex)
            {
                content.LegacyLastError = ex.ToString();
                return false;
            }
        }

        async Task HandleFlashAsync(
            WebView2 web,
            WebTabContent content,
            BrowserTabHeader header,
            FlashUxOverlay overlay,
            bool forceRecheck = false)
        {
            if (web.Source == null || content.FlashService == null || web.CoreWebView2 == null)
                return;

            Uri uri = web.Source;
            FlashDbg($"HandleFlashAsync ENTER url={uri} force={forceRecheck}");

            FlashRuleMode rule = FlashDomainRules.GetRule(uri);
            bool forceLegacy = rule == FlashRuleMode.Legacy || content.ForceLegacyOnce;
            content.ForceLegacyOnce = false;

            if (rule == FlashRuleMode.Disabled)
            {
                content.FlashChecked = true;
                content.FlashRequired = false;
                content.LastFlashDetection = FlashDetectionResult.None;
                content.FlashMode = FlashMode.None;
                StopRuffleMonitoring(content);
                overlay.Hide();
                ApplyLegacyRuleToMode(content);
                return;
            }

            async Task LaunchLegacyExplicitAsync()
            {
                if (!_legacyLauncher.CanLaunch())
                {
                    overlay.BindHost(content.HostGrid);
                    overlay.ShowBlocked(Tr("Le moteur Legacy n'est pas configuré."));
                    overlay.SetActions(Tr("Configurer Basilisk"), OpenSettings);
                    return;
                }

                StopRuffleMonitoring(content);
                content.FlashMode = FlashMode.Legacy;
                overlay.BindHost(content.HostGrid);
                overlay.TryShow(Tr("Ouverture manuelle avec le moteur Legacy…"));
                ApplyLegacyRuleToMode(content);

                bool ok = await TryLaunchLegacy(content, uri, header).ConfigureAwait(true);
                if (ok)
                {
                    FlashCompatibilityMemory.RecordLegacySuccess(
                        uri,
                        content.LastFlashDetection);
                    overlay.Hide();
                    SyncWebHostWithSelection();
                }
                else
                {
                    overlay.ShowBlocked(
                        Tr("Impossible de lancer Basilisk.\n\n") +
                        (content.LegacyLastError ?? Tr("Erreur inconnue.")));
                    overlay.SetActions(Tr("Réessayer"), () => _ = LaunchLegacyExplicitAsync(),
                        Tr("Paramètres"), OpenSettings);
                }
            }

            void ShowRuffleFailure(string reason)
            {
                content.RuffleFailureReason = reason;
                content.RuffleFailureStatus ??= content.RuffleMonitor?.LastStatus;
                if (!IsActiveTab(content))
                    return;

                overlay.BindHost(content.HostGrid);
                overlay.ShowBlocked(FlashCompatibilityPolicy.BuildRuffleFailureMessage(
                    content.LastFlashDetection, reason, content.RuffleFailureStatus));

                if (_legacyLauncher.CanLaunch())
                {
                    overlay.SetActions(
                        Tr("Ouvrir avec le moteur Legacy"),
                        () => _ = LaunchLegacyExplicitAsync(),
                        Tr("Réessayer Ruffle"),
                        () => _ = HandleFlashAsync(web, content, header, overlay, forceRecheck: true));
                }
                else
                {
                    overlay.SetActions(
                        Tr("Réessayer Ruffle"),
                        () => _ = HandleFlashAsync(web, content, header, overlay, forceRecheck: true),
                        Tr("Configurer Basilisk"),
                        OpenSettings);
                }
            }

            if (forceLegacy)
            {
                await LaunchLegacyExplicitAsync().ConfigureAwait(true);
                return;
            }

            if (!forceRecheck && content.FlashChecked && !content.FlashRequired)
                return;

            FlashDetectionResult detection = await content.FlashService.DetectAsync().ConfigureAwait(true);

            content.FlashChecked = true;
            content.FlashRequired = detection.Detected;
            content.LastFlashDetection = detection;
            ApplyLegacyRuleToMode(content);

            if (!detection.Detected)
            {
                if (IsActiveTab(content))
                    overlay.Hide();
                return;
            }

            if (IsActiveTab(content))
                overlay.BindHost(content.HostGrid);

            if (!CanUseFlashOrLegacy(uri, flashDetected: true, out string reason))
            {
                overlay.ShowBlocked(reason);
                overlay.SetActions(Tr("Paramètres"), OpenSettings);
                return;
            }

            FlashMode mode = content.FlashService.DecideInitialMode(
                uri, forceLegacyOnce: false, detection);
            content.FlashMode = mode;

            if (mode == FlashMode.Legacy)
            {
                await LaunchLegacyExplicitAsync().ConfigureAwait(true);
                return;
            }

            if (IsActiveTab(content))
                overlay.TryShow(Tr("Flash détecté — démarrage du moteur Ruffle intégré…\n") + detection.Describe());
            content.FlashMode = FlashMode.Ruffle;
            StopRuffleMonitoring(content);

            content.RuffleFailureReason = null;
            content.RuffleFailureStatus = null;
            RuffleInjectionResult injection = await RuffleInjector.InjectAsync(web, detection).ConfigureAwait(true);
            if (!injection.Success)
            {
                string injectionReason = injection.Error ?? injection.Status;
                FlashCompatibilityMemory.RecordRuffleFailure(uri, detection, injectionReason);
                ShowRuffleFailure(injectionReason);
                return;
            }

            content.RuffleMonitor = new RuffleMonitor(web);
            content.RuffleMonitor.ReadyDetected += status =>
            {
                content.RuffleFailureReason = null;
                content.RuffleFailureStatus = null;
                FlashCompatibilityMemory.RecordRuffleSuccess(uri, detection);
                Dispatcher.Invoke(() =>
                {
                    if (IsActiveTab(content))
                        overlay.Hide();
                });
            };

            content.RuffleMonitor.FailureDetected += failure =>
            {
                content.RuffleFailureStatus = failure.Status;
                FlashCompatibilityMemory.RecordRuffleFailure(uri, detection, failure.Reason);
                Dispatcher.Invoke(() => ShowRuffleFailure(failure.Reason));
            };

            content.RuffleMonitor.Start();
            UpdateManualLegacyButton();
        }

        private void ManualLegacyButton_Click(object sender, RoutedEventArgs e)
        {
            if (Tabs.SelectedItem is not TabItem tab ||
                tab.Tag is not WebTabContent content ||
                content.Web?.Source == null)
                return;

            var uri = content.Web.Source;

            // déjà en legacy => rien (et bouton déjà disabled normalement)
            bool alreadyLegacy =
                content.FlashMode == FlashMode.Legacy ||
                content.IsLegacyExternal ||
                FlashDomainRules.GetRule(uri) == FlashRuleMode.Legacy;

            if (alreadyLegacy)
            {
                UpdateManualLegacyButton();
                return;
            }

            // Basilisk pas dispo => rien (car activer legacy ne servirait à rien)
            if (!_legacyLauncher.CanLaunch())
            {
                content.FlashOverlay?.ShowBlocked(Tr("Flash Legacy indisponible : Basilisk n’est pas configuré."));
                UpdateManualLegacyButton();
                return;
            }

            // ✅ Popup custom
            var dlg = new controles.LegacyConfirmDialog();
            dlg.Owner = this;

            bool? ok = dlg.ShowDialog();
            if (ok != true)
                return;

            // ✅ Si Oui → on le met en Legacy direct (règle persistante)
            if (dlg.AddRule)
            {
                FlashDomainRules.SetRule(uri, FlashRuleMode.Legacy);

                // ✅ UI immédiate
                content.FlashMode = FlashMode.Legacy;

                ManualLegacyButton.IsEnabled = false;
                content.Web.CoreWebView2?.Reload();

                UpdateManualLegacyButton();
                return;
            }

            // ✅ Sinon : Legacy une fois (non persisté)
            content.ForceLegacyOnce = true;

            // ✅ UI immédiate (bouton orange dès le 1er clic)
            content.FlashMode = FlashMode.Legacy;

            ManualLegacyButton.IsEnabled = false;
            content.Web.CoreWebView2?.Reload();

            UpdateManualLegacyButton();
        }

        private void FlashDbg(string msg)
        {
            if (_settings.Settings.FlashDebugEnabled)
                MyHomelabBrowser.classes.Flash.FlashDebugConsole.Log(msg);
        }
    }
}
