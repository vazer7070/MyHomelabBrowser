using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Profiles;
using MyHomelabBrowser.classes.Profiles.Credentials;
using PommeBrowser;
using PommeBrowser.Core;
using PommeBrowser.Views;

[assembly: Avalonia.Headless.AvaloniaTestApplication(typeof(PommeBrowser.UiTests.TestBrowser))]

namespace PommeBrowser.UiTests;

/// <summary>
/// Navigateur de test : l'application de PommeBrowser (thèmes, ressources) sans écran, avec ses
/// données dans un dossier temporaire. Une seule instance pour tous les tests, comme au lancement.
/// </summary>
public static class TestBrowser
{
    static readonly string Root = Path.Combine(Path.GetTempPath(), "pomme-ui-" + Guid.NewGuid().ToString("N"));
    static BrowserApp? _app;

    public static AppBuilder BuildAvaloniaApp()
    {
        // Avant toute lecture des chemins : rien n'est écrit dans les données de l'utilisateur.
        Environment.SetEnvironmentVariable("HOME", Root);
        Environment.SetEnvironmentVariable("XDG_CONFIG_HOME", Path.Combine(Root, "config"));
        Environment.SetEnvironmentVariable("XDG_DATA_HOME", Path.Combine(Root, "data"));
        Environment.SetEnvironmentVariable("XDG_CACHE_HOME", Path.Combine(Root, "cache"));
        Directory.CreateDirectory(Root);
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
            }
        };
        AppPaths.Initialize();
        AppPaths.UseProfile(null);

        return AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
    }

    public static BrowserApp App => _app ??= new BrowserApp(new ProfileService(AppDataContext.GlobalRoot), new AppearanceSettings());

    /// <summary>Nouvelle fenêtre affichée, sur la page d'accueil.</summary>
    public static MainWindow OpenWindow()
    {
        MainWindow window = App.OpenWindow();
        window.NewTab(null, select: true);
        window.Show();
        Pump();
        return window;
    }

    public const string VaultPassword = "mot-de-passe-de-test";

    /// <summary>Coffre déverrouillé (créé au premier appel), ne contenant que ces identifiants.</summary>
    public static void UnlockVaultWith(params (string Site, string User)[] entries)
    {
        CredentialVaultService service = App.Vault.Service;
        if (!service.VaultExists)
            Assert.True(service.TryInitializeNewVault(VaultPassword));
        else if (!service.IsUnlocked)
            Assert.True(service.TryUnlock(VaultPassword));

        foreach (CredentialEntry entry in service.GetAll().ToList())
            service.Delete(entry.Host, entry.Username);
        foreach ((string site, string user) in entries)
            service.Upsert(site, user, "secret-" + user, formAction: null);
        App.Vault.Touch();
        App.Vault.NotifyChanged();
    }

    /// <summary>Contrôles affichés sous <paramref name="root"/>.</summary>
    public static IEnumerable<T> Find<T>(Visual root) where T : Visual
        => root.GetVisualDescendants().OfType<T>().Where(v => v.IsEffectivelyVisible);

    /// <summary>Point au centre du contrôle, dans sa fenêtre (ou sa liste déroulante).</summary>
    public static (TopLevel Root, Point Point) CenterOf(Control control)
    {
        TopLevel root = TopLevel.GetTopLevel(control) ?? throw new InvalidOperationException("Contrôle non affiché.");
        Point point = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), root)
                      ?? throw new InvalidOperationException("Position introuvable.");
        return (root, point);
    }

    /// <summary>Clic gauche au centre du contrôle.</summary>
    public static void Click(Control control)
    {
        (TopLevel root, Point point) = CenterOf(control);
        root.MouseDown(point, MouseButton.Left);
        Pump();
        root.MouseUp(point, MouseButton.Left);
        Pump();
    }

    /// <summary>
    /// Exécute ce qui attend sur le fil de l'interface (mises en page, messages différés…) jusqu'à
    /// ce qu'elle soit au repos. Une tâche qui se relance sans fin (page reconstruite en boucle)
    /// l'empêcherait d'y revenir : le test échoue alors au bout de quelques secondes au lieu de
    /// bloquer.
    /// </summary>
    public static void Pump(int rounds = 1)
    {
        for (int i = 0; i < rounds; i++)
        {
            var frame = new DispatcherFrame();
            bool timedOut = false;
            Dispatcher.UIThread.Post(() => frame.Continue = false, DispatcherPriority.SystemIdle);
            // Minuterie hors du fil de l'interface : celles du répartiteur attendent qu'il soit libre.
            using (new Timer(_ =>
                   {
                       timedOut = true;
                       frame.Continue = false;
                   }, null, TimeSpan.FromSeconds(5), Timeout.InfiniteTimeSpan))
            {
                Dispatcher.UIThread.PushFrame(frame);
            }
            if (timedOut)
            {
                // Fenêtres fermées (pages libérées) : la boucle s'arrête, les tests suivants et la
                // fin de l'exécution ne restent pas bloqués derrière elle.
                foreach (MainWindow window in App.Windows.ToList())
                    window.Close();
                throw new TimeoutException("L'interface ne revient jamais au repos : une tâche se relance sans fin.");
            }
        }
    }
}
