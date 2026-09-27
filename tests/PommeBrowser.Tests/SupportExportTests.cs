using MyHomelabBrowser.classes.Support.Models;
using MyHomelabBrowser.classes.Support.Transports;
using System.IO.Compression;
using System.Text;

namespace PommeBrowser.Tests;

public sealed class SupportExportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "pomme-support-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    [Fact]
    public async Task Report_is_saved_as_an_archive_with_its_log()
    {
        var transport = new LocalSupportExportTransport(_folder);
        var report = new SupportReportRequest
        {
            ClientReportId = "PB-20260927-ABCD",
            ClientVersion = "0.9.8",
            Title = "Onglet figé",
            Description = "L'onglet ne répond plus après la veille.",
            TechnicalInformation = "- Système : test"
        };
        var log = new SupportAttachment { FileName = "log.txt", Content = Encoding.UTF8.GetBytes("ligne de log") };

        SupportSubmissionResult result = await transport.SendAsync(report, log);

        Assert.Equal(SupportDeliveryChannel.LocalFile, result.Channel);
        Assert.NotNull(result.FilePath);
        Assert.True(File.Exists(result.FilePath));

        using ZipArchive archive = ZipFile.OpenRead(result.FilePath!);
        Assert.NotNull(archive.GetEntry("rapport.txt"));
        Assert.NotNull(archive.GetEntry("rapport.json"));
        Assert.NotNull(archive.GetEntry("log.txt"));

        using var reader = new StreamReader(archive.GetEntry("rapport.txt")!.Open());
        Assert.Contains("Onglet figé", reader.ReadToEnd());
    }

    [Fact]
    public async Task Report_ids_cannot_escape_the_output_folder()
    {
        var transport = new LocalSupportExportTransport(_folder);
        var report = new SupportReportRequest { ClientReportId = "..\\..\\evil", Title = "t", Description = "d" };

        SupportSubmissionResult result = await transport.SendAsync(report, null);

        Assert.Equal(Path.GetFullPath(_folder), Path.GetDirectoryName(Path.GetFullPath(result.FilePath!)));
    }
}
