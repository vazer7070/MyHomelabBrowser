using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // Aperçus des onglets (capture de la page).

        static readonly DependencyProperty CachedTabPreviewProperty =
    DependencyProperty.RegisterAttached(
        "CachedTabPreview",
        typeof(BitmapSource),
        typeof(MainWindow),
        new PropertyMetadata(null)
    );

        static async System.Threading.Tasks.Task CaptureAndCachePreviewAsync(TabItem tab, WebView2 web)
        {
            if (tab == null || web?.CoreWebView2 == null)
                return;

            // WebView2 pas visible => capture noire
            if (!web.IsVisible)
                return;

            try
            {
                using var stream = new MemoryStream();
                await web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);

                stream.Position = 0;

                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = stream;
                bmp.EndInit();
                bmp.Freeze();

                SetCachedTabPreview(tab, bmp);

                // si le tooltip est déjà visible, on refresh
                if (tab.ToolTip is Border b)
                {
                    if (b.Child is Image img)
                        img.Source = bmp;
                }

                TabPreviewState.SetLastCaptureAt(tab, DateTime.Now);
            }
            catch
            {
                // jamais casser la nav
            }
        }

        static void SetCachedTabPreview(TabItem tab, BitmapSource? bmp)
            => tab.SetValue(CachedTabPreviewProperty, bmp);

        static BitmapSource? GetCachedTabPreview(TabItem tab)
            => tab.GetValue(CachedTabPreviewProperty) as BitmapSource;

        static void AttachPreview(TabItem tab, WebView2 web)
        {
            if (tab == null || web == null)
                return;

            // ✅ ne pas rehacker 50 fois
            if (TabPreviewState.GetIsHooked(tab))
                return;

            TabPreviewState.SetIsHooked(tab, true);

            // Tooltip container (une seule fois)
            var border = new Border
            {
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(32, 32, 32)),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(6),
                Width = 320,
                Height = 200
            };
            tab.ToolTip = border;

            tab.MouseLeave += (_, _) =>
            {
                // ✅ annule si on quitte
                var cts = TabPreviewState.GetCts(tab);
                if (cts != null)
                {
                    try { cts.Cancel(); } catch { }
                    try { cts.Dispose(); } catch { }
                    TabPreviewState.SetCts(tab, null);
                }
            };

            tab.MouseEnter += async (_, _) =>
            {
                if (web.CoreWebView2 == null)
                    return;

                // Tooltip content (Image) créé une fois
                if (tab.ToolTip is Border bb && bb.Child == null)
                {
                    bb.Child = new Image
                    {
                        Width = 320,
                        Height = 200,
                        Stretch = Stretch.UniformToFill
                    };
                }

                // 1) Affiche immédiatement le cache si présent
                var cached = GetCachedTabPreview(tab);
                if (cached != null && tab.ToolTip is Border b1 && b1.Child is Image img1)
                    img1.Source = cached;

                // 2) si le WebView n'est PAS visible => ne pas capturer (sinon noir)
                if (!web.IsVisible)
                    return;

                // 3) cooldown : max 1 capture / 2s
                var last = TabPreviewState.GetLastCaptureAt(tab);
                if ((DateTime.Now - last) < TimeSpan.FromSeconds(2))
                    return;

                // 4) annule ancien job
                var old = TabPreviewState.GetCts(tab);
                if (old != null)
                {
                    try { old.Cancel(); } catch { }
                    try { old.Dispose(); } catch { }
                }

                var cts = new CancellationTokenSource();
                TabPreviewState.SetCts(tab, cts);

                try
                {
                    await System.Threading.Tasks.Task.Delay(180, cts.Token);
                    if (cts.IsCancellationRequested || web.CoreWebView2 == null)
                        return;

                    // Capture + cache (et refresh tooltip si visible)
                    await CaptureAndCachePreviewAsync(tab, web);
                }
                catch
                {
                }
                finally
                {
                    var cur = TabPreviewState.GetCts(tab);
                    if (ReferenceEquals(cur, cts))
                    {
                        try { cts.Dispose(); } catch { }
                        TabPreviewState.SetCts(tab, null);
                    }
                }
            };

        }
    }
}
