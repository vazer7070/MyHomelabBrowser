using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles
{
    public partial class BrowserTabHeader : UserControl
    {
        public event Action<int>? ReorderRequested;
        public event Action? CloseRequested;
        public event Action? PinRequested;
        public event Action? DetachRequested;

        private double _lastReorderX;
        private bool _pinned;
        private bool _hovered;
        private bool _loading;
        private bool _hasIcon;
        private Point _dragStart;
        private bool _dragging;
        private readonly DoubleAnimation _spin;

        public BrowserTabHeader()
        {
            InitializeComponent();

            CloseBtn.Click += (_, _) => CloseRequested?.Invoke();
            PinBtn.Click += (_, _) => PinRequested?.Invoke();
            PinnedUnpinBtn.Click += (_, _) => PinRequested?.Invoke();

            _spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
            {
                RepeatBehavior = RepeatBehavior.Forever
            };
        }

        public string TabTitle => Title.Text ?? string.Empty;

        public void SetPrivate(bool isPrivate)
        {
            PrivateBadge.Visibility = isPrivate ? Visibility.Visible : Visibility.Collapsed;
        }

        public void SetTitle(string title)
        {
            Title.Text = title ?? string.Empty;
            ToolTip = string.IsNullOrWhiteSpace(Title.Text) ? null : Title.Text;

            if (_pinned)
                Root.ToolTip = string.IsNullOrWhiteSpace(Title.Text)
                    ? Tr("Onglet épinglé")
                    : Tr("{0}\nOnglet épinglé", Title.Text);
        }

        public void SetIcon(ImageSource? icon)
        {
            _hasIcon = icon != null;
            Icon.Source = icon;
            PinnedIcon.Source = icon;
            UpdateIconVisibility();
        }

        public void SetLoading(bool loading)
        {
            if (_loading == loading)
                return;

            _loading = loading;

            if (loading)
                SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, _spin);
            else
                SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);

            UpdateIconVisibility();
        }

        private void UpdateIconVisibility()
        {
            LoadingSpinner.Visibility = _loading ? Visibility.Visible : Visibility.Collapsed;
            Icon.Visibility = !_loading && _hasIcon ? Visibility.Visible : Visibility.Collapsed;
            DefaultIcon.Visibility = !_loading && !_hasIcon ? Visibility.Visible : Visibility.Collapsed;

            PinnedIcon.Visibility = _hasIcon ? Visibility.Visible : Visibility.Collapsed;
            PinnedDefaultIcon.Visibility = _hasIcon ? Visibility.Collapsed : Visibility.Visible;
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
                ? new Thickness(4, 0, 4, 0)
                : new Thickness(10, 0, 6, 0);

            Root.ToolTip = pinned
                ? (string.IsNullOrWhiteSpace(TabTitle)
                    ? Tr("Onglet épinglé")
                    : Tr("{0}\nOnglet épinglé", TabTitle))
                : null;

            PinBtn.ToolTip = pinned
                ? Tr("Désépingler l’onglet")
                : Tr("Épingler l’onglet");

            if (!pinned)
                HidePinnedAction(immediate: true);

            UpdateActionVisibility();
        }

        public void ShowSuspended(bool suspended)
        {
            Root.Opacity = suspended ? 0.55 : 1.0;
            Title.FontStyle = suspended ? FontStyles.Italic : FontStyles.Normal;
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

        /// <summary>
        /// Le bouton d'épinglage n'apparaît qu'au survol pour laisser la place au titre.
        /// </summary>
        private void UpdateActionVisibility()
        {
            PinBtn.Visibility = !_pinned && _hovered ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Header_MouseEnter(object sender, MouseEventArgs e)
        {
            _hovered = true;
            UpdateActionVisibility();

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
            _hovered = false;
            UpdateActionVisibility();

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

        // Clic milieu : fermeture de l'onglet, comme dans les autres navigateurs.
        private void Header_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Middle)
                return;

            e.Handled = true;
            CloseRequested?.Invoke();
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Ne démarre pas un glisser-déposer lorsque l’utilisateur clique
            // sur une action du header.
            if (FindButtonAncestor(e.OriginalSource as DependencyObject) != null)
                return;

            ResetGhost();
            _dragStart = e.GetPosition(null);
            _lastReorderX = _dragStart.X;
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

                current = current is Visual or System.Windows.Media.Media3D.Visual3D
                    ? VisualTreeHelper.GetParent(current)
                    : LogicalTreeHelper.GetParent(current);
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
            double deltaY = position.Y - _dragStart.Y;
            double deltaX = position.X - _dragStart.X;

            // Glisser l'onglet vers le bas (hors de la barre) : détachement.
            if (Math.Abs(deltaY) > 48)
            {
                _dragging = false;
                ReleaseMouseCapture();

                var animation = new DoubleAnimation
                {
                    To = deltaY > 0 ? 20 : -20,
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

            if (Math.Abs(deltaX) <= 40)
                return;

            // Un pas de réordonnancement toutes les ~largeur d'un demi-onglet.
            double step = Math.Max(60, ActualWidth * 0.6);
            if (Math.Abs(position.X - _lastReorderX) < step)
                return;

            int direction = position.X > _lastReorderX ? 1 : -1;
            _lastReorderX = position.X;
            ReorderRequested?.Invoke(direction);
        }
    }
}
