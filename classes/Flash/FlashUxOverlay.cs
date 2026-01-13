using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace MyHomelabBrowser.classes.Flash
{
    public sealed class FlashUxOverlay
    {
        private readonly Window _owner;
        private Border? _overlay;
        private TextBlock? _message;

        public event Action? OpenSettingsRequested;

        public FlashUxOverlay(Window owner)
        {
            _owner = owner;
        }

        /// <summary>
        /// Force un refresh layout léger (sans Measure/Arrange manuel).
        /// Utile quand l'overlay vient d'être injecté et que tu veux un rendu immédiat.
        /// </summary>
        public void InvalidateLayout()
        {
            _owner.Dispatcher.InvokeAsync(() =>
            {
                if (_overlay == null) return;

                // refresh doux : WPF recalculera proprement
                _overlay.InvalidateMeasure();
                _overlay.InvalidateArrange();
                _overlay.UpdateLayout();
            }, DispatcherPriority.Loaded);
        }

        // ===============================
        // OVERLAY BLOQUANT (ANCRÉ WEBVIEW)
        // ===============================
        public bool ShowBlocked(string message)
        {
            try
            {
                _owner.Dispatcher.InvokeAsync(() =>
                {
                    if (_owner.FindName("WebHost") is not ContentControl webHost)
                        return;

                    // 🔑 Overlay enfant DIRECT du contenu WebHost (jamais du parent => ne mange pas la barre d'adresse)
                    if (webHost.Content is not UIElement webContent)
                        return;

                    // On wrap une seule fois dans un Grid pour superposer l'overlay au contenu web
                    if (webHost.Content is not Grid hostGrid)
                    {
                        hostGrid = new Grid();
                        webHost.Content = hostGrid;
                        hostGrid.Children.Add(webContent);
                    }

                    if (_overlay == null)
                    {
                        _message = new TextBlock
                        {
                            Foreground = Brushes.White,
                            FontSize = 14,
                            TextWrapping = TextWrapping.Wrap,
                            Margin = new Thickness(0, 0, 0, 12),
                            MaxWidth = 720
                        };

                        var btn = new Button
                        {
                            Content = "Configurer Flash / Legacy",
                            Padding = new Thickness(12, 6, 12, 6),
                            HorizontalAlignment = HorizontalAlignment.Left
                        };

                        btn.Click += (_, _) => OpenSettingsRequested?.Invoke();

                        var stack = new StackPanel();
                        stack.Children.Add(_message);
                        stack.Children.Add(btn);

                        _overlay = new Border
                        {
                            Background = new SolidColorBrush(Color.FromArgb(220, 20, 20, 20)),
                            CornerRadius = new CornerRadius(12),
                            Padding = new Thickness(16),
                            HorizontalAlignment = HorizontalAlignment.Center,
                            VerticalAlignment = VerticalAlignment.Top,
                            Margin = new Thickness(0, 24, 0, 0),
                            Opacity = 0,
                            Child = stack,
                            IsHitTestVisible = true
                        };

                        Panel.SetZIndex(_overlay, 10_000);
                        hostGrid.Children.Add(_overlay);
                    }

                    _message!.Text = message;

                    // refresh léger juste avant anim (évite "apparition tardive")
                    InvalidateLayout();

                    var fadeIn = new DoubleAnimation
                    {
                        From = 0,
                        To = 1,
                        Duration = TimeSpan.FromMilliseconds(160),
                        EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                    };

                    _overlay.BeginAnimation(UIElement.OpacityProperty, fadeIn);

                }, DispatcherPriority.Loaded);

                return true;
            }
            catch
            {
                return false;
            }
        }

        // ===============================
        // MASQUER
        // ===============================
        public void Hide()
        {
            if (_overlay == null)
                return;

            _owner.Dispatcher.Invoke(() =>
            {
                var fadeOut = new DoubleAnimation
                {
                    To = 0,
                    Duration = TimeSpan.FromMilliseconds(140),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn }
                };

                _overlay.BeginAnimation(UIElement.OpacityProperty, fadeOut);
            });
        }

        // ===============================
        // MESSAGE NON BLOQUANT (alias)
        // ===============================
        public bool TryShow(string text)
        {
            return ShowBlocked(text);
        }
    }
}
