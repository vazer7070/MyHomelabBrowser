using Microsoft.Web.WebView2.Wpf;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        private readonly HashSet<TabItem> _tabsBeingClosed = new();
        private readonly HashSet<WebView2> _disposedClosedWebViews = new();

        /// <summary>
        /// Arrête réellement le moteur WebView2 d'un onglet.
        /// Le mute est appliqué avant tout await afin de couper immédiatement l'audio.
        /// </summary>
        private async Task ShutdownWebTabAsync(WebTabContent content)
        {
            if (content == null)
                return;

            WebView2? web = content.Web;
            if (web == null || !_disposedClosedWebViews.Add(web))
                return;

            // Coupure immédiate côté WPF.
            try
            {
                web.IsHitTestVisible = false;
                web.Visibility = Visibility.Collapsed;
            }
            catch { }

            // Détacher les modules avant la destruction du CoreWebView2.
            try
            {
                if (_adBlockUiInitialized && _adBlockBrowser != null)
                    _adBlockBrowser.Detach(web);
            }
            catch { }

            try
            {
                content.FlashNavigationCts?.Cancel();
                content.FlashNavigationCts?.Dispose();
                content.FlashNavigationCts = null;
            }
            catch { }

            try
            {
                content.RuffleMonitor?.Dispose();
                content.RuffleMonitor = null;
            }
            catch { }

            try { content.FlashOverlay?.Hide(); } catch { }
            try { content.LegacyHost?.DetachExternalWindow(); } catch { }

            // Fermer uniquement le Basilisk appartenant à cet onglet.
            try
            {
                if (content.LegacyProc is { HasExited: false })
                {
                    content.LegacyProc.CloseMainWindow();
                    if (!content.LegacyProc.WaitForExit(500))
                        content.LegacyProc.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                try { content.LegacyProc?.Kill(entireProcessTree: true); } catch { }
            }
            finally
            {
                try { content.LegacyProc?.Dispose(); } catch { }
                content.LegacyProc = null;
                content.LegacyProfileLease?.Dispose();
                content.LegacyProfileLease = null;
            }

            var core = web.CoreWebView2;
            if (core != null)
            {
                // IsMuted agit sur l'ensemble du contenu audio du CoreWebView2,
                // y compris les lecteurs et frames internes.
                try { core.IsMuted = true; } catch { }
                try { core.Stop(); } catch { }

                // Arrêt explicite des médias de la page avant la navigation blanche.
                try
                {
                    Task stopMediaTask = core.ExecuteScriptAsync("""
                        (() => {
                            try { window.stop(); } catch (_) {}

                            const stopMediaIn = (root) => {
                                if (!root || !root.querySelectorAll) return;

                                root.querySelectorAll('video, audio').forEach(media => {
                                    try { media.muted = true; } catch (_) {}
                                    try { media.volume = 0; } catch (_) {}
                                    try { media.pause(); } catch (_) {}
                                    try { media.removeAttribute('autoplay'); } catch (_) {}
                                    try { media.removeAttribute('src'); } catch (_) {}
                                    try {
                                        media.querySelectorAll('source').forEach(source => {
                                            source.removeAttribute('src');
                                        });
                                    } catch (_) {}
                                    try { media.load(); } catch (_) {}
                                });

                                root.querySelectorAll('iframe').forEach(frame => {
                                    try { frame.src = 'about:blank'; } catch (_) {}
                                });
                            };

                            stopMediaIn(document);
                            return true;
                        })();
                        """);

                    await Task.WhenAny(stopMediaTask, Task.Delay(150));
                }
                catch { }

                // Force le processus de rendu à abandonner le document YouTube.
                try { core.Navigate("about:blank"); } catch { }
                await Task.Delay(100);

                try { core.Stop(); } catch { }
                try { core.IsMuted = true; } catch { }
            }

            // Enlever toutes les références visuelles avant Dispose().
            try
            {
                if (ReferenceEquals(WebHost.Content, content.HostGrid))
                    WebHost.Content = null;
            }
            catch { }

            try
            {
                if (content.HostGrid.Children.Contains(web))
                    content.HostGrid.Children.Remove(web);
            }
            catch { }

            try { web.Dispose(); } catch { }
        }
    }
}
