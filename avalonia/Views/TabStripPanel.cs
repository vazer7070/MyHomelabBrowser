using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;

namespace PommeBrowser.Views
{
    /// <summary>
    /// Panneau de la barre d'onglets : répartit la largeur disponible entre les onglets
    /// (comme Chrome ou Edge) au lieu de les laisser déborder hors de la fenêtre.
    /// Les onglets épinglés gardent une largeur fixe (icône seule).
    /// </summary>
    public sealed class TabStripPanel : Panel
    {
        /// <summary>En dessous, seuls l'icône et la croix restent lisibles.</summary>
        public const double CompressedWidth = 52;
        public const double MaximumWidth = 240;
        public const double PinnedWidth = 44;

        protected override Size MeasureOverride(Size availableSize)
        {
            double[] widths = ComputeWidths(availableSize.Width);
            double height = 0;
            double total = 0;

            for (int i = 0; i < Children.Count; i++)
            {
                Children[i].Measure(new Size(widths[i], availableSize.Height));
                total += widths[i];
                height = Math.Max(height, Children[i].DesiredSize.Height);
            }

            return new Size(double.IsInfinity(availableSize.Width) ? total : Math.Min(total, availableSize.Width), height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            double[] widths = ComputeWidths(finalSize.Width);
            double x = 0;
            for (int i = 0; i < Children.Count; i++)
            {
                Children[i].Arrange(new Rect(x, 0, widths[i], finalSize.Height));
                x += widths[i];
            }
            return finalSize;
        }

        static bool IsPinned(Control child)
            => child is ContentPresenter { Content: BrowserTab { IsPinned: true } } || child.DataContext is BrowserTab { IsPinned: true };

        double[] ComputeWidths(double available)
        {
            int count = Children.Count;
            var widths = new double[count];
            double fixedWidth = 0;
            int flexible = 0;

            for (int i = 0; i < count; i++)
            {
                if (IsPinned(Children[i]))
                {
                    widths[i] = PinnedWidth;
                    fixedWidth += PinnedWidth;
                }
                else
                {
                    flexible++;
                }
            }

            if (flexible == 0)
                return widths;

            double share = double.IsInfinity(available) ? MaximumWidth : Math.Max(0, available - fixedWidth) / flexible;
            for (int i = 0; i < count; i++)
            {
                if (!IsPinned(Children[i]))
                    widths[i] = Math.Floor(Math.Clamp(share, CompressedWidth, MaximumWidth));
            }
            return widths;
        }
    }
}
