using System;
using System.IO;
using System.Text.Json;

namespace MyHomelabBrowser.classes.Session
{
    public static class SessionPersistence
    {
        private static string GetPath()
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MyHomelabBrowser"
            );

            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "session.json");
        }

        public static void Save(BrowserSessionState state)
        {
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            AtomicFile.WriteAllText(GetPath(), json);
        }

        public static BrowserSessionState? Load()
        {
            try
            {
                var path = GetPath();
                if (!File.Exists(path))
                    return null;

                var json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<BrowserSessionState>(json);
            }
            catch
            {
                // Session illisible : démarrage normal.
                return null;
            }
        }

        public static void Clear()
        {
            try
            {
                var path = GetPath();
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
            }
        }
    }
}
