using Avalonia.Headless.XUnit;
using PommeBrowser.Core;
using PommeBrowser.Views.Pages;

namespace PommeBrowser.UiTests;

/// <summary>« Signaler un problème » : journaux joints au rapport.</summary>
public sealed class ReportTests
{
    [AvaloniaFact]
    public void The_attached_logs_contain_the_error_log()
    {
        ErrorLog.Write("Test de l'interface", new InvalidOperationException("erreur-témoin-" + Guid.NewGuid().ToString("N")[..8]));
        string tail = ErrorLog.ReadTail(64 * 1024)!;
        string marker = tail[tail.LastIndexOf("erreur-témoin-", StringComparison.Ordinal)..][..22];

        string logs = ReportPage.LogsForReport("0.0.0");

        Assert.Contains("=== Journal de la session ===", logs, StringComparison.Ordinal);
        Assert.Contains("=== Journal des erreurs (errors.log, fin) ===", logs, StringComparison.Ordinal);
        Assert.Contains(marker, logs, StringComparison.Ordinal);
        Assert.True(System.Text.Encoding.UTF8.GetByteCount(logs) < 1024 * 1024);
    }
}
