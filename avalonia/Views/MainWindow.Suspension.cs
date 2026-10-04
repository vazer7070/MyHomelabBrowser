using System;
using System.Linq;
using Avalonia.Threading;
using PommeBrowser.Views.Dialogs;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>
    /// Mise en veille des onglets inactifs (comme l'édition Windows) : la vue native est fermée,
    /// l'adresse est gardée, la page est rechargée quand on revient sur l'onglet.
    /// </summary>
    public sealed partial class MainWindow
    {
        DispatcherTimer? _suspendTimer;

        void InitializeSuspension()
        {
            _suspendTimer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => SuspendInactiveTabs(force: false));
            _suspendTimer.Start();
            Closed += (_, _) => _suspendTimer.Stop();
        }

        /// <summary>
        /// Met en veille les onglets quittés depuis longtemps (ou tous les onglets inactifs si
        /// <paramref name="force"/>). Jamais un onglet dont le Flash joue (moteur intégré dans la
        /// page) : la veille fermerait le jeu.
        /// </summary>
        void SuspendInactiveTabs(bool force)
        {
            if (!force && !App.Settings.EnableSuspension)
                return;
            TimeSpan delay = TimeSpan.FromMinutes(Math.Max(1, App.Settings.SuspendDelayMinutes));
            foreach (BrowserTab tab in _tabs.ToList())
            {
                if (tab == _selected || tab == _splitPartner || tab.Engine == null || tab.IsPrivate || tab.HasFlashOverlay)
                    continue;
                if (force || DateTime.Now - tab.LastActivated > delay)
                    tab.Suspend();
            }
        }

        void SuspendTab(BrowserTab tab)
        {
            if (tab.IsPrivate)
            {
                // Une page privée ne peut pas être rechargée à l'identique (session en mémoire).
                ShowToast(Tr("Les onglets privés ne sont pas mis en veille."));
                return;
            }
            if (tab == _selected)
            {
                BrowserTab? other = _tabs.FirstOrDefault(t => t != tab);
                if (other == null)
                    return;
                SelectTab(other);
            }
            tab.Suspend();
        }

        /// <summary>Ajoute la page affichée aux services du homelab.</summary>
        void AddCurrentPageAsService()
        {
            if (_selected is not { Page: TabPage.Web } tab || !Uri.TryCreate(tab.WebUrl, UriKind.Absolute, out Uri? uri))
                return;
            string root = uri.GetLeftPart(UriPartial.Authority) + "/";
            _ = ServiceDialog.EditAsync(this, null, root, tab.Title);
        }
    }
}
