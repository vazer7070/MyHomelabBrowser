using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Profiles;
using System.Windows;
using Velopack;

namespace MyHomelabBrowser
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            VelopackApp.Build().Run();

            // Avant toute fenêtre : les styles sont construits avec la palette choisie.
            ThemeManager.Apply(this, AppearanceSettings.Load());
            DarkTitleBar.RegisterForAllWindows();

            // Renommages et suppressions de profils restés en attente (dossier verrouillé).
            WebViewProfileData.ApplyPendingOperations();
            base.OnStartup(e);
        }
    }
}