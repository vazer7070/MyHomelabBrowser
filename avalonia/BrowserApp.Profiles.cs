using System;
using System.Linq;
using MyHomelabBrowser.classes;
using PommeBrowser.Core;
using PommeBrowser.Legacy;

namespace PommeBrowser
{
    /// <summary>Changement de profil et relance.</summary>
    public sealed partial class BrowserApp
    {
        bool _quitting;

        public bool IsQuitting => _quitting;

        /// <summary>
        /// Change de profil (connexion, création, renommage, suppression) puis relance PommeBrowser.
        /// La session du profil quitté est enregistrée avant le changement. Si <paramref name="change"/>
        /// échoue, l'exception remonte à l'appelant et rien n'est relancé ; sinon la relance a lieu
        /// juste après, une fois la boîte de dialogue refermée.
        /// </summary>
        public void ChangeProfile(Action change)
        {
            SaveSession();
            History.Flush();
            change();
            Post(() => Restart(saveSession: false));
        }

        /// <summary>Relance PommeBrowser (nouveau profil, langue, mise à jour installée).</summary>
        public void Restart(bool saveSession = true, bool isUpdateRestart = false)
        {
            if (saveSession)
                SaveSession(isUpdateRestart);
            try
            {
                Relauncher.Schedule();
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException)
            {
                // PommeBrowser se ferme quand même : le prochain lancement ouvrira le bon profil.
                RuntimeLogBuffer.Append("[Relance] " + ex.Message);
            }
            Shutdown();
        }

        /// <summary>Ferme toutes les fenêtres sans réenregistrer la session.</summary>
        void Shutdown()
        {
            _quitting = true;
            LegacyBrowser.CloseAll();
            _lifetime.Shutdown();
        }
    }
}
