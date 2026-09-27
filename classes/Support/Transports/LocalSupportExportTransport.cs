using MyHomelabBrowser.classes.Support.Models;
using System;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.classes.Support.Transports
{
    /// <summary>
    /// Tant que le service de support n'est pas en ligne, le rapport est enregistré
    /// dans une archive locale que l'utilisateur peut transmettre lui-même.
    /// Aucun secret (webhook, clé) n'a besoin d'être embarqué dans l'application.
    /// </summary>
    public sealed class LocalSupportExportTransport : ISupportTransport
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly string _outputDirectory;

        public LocalSupportExportTransport(string? outputDirectory = null)
        {
            _outputDirectory = outputDirectory ?? DefaultOutputDirectory;
        }

        public static string DefaultOutputDirectory => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            "PommeBrowser",
            "Rapports");

        public string Name => "Export local";

        public Task<SupportSubmissionResult> SendAsync(
            SupportReportRequest report,
            SupportAttachment? attachment,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(report);
            cancellationToken.ThrowIfCancellationRequested();

            Directory.CreateDirectory(_outputDirectory);

            string reportId = string.IsNullOrWhiteSpace(report.ClientReportId)
                ? "PB-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
                : report.ClientReportId;

            string path = Path.Combine(_outputDirectory, SanitizeFileName(reportId) + ".zip");

            using (FileStream stream = new(path, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var archive = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                AddText(archive, "rapport.txt", BuildReadableReport(report));
                AddText(archive, "rapport.json", JsonSerializer.Serialize(report, JsonOptions));

                if (attachment != null && !attachment.IsEmpty)
                {
                    ZipArchiveEntry entry = archive.CreateEntry(SanitizeFileName(attachment.FileName), CompressionLevel.Optimal);
                    using Stream entryStream = entry.Open();
                    entryStream.Write(attachment.Content, 0, attachment.Content.Length);
                }
            }

            return Task.FromResult(new SupportSubmissionResult
            {
                Success = true,
                ReportId = reportId,
                Message = Tr("Rapport enregistré sur cet ordinateur."),
                Channel = SupportDeliveryChannel.LocalFile,
                FilePath = path
            });
        }

        internal static string BuildReadableReport(SupportReportRequest report)
        {
            var builder = new StringBuilder();
            builder.AppendLine($"Rapport {report.ClientReportId}");
            builder.AppendLine($"Application : {report.Client} {report.ClientVersion}");
            builder.AppendLine($"Date (UTC)  : {report.CreatedAtUtc:yyyy-MM-dd HH:mm:ss}");
            builder.AppendLine($"Module      : {report.ModuleLabel}");
            builder.AppendLine(Tr("Catégorie   : {0}", report.CategoryLabel));
            builder.AppendLine();
            builder.AppendLine("Titre");
            builder.AppendLine(report.Title);
            builder.AppendLine();
            builder.AppendLine("Description");
            builder.AppendLine(report.Description);
            builder.AppendLine();
            builder.AppendLine("Informations techniques");
            builder.AppendLine(report.TechnicalInformation);
            return builder.ToString();
        }

        private static void AddText(ZipArchive archive, string name, string content)
        {
            ZipArchiveEntry entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            writer.Write(content);
        }

        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var builder = new StringBuilder(name.Length);
            foreach (char c in name)
                builder.Append(Array.IndexOf(invalid, c) >= 0 || c is '/' or '\\' ? '_' : c);

            string result = builder.ToString().Trim('.', ' ');
            return result.Length == 0 ? "rapport" : result;
        }
    }
}
