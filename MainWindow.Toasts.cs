using MyHomelabBrowser.classes;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        private const int MaxVisibleToasts = 4;
        private readonly ObservableCollection<ToastItem> _toasts = new();

        /// <summary>
        /// Les notifications vivent dans un Popup : le WebView2 est une fenêtre native
        /// (HwndHost) et masque tout élément WPF dessiné par-dessus. Auparavant les toasts
        /// étaient en plus placés dans la ligne de la barre d'onglets et rognés.
        /// </summary>
        private void InitializeToasts()
        {
            ToastHost.ItemsSource = _toasts;
            ToastPopup.CustomPopupPlacementCallback = PlaceToastPopup;

            LocationChanged += (_, _) => RepositionToastPopup();
            SizeChanged += (_, _) => RepositionToastPopup();
            _toasts.CollectionChanged += (_, _) =>
            {
                ToastPopup.IsOpen = _toasts.Count > 0 && IsActive && WindowState != WindowState.Minimized;
                RepositionToastPopup();
            };

            Activated += (_, _) =>
            {
                if (_toasts.Count > 0)
                    ToastPopup.IsOpen = true;
            };
        }

        private CustomPopupPlacement[] PlaceToastPopup(Size popupSize, Size targetSize, Point offset)
        {
            // Coin inférieur droit de la zone web, avec une marge.
            var point = new Point(
                Math.Max(0, targetSize.Width - popupSize.Width - 18),
                Math.Max(0, targetSize.Height - popupSize.Height - 18));

            return new[] { new CustomPopupPlacement(point, PopupPrimaryAxis.None) };
        }

        private void RepositionToastPopup()
        {
            if (!ToastPopup.IsOpen)
                return;

            // Forcer WPF à recalculer la position du Popup.
            ToastPopup.HorizontalOffset += 0.01;
            ToastPopup.HorizontalOffset -= 0.01;
        }

        private void HideTransientPopups()
        {
            ToastPopup.IsOpen = false;
            CommandSuggestionsPopup.IsOpen = false;

            // La barre de recherche reste ouverte : elle réapparaît à la réactivation.
            FindPopup.IsOpen = false;
        }

        public void ShowToast(string title, string message, DownloadItem? item)
        {
            if (item == null)
            {
                ShowToast(title, message);
                return;
            }

            ShowToast(
                title,
                message,
                ToastKind.Success,
                "Ouvrir le fichier",
                () => DownloadManager.Instance.OpenFile(item));
        }

        public void ShowToast(
            string title,
            string message,
            ToastKind kind = ToastKind.Info,
            string? actionLabel = null,
            Action? action = null,
            TimeSpan? duration = null)
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(() => ShowToast(title, message, kind, actionLabel, action, duration));
                return;
            }

            var toast = new ToastItem
            {
                Title = title,
                Message = message,
                Kind = kind,
                ActionLabel = actionLabel,
                Action = action,
                Duration = duration ?? (action != null ? TimeSpan.FromSeconds(8) : TimeSpan.FromSeconds(4.5))
            };

            _toasts.Add(toast);

            while (_toasts.Count > MaxVisibleToasts)
                _toasts.RemoveAt(0);

            var timer = new DispatcherTimer { Interval = toast.Duration };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                _toasts.Remove(toast);
            };
            timer.Start();
        }

        private void ToastAction_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is not ToastItem toast)
                return;

            _toasts.Remove(toast);

            try
            {
                toast.Action?.Invoke();
            }
            catch (Exception ex)
            {
                ShowToast("Action impossible", ex.Message, ToastKind.Warning);
            }
        }

        private void ToastClose_Click(object sender, RoutedEventArgs e)
        {
            if ((sender as FrameworkElement)?.DataContext is ToastItem toast)
                _toasts.Remove(toast);
        }

        private void ToastCard_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement card)
                return;

            var translate = new TranslateTransform(24, 0);
            card.RenderTransform = translate;
            card.Opacity = 0;

            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            card.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
            translate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(220)) { EasingFunction = easing });
        }
    }
}
