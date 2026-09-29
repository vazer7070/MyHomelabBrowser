using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;

namespace PommeBrowser
{
    public sealed partial class App : Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                new BrowserApp(Program.Profiles, Program.Appearance).Start(desktop, Program.StartupUrls);
            base.OnFrameworkInitializationCompleted();
        }
    }
}
