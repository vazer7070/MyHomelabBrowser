using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MyHomelabBrowser.classes.Flash
{
    public sealed class FlashUxOverlay
    {
        private readonly Window _owner;

        private Popup? _popup;
        private Border? _root;
        private TextBlock? _message;
        private Button? _primaryBtn;
        private Button? _secondaryBtn;

        private Action? _primaryAction;
        private Action? _secondaryAction;

        private UIElement? _placementTarget;
        private bool _isVisible;

        public event Action? OpenSettingsRequested;

        public FlashUxOverlay(Window owner)
        {
            _owner = owner;

            // Reposition si la fenêtre bouge / resize (quand overlay visible)
            _owner.LocationChanged += (_, _) => { if (_isVisible) InvalidateLayout(); };
            _owner.SizeChanged += (_, _) => { if (_isVisible) InvalidateLayout(); };
        }

        /// <summary>
        /// Cible visuelle à couvrir (conseillé : WebHost, stable).
        /// </summary>
        public void BindHost(UIElement placementTarget)
        {
            if (placementTarget == null) return;

            _placementTarget = placementTarget;

            if (_popup != null)
                _popup.PlacementTarget = _owner; // on place en AbsolutePoint (écran), target = owner

            if (_isVisible)
                InvalidateLayout();
        }

        private void EnsureCreated()
        {
            if (_popup != null)
                return;

            _message = new TextBlock
            {
                Foreground = Brushes.White,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                MaxWidth = 720
            };

            _primaryBtn = new Button
            {
                Content = "OK",
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _primaryBtn.Click += (_, _) => _primaryAction?.Invoke();

            _secondaryBtn = new Button
            {
                Content = "Paramètres",
                Padding = new Thickness(12, 6, 12, 6),
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = Visibility.Collapsed
            };
            _secondaryBtn.Click += (_, _) => _secondaryAction?.Invoke();

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
            btnRow.Children.Add(_primaryBtn);
            btnRow.Children.Add(_secondaryBtn);

            var stack = new StackPanel();
            stack.Children.Add(_message);
            stack.Children.Add(btnRow);

            _root = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(230, 20, 20, 20)),
                CornerRadius = new CornerRadius(12),
                Padding = new Thickness(16),
                Opacity = 0,
                Child = stack,
                IsHitTestVisible = true
            };

            // Popup en coord écran
            _popup = new Popup
            {
                AllowsTransparency = true,
                StaysOpen = true,

                // on calcule nous-mêmes les offsets
                Placement = PlacementMode.AbsolutePoint,
                PlacementTarget = _owner,
                Child = _root
            };
        }

        public void SetActions(
            string primaryText,
            Action? primaryAction,
            string? secondaryText = null,
            Action? secondaryAction = null)
        {
            _primaryAction = primaryAction;
            _secondaryAction = secondaryAction;

            if (_primaryBtn != null)
            {
                _primaryBtn.Content = primaryText;
                _primaryBtn.Visibility = Visibility.Visible;
            }

            if (_secondaryBtn != null)
            {
                if (!string.IsNullOrWhiteSpace(secondaryText))
                {
                    _secondaryBtn.Content = secondaryText!;
                    _secondaryBtn.Visibility = Visibility.Visible;
                }
                else
                {
                    _secondaryBtn.Visibility = Visibility.Collapsed;
                }
            }
        }

        private bool TryUpdatePlacement()
        {
            if (_popup == null || _root == null || _placementTarget == null)
                return false;

            if (_placementTarget is not FrameworkElement fe)
                return false;

            if (!fe.IsVisible)
                return false;

            // Mesure du contenu du popup pour connaitre sa largeur/hauteur
            _root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var popupSize = _root.DesiredSize;

            // Coord écran (device px) du target
            var topLeftPx = fe.PointToScreen(new Point(0, 0));

            // Conversion device px -> WPF DIPs
            var src = PresentationSource.FromVisual(_owner);
            if (src?.CompositionTarget == null)
                return false;

            var fromDevice = src.CompositionTarget.TransformFromDevice;
            var topLeftDip = fromDevice.Transform(topLeftPx);

            // Centrage horizontal au-dessus du target, et y = top + 24
            var x = topLeftDip.X + (fe.ActualWidth - popupSize.Width) / 2.0;
            var y = topLeftDip.Y + 24;

            _popup.HorizontalOffset = x;
            _popup.VerticalOffset = y;

            return true;
        }

        public void InvalidateLayout()
        {
            if (_owner.Dispatcher.CheckAccess())
            {
                TryUpdatePlacement();
            }
            else
            {
                _owner.Dispatcher.Invoke(() => TryUpdatePlacement(), DispatcherPriority.Render);
            }
        }

        public bool ShowBlocked(string message)
        {
            try
            {
                Action show = () =>
                {
                    if (_placementTarget == null)
                        return;

                    EnsureCreated();
                    if (_popup == null || _root == null || _message == null)
                        return;

                    _message.Text = message;

                    // Important : ouvrir d'abord, puis placer
                    _popup.IsOpen = true;

                    // recalcul placement maintenant que layout existe
                    TryUpdatePlacement();

                    if (_isVisible)
                        return;

                    _isVisible = true;

                    var fadeIn = new DoubleAnimation
                    {
                        From = 0,
                        To = 1,
                        Duration = TimeSpan.FromMilliseconds(160),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };

                    _root.BeginAnimation(UIElement.OpacityProperty, fadeIn);
                };

                if (_owner.Dispatcher.CheckAccess())
                    show();
                else
                    _owner.Dispatcher.Invoke(show, DispatcherPriority.Send);

                return true;
            }
            catch
            {
                return false;
            }
        }

        public bool TryShow(string text) => ShowBlocked(text);

        public void Hide()
        {
            if (_popup == null || _root == null)
                return;

            Action hide = () =>
            {
                var fadeOut = new DoubleAnimation
                {
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(140),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };

                fadeOut.Completed += (_, _) =>
                {
                    _isVisible = false;
                    _popup.IsOpen = false;
                };

                _root.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            };

            if (_owner.Dispatcher.CheckAccess())
                hide();
            else
                _owner.Dispatcher.Invoke(hide, DispatcherPriority.Send);
        }

        public void DeactivateTabVisuals() => Hide();
        public void DetachVisualOnly() => Hide();

        // Optionnel si tu veux garder la sémantique “settings”
        public void SetOpenSettingsAction(Action action)
        {
            OpenSettingsRequested = null;
            OpenSettingsRequested += action;
        }
    }
}
