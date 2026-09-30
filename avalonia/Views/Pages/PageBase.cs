using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace PommeBrowser.Views.Pages
{
    /// <summary>
    /// Page de PommeBrowser affichée dans un onglet (historique, favoris, coffre…) : colonne
    /// centrée, titre, barre d'outils facultative et contenu reconstruit à chaque changement.
    /// </summary>
    public abstract class PageBase : UserControl, IDisposable
    {
        readonly StackPanel _content = new() { Spacing = 10 };
        readonly StackPanel _toolbar = new() { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        bool _refreshQueued;
        bool _disposed;

        protected PageBase(MainWindow window, string title, double width = 760)
        {
            Window = window;
            App = window.App;
            this.Bind(BackgroundProperty, this.GetResourceObservable("WindowBackgroundBrush"));

            var heading = new TextBlock { Text = title, FontSize = 24, FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center };
            var header = new DockPanel { Margin = new Thickness(0, 0, 0, 16) };
            DockPanel.SetDock(_toolbar, Dock.Right);
            header.Children.Add(_toolbar);
            header.Children.Add(heading);

            var column = new StackPanel { MaxWidth = width, Margin = new Thickness(24, 32, 24, 48) };
            column.Children.Add(header);
            column.Children.Add(_content);

            Content = new ScrollViewer { Content = column, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }

        protected MainWindow Window { get; }
        protected BrowserApp App { get; }

        protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
        {
            base.OnAttachedToVisualTree(e);
            Refresh();
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;
            OnDisposed();
        }

        protected virtual void OnDisposed()
        {
        }

        /// <summary>Contenu reconstruit (au plus une fois par passage du fil de l'interface).</summary>
        protected void ScheduleRefresh()
        {
            if (_refreshQueued || _disposed)
                return;
            _refreshQueued = true;
            Dispatcher.UIThread.Post(() =>
            {
                _refreshQueued = false;
                if (!_disposed)
                    Refresh();
            }, DispatcherPriority.Background);
        }

        protected void Refresh()
        {
            _content.Children.Clear();
            Build(_content);
        }

        protected abstract void Build(StackPanel content);

        protected void AddToolbar(Control control) => _toolbar.Children.Add(control);

        // ---------------------------------------------------------------
        // Éléments communs
        // ---------------------------------------------------------------

        protected static TextBlock Heading(string text)
        {
            var block = new TextBlock { Text = text, FontSize = 13, FontWeight = FontWeight.SemiBold, Margin = new Thickness(4, 12, 0, 2) };
            block.Classes.Add("subtitle");
            return block;
        }

        protected static TextBlock Hint(string text)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            block.Classes.Add("hint");
            return block;
        }

        /// <summary>Liste encadrée de lignes.</summary>
        protected Border Card(params Control[] rows)
        {
            var list = new StackPanel();
            for (int i = 0; i < rows.Length; i++)
            {
                if (i > 0)
                {
                    var line = new Border { Height = 1, Margin = new Thickness(14, 0) };
                    line.Bind(Border.BackgroundProperty, this.GetResourceObservable("BorderBrush"));
                    list.Children.Add(line);
                }
                list.Children.Add(rows[i]);
            }
            var card = new Border { Child = list, CornerRadius = new CornerRadius(12), BorderThickness = new Thickness(1), ClipToBounds = true };
            card.Bind(Border.BackgroundProperty, this.GetResourceObservable("PanelBrush"));
            card.Bind(Border.BorderBrushProperty, this.GetResourceObservable("BorderBrush"));
            return card;
        }

        /// <summary>Ligne : icône ou image, titre, sous-titre, boutons à droite ; un clic lance <paramref name="onClick"/>.</summary>
        protected Control Row(string title, string? subtitle, Control? leading = null, Action? onClick = null, params Control[] suffix)
        {
            var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            texts.Children.Add(new TextBlock { Text = title, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(subtitle))
            {
                var sub = new TextBlock { Text = subtitle, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
                sub.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("TextTertiaryBrush"));
                texts.Children.Add(sub);
            }

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(14, 10) };
            if (leading != null)
            {
                leading.Margin = new Thickness(0, 0, 12, 0);
                leading.VerticalAlignment = VerticalAlignment.Center;
                grid.Children.Add(leading);
            }
            Grid.SetColumn(texts, 1);
            grid.Children.Add(texts);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            foreach (Control control in suffix)
                actions.Children.Add(control);
            Grid.SetColumn(actions, 2);
            grid.Children.Add(actions);

            var row = new Border { Child = grid, Background = Brushes.Transparent };
            if (onClick != null)
            {
                row.Cursor = new Cursor(StandardCursorType.Hand);
                row.PointerEntered += (_, _) => row.Bind(Border.BackgroundProperty, row.GetResourceObservable("SurfaceHoverBrush"));
                row.PointerExited += (_, _) => row.Background = Brushes.Transparent;
                row.PointerReleased += (_, e) =>
                {
                    if (e.InitialPressMouseButton == MouseButton.Left && e.Source is not Button && !IsInButton(e.Source as Visual))
                        onClick();
                };
            }
            return row;
        }

        static bool IsInButton(Visual? visual)
        {
            for (Visual? v = visual; v != null; v = v.GetVisualParent())
            {
                if (v is Button)
                    return true;
            }
            return false;
        }

        protected Button IconButton(string icon, string tip, Action action, bool danger = false)
        {
            var glyph = new PathIcon { Width = 14, Height = 14 };
            glyph.Bind(PathIcon.DataProperty, this.GetResourceObservable(icon));
            if (danger)
                glyph.Bind(PathIcon.ForegroundProperty, this.GetResourceObservable("DangerBrush"));
            var button = new Button { Content = glyph, Width = 30, Height = 30 };
            button.Classes.Add("inline");
            ToolTip.SetTip(button, tip);
            button.Click += (_, _) => action();
            return button;
        }

        protected Button TextButton(string text, Action action, bool primary = false, bool danger = false)
        {
            var button = new Button { Content = text };
            if (primary)
                button.Classes.Add("primary");
            if (danger)
                button.Classes.Add("danger");
            button.Click += (_, _) => action();
            return button;
        }

        protected Control Glyph(string icon, double size = 18, string brush = "TextSecondaryBrush")
        {
            var glyph = new PathIcon { Width = size, Height = size };
            glyph.Bind(PathIcon.DataProperty, this.GetResourceObservable(icon));
            glyph.Bind(PathIcon.ForegroundProperty, this.GetResourceObservable(brush));
            return glyph;
        }

        /// <summary>Icône du site (cache), sinon un globe.</summary>
        protected Control SiteIcon(string url)
            => FaviconStore.TryGet(url) is { } favicon ? new Image { Source = favicon, Width = 16, Height = 16 } : Glyph("IconGlobe", 16, "TextTertiaryBrush");

        protected Control EmptyState(string icon, string title, string text)
        {
            var box = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 48, 0, 0), Spacing = 8 };
            var glyph = Glyph(icon, 48, "TextTertiaryBrush");
            glyph.HorizontalAlignment = HorizontalAlignment.Center;
            box.Children.Add(glyph);
            box.Children.Add(new TextBlock { Text = title, FontSize = 17, FontWeight = FontWeight.SemiBold, HorizontalAlignment = HorizontalAlignment.Center });
            var body = Hint(text);
            body.TextAlignment = TextAlignment.Center;
            body.MaxWidth = 420;
            box.Children.Add(body);
            return box;
        }

        protected TextBox SearchBox(string placeholder, Action<string> changed)
        {
            var box = new TextBox { PlaceholderText = placeholder, Width = 280 };
            box.TextChanged += (_, _) => changed(box.Text ?? string.Empty);
            return box;
        }
    }
}
