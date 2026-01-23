using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MyHomelabBrowser
{
    internal static class HistoryNavigationIdleSave
    {
        public static readonly DependencyProperty SaveCtsProperty =
            DependencyProperty.RegisterAttached(
                "SaveCts",
                typeof(CancellationTokenSource),
                typeof(HistoryNavigationIdleSave),
                new PropertyMetadata(null));

        public static CancellationTokenSource? GetSaveCts(DependencyObject obj)
            => (CancellationTokenSource?)obj.GetValue(SaveCtsProperty);

        public static void SetSaveCts(DependencyObject obj, CancellationTokenSource? value)
            => obj.SetValue(SaveCtsProperty, value);

        public static async void RequestIdleSave(Window owner, Action saveAction, int idleMs = 2500)
        {
            if (owner == null || saveAction == null)
                return;

            // ✅ annule la demande précédente
            var old = GetSaveCts(owner);
            if (old != null)
            {
                try { old.Cancel(); } catch { }
                try { old.Dispose(); } catch { }
            }

            var cts = new CancellationTokenSource();
            SetSaveCts(owner, cts);

            try
            {
                // ✅ on attend que ça "se calme"
                await Task.Delay(idleMs, cts.Token);
                if (cts.IsCancellationRequested)
                    return;

                saveAction();
            }
            catch
            {
                // silence
            }
            finally
            {
                var cur = GetSaveCts(owner);
                if (ReferenceEquals(cur, cts))
                {
                    try { cts.Dispose(); } catch { }
                    SetSaveCts(owner, null);
                }
            }
        }
    }
}
