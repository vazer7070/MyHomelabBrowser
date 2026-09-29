using System;
using MyHomelabBrowser.classes.Profiles;
using PommeBrowser.Linux.Core;

namespace PommeBrowser.Core
{
    /// <summary>
    /// Données d'un profil rangées hors de son dossier de réglages (que ProfileService déplace
    /// lui-même) : données du moteur et cache. Le moteur les garde ouvertes jusqu'à la fin du
    /// processus : déplacement ou effacement au démarrage suivant.
    /// </summary>
    public static class ProfileStorage
    {
        public static void ScheduleRename(string oldName, string newName)
        {
            if (OperatingSystem.IsLinux())
                ProfileData.ScheduleMove(oldName, newName);
            else if (OperatingSystem.IsWindows())
                WebViewProfileData.MoveOrSchedule(oldName, newName, folderInUse: true);
        }

        /// <summary>Sous Linux, les dossiers sans profil sont effacés au démarrage (ProfileData.RemoveOrphans).</summary>
        public static void ScheduleDelete(string name)
        {
            if (OperatingSystem.IsWindows())
                WebViewProfileData.DeleteOrSchedule(name, folderInUse: true);
        }
    }
}
