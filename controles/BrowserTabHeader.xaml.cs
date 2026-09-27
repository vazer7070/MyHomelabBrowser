using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MyHomelabBrowser.controles
{
    public partial class BrowserTabHeader : UserControl
    {
        public event Action<int>? ReorderRequested;
        public event Action CloseRequested;
        public event Action? PinRequested;
        public event Action? DetachRequested;

        private double _lastReorderX;
        private bool _pinned;
        private Point _dragStart;
        private bool _dragging;

        public BrowserTabHeader()
        {
            InitializeComponent();

            CloseBtn.Click += (_, _) => CloseRequested?.Invoke();
            PinBtn.Click += (_, _) => PinRequested?.Invoke();
            PinnedUnpinBtn.Click += (_, _) => PinRequested?.Invoke();
        }

        public string TabTitle => Title.Text ?? string.Empty;

        public void SetPrivate(bool isPrivate)
        {
            string current = Title.Text ?? string.Empty;
            const string prefix = "🕶️ ";

            if (isPrivate && !current.StartsWith(prefix, StringComparison.Ordinal))
                SetTitle(prefix + current);
            else if (!isPrivate && current.StartsWith(prefix, StringComparison.Ordinal))
                SetTitle(current[prefix.Length..]);
        }

        public void SetTitle(string title)
        {
            Title.Text = title ?? string.Empty;

            if (_pinned)
                Root.ToolTip = string.IsNullOrWhiteSpace(Title.Text)
                    ? "Onglet épinglé"
                    : $"{Title.Text}\nOnglet épinglé";
        }

        public void SetIcon(ImageSource icon)
        {
            Icon.Source = icon;
            PinnedIcon.Source = icon;
        }

        public void ResetVisualState() => ResetGhost();

        public void SetPinned(bool pinned)
        {
            _pinned = pinned;

            NormalLayout.Visibility = pinned
                ? Visibility.Collapsed
                : Visibility.Visible;

            PinnedLayout.Visibility = pinned
                ? Visibility.Visible
                : Visibility.Collapsed;

            Root.Padding = pinned
                ? new Thickness(4, 0)
                : new Thickness(8, 0);

            Root.ToolTip = pinned
                ? (string.IsNullOrWhiteSpace(TabTitle)
                    ? "Onglet épinglé"
                    : $"{TabTitle}\nOnglet épinglé")
                : null;

            PinBtn.ToolTip = pinned
                ? "Désépingler l’onglet"
                : "Épingler l’onglet";

            if (!pinned)
                HidePinnedAction(immediate: true);
        }

        public void ShowSuspended(bool suspended)
        {
            Root.Opacity = suspended ? 0.6 : 1.0;
        }

        public void AnimateReorder(double fromX)
        {
            ReorderOffset.BeginAnimation(TranslateTransform.XProperty, null);
            ReorderOffset.X = fromX;

            var animation = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            };

            ReorderOffset.BeginAnimation(TranslateTransform.XProperty, animation);
        }

        private void Header_MouseEnter(object sender, MouseEventArgs e)
        {
            if (!_pinned)
                return;

            PinnedUnpinBtn.IsHitTestVisible = true;

            PinnedUnpinBtn.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(1, TimeSpan.FromMilliseconds(90)));

            PinnedFaviconHost.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0.18, TimeSpan.FromMilliseconds(90)));

            PinnedMarker.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, TimeSpan.FromMilliseconds(70)));
        }

        private void Header_MouseLeave(object sender, MouseEventArgs e)
        {
            if (_pinned)
                HidePinnedAction(immediate: false);
        }

        private void HidePinnedAction(bool immediate)
        {
            var duration = immediate
                ? TimeSpan.Zero
                : TimeSpan.FromMilliseconds(100);

            PinnedUnpinBtn.IsHitTestVisible = false;

            PinnedUnpinBtn.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(0, duration));

            PinnedFaviconHost.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(1, duration));

            PinnedMarker.BeginAnimation(
                OpacityProperty,
                new DoubleAnimation(1, duration));
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Ne démarre pas un glisser-déposer lorsque l’utilisateur clique
            // sur une action du header.
            if (FindButtonAncestor(e.OriginalSource as DependencyObject) != null)
                return;

            ResetGhost();
            _dragStart = e.GetPosition(null);
            _dragging = true;
            CaptureMouse();
        }

        private void Header_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            _dragging = false;
            ReleaseMouseCapture();
            ResetGhost();
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            _dragging = false;
            base.OnLostMouseCapture(e);
        }

        private static Button? FindButtonAncestor(DependencyObject? current)
        {
            while (current != null)
            {
                if (current is Button button)
                    return button;

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private void ResetGhost()
        {
            GhostOffset.BeginAnimation(TranslateTransform.YProperty, null);
            GhostOffset.Y = 0;
        }

        private void Header_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
                return;

            Point position = e.GetPosition(null);
            double deltaY = _dragStart.Y - position.Y;
            double deltaX = position.X - _dragStart.X;

            if (deltaY > 40)
            {
                _dragging = false;
                ReleaseMouseCapture();

                var animation = new DoubleAnimation
                {
                    To = -20,
                    Duration = TimeSpan.FromMilliseconds(120),
                    EasingFunction = new CubicEase
                    {
                        EasingMode = EasingMode.EaseOut
                    }
                };

                GhostOffset.BeginAnimation(TranslateTransform.YProperty, animation);
                DetachRequested?.Invoke();
                return;
            }

            if (Math.Abs(deltaX) <= 60)
                return;

            if (Math.Abs(position.X - _lastReorderX) < 40)
                return;

            _lastReorderX = position.X;
            ReorderRequested?.Invoke(deltaX > 0 ? 1 : -1);
        }
    }
}
