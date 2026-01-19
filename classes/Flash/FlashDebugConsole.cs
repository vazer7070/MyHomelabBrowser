using System;
using System.Collections.Generic;

namespace MyHomelabBrowser.classes.Flash
{
    public static class FlashDebugConsole
    {
        public static event Action<string>? LineAdded;

        static readonly Queue<string> _lines = new();
        public static IReadOnlyCollection<string> Lines => _lines;

        public static void Log(string msg)
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";

            _lines.Enqueue(line);
            while (_lines.Count > 200)
                _lines.Dequeue();

            LineAdded?.Invoke(line);
        }

        public static void Clear()
        {
            _lines.Clear();
            LineAdded?.Invoke("---- cleared ----");
        }
    }
}
