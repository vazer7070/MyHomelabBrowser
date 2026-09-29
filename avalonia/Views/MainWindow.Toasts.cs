using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>
    /// Notifications en bas à droite de la page (fenêtre surgissante, au-dessus de la vue native) :
    /// message, action éventuelle, fermeture automatique.
    /// </summary>
    public sealed partial class MainWindow
    {
        const int MaxToasts = 3;

        /// <summary>Affiche une notification ; <paramref name="timeout"/> en secondes (0 : jusqu'à sa fermeture).</summary>
        public void ShowToast(string message, string? actionLabel = null, Action? action = null, double timeout = 6, bool warning = false)
        {
            var card = new Border
            {
                Padding = new Thickness(14, 12, 8, 12),
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(1),
                BoxShadow = BoxShadows.Parse("0 6 24 0 #50000000")
            };
            card.Bind(Border.BackgroundProperty, this.GetResourceObservable("PanelBrush"));
            card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BorderStrongBrush"));

            var icon = new PathIcon { Width = 16, Height = 16, Margin = new Thickness(0, 1, 12, 0), VerticalAlignment = VerticalAlignment.Top };
            icon.Bind(PathIcon.DataProperty, this.GetResourceObservable(warning ? "IconWarning" : "IconInfo"));
            icon.Bind(PathIcon.ForegroundProperty, this.GetResourceObservable(warning ? "WarningBrush" : "AccentBrush"));

            var text = new StackPanel();
            text.Children.Add(new TextBlock { Text = message, FontSize = 13, TextWrapping = TextWrapping.Wrap, MaxHeight = 80 });
            if (actionLabel != null && action != null)
            {
                var button = new Button
                {
                    Content = actionLabel,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    MinHeight = 28,
                    Padding = new Thickness(12, 4),
                    FontSize = 12,
                    Margin = new Thickness(0, 10, 0, 0)
                };
                button.Classes.Add("primary");
                button.Click += (_, _) =>
                {
                    Dismiss(card);
                    action();
                };
                text.Children.Add(button);
            }

            var close = new Button { Width = 24, Height = 24, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(6, -4, 0, 0) };
            close.Classes.Add("inline");
            ToolTip.SetTip(close, Tr("Fermer"));
            var closeIcon = new PathIcon { Width = 10, Height = 10 };
            closeIcon.Bind(PathIcon.DataProperty, this.GetResourceObservable("IconDismiss"));
            close.Content = closeIcon;
            close.Click += (_, _) => Dismiss(card);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            grid.Children.Add(icon);
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);
            Grid.SetColumn(close, 2);
            grid.Children.Add(close);
            card.Child = grid;

            while (ToastHost.Children.Count >= MaxToasts)
                ToastHost.Children.RemoveAt(0);
            ToastHost.Children.Add(card);
            ToastPopup.IsOpen = true;

            if (timeout > 0)
                DispatcherTimer.RunOnce(() => Dismiss(card), TimeSpan.FromSeconds(timeout));
        }

        void Dismiss(Control card)
        {
            ToastHost.Children.Remove(card);
            if (ToastHost.Children.Count == 0)
                ToastPopup.IsOpen = false;
        }
    }
}
