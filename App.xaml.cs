using System.Windows;
using Velopack;

namespace MyHomelabBrowser
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            VelopackApp.Build().Run();
            base.OnStartup(e);
        }
    }
}