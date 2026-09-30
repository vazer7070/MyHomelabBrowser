using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.AdBlock.Services;
using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.classes.Security;
using PommeBrowser.Core;
using PommeBrowser.Engine;
using PommeBrowser.Legacy;
using PommeBrowser.Views;

namespace PommeBrowser
{
    /// <summary>Position, taille et état d'une fenêtre, repris par la fenêtre du profil suivant.</summary>
    public sealed record WindowPlacement(PixelPoint Position, double Width, double Height, WindowState State)
    {
        public static WindowPlacement Of(Window window)
            => new(window.Position, window.Width, window.Height, window.WindowState == WindowState.Minimized ? WindowState.Normal : window.WindowState);

        public void ApplyTo(Window window)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = Position;
            window.Width = Width;
            window.Height = Height;
            window.WindowState = State;
        }
    }

    /// <summary>Changement de profil, sans relance quand le moteur le permet, et relance.</summary>
    public sealed partial class BrowserApp
    {
        bool _quitting;

        public bool IsQuitting => _quitting;

        /// <summary>
        /// Profil ouvert sans relancer PommeBrowser : Windows et macOS, où chaque nouvelle page
        /// ouvre les données du profil (dossier WebView2, magasin de données WebKit). Sous Linux,
        /// WebKitGTK fixe ses dossiers de données au démarrage du programme : PommeBrowser se relance,
        /// sinon cookies et données des sites passeraient d'un profil à l'autre.
        /// </summary>
        public static bool SwitchesProfileInPlace => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

        /// <summary>
        /// Change de profil (connexion, création, déconnexion, renommage, suppression). La session du
        /// profil quitté est enregistrée avant le changement. Si <paramref name="change"/> échoue,
        /// l'exception remonte à l'appelant et rien ne change ; sinon le nouveau profil s'ouvre juste
        /// après, une fois la boîte de dialogue refermée : sans relance si possible, sinon (ou avec
        /// <paramref name="relaunch"/>, quand des dossiers ne se déplacent qu'au démarrage) après relance.
        /// </summary>
        public void ChangeProfile(Action change, bool relaunch = false)
        {
            SaveSession();
            History.Flush();
            change();
            if (relaunch || !SwitchesProfileInPlace)
                Post(() => Restart(saveSession: false));
            else
                Post(() => _ = SwitchProfileInPlaceAsync());
        }

        /// <summary>
        /// Les fenêtres du profil quitté se ferment, ce qui tourne pour lui s'arrête, puis une
        /// nouvelle application (réglages, coffre, favoris, historique…) ouvre le profil courant et
        /// ses onglets, à la place de la fenêtre active.
        /// </summary>
        async Task SwitchProfileInPlaceAsync()
        {
            WindowPlacement? placement = ActiveWindow is { } active ? WindowPlacement.Of(active) : null;
            _quitting = true;
            // Aucune fenêtre pendant l'échange : l'application ne doit pas s'arrêter pour autant.
            _lifetime.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            foreach (MainWindow window in _windows.ToList())
                window.Close();
            ReleaseProfile();

            try
            {
                AppPaths.UseProfile(Profiles.Current?.Username);
                SiteSecurityStore.Current.Reload();
                FlashDomainRules.ReloadForCurrentProfile();
                FlashCompatibilityMemory.ReloadForCurrentProfile();
                FaviconStore.ForgetMemory();
                EngineHost.ForgetSession();
                // Module anti-pub commun à l'application : réglages et règles du nouveau profil.
                await AdBlockModuleHost.Current.ReloadForCurrentProfileAsync();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                ErrorLog.Write("Changement de profil", ex);
            }

            try
            {
                new BrowserApp(Profiles, Appearance).Start(_lifetime, Array.Empty<string>(), placement);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Le profil ne s'ouvre pas sur place : relance, qui l'ouvrira au démarrage.
                ErrorLog.Write("Changement de profil", ex);
                Restart(saveSession: false);
            }
        }

        /// <summary>Profil quitté sans relance : minuteries, fichiers ouverts et Basilisk s'arrêtent.</summary>
        void ReleaseProfile()
        {
            Dispatcher.UIThread.UnhandledException -= OnUnhandledException;
            _lifetime.ShutdownRequested -= OnShutdownRequested;
            _monitor?.Stop();
            AdBlock.Detach();
            Vault.Dispose();
            History.Dispose();
            LegacyBrowser.CloseAll();
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
