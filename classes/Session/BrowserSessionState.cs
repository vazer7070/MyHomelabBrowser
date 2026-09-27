using System.Collections.Generic;

namespace MyHomelabBrowser.classes.Session
{
    public class BrowserSessionState
    {
        public List<TabState> Tabs { get; set; } = new();
        public int SelectedIndex { get; set; } = 0;

        // Session écrite juste avant un redémarrage de mise à jour : restaurée
        // quel que soit le mode de démarrage choisi.
        public bool IsUpdateRestart { get; set; }
    }

    public class TabState
    {
        public string Url { get; set; } = "";
        public string? Title { get; set; }
        public bool IsPinned { get; set; }

        // Conservé pour lire les anciennes sessions ; les onglets privés ne sont plus enregistrés.
        public bool IsPrivate { get; set; }

        // legacy
        public bool IsLegacy { get; set; }
        public string? LegacyUrl { get; set; }
    }
}
