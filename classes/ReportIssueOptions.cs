using MyHomelabBrowser.classes;

public enum ReportModule
{
    Browser,
    AdBlock
}

public class ReportIssueOptions
{
    public bool IncludeLogs { get; set; }
    public bool IncludePcInfo { get; set; }
    public bool IncludeMode { get; set; }

    public ReportModule Module { get; set; } = ReportModule.Browser;

    public BrowserContext? Context { get; set; }
}
