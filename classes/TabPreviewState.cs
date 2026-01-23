using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace MyHomelabBrowser
{
    internal static class TabPreviewState
    {
        public static readonly DependencyProperty LastCaptureAtProperty =
            DependencyProperty.RegisterAttached(
                "LastCaptureAt",
                typeof(DateTime),
                typeof(TabPreviewState),
                new PropertyMetadata(DateTime.MinValue));

        public static DateTime GetLastCaptureAt(DependencyObject obj)
            => (DateTime)obj.GetValue(LastCaptureAtProperty);

        public static void SetLastCaptureAt(DependencyObject obj, DateTime value)
            => obj.SetValue(LastCaptureAtProperty, value);

        public static readonly DependencyProperty CtsProperty =
            DependencyProperty.RegisterAttached(
                "Cts",
                typeof(CancellationTokenSource),
                typeof(TabPreviewState),
                new PropertyMetadata(null));

        public static CancellationTokenSource? GetCts(DependencyObject obj)
            => (CancellationTokenSource?)obj.GetValue(CtsProperty);

        public static void SetCts(DependencyObject obj, CancellationTokenSource? value)
            => obj.SetValue(CtsProperty, value);

        public static readonly DependencyProperty IsHookedProperty =
            DependencyProperty.RegisterAttached(
                "IsHooked",
                typeof(bool),
                typeof(TabPreviewState),
                new PropertyMetadata(false));

        public static bool GetIsHooked(DependencyObject obj)
            => (bool)obj.GetValue(IsHookedProperty);

        public static void SetIsHooked(DependencyObject obj, bool value)
            => obj.SetValue(IsHookedProperty, value);
    }
}
