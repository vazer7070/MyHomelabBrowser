using Microsoft.Web.WebView2.Wpf;
using System.Windows;
using System.Windows.Controls;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        private readonly HashSet<TabItem> _tabsBeingClosed = new();

        /// <summary>
        /// Arrête réellement le moteur WebView2 d'un onglet.
        /// Le mute est appliqué avant tout await afin de couper immédiatement l'audio.
        /// </summary>
        private async Task ShutdownWebTabAsync(WebTabContent content)
        {
            if (content == null)
                return;

            content.IsClosed = true;

            WebView2? web = content.Web;
            if (web == null || content.IsShutDown)
            {
                await StopLegacyProcessAsync(content);
                return;
            }

            // Marqueur porté par l'onglet : l'ancien HashSet gardait chaque WebView2
            // fermé en mémoire jusqu'à la fin de la session.
            content.IsShutDown = true;

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

            // Basilisk est arrêté après la coupure du son de la page.
            await StopLegacyProcessAsync(content);

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

        /// <summary>
        /// Ferme uniquement le Basilisk de cet onglet, sans bloquer l'interface.
        /// </summary>
        private static async Task StopLegacyProcessAsync(WebTabContent content)
        {
            var process = content.LegacyProc;
            content.LegacyProc = null;

            try
            {
                if (process is { HasExited: false })
                {
                    process.CloseMainWindow();

                    using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromMilliseconds(800));
                    try
                    {
                        await process.WaitForExitAsync(timeout.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
            }
            catch
            {
                try { process?.Kill(entireProcessTree: true); } catch { }
            }
            finally
            {
                try { process?.Dispose(); } catch { }
                content.LegacyProfileLease?.Dispose();
                content.LegacyProfileLease = null;
            }
        }
    }
}
