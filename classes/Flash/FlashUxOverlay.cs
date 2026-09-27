using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.classes.Flash
{
    /// <summary>
    /// Bandeau d'information Flash affiché au-dessus de la page. C'est un Popup :
    /// un élément WPF classique serait masqué par la fenêtre native du WebView2.
    /// </summary>
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

        public FlashUxOverlay(Window owner)
        {
            _owner = owner;

            // Repositionnement si la fenêtre bouge ou change de taille.
            _owner.LocationChanged += (_, _) => { if (_isVisible) InvalidateLayout(); };
            _owner.SizeChanged += (_, _) => { if (_isVisible) InvalidateLayout(); };

            // Un Popup reste au premier plan : on le masque quand la fenêtre est réduite.
            _owner.StateChanged += (_, _) =>
            {
                if (_popup != null && _isVisible)
                    _popup.IsOpen = _owner.WindowState != WindowState.Minimized;
            };
        }

        /// <summary>
        /// Cible visuelle à couvrir (l'hôte de l'onglet).
        /// </summary>
        public void BindHost(UIElement placementTarget)
        {
            if (placementTarget == null) return;

            _placementTarget = placementTarget;

            if (_popup != null)
                _popup.PlacementTarget = _owner;

            if (_isVisible)
                InvalidateLayout();
        }

        private void EnsureCreated()
        {
            if (_popup != null)
                return;

            _message = new TextBlock
            {
                FontSize = 13.5,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 12),
                MaxWidth = 640
            };
            _message.SetResourceReference(TextBlock.ForegroundProperty, "TextPrimaryBrush");

            _primaryBtn = new Button
            {
                Content = Tr("Fermer"),
                Margin = new Thickness(0, 0, 8, 0),
                HorizontalAlignment = HorizontalAlignment.Left
            };
            _primaryBtn.SetResourceReference(FrameworkElement.StyleProperty, "PrimaryButtonStyle");
            _primaryBtn.Click += (_, _) => (_primaryAction ?? Hide).Invoke();

            _secondaryBtn = new Button
            {
                Content = Tr("Paramètres"),
                HorizontalAlignment = HorizontalAlignment.Left,
                Visibility = Visibility.Collapsed
            };
            _secondaryBtn.Click += (_, _) => _secondaryAction?.Invoke();

            var btnRow = new StackPanel { Orientation = Orientation.Horizontal };
            btnRow.Children.Add(_primaryBtn);
            btnRow.Children.Add(_secondaryBtn);

            var icon = new TextBlock
            {
                Text = "",
                FontSize = 18,
                Margin = new Thickness(0, 1, 14, 0),
                VerticalAlignment = VerticalAlignment.Top
            };
            icon.SetResourceReference(TextBlock.FontFamilyProperty, "IconFont");
            icon.SetResourceReference(TextBlock.ForegroundProperty, "WarningBrush");

            var content = new StackPanel();
            content.Children.Add(_message);
            content.Children.Add(btnRow);

            var layout = new DockPanel();
            DockPanel.SetDock(icon, Dock.Left);
            layout.Children.Add(icon);
            layout.Children.Add(content);

            _root = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(18, 16, 18, 16),
                Margin = new Thickness(12),
                Opacity = 0,
                Child = layout,
                IsHitTestVisible = true
            };
            _root.SetResourceReference(Border.BackgroundProperty, "PanelBrush");
            _root.SetResourceReference(Border.BorderBrushProperty, "BorderStrongBrush");
            _root.SetResourceReference(UIElement.EffectProperty, "PopupShadow");

            // Coordonnées écran calculées par TryUpdatePlacement.
            _popup = new Popup
            {
                AllowsTransparency = true,
                StaysOpen = true,
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
            EnsureCreated();

            _primaryAction = primaryAction;
            _secondaryAction = secondaryAction;

            _primaryBtn!.Content = primaryText;
            _primaryBtn.Visibility = Visibility.Visible;

            if (!string.IsNullOrWhiteSpace(secondaryText))
            {
                _secondaryBtn!.Content = secondaryText!;
                _secondaryBtn.Visibility = Visibility.Visible;
            }
            else
            {
                _secondaryBtn!.Visibility = Visibility.Collapsed;
            }
        }

        private void ResetActions()
        {
            _primaryAction = null;
            _secondaryAction = null;

            if (_primaryBtn != null)
                _primaryBtn.Content = Tr("Fermer");

            if (_secondaryBtn != null)
                _secondaryBtn.Visibility = Visibility.Collapsed;
        }

        private bool TryUpdatePlacement()
        {
            if (_popup == null || _root == null || _placementTarget == null)
                return false;

            if (_placementTarget is not FrameworkElement fe || !fe.IsVisible)
                return false;

            _root.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            var popupSize = _root.DesiredSize;

            var topLeftPx = fe.PointToScreen(new Point(0, 0));

            var src = PresentationSource.FromVisual(_owner);
            if (src?.CompositionTarget == null)
                return false;

            var topLeftDip = src.CompositionTarget.TransformFromDevice.Transform(topLeftPx);

            // Centré horizontalement, en haut de la zone de la page.
            _popup.HorizontalOffset = topLeftDip.X + (fe.ActualWidth - popupSize.Width) / 2.0;
            _popup.VerticalOffset = topLeftDip.Y + 12;

            return true;
        }

        public void InvalidateLayout()
        {
            if (_owner.Dispatcher.CheckAccess())
                TryUpdatePlacement();
            else
                _owner.Dispatcher.Invoke(() => TryUpdatePlacement(), DispatcherPriority.Render);
        }

        public bool ShowBlocked(string message)
        {
            try
            {
                void Show()
                {
                    if (_placementTarget == null)
                        return;

                    EnsureCreated();
                    if (_popup == null || _root == null || _message == null)
                        return;

                    // Les actions sont redéfinies par l'appelant (SetActions) après l'affichage.
                    ResetActions();
                    _message.Text = message;

                    _popup.IsOpen = _owner.WindowState != WindowState.Minimized;
                    TryUpdatePlacement();

                    _isVisible = true;

                    // Toujours relancer le fondu : un Hide encore en cours ne doit pas
                    // refermer l'overlay qu'on vient d'afficher.
                    _root.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation
                    {
                        To = 1,
                        Duration = TimeSpan.FromMilliseconds(160),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    });
                }

                if (_owner.Dispatcher.CheckAccess())
                    Show();
                else
                    _owner.Dispatcher.Invoke(Show, DispatcherPriority.Send);

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

            void DoHide()
            {
                if (!_isVisible)
                    return;

                _isVisible = false;

                var fadeOut = new DoubleAnimation
                {
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(140),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };

                fadeOut.Completed += (_, _) =>
                {
                    // Réaffiché pendant le fondu : on ne ferme pas.
                    if (!_isVisible)
                        _popup.IsOpen = false;
                };

                _root.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            }

            if (_owner.Dispatcher.CheckAccess())
                DoHide();
            else
                _owner.Dispatcher.Invoke(DoHide, DispatcherPriority.Send);
        }

        public void DeactivateTabVisuals() => Hide();
        public void DetachVisualOnly() => Hide();
    }
}
