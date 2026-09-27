using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Session;
using MyHomelabBrowser.controles;
using System.Windows.Controls;
using static MyHomelabBrowser.classes.BrowserSettings;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        private BrowserSessionState BuildSessionState(bool isUpdateRestart)
        {
            var state = new BrowserSessionState { IsUpdateRestart = isUpdateRestart };
            int selectedIndex = 0;

            foreach (var item in Tabs.Items)
            {
                if (item is not TabItem tab)
                    continue;

                // Seuls les onglets web normaux sont enregistrés : jamais les onglets privés,
                // qui ne doivent laisser aucune trace sur le disque.
                if (tab.Tag is not WebTabContent wt || wt.IsPrivate || wt.IsCustomView)
                    continue;

                string url = "";

                if (wt.IsLegacyExternal && !string.IsNullOrWhiteSpace(wt.LegacyUrl))
                    url = wt.LegacyUrl;
                else if (!string.IsNullOrWhiteSpace(wt.PendingUrl))
                    url = wt.PendingUrl;
                else if (wt.Web?.Source != null)
                    url = wt.Web.Source.AbsoluteUri;

                if (string.IsNullOrWhiteSpace(url) ||
                    url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (ReferenceEquals(Tabs.SelectedItem, tab))
                    selectedIndex = state.Tabs.Count;

                state.Tabs.Add(new TabState
                {
                    Url = url,
                    Title = (tab.Header as BrowserTabHeader)?.TabTitle,
                    IsPinned = wt.IsPinned,
                    IsLegacy = wt.IsLegacyExternal,
                    LegacyUrl = wt.LegacyUrl
                });
            }

            state.SelectedIndex = selectedIndex;
            return state;
        }

        private bool _restartPending;

        /// <summary>
        /// Redémarre le navigateur en rouvrant les onglets (changement de thème ou de langue).
        /// </summary>
        public void RestartApplication()
        {
            string? executable = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(executable))
                return;

            _restartPending = true;
            SaveSessionForUpdateRestart();
            FlushPersistentState();

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(executable) { UseShellExecute = false });
            }
            catch (Exception ex)
            {
                _restartPending = false;
                ShowToast(Tr("Redémarrage impossible"), ex.Message, ToastKind.Warning);
                return;
            }

            System.Windows.Application.Current.Shutdown();
        }

        private void SaveSessionForUpdateRestart()
        {
            try
            {
                SessionPersistence.Save(BuildSessionState(isUpdateRestart: true));
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[Session] " + ex.Message);
            }
        }

        /// <summary>
        /// Enregistre la session à la fermeture si l'utilisateur a choisi de la restaurer
        /// (auparavant elle n'était écrite qu'avant une mise à jour : l'option
        /// « Restaurer la session » ouvrait donc une fenêtre vide).
        /// </summary>
        private void SaveSessionOnExit()
        {
            try
            {
                if (_settings.Settings.Startup == StartupMode.RestoreSession)
                    SessionPersistence.Save(BuildSessionState(isUpdateRestart: false));
                else
                    SessionPersistence.Clear();
            }
            catch (Exception ex)
            {
                RuntimeLogBuffer.Append("[Session] " + ex.Message);
            }
        }

        /// <summary>
        /// Restaure les onglets enregistrés. Seul l'onglet sélectionné charge sa page
        /// immédiatement ; les autres attendent d'être affichés.
        /// </summary>
        private async Task<bool> RestoreSessionIfAnyAsync()
        {
            var s = SessionPersistence.Load();

            // Évite une boucle de plantage si la restauration échoue.
            SessionPersistence.Clear();

            if (s == null || s.Tabs.Count == 0)
                return false;

            if (!s.IsUpdateRestart && _settings.Settings.Startup != StartupMode.RestoreSession)
                return false;

            int selectedIndex = Math.Clamp(s.SelectedIndex, 0, s.Tabs.Count - 1);

            for (int i = 0; i < s.Tabs.Count; i++)
            {
                TabState t = s.Tabs[i];

                // Anciennes sessions : les onglets privés ne sont plus rouverts.
                if (t.IsPrivate || string.IsNullOrWhiteSpace(t.Url))
                    continue;

                bool isSelected = i == selectedIndex;

                WebTabContent? content = await CreateWebTabAsync(
                    t.Url,
                    isPrivate: false,
                    select: isSelected,
                    pendingTitle: t.Title,
                    deferNavigation: !isSelected && !t.IsLegacy);

                if (content == null || content.IsClosed)
                    continue;

                TabItem? tab = FindTabFor(content);
                if (tab?.Header is not BrowserTabHeader header)
                    continue;

                if (t.IsPinned)
                {
                    content.IsPinned = true;
                    ApplyPinState(tab, header, true);
                }

                if (t.IsLegacy && Uri.TryCreate(t.LegacyUrl ?? t.Url, UriKind.Absolute, out var legacyUri))
                    _ = LaunchLegacyIntoInternalTabAsync(content, legacyUri, header);
            }

            if (Tabs.Items.Count == 0)
                return false;

            if (Tabs.SelectedItem == null)
                Tabs.SelectedIndex = 0;

            SyncWebHostWithSelection();
            return true;
        }

        TabItem? FindTabFor(WebTabContent content)
        {
            foreach (var item in Tabs.Items)
            {
                if (item is TabItem tab && ReferenceEquals(tab.Tag, content))
                    return tab;
            }

            return null;
        }

        void OpenInitialTab()
        {
            switch (_settings.Settings.Startup)
            {
                case StartupMode.CustomPage:
                    if (!string.IsNullOrWhiteSpace(_settings.Settings.StartPage))
                        _ = CreateTabInternal(UrlResolver.ResolveOrSearch(_settings.Settings.StartPage, _settings.Settings.Search));
                    else
                        CreateEmptyStartTab();
                    break;

                default:
                    // EmptyTab, ou RestoreSession sans session enregistrée.
                    CreateEmptyStartTab();
                    break;
            }
        }
    }
}
