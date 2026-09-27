using System.Text.Encodings.Web;
using System.Text.Json;

namespace PommeBrowser.SupportServer;

/// <summary>
/// Copie de chaque rapport sur disque : data/reports/AAAA-MM/PB-…/report.json (+ log.txt).
/// Rien n'est perdu si Discord est indisponible.
/// </summary>
public sealed class ReportStore(SupportServerOptions options, TimeProvider time, ILogger<ReportStore> logger)
{
    static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public bool Enabled => options.DataDirectory != null;

    string ReportsDirectory => Path.Combine(options.DataDirectory!, "reports");

    public async Task<bool> TrySaveAsync(SupportReport report, CancellationToken cancellationToken)
    {
        if (!Enabled)
            return false;

        try
        {
            string folder = Path.Combine(ReportsDirectory, report.ReceivedAtUtc.UtcDateTime.ToString("yyyy-MM"), report.Id);
            Directory.CreateDirectory(folder);

            await File.WriteAllTextAsync(Path.Combine(folder, "report.json"), JsonSerializer.Serialize(report, JsonOptions), cancellationToken);
            if (report.Log != null)
                await File.WriteAllBytesAsync(Path.Combine(folder, "log.txt"), report.Log, cancellationToken);

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Impossible d'enregistrer le rapport {ReportId} sur disque", report.Id);
            return false;
        }
    }

    /// <summary>Supprime les rapports plus anciens que la durée de conservation.</summary>
    public int DeleteExpired()
    {
        if (!Enabled || options.RetentionDays == 0 || !Directory.Exists(ReportsDirectory))
            return 0;

        DateTime limit = time.GetUtcNow().UtcDateTime.AddDays(-options.RetentionDays);
        int deleted = 0;

        foreach (string month in Directory.EnumerateDirectories(ReportsDirectory))
        {
            foreach (string report in Directory.EnumerateDirectories(month))
            {
                try
                {
                    if (Directory.GetLastWriteTimeUtc(report) < limit)
                    {
                        Directory.Delete(report, recursive: true);
                        deleted++;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    logger.LogWarning(ex, "Impossible de supprimer {Folder}", report);
                }
            }

            try
            {
                if (!Directory.EnumerateFileSystemEntries(month).Any())
                    Directory.Delete(month);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return deleted;
    }
}

/// <summary>Nettoyage des anciens rapports au démarrage puis deux fois par jour.</summary>
public sealed class RetentionService(ReportStore store, ILogger<RetentionService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(12));
        do
        {
            int deleted = store.DeleteExpired();
            if (deleted > 0)
                logger.LogInformation("{Count} ancien(s) rapport(s) supprimé(s)", deleted);
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
