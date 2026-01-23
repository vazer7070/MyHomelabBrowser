using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MyHomelabBrowser
{
    internal static class HistorySaveThrottle
    {
        public static readonly DependencyProperty LastWriteAtProperty =
            DependencyProperty.RegisterAttached(
                "LastWriteAt",
                typeof(DateTime),
                typeof(HistorySaveThrottle),
                new PropertyMetadata(DateTime.MinValue));

        public static DateTime GetLastWriteAt(DependencyObject obj)
            => (DateTime)obj.GetValue(LastWriteAtProperty);

        public static void SetLastWriteAt(DependencyObject obj, DateTime value)
            => obj.SetValue(LastWriteAtProperty, value);

        public static readonly DependencyProperty CtsProperty =
            DependencyProperty.RegisterAttached(
                "Cts",
                typeof(CancellationTokenSource),
                typeof(HistorySaveThrottle),
                new PropertyMetadata(null));

        public static CancellationTokenSource? GetCts(DependencyObject obj)
            => (CancellationTokenSource?)obj.GetValue(CtsProperty);

        public static void SetCts(DependencyObject obj, CancellationTokenSource? value)
            => obj.SetValue(CtsProperty, value);

        public static async void RequestSave(Window owner, Action saveAction, int delayMs = 800)
        {
            if (owner == null || saveAction == null)
                return;

            try
            {
                // ✅ TOUT ce qui touche aux DependencyProperty DOIT être sur le thread UI
                if (!owner.Dispatcher.CheckAccess())
                {
                    owner.Dispatcher.Invoke(() => RequestSave(owner, saveAction, delayMs));
                    return;
                }

                // ✅ annule l'attente précédente
                var old = GetCts(owner);
                if (old != null)
                {
                    try { old.Cancel(); } catch { }
                    try { old.Dispose(); } catch { }
                }

                var cts = new CancellationTokenSource();
                SetCts(owner, cts);

                try
                {
                    await Task.Delay(delayMs, cts.Token);
                    if (cts.IsCancellationRequested)
                        return;

                    // ✅ cooldown dur : pas plus d'un write toutes les 2s
                    var last = GetLastWriteAt(owner);
                    var since = DateTime.Now - last;
                    if (since < TimeSpan.FromSeconds(2))
                    {
                        int wait = (int)(TimeSpan.FromSeconds(2) - since).TotalMilliseconds;
                        await Task.Delay(wait, cts.Token);
                        if (cts.IsCancellationRequested)
                            return;
                    }

                    // ✅ saveAction peut écrire fichier => OK de rester sur UI (simple)
                    // Si un jour ça lag, on pourra Task.Run le saveAction
                    saveAction();

                    SetLastWriteAt(owner, DateTime.Now);
                }
                catch
                {
                    // silence : jamais bloquer le navigateur
                }
                finally
                {
                    var cur = GetCts(owner);
                    if (ReferenceEquals(cur, cts))
                    {
                        try { cts.Dispose(); } catch { }
                        SetCts(owner, null);
                    }
                }
            }
            catch
            {
                // silence total (comportement actuel)
            }
        }

    }
}
