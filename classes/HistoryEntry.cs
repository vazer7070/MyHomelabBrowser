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

        /// <summary>Identifiant dans la base d'historique (0 tant que l'entrée n'y est pas).</summary>
        [System.Text.Json.Serialization.JsonIgnore]
        public long Id { get; set; }
    }

}
