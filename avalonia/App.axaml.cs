using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Avalonia.Threading;

namespace PommeBrowser
{
    public sealed partial class App : Application
    {
        public override void Initialize() => AvaloniaXamlLoader.Load(this);

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                // macOS : liens et pages ouverts par le système (navigateur par défaut, Finder) ;
                // ailleurs, ils arrivent par l'instance unique.
                if (this.TryGetFeature<IActivatableLifetime>() is { } activatable)
                    activatable.Activated += OnActivated;
                new BrowserApp(Program.Profiles, Program.Appearance).Start(desktop, Program.StartupUrls);
            }
            base.OnFrameworkInitializationCompleted();
        }

        static void OnActivated(object? sender, ActivatedEventArgs e)
        {
            string[] targets = e switch
            {
                ProtocolActivatedEventArgs { Uri: { } uri } => new[] { uri.AbsoluteUri },
                FileActivatedEventArgs files => files.Files.Select(f => f.TryGetLocalPath()).OfType<string>().Select(p => new Uri(p).AbsoluteUri).ToArray(),
                _ => Array.Empty<string>()
            };
            if (targets.Length > 0)
                Dispatcher.UIThread.Post(() => BrowserApp.Current?.OpenFromOutside(targets));
        }
    }
}
