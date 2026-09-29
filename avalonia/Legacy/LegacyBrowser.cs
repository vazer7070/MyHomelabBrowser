using System;

namespace PommeBrowser.Legacy
{
    /// <summary>Basilisk lancé pour un onglet.</summary>
    public interface ILegacyBrowser
    {
        /// <summary>Basilisk s'est fermé (fenêtre fermée ou arrêt demandé), sur le fil de l'interface.</summary>
        event Action? Exited;

        /// <summary>Fermeture douce, puis forcée.</summary>
        void Close();
    }

    /// <summary>Lancement de Basilisk selon le système.</summary>
    public static class LegacyBrowser
    {
        public static ILegacyBrowser Start(string executable, Uri url, bool isPrivate)
        {
            if (OperatingSystem.IsLinux())
                return BasiliskProcess.Start(executable, url, isPrivate);
            if (OperatingSystem.IsWindows())
                return WindowsBasilisk.Start(executable, url, isPrivate);
            throw new PlatformNotSupportedException("Basilisk");
        }

        /// <summary>Arrêt de PommeBrowser : tous les Basilisk lancés par lui se ferment.</summary>
        public static void CloseAll()
        {
            if (OperatingSystem.IsLinux())
                BasiliskProcess.CloseAll();
            else if (OperatingSystem.IsWindows())
                WindowsBasilisk.CloseAll();
        }
    }
}
