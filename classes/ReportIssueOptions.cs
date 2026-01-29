using MyHomelabBrowser.classes;

public class ReportIssueOptions
{
    public bool IncludeLogs { get; set; }
    public bool IncludePcInfo { get; set; }
    public bool IncludeMode { get; set; }

    // 🔥 NOUVEAU
    public BrowserContext? Context { get; set; }
}