using System;
using System.Collections.Generic;
using System.Text;

namespace MyHomelabBrowser.classes
{
     public class HistoryEntry
    {
        public string Title { get; set; } = "";
        public string Url { get; set; } = "";
        public DateTime VisitedAt { get; set; }
    }

}
