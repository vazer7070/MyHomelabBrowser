using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.controles;
using System.Windows;
using System.Windows.Controls;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // Onglets de vues internes (paramètres, signalement…).

        void OpenReportIssueView(ReportIssueOptions options)
        {
            // ===============================
            // 🔍 CONTEXTE NAVIGATION ACTUEL
            // ===============================
            WebTabContent? wt = null;

            // 1️⃣ priorité ABSOLUE : onglet sélectionné SI c’est un WebTabContent
            if (Tabs.SelectedItem is TabItem selected &&
                selected.Tag is WebTabContent webTab)
            {
                wt = webTab;
            }
            else
            {
                // 2️⃣ fallback : dernier onglet Web existant
                wt = Tabs.Items
                    .OfType<TabItem>()
                    .Select(t => t.Tag)
                    .OfType<WebTabContent>()
                    .LastOrDefault();
            }

            if (wt != null && !wt.IsCustomView)
            {
                string url =
                    wt.IsLegacyExternal && !string.IsNullOrWhiteSpace(wt.LegacyUrl)
                        ? wt.LegacyUrl
                        : wt.Web?.Source?.AbsoluteUri ?? "inconnu";

                string title =
                    wt.IsLegacyExternal
                        ? "Legacy (Basilisk)"
                        : wt.Web?.CoreWebView2?.DocumentTitle ?? "inconnu";

                string flashMode =
                    wt.IsLegacyExternal ? "legacy" :
                    wt.FlashMode == FlashMode.Ruffle ? "ruffle" :
                    wt.FlashMode == FlashMode.Legacy ? "legacy" :
                    "auto";

                // Un onglet privé ne laisse jamais son adresse partir dans un rapport.
                options.Context = new BrowserContext
                {
                    IsPrivate = wt.IsPrivate,
                    IsLegacy = wt.IsLegacyExternal,
                    CurrentUrl = wt.IsPrivate ? null : url,
                    PageTitle = wt.IsPrivate ? null : title,
                    FlashMode = flashMode
                };
            }
            else
            {
                options.Context = null;
            }

            // ===============================
            // 🔁 ANTI DOUBLON
            // ===============================
            foreach (TabItem t in Tabs.Items)
            {
                if (t.Tag is ViewTabContent v && v.View is ReportIssueView)
                {
                    Tabs.SelectedItem = t;
                    SyncWebHostWithSelection();
                    return;
                }
            }

            // ===============================
            // 🆕 ONGLET REPORT
            // ===============================
            var view = new ReportIssueView(options);

            view.CloseRequested += () =>
            {
                var tabToClose = Tabs.Items
                    .OfType<TabItem>()
                    .FirstOrDefault(t => t.Tag is ViewTabContent vc && vc.View == view);

                if (tabToClose != null)
                    CloseTab(tabToClose);
            };

            // OpenViewTab branche aussi la croix de fermeture, absente auparavant.
            OpenViewTab(view, "Signaler un problème");
        }

        void OpenSettingsSection(string? sectionName)
        {
            // ✅ Si déjà ouvert : sélectionner UNIQUEMENT l'onglet Settings
            foreach (TabItem t in Tabs.Items)
            {
                if (t.Tag is ViewTabContent v && v.View is SettingsView existingSettings)
                {
                    if (!string.IsNullOrWhiteSpace(sectionName))
                        existingSettings.SelectSection(sectionName);

                    Tabs.SelectedItem = t;
                    SyncWebHostWithSelection();
                    return;
                }
            }

            // ✅ Sinon créer Settings
            var view = new SettingsView(_settings);
            if (!string.IsNullOrWhiteSpace(sectionName))
                view.SelectSection(sectionName);

            // ✅ history
            view.OpenHistoryRequested += OpenHistory;
            view.OpenReportIssueRequested += OpenReportIssueView;

            WireUpdateActions(view);

            OpenViewTab(view, "Paramètres");
        }

        /// <summary>
        /// Ouvre une vue interne (paramètres, historique, rapport) dans un onglet.
        /// </summary>
        TabItem OpenViewTab(UserControl view, string title)
        {
            var header = new BrowserTabHeader();
            header.SetTitle(title);
            header.SetIcon(StartTabIcon.Value);

            var tab = new TabItem
            {
                Header = header,
                Tag = new ViewTabContent { View = view }
            };

            header.CloseRequested += () => CloseTab(tab);
            header.ReorderRequested += dir => ReorderTab(tab, dir);

            Tabs.Items.Add(tab);
            Tabs.SelectedItem = tab;
            SyncWebHostWithSelection();
            return tab;
        }

        // ---------------------------
        // Settings button handler
        // ---------------------------
        private void OpenSettings_Click(object sender, RoutedEventArgs e)
            => OpenSettings();
    }
}
