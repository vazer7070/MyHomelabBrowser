namespace PommeFlash.Host
{
    /// <summary>
    /// Fenêtre de l'hôte et boucle de messages du fil du module, propres au système : Win32 sous
    /// Windows, GTK 2 sous Linux (le module Flash de Linux s'en sert, et loge sa fenêtre par XEmbed).
    /// </summary>
    interface IHostDisplay
    {
        /// <summary>Fenêtre que PommeBrowser loge dans l'onglet (HWND, ou XID sous X11).</summary>
        nint Frame { get; }

        /// <summary>Fenêtre du navigateur pour le module (NPNVnetscapeWindow).</summary>
        nint NetscapeWindow { get; }

        /// <summary>Affichage X11 (Display*), 0 sous Windows.</summary>
        nint XDisplay { get; }

        /// <summary>Fenêtre donnée au module (NPWindow.window), ses informations système (ws_info) et sa taille.</summary>
        (nint Window, nint Info, int Width, int Height) PluginArea { get; }

        void Create(HostOptions options);

        /// <summary>Instance affichée (tailles transmises au module) et fermeture demandée par la fenêtre.</summary>
        void Attach(PluginInstance instance, Action closeRequested);

        /// <summary>Boucle de messages, jusqu'à <see cref="Close"/> ; code de sortie.</summary>
        int Run();

        /// <summary>Fermeture de la fenêtre : la boucle de messages se termine.</summary>
        void Close();

        /// <summary>Depuis n'importe quel fil : le fil du module exécutera <see cref="UiThread.RunPending"/>.</summary>
        void Wake();

        /// <summary>Plus tard, sur le fil du module.</summary>
        void Delay(uint milliseconds, Action action);

        /// <summary>Minuterie du module (NPN_ScheduleTimer) : <see cref="PluginInstance.OnTimer"/> à chaque échéance.</summary>
        void StartTimer(uint id, uint interval);

        void StopTimer(uint id);

        /// <summary>Attente sur le fil du module (réponse de PommeBrowser) sans bloquer ce que le système exige.</summary>
        bool Wait(WaitHandle signal, TimeSpan timeout);
    }

    static class HostDisplay
    {
        /// <summary>Affichage de ce système ; null s'il n'est pas pris en charge.</summary>
        public static IHostDisplay? ForThisSystem()
        {
            if (OperatingSystem.IsWindows())
                return new Win32Display();
            if (OperatingSystem.IsLinux())
                return new GtkDisplay();
            return null;
        }
    }
}
