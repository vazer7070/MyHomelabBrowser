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
            DarkTitleBar.RegisterForAllWindows();

            // Renommages et suppressions de profils restés en attente (dossier verrouillé).
            WebViewProfileData.ApplyPendingOperations();
            base.OnStartup(e);
        }
    }
}