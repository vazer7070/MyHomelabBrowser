using System;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Input;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>
    /// Vue côte à côte : l'onglet actif occupe un volet, _splitPartner l'autre. Les zones des
    /// onglets restent dans WebHost (pas de déplacement des vues natives) : seules leurs colonnes changent.
    /// </summary>
    public sealed partial class MainWindow
    {
        BrowserTab? _splitPartner;
        bool _splitActiveIsLeft = true;
        bool _splitInitialized;

        bool IsSplitViewActive => _splitPartner != null && _tabs.Contains(_splitPartner) && _splitPartner != _selected;

        /// <summary>Affiche <paramref name="partner"/> à côté de l'onglet actif.</summary>
        public void ShowSideBySide(BrowserTab partner)
        {
            if (_selected == null || partner == _selected || !_tabs.Contains(partner))
                return;
            _splitPartner = partner;
            _splitActiveIsLeft = true;
            partner.LoadPendingIfNeeded();
            LayoutContents();
        }

        public void ExitSplitView()
        {
            if (_splitPartner == null)
                return;
            _splitPartner = null;
            LayoutContents();
        }

        /// <summary>Menu « Vue côte à côte » : l'onglet utilisé le plus récemment, ou un nouvel accueil.</summary>
        public void ToggleSplitView()
        {
            if (IsSplitViewActive)
            {
                ExitSplitView();
                return;
            }
            if (_selected == null)
                return;

            BrowserTab? partner = _tabs.Where(t => t != _selected).OrderByDescending(t => t.LastActivated).FirstOrDefault();
            if (partner == null)
            {
                BrowserTab current = _selected;
                partner = NewTab(null, select: false);
                if (_selected != current)
                    SelectTab(current);
            }
            ShowSideBySide(partner);
        }

        /// <summary>Zones visibles : l'onglet actif, et son voisin en vue côte à côte.</summary>
        void LayoutContents()
        {
            InitializeSplit();
            bool split = IsSplitViewActive;
            WebHost.ColumnDefinitions[1].Width = new GridLength(split ? 6 : 0);
            WebHost.ColumnDefinitions[2].Width = split ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            if (!split)
                WebHost.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
            SplitSplitter.IsVisible = split;
            SplitHeaderLeft.IsVisible = split;
            SplitHeaderRight.IsVisible = split;

            foreach (BrowserTab tab in _tabs)
            {
                bool active = tab == _selected;
                bool partner = split && tab == _splitPartner;
                tab.Content.IsVisible = active || partner;
                Grid.SetColumn(tab.Content, !split ? 0 : active == _splitActiveIsLeft ? 0 : 2);
            }
            if (split)
                UpdateSplitHeaders();
        }

        void UpdateSplitHeaders()
        {
            BrowserTab? left = _splitActiveIsLeft ? _selected : _splitPartner;
            BrowserTab? right = _splitActiveIsLeft ? _splitPartner : _selected;
            SplitTitleLeft.Text = left?.Title;
            SplitTitleRight.Text = right?.Title;
            SplitHeaderLeft.Classes.Set("active", _splitActiveIsLeft);
            SplitHeaderRight.Classes.Set("active", !_splitActiveIsLeft);
        }

        void InitializeSplit()
        {
            if (_splitInitialized)
                return;
            _splitInitialized = true;
            ToolTip.SetTip(SplitHeaderLeft, Tr("Cliquer pour activer ce volet"));
            ToolTip.SetTip(SplitHeaderRight, Tr("Cliquer pour activer ce volet"));
            // Un clic sur le titre d'un volet en fait l'onglet actif.
            SplitHeaderLeft.PointerPressed += (_, _) =>
            {
                if (IsSplitViewActive && !_splitActiveIsLeft && _splitPartner != null)
                    SelectTab(_splitPartner);
            };
            SplitHeaderRight.PointerPressed += (_, _) =>
            {
                if (IsSplitViewActive && _splitActiveIsLeft && _splitPartner != null)
                    SelectTab(_splitPartner);
            };
        }
    }
}
