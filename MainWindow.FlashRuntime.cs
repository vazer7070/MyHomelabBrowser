using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.controles;
using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

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
                        "Ouvrir avec le moteur Legacy",
                        () =>
                        {
                            content.FlashMode = FlashMode.Legacy;
                            _ = LaunchLegacyIntoInternalTabAsync(content, uri, legacyHeader);
                        },
                        "Réessayer Ruffle",
                        retryRuffle);
                }
                else
                {
                    overlay.SetActions(
                        "Réessayer Ruffle",
                        retryRuffle,
                        "Configurer Basilisk",
                        OpenSettings);
                }

                return;
            }

            if (content.FlashRequired &&
                content.FlashMode == FlashMode.Ruffle &&
                content.RuffleMonitor != null)
            {
                overlay.TryShow("Démarrage du moteur Ruffle intégré…");
                return;
            }

            overlay.Hide();
        }

        internal Task ShutdownDetachedTabAsync(WebTabContent content) =>
            ShutdownWebTabAsync(content);
    }
}
