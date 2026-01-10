using System;
using System.Collections.Generic;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace MyHomelabBrowser.controles
{
    /// <summary>
    /// Logique d'interaction pour BrowserTabHeader.xaml
    /// </summary>
    public partial class BrowserTabHeader : UserControl
    {
        public event Action<int>? ReorderRequested;
        double _lastReorderX;
        public event Action CloseRequested;
        public event Action? PinRequested;

        public BrowserTabHeader()
        {
            InitializeComponent();
            CloseBtn.Click += (_, _) => CloseRequested?.Invoke();
            PinBtn.Click += (_, _) => PinRequested?.Invoke();
        }

        public void SetTitle(string title) => Title.Text = title;
        public void SetIcon(ImageSource icon) => Icon.Source = icon;
        bool _pinned;
        Point _dragStart;
        bool _dragging;

        public event Action? DetachRequested;
        public void ResetVisualState() => ResetGhost();

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
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
        void ResetGhost()
        {
            GhostOffset.BeginAnimation(TranslateTransform.YProperty, null);
            GhostOffset.Y = 0;
        }

        private void Header_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_dragging || e.LeftButton != MouseButtonState.Pressed)
                return;

            var pos = e.GetPosition(null);
            double deltaY = _dragStart.Y - pos.Y;
            double deltaX = pos.X - _dragStart.X;

            // --------------------
            // DETACH VERTICAL
            // --------------------
            if (deltaY > 40)
            {
                _dragging = false;
                ReleaseMouseCapture();

                var anim = new DoubleAnimation
                {
                    To = -20,
                    Duration = TimeSpan.FromMilliseconds(120),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };

                GhostOffset.BeginAnimation(TranslateTransform.YProperty, anim);
                DetachRequested?.Invoke();
                return;
            }

            // --------------------
            // REORDER HORIZONTAL
            // --------------------
            if (Math.Abs(deltaX) > 60)
            {
                // anti-spam
                if (Math.Abs(pos.X - _lastReorderX) < 40)
                    return;

                _lastReorderX = pos.X;

                ReorderRequested?.Invoke(deltaX > 0 ? +1 : -1);
            }


        }
        public void ShowSuspended(bool suspended)
        {
            Root.Opacity = suspended ? 0.6 : 1.0;
        }

        public void AnimateReorder(double fromX)
        {
            ReorderOffset.BeginAnimation(TranslateTransform.XProperty, null);
            ReorderOffset.X = fromX;

            var anim = new DoubleAnimation
            {
                To = 0,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = new CubicEase
                {
                    EasingMode = EasingMode.EaseOut
                }
            };

            ReorderOffset.BeginAnimation(TranslateTransform.XProperty, anim);
        }


        public void SetPinned(bool pinned)
        {
            PinIcon.Foreground = pinned
                ? Brushes.White
                : new SolidColorBrush(Color.FromRgb(120, 120, 120));

            PinBtn.ToolTip = pinned
                ? "Désépingler l’onglet"
                : "Épingler l’onglet";

            // titre
            Title.Visibility = pinned
                ? Visibility.Collapsed
                : Visibility.Visible;

            // close
            CloseBtn.Visibility = pinned
                ? Visibility.Collapsed
                : Visibility.Visible;

            // 🔑 padding adapté
            Root.Padding = pinned
                ? new Thickness(6, 0, 0, 0)
                : new Thickness(8, 0, 0, 0);
        }




    }

}