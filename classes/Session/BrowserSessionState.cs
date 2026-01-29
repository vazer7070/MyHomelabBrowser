using System.Collections.Generic;

namespace MyHomelabBrowser.classes.Session
{
    public class BrowserSessionState
    {
        public List<TabState> Tabs { get; set; } = new();
        public int SelectedIndex { get; set; } = 0;
    }

    public class TabState
    {
        public string Url { get; set; } = "";
        public bool IsPinned { get; set; }
        public bool IsPrivate { get; set; }

        // legacy
        public bool IsLegacy { get; set; }
        public string? LegacyUrl { get; set; }
    }
}