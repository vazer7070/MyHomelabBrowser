using System;
using System.Collections.Generic;

namespace PommeBrowser.Legacy
{
    /// <summary>Basilisk lancé pour un onglet.</summary>
    public interface ILegacyBrowser
    {
        /// <summary>Basilisk s'est fermé (fenêtre fermée ou arrêt demandé), sur le fil de l'interface.</summary>
        event Action? Exited;

        /// <summary>Fermeture douce, puis forcée.</summary>
        void Close();

        bool HasExited { get; }

        /// <summary>Processus lancés (Linux : la fenêtre est cherchée parmi eux et leurs enfants).</summary>
        IEnumerable<int> ProcessIds { get; }

        /// <summary>Fenêtre principale (Windows : HWND), 0 tant qu'elle n'est pas affichée.</summary>
        nint FindWindow();

        /// <summary>Onglet en arrière-plan : priorité plus basse.</summary>
        void SetBackground(bool background);
    }

    /// <summary>Lancement de Basilisk selon le système.</summary>
    public static class LegacyBrowser
    {
        /// <summary><paramref name="embedded"/> : Basilisk sera logé dans l'onglet (barres d'outils masquées).</summary>
        public static ILegacyBrowser Start(string executable, Uri url, bool isPrivate, bool embedded)
        {
            if (OperatingSystem.IsLinux())
                return BasiliskProcess.Start(executable, url, isPrivate, embedded);
            if (OperatingSystem.IsWindows())
                return WindowsBasilisk.Start(executable, url, isPrivate, embedded);
            throw new PlatformNotSupportedException("Basilisk");
        }

        /// <summary>Arrêt de PommeBrowser : tous les Basilisk et moteurs Flash intégrés lancés par lui se ferment.</summary>
        public static void CloseAll()
        {
            if (OperatingSystem.IsLinux())
            {
                BasiliskProcess.CloseAll();
            }
            else if (OperatingSystem.IsWindows())
            {
                WindowsBasilisk.CloseAll();
                FlashHostProcess.CloseAll();
            }
        }
    }
}
