using Avalonia.Headless.XUnit;
using PommeBrowser.Core;

namespace PommeBrowser.UiTests;

/// <summary>Arrêt brutal de la session précédente : consigné au démarrage suivant.</summary>
public sealed class CrashWatchTests
{
    [AvaloniaFact]
    public async Task A_session_left_running_is_reported_with_the_end_of_its_log()
    {
        string marker = AppPaths.SharedData("session.running");
        string tailFile = AppPaths.SharedData("session-tail.log");
        string witness = "dernière-ligne-" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
        File.WriteAllText(marker, "123");
        File.WriteAllText(tailFile, "[12:00:00] [Flash] avant l'arrêt\n[12:00:01] " + witness + "\n");

        await CrashWatch.Start();
        string errors = ErrorLog.ReadTail(64 * 1024)!;

        Assert.Contains("Arrêt brutal de la session précédente", errors, StringComparison.Ordinal);
        Assert.Contains(witness, errors, StringComparison.Ordinal);
        // Nouvelle session : marque posée, fin de journal de l'ancienne effacée.
        Assert.True(File.Exists(marker));
        Assert.False(File.Exists(tailFile));

        // Fin normale : plus de marque, rien à signaler au démarrage suivant.
        CrashWatch.Stop();
        Assert.False(File.Exists(marker));
        await CrashWatch.Start();
        Assert.Equal(1, CountOf(ErrorLog.ReadTail(256 * 1024)!, witness));
        CrashWatch.Stop();
    }

    static int CountOf(string text, string part)
    {
        int count = 0;
        for (int index = text.IndexOf(part, StringComparison.Ordinal); index >= 0; index = text.IndexOf(part, index + part.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
