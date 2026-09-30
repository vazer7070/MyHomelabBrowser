using System;
using Avalonia.Input;
using Avalonia.Interactivity;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>Recherche dans la page (Ctrl+F) : Entrée suivant, Maj+Entrée précédent, Échap ferme.</summary>
    public sealed partial class MainWindow
    {
        void InitializeFind()
        {
            FindBox.TextChanged += (_, _) => RunFind();
            FindCaseToggle.IsCheckedChanged += (_, _) => RunFind();
            FindBox.AddHandler(KeyDownEvent, (_, e) =>
            {
                switch (e.Key)
                {
                    case Key.Enter:
                        _selected?.Engine?.FindNext(backward: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                        e.Handled = true;
                        break;
                    case Key.Escape:
                        CloseFind();
                        _selected?.FocusPage();
                        e.Handled = true;
                        break;
                }
            }, RoutingStrategies.Tunnel);
        }

        public void OpenFind()
        {
            if (_selected is not { Page: TabPage.Web, Engine: not null })
                return;
            FindPopup.IsOpen = true;
            FindBox.Focus();
            FindBox.SelectAll();
            if (!string.IsNullOrEmpty(FindBox.Text))
                RunFind();
        }

        void RunFind()
        {
            if (!FindPopup.IsOpen || _selected?.Engine is not { } engine)
                return;
            string text = FindBox.Text ?? string.Empty;
            FindCountText.Text = string.Empty;
            engine.Find(text, FindCaseToggle.IsChecked == true);
        }

        public void CloseFind()
        {
            if (!FindPopup.IsOpen)
                return;
            FindPopup.IsOpen = false;
            _selected?.Engine?.StopFind();
        }

        public void OnFindResult(BrowserTab tab, int count)
        {
            if (tab != _selected || !FindPopup.IsOpen)
                return;
            FindCountText.Text = string.IsNullOrEmpty(FindBox.Text)
                ? string.Empty
                : count == 0 ? Tr("Aucun résultat") : count == 1 ? Tr("1 résultat") : Tr("{0} résultats", count >= 1000 ? "1000+" : count.ToString(Culture));
        }

        void FindNext_Click(object? sender, RoutedEventArgs e) => _selected?.Engine?.FindNext(backward: false);

        void FindPrevious_Click(object? sender, RoutedEventArgs e) => _selected?.Engine?.FindNext(backward: true);

        void FindClose_Click(object? sender, RoutedEventArgs e) => CloseFind();
    }
}
