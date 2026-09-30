using System;
using System.Linq;
using Avalonia.Controls;

namespace PommeBrowser.Views
{
    /// <summary>Déplacement d'un onglet vers une nouvelle fenêtre, sans recharger sa page.</summary>
    public sealed partial class MainWindow
    {
        public void MoveToNewWindow(BrowserTab tab)
        {
            if (_tabs.Count < 2 || !_tabs.Contains(tab))
                return;

            MainWindow target = App.OpenWindow();
            target.Position = Position + new Avalonia.PixelPoint(40, 40);
            target.Show();

            // La vue native change de fenêtre parente sans être détruite.
            using (tab.BeginMove())
            {
                DetachTab(tab);
                target.AdoptTab(tab);
            }
        }

        /// <summary>Retire l'onglet de cette fenêtre sans le fermer.</summary>
        void DetachTab(BrowserTab tab)
        {
            int index = _tabs.IndexOf(tab);
            if (tab == _selected)
            {
                BrowserTab? next = index + 1 < _tabs.Count ? _tabs[index + 1] : index > 0 ? _tabs[index - 1] : null;
                if (next != null)
                    SelectTab(next);
            }
            if (tab == _splitPartner)
                _splitPartner = null;
            _tabs.Remove(tab);
            tab.Changed -= OnTabChanged;
            WebHost.Children.Remove(tab.Content);
            tab.IsSelected = false;
            foreach (BrowserTab other in _tabs.Where(t => t.Opener == tab))
                other.Opener = null;
            tab.Opener = null;
            LayoutContents();
            UpdateChrome();
        }

        /// <summary>Accueille un onglet venu d'une autre fenêtre.</summary>
        void AdoptTab(BrowserTab tab)
        {
            tab.Window = this;
            AddTab(tab, _tabs.Count);
            SelectTab(tab);
        }
    }
}
