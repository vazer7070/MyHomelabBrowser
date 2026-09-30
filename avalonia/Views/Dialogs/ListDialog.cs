using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace PommeBrowser.Views.Dialogs
{
    /// <summary>Boîte présentant une liste modifiable (autorisations, certificats…), reconstruite après chaque action.</summary>
    public sealed class ListDialog : FormDialog
    {
        readonly StackPanel _list = new() { Spacing = 2 };
        readonly Func<ListDialog, IEnumerable<Control>> _rows;
        readonly TextBlock _empty;

        public ListDialog(string title, string description, string emptyText, Func<ListDialog, IEnumerable<Control>> rows)
            : base(title, string.Empty, width: 620, cancelLabel: MyHomelabBrowser.classes.Localization.Loc.Tr("Fermer"))
        {
            _rows = rows;
            AddText(description);
            _empty = new TextBlock { Text = emptyText, Margin = new Thickness(0, 8) };
            _empty.Classes.Add("hint");
            Add(new ScrollViewer { Content = _list, MaxHeight = 420 });
            Add(_empty);
            Refresh();
        }

        public void Refresh()
        {
            _list.Children.Clear();
            foreach (Control row in _rows(this))
                _list.Children.Add(row);
            _empty.IsVisible = _list.Children.Count == 0;
        }

        /// <summary>Ligne : titre, détail, bouton d'action.</summary>
        public Control Row(string title, string? detail, string actionLabel, Action action)
        {
            var remove = new Button { Content = actionLabel, VerticalAlignment = VerticalAlignment.Center };
            remove.Classes.Add("link");
            remove.Click += (_, _) =>
            {
                action();
                Refresh();
            };
            var texts = new StackPanel();
            texts.Children.Add(new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis });
            if (!string.IsNullOrEmpty(detail))
            {
                var sub = new TextBlock { Text = detail, TextWrapping = TextWrapping.Wrap };
                sub.Classes.Add("hint");
                texts.Children.Add(sub);
            }
            var row = new DockPanel { Margin = new Thickness(0, 4) };
            DockPanel.SetDock(remove, Dock.Right);
            row.Children.Add(remove);
            row.Children.Add(texts);
            return row;
        }
    }
}
