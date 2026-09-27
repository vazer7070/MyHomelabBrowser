using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Localization;
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

            AppearanceSettings appearance = AppearanceSettings.Load();
            InitializeLanguage(appearance.Language);

            // Avant toute fenêtre : les styles sont construits avec la palette choisie.
            ThemeManager.Apply(this, appearance);
            DarkTitleBar.RegisterForAllWindows();

            // Renommages et suppressions de profils restés en attente (dossier verrouillé).
            WebViewProfileData.ApplyPendingOperations();
            base.OnStartup(e);
        }

        static void InitializeLanguage(string language)
        {
            if (language != "en")
                return;

            // Le français est la langue source : seule la traduction anglaise est embarquée.
            using var stream = typeof(App).Assembly.GetManifestResourceStream("lang.en.json");
            Loc.Initialize("en", stream != null ? Loc.LoadTable(stream) : null);
            UiTranslator.Register();
        }
    }
}