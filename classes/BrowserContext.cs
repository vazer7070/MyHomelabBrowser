namespace MyHomelabBrowser.classes
{
    public class BrowserContext
    {
        public bool IsPrivate { get; set; }
        public bool IsLegacy { get; set; }


        public string? CurrentUrl { get; set; }
        public string? PageTitle { get; set; }


        public string FlashMode { get; set; } = "inconnu";
        public int? TabId { get; set; }
    }
}