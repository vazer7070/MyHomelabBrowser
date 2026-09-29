using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace PommeBrowser.Views
{
    /// <summary>Indicateur de chargement (arc qui tourne), dessiné seulement quand il est visible.</summary>
    public sealed class Spinner : Control
    {
        public static readonly StyledProperty<IBrush?> ForegroundProperty =
            AvaloniaProperty.Register<Spinner, IBrush?>(nameof(Foreground));

        readonly DispatcherTimer _timer;
        double _angle;

        public Spinner()
        {
            Width = 16;
            Height = 16;
            _timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
            {
                _angle = (_angle + 7) % 360;
                InvalidateVisual();
            });
        }

        public IBrush? Foreground
        {
            get => GetValue(ForegroundProperty);
            set => SetValue(ForegroundProperty, value);
        }

        protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
        {
            base.OnPropertyChanged(change);
            if (change.Property == IsVisibleProperty)
                UpdateTimer();
        }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            UpdateTimer();
        }

        protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnDetachedFromVisualTree(e);
            _timer.Stop();
        }

        void UpdateTimer()
        {
            if (IsVisible && VisualRoot != null)
                _timer.Start();
            else
                _timer.Stop();
        }

        public override void Render(DrawingContext context)
        {
            IBrush brush = Foreground ?? this.FindResource("AccentBrush") as IBrush ?? Brushes.DodgerBlue;
            var pen = new Pen(brush, 2, lineCap: PenLineCap.Round);
            double radius = Math.Min(Bounds.Width, Bounds.Height) / 2 - 1.5;
            var center = new Point(Bounds.Width / 2, Bounds.Height / 2);

            var geometry = new StreamGeometry();
            using (StreamGeometryContext g = geometry.Open())
            {
                double start = _angle * Math.PI / 180;
                double end = start + Math.PI * 1.4;
                g.BeginFigure(center + new Vector(Math.Cos(start), Math.Sin(start)) * radius, false);
                g.ArcTo(center + new Vector(Math.Cos(end), Math.Sin(end)) * radius, new Size(radius, radius), 0, true, SweepDirection.Clockwise);
                g.EndFigure(false);
            }
            context.DrawGeometry(null, pen, geometry);
        }
    }
}
