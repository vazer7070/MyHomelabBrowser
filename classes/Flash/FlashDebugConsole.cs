using System;
using System.Collections.Generic;

namespace MyHomelabBrowser.classes.Flash
{
    public static class FlashDebugConsole
    {
        public static event Action<string>? LineAdded;

        // Journal alimenté depuis le thread UI et depuis les lancements Basilisk en arrière-plan.
        static readonly Queue<string> _lines = new();
        static readonly object Sync = new();

        public static IReadOnlyCollection<string> Lines
        {
            get
            {
                lock (Sync)
                    return _lines.ToArray();
            }
        }

        public static void Log(string msg)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";

            lock (Sync)
            {
                _lines.Enqueue(line);
                while (_lines.Count > 200)
                    _lines.Dequeue();
            }

            LineAdded?.Invoke(line);
        }

        public static void Clear()
        {
            lock (Sync)
                _lines.Clear();
            LineAdded?.Invoke("---- cleared ----");
        }
    }
}
