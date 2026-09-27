using MyHomelabBrowser.controles;
using System.Windows.Controls;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // Détacher un onglet dans sa propre fenêtre.

        // ---------------------------
        // Détacher / réancrer
        // ---------------------------
        void DetachTab(TabItem tab)
        {
            if (tab.Tag is not WebTabContent state)
                return;

            bool wasSelected = Equals(Tabs.SelectedItem, tab);
            if (wasSelected)
                SelectFallbackTab(tab);

            // Page d'accueil : une nouvelle page est ouverte dans une fenêtre séparée.
            if (state.IsCustomView)
            {
                Tabs.Items.Remove(tab);
                OpenDetachedCustomTab(state);
                EnsureAtLeastOneTab();
                return;
            }

            if (state.Web == null)
                return;

            Tabs.Items.Remove(tab);

            // Fenêtre détachée créée avant de toucher au WebView.
            var win = new DetachedWindow(this, state)
            {
                Owner = this,
                RequestRedock = RedockWebTab
            };

            if (ReferenceEquals(WebHost.Content, state.HostGrid))
                WebHost.Content = null;

            win.Show();
            EnsureAtLeastOneTab();
            SyncWebHostWithSelection();
        }

        void EnsureAtLeastOneTab()
        {
            if (Tabs.Items.Count == 0)
                CreateEmptyStartTab();
        }

        void OpenDetachedCustomTab(WebTabContent content)
        {
            var win = new DetachedCustomWindow();
            win.SetContent(CreateStartPageView(null));

            // Au réancrage, un onglet d'accueil complet (fermable, déplaçable) est recréé.
            win.RequestRedock += _ => CreateEmptyStartTab();
            win.Show();
        }
    }
}
