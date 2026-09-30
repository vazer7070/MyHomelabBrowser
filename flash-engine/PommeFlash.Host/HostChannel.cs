using System.Text;
using System.Text.Json;

namespace PommeFlash.Host
{
    /// <summary>
    /// Échanges avec PommeBrowser : un événement JSON par ligne sur la sortie standard (ready,
    /// status, navigate, script, log, exit) ; une commande par ligne sur l'entrée standard
    /// (« close »). La fin de l'entrée standard (PommeBrowser fermé) arrête l'hôte.
    /// </summary>
    static class HostChannel
    {
        static readonly object Gate = new();
        static readonly Stream Output = Console.OpenStandardOutput();

        public static void Send(string name, params (string Key, object? Value)[] fields)
        {
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteString("event", name);
                foreach ((string key, object? value) in fields)
                {
                    switch (value)
                    {
                        case null:
                            writer.WriteNull(key);
                            break;
                        case string text:
                            writer.WriteString(key, text);
                            break;
                        case bool flag:
                            writer.WriteBoolean(key, flag);
                            break;
                        case int number:
                            writer.WriteNumber(key, number);
                            break;
                        case long number:
                            writer.WriteNumber(key, number);
                            break;
                        default:
                            writer.WriteString(key, value.ToString());
                            break;
                    }
                }
                writer.WriteEndObject();
            }
            buffer.WriteByte((byte)'\n');

            lock (Gate)
            {
                try
                {
                    Output.Write(buffer.GetBuffer(), 0, (int)buffer.Length);
                    Output.Flush();
                }
                catch (IOException)
                {
                    // PommeBrowser n'écoute plus : l'entrée standard se fermera aussi.
                }
            }
        }

        public static void Log(string message) => Send("log", ("level", "info"), ("message", message));

        public static void Error(string message) => Send("log", ("level", "error"), ("message", message));

        const int MaxTraces = 300;
        static readonly HashSet<string> Traced = new(StringComparer.Ordinal);

        /// <summary>
        /// Trace de diagnostic : ce que le module demande (fichiers, scripts, objets de la page).
        /// Une fois par clé, et 300 au plus par lancement : le journal de PommeBrowser reste lisible.
        /// </summary>
        public static void Trace(string key, string message)
        {
            lock (Traced)
            {
                if (Traced.Count > MaxTraces || !Traced.Add(key))
                    return;
                if (Traced.Count > MaxTraces)
                    message = "Traces suivantes non notées (" + MaxTraces + " au plus).";
            }
            Send("log", ("level", "trace"), ("message", message));
        }

        /// <summary>Texte raccourci pour le journal.</summary>
        public static string Excerpt(string? text, int length = 300)
        {
            if (string.IsNullOrEmpty(text))
                return string.Empty;
            string line = text.ReplaceLineEndings(" ");
            return line.Length <= length ? line : line[..length] + "…";
        }

        /// <summary>Lit les commandes sur un fil à part ; <paramref name="ended"/> quand l'entrée se ferme.</summary>
        public static void StartReading(Action<string> command, Action ended)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                    while (reader.ReadLine() is { } line)
                    {
                        if (line.Trim().Length > 0)
                            command(line.Trim());
                    }
                }
                catch (IOException)
                {
                }
                ended();
            })
            {
                IsBackground = true,
                Name = "Commandes de PommeBrowser"
            };
            thread.Start();
        }
    }
}
