using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using PommeBrowser.Engine;
using PommeBrowser.Legacy;

namespace PommeBrowser.UiTests;

/// <summary>
/// Moteur Flash intégré vu de PommeBrowser, avec l'hôte réel et le greffon de test (variables
/// POMMEFLASH_HOST et POMMEFLASH_TEST_PLUGIN, comme les tests du moteur ; Linux, sous X11) :
/// lecteur figé signalé puis rétabli, plantage reconnu.
/// </summary>
public sealed class FlashHostTests
{
    const int SigCont = 18;
    const int SigStop = 19;

    [DllImport("libc", EntryPoint = "kill")]
    static extern int Kill(int pid, int signal);

    static async Task Until(Func<bool> condition, TimeSpan timeout, string what)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > timeout)
                throw new TimeoutException(what);
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(50);
        }
    }

    [AvaloniaFact(Timeout = 180_000)]
    public async Task A_frozen_player_is_reported_then_a_crash_is_recognised()
    {
        string? hostPath = Environment.GetEnvironmentVariable("POMMEFLASH_HOST");
        string? plugin = Environment.GetEnvironmentVariable("POMMEFLASH_TEST_PLUGIN");
        if (!OperatingSystem.IsLinux() || string.IsNullOrEmpty(hostPath) || string.IsNullOrEmpty(plugin) ||
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY")) || hostPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Skip("Hôte Linux et greffon de test non fournis (POMMEFLASH_HOST, POMMEFLASH_TEST_PLUGIN), ou pas d'affichage X11.");
        }

        // PommeBrowser cherche l'hôte à côté de lui (flash/PommeFlashHost).
        string flash = Path.Combine(AppContext.BaseDirectory, "flash");
        Directory.CreateDirectory(flash);
        foreach (string file in Directory.GetFiles(Path.GetDirectoryName(hostPath)!))
            File.Copy(file, Path.Combine(flash, Path.GetFileName(file)), overwrite: true);

        // Contenu injoignable : le lecteur s'affiche quand même (le chargement échoue seul).
        var content = new FlashContent(new Uri("http://127.0.0.1:9/movie.swf"), new Uri("http://127.0.0.1:9/page.html"),
            null, 200, 150, null, Array.Empty<KeyValuePair<string, string>>());
        FlashHostProcess host = FlashHostProcess.Start(content, plugin, isPrivate: true);
        var changes = new List<bool>();
        bool exited = false;
        host.ResponsivenessChanged += changes.Add;
        host.Exited += () => exited = true;
        try
        {
            await Until(() => host.FindWindow() != 0, TimeSpan.FromSeconds(30), "fenêtre du lecteur annoncée");
            int pid = host.ProcessIds.Single();

            // Le lecteur répond : rien n'est signalé.
            await Task.Delay(TimeSpan.FromSeconds(8));
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(changes);

            // Processus arrêté (SIGSTOP), comme un fil du module bloqué : signalé, puis rétabli.
            Assert.Equal(0, Kill(pid, SigStop));
            await Until(() => changes.Count >= 1, FlashHostProcess.UnresponsiveAfter + TimeSpan.FromSeconds(15), "lecteur figé signalé");
            Assert.True(changes[0]);
            Assert.Equal(0, Kill(pid, SigCont));
            await Until(() => changes.Count >= 2, TimeSpan.FromSeconds(15), "lecteur rétabli signalé");
            Assert.False(changes[1]);

            // Processus tué après l'affichage : plantage, pas un échec au démarrage.
            Process.GetProcessById(pid).Kill();
            await Until(() => exited, TimeSpan.FromSeconds(15), "arrêt du lecteur");
            Assert.True(host.Crashed);
            Assert.False(host.FailedToStart);
        }
        finally
        {
            host.Close();
        }
    }
}
