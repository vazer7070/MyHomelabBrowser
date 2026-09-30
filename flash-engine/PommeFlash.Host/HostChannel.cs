using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using PommeFlash.Host.Native;

namespace PommeFlash.Host
{
    /// <summary>
    /// Échanges avec PommeBrowser : un événement JSON par ligne sur la sortie standard (ready,
    /// status, navigate, script, eval, log, exit) ; une commande par ligne sur l'entrée standard
    /// (« close », « result &lt;id&gt; &lt;json&gt; »). La fin de l'entrée standard (PommeBrowser
    /// fermé) arrête l'hôte.
    /// </summary>
    static unsafe class HostChannel
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

        /// <summary>
        /// Lit les commandes sur un fil à part ; <paramref name="ended"/> quand l'entrée se ferme.
        /// Les réponses aux scripts (« result ») sont remises directement au fil du module, qui les attend.
        /// </summary>
        public static void StartReading(Action<string> command, Action ended)
        {
            var thread = new Thread(() =>
            {
                try
                {
                    using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                    while (reader.ReadLine() is { } line)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("result ", StringComparison.Ordinal))
                            OnResult(trimmed);
                        else if (trimmed.Length > 0)
                            command(trimmed);
                    }
                }
                catch (IOException)
                {
                }
                _inputEnded = true;
                ResultArrived.Set();
                ended();
            })
            {
                IsBackground = true,
                Name = "Commandes de PommeBrowser"
            };
            thread.Start();
        }

        // ---------------------------------------------------------------
        // Scripts exécutés dans la page par PommeBrowser
        // ---------------------------------------------------------------

        static readonly Dictionary<int, (bool Ok, object? Value)> Results = new();
        static readonly AutoResetEvent ResultArrived = new(false);
        static int _nextScript;
        static volatile bool _inputEnded;

        /// <summary>
        /// Script de la page (NPN_Evaluate) : PommeBrowser l'exécute dans la page et renvoie son
        /// résultat. Le module attend la réponse, comme dans un navigateur ; pendant ce temps, les
        /// messages envoyés par les autres fils sont traités (PommeBrowser place et affiche la
        /// fenêtre du module : sans cela, les deux processus s'attendraient l'un l'autre).
        /// Faux si PommeBrowser refuse, ne répond pas à temps ou s'est fermé.
        /// </summary>
        public static bool RunInPage(string code, TimeSpan timeout, out object? value)
        {
            int id = Interlocked.Increment(ref _nextScript);
            Send("eval", ("id", id), ("code", code));

            nint handle = ResultArrived.SafeWaitHandle.DangerousGetHandle();
            var clock = Stopwatch.StartNew();
            while (true)
            {
                lock (Results)
                {
                    if (Results.Remove(id, out (bool Ok, object? Value) result))
                    {
                        value = result.Value;
                        return result.Ok;
                    }
                }
                long remaining = (long)(timeout - clock.Elapsed).TotalMilliseconds;
                if (remaining <= 0 || _inputEnded)
                    break;
                uint wait = Win32.MsgWaitForMultipleObjectsEx(1, &handle, (uint)remaining, Win32.QS_SENDMESSAGE, 0);
                if (wait == Win32.WAIT_OBJECT_0 + 1)
                {
                    Win32.MSG message;
                    Win32.PeekMessageW(&message, 0, 0, 0, Win32.PM_NOREMOVE | Win32.PM_QS_SENDMESSAGE);
                }
            }

            if (!_inputEnded)
                Log($"Script de la page sans réponse après {timeout.TotalSeconds:0} s.");
            value = null;
            return false;
        }

        /// <summary>« result &lt;id&gt; {"ok":…,"value":…} » : réponse de PommeBrowser à un script.</summary>
        static void OnResult(string line)
        {
            string[] parts = line.Split(' ', 3);
            if (parts.Length < 3 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int id))
                return;
            (bool Ok, object? Value) result = (false, null);
            try
            {
                using JsonDocument document = JsonDocument.Parse(parts[2]);
                JsonElement root = document.RootElement;
                bool ok = root.TryGetProperty("ok", out JsonElement flag) && flag.ValueKind == JsonValueKind.True;
                object? value = !root.TryGetProperty("value", out JsonElement element) ? null : element.ValueKind switch
                {
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number => element.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null
                };
                result = (ok, value);
            }
            catch (JsonException)
            {
            }
            lock (Results)
                Results[id] = result;
            ResultArrived.Set();
        }
    }
}
