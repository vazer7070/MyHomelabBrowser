using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PommeBrowser.Views.Pages
{
    /// <summary>Page d'état : icône, titre, explication et boutons (erreur de chargement, certificat, Basilisk…).</summary>
    public sealed class StatusPage : UserControl
    {
        public StatusPage(string icon, string title, string description, (string Label, bool Primary, Action Action)[] buttons)
        {
            this.Bind(BackgroundProperty, this.GetResourceObservable("WindowBackgroundBrush"));

            var glyph = new PathIcon { Width = 56, Height = 56, HorizontalAlignment = HorizontalAlignment.Center };
            glyph.Bind(PathIcon.DataProperty, this.GetResourceObservable(icon));
            glyph.Bind(PathIcon.ForegroundProperty, this.GetResourceObservable("TextTertiaryBrush"));

            var heading = new TextBlock
            {
                Text = title,
                FontSize = 24,
                FontWeight = FontWeight.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 20, 0, 0)
            };
            var body = new SelectableTextBlock
            {
                Text = description,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 12, 0, 0)
            };
            body.Bind(SelectableTextBlock.ForegroundProperty, this.GetResourceObservable("TextSecondaryBrush"));

            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 28, 0, 0) };
            foreach ((string label, bool primary, Action action) in buttons)
            {
                var button = new Button { Content = label, Padding = new Thickness(20, 8), CornerRadius = new CornerRadius(18) };
                if (primary)
                    button.Classes.Add("primary");
                button.Click += (_, _) => action();
                row.Children.Add(button);
            }

            var column = new StackPanel { MaxWidth = 620, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(24, 40) };
            column.Children.Add(glyph);
            column.Children.Add(heading);
            column.Children.Add(body);
            if (buttons.Length > 0)
                column.Children.Add(row);

            Content = new ScrollViewer { Content = column, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        }
    }
}
