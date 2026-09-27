using System;
using System.Windows;
using System.Windows.Controls;

namespace MyHomelabBrowser.controles
{
    /// <summary>
    /// Panneau de la barre d'onglets : répartit la largeur disponible entre les onglets
    /// (comme Chrome ou Edge) au lieu de les laisser déborder hors de la fenêtre.
    /// Les onglets épinglés gardent leur largeur fixe (MinWidth == MaxWidth).
    /// </summary>
    public sealed class TabStripPanel : Panel
    {
        // En dessous, seul le favicon et la croix restent lisibles.
        public const double CompressedWidth = 52;

        protected override Size MeasureOverride(Size availableSize)
        {
            double[] widths = ComputeWidths(availableSize.Width);
            double height = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
            double total = 0;

            for (int i = 0; i < InternalChildren.Count; i++)
            {
                UIElement child = InternalChildren[i];
                child.Measure(new Size(widths[i], availableSize.Height));
                total += widths[i];
                height = Math.Max(height, child.DesiredSize.Height);
            }

            return new Size(double.IsInfinity(availableSize.Width) ? total : Math.Min(total, availableSize.Width), height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double[] widths = ComputeWidths(finalSize.Width);
            double x = 0;

            for (int i = 0; i < InternalChildren.Count; i++)
            {
                InternalChildren[i].Arrange(new Rect(x, 0, widths[i], finalSize.Height));
                x += widths[i];
            }

            return finalSize;
        }

        private double[] ComputeWidths(double available)
        {
            int count = InternalChildren.Count;
            var widths = new double[count];
            if (count == 0)
                return widths;

            double fixedWidth = 0;
            int flexibleCount = 0;
            double maxFlexible = 0;

            for (int i = 0; i < count; i++)
            {
                if (InternalChildren[i] is FrameworkElement fe && IsFixed(fe))
                {
                    widths[i] = fe.MinWidth;
                    fixedWidth += fe.MinWidth;
                }
                else
                {
                    flexibleCount++;
                    double max = InternalChildren[i] is FrameworkElement f && !double.IsInfinity(f.MaxWidth) ? f.MaxWidth : 240;
                    maxFlexible = Math.Max(maxFlexible, max);
                }
            }

            if (flexibleCount == 0)
                return widths;

            double share = double.IsInfinity(available)
                ? maxFlexible
                : Math.Max(0, available - fixedWidth) / flexibleCount;

            for (int i = 0; i < count; i++)
            {
                if (InternalChildren[i] is FrameworkElement fe && IsFixed(fe))
                    continue;

                double max = InternalChildren[i] is FrameworkElement f && !double.IsInfinity(f.MaxWidth) ? f.MaxWidth : 240;
                widths[i] = Math.Floor(Math.Clamp(share, CompressedWidth, max));
            }

            return widths;
        }

        private static bool IsFixed(FrameworkElement element)
            => element.MinWidth > 0 && Math.Abs(element.MinWidth - element.MaxWidth) < 0.5;
    }
}
