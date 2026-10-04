using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace PommeFlash.Host
{
    /// <summary>
    /// Échanges avec PommeBrowser : un événement JSON par ligne sur la sortie standard (ready,
    /// status, navigate, script, eval, log, called, exit) ; une commande par ligne sur l'entrée
    /// standard (« close », « result &lt;id&gt; &lt;json&gt; », « call &lt;id&gt; &lt;json&gt; »).
    /// La fin de l'entrée standard (PommeBrowser fermé) arrête l'hôte.
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

        /// <summary>
        /// Lit les commandes sur un fil à part ; <paramref name="ended"/> quand l'entrée se ferme.
        /// Les réponses aux questions (« result ») sont remises directement à qui les attend. Les
        /// appels de la page vers le contenu (« call ») vont à <paramref name="call"/>, sur le fil
        /// du module, y compris pendant qu'il attend un script de la page (voir <see cref="RunCalls"/>).
        /// </summary>
        public static void StartReading(Action<string> command, Action<string> call, Action ended)
        {
            _call = call;
            var thread = new Thread(() =>
            {
                try
                {
                    using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                    while (reader.ReadLine() is { } line)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("result ", StringComparison.Ordinal))
                        {
                            OnResult(trimmed);
                        }
                        else if (trimmed.StartsWith("call ", StringComparison.Ordinal))
                        {
                            Calls.Enqueue(trimmed);
                            Wakeup.Set();
                            UiThread.Post(RunCalls);
                        }
                        else if (trimmed.Length > 0)
                        {
                            command(trimmed);
                        }
                    }
                }
                catch (IOException)
                {
                }
                _inputEnded = true;
                FailPending();
                ended();
            })
            {
                IsBackground = true,
                Name = "Commandes de PommeBrowser"
            };
            thread.Start();
        }

        // ---------------------------------------------------------------
        // Appels de la page vers le contenu
        // ---------------------------------------------------------------

        // Appels reçus, dans l'ordre ; exécutés par le fil du module seulement.
        static readonly ConcurrentQueue<string> Calls = new();
        // Réveil du fil du module qui attend un script : appel reçu ou réponse arrivée.
        static readonly AutoResetEvent Wakeup = new(false);
        static Action<string>? _call;
        // Scripts de la page attendus en ce moment par le fil du module, les uns dans les autres.
        static int _scriptDepth;

        /// <summary>
        /// Appels imbriqués au plus : page → contenu → page → contenu… (au-delà, l'appel attend la
        /// fin des précédents, comme sans imbrication).
        /// </summary>
        const int MaxScriptDepth = 8;

        /// <summary>Sur le fil du module : exécute les appels de la page reçus, dans l'ordre.</summary>
        static void RunCalls()
        {
            if (_call is not { } call)
                return;
            while (Calls.TryDequeue(out string? line))
                UiThread.Run(() => call(line));
        }

        // ---------------------------------------------------------------
        // Questions à PommeBrowser : scripts de la page, cookies
        // ---------------------------------------------------------------

        static readonly Dictionary<int, TaskCompletionSource<(bool Ok, object? Value)>> Pending = new();
        static readonly TimeSpan CookieTimeout = TimeSpan.FromSeconds(10);
        static int _nextRequest;
        static volatile bool _inputEnded;

        /// <summary>Question numérotée ; la réponse arrive par « result &lt;id&gt; {"ok":…,"value":…} ».</summary>
        static (int Id, Task<(bool Ok, object? Value)> Reply) Ask(string name, params (string Key, object? Value)[] fields)
        {
            int id = Interlocked.Increment(ref _nextRequest);
            var reply = new TaskCompletionSource<(bool Ok, object? Value)>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (Pending)
                Pending[id] = reply;
            if (_inputEnded)
                Complete(id, (false, null));
            else
                Send(name, new[] { ("id", (object?)id) }.Concat(fields).ToArray());
            return (id, reply.Task);
        }

        static void Complete(int id, (bool Ok, object? Value) result)
        {
            TaskCompletionSource<(bool Ok, object? Value)>? reply;
            lock (Pending)
            {
                if (!Pending.Remove(id, out reply))
                    return;
            }
            reply.TrySetResult(result);
            Wakeup.Set();
        }

        static void FailPending()
        {
            int[] ids;
            lock (Pending)
                ids = Pending.Keys.ToArray();
            foreach (int id in ids)
                Complete(id, (false, null));
        }

        /// <summary>
        /// Attente d'une réponse sur le fil du module, qui reste bloqué comme dans un navigateur
        /// (sous Windows, les messages envoyés par les autres fils sont traités pendant ce temps).
        /// Avec <paramref name="allowCalls"/> (script de la page), les appels de la page vers le
        /// contenu reçus pendant l'attente sont exécutés aussitôt : dans un navigateur, le script
        /// peut appeler le contenu, qui répond depuis son appel à NPN_Evaluate. Sans cela, chacun
        /// attendrait l'autre.
        /// </summary>
        static bool Wait(int id, Task<(bool Ok, object? Value)> reply, TimeSpan timeout, bool allowCalls, out object? value)
        {
            if (allowCalls && _scriptDepth >= MaxScriptDepth)
                allowCalls = false;
            if (allowCalls)
                _scriptDepth++;
            try
            {
                var clock = Stopwatch.StartNew();
                while (!reply.IsCompleted)
                {
                    if (allowCalls)
                        RunCalls();
                    if (reply.IsCompleted)
                        break;
                    TimeSpan remaining = timeout - clock.Elapsed;
                    if (remaining <= TimeSpan.Zero)
                        break;
                    UiThread.Display.Wait(allowCalls ? Wakeup : ((IAsyncResult)reply).AsyncWaitHandle, remaining);
                }
            }
            finally
            {
                if (allowCalls)
                    _scriptDepth--;
            }

            if (!reply.IsCompleted)
            {
                Complete(id, (false, null));
                if (!_inputEnded)
                    Log($"PommeBrowser n'a pas répondu après {timeout.TotalSeconds:0} s.");
            }
            (bool ok, value) = reply.Result;
            return ok;
        }

        /// <summary>
        /// Script de la page (NPN_Evaluate) : PommeBrowser l'exécute dans la page et renvoie son
        /// résultat. Faux si PommeBrowser refuse, ne répond pas à temps ou s'est fermé.
        /// </summary>
        public static bool RunInPage(string code, TimeSpan timeout, out object? value)
        {
            (int id, Task<(bool Ok, object? Value)> reply) = Ask("eval", ("code", code));
            return Wait(id, reply, timeout, allowCalls: true, out value);
        }

        /// <summary>
        /// Cookies de la page pour une adresse (en-tête Cookie), demandés depuis le fil du module
        /// (NPN_GetValueForURL) : ceux qu'un script de la page verrait, sans les HttpOnly. Null si
        /// PommeBrowser ne les donne pas.
        /// </summary>
        public static string? PageCookies(Uri url)
        {
            (int id, Task<(bool Ok, object? Value)> reply) = Ask("cookies", ("url", url.AbsoluteUri), ("http", false));
            return Wait(id, reply, CookieTimeout, allowCalls: false, out object? value) ? value as string : null;
        }

        /// <summary>
        /// Cookies de la page pour un chargement (HttpOnly compris, comme une requête du
        /// navigateur), depuis n'importe quel fil. Null si PommeBrowser ne les donne pas à temps.
        /// </summary>
        public static async Task<string?> PageCookiesAsync(Uri url, CancellationToken cancellation)
        {
            (int id, Task<(bool Ok, object? Value)> reply) = Ask("cookies", ("url", url.AbsoluteUri), ("http", true));
            Task finished = await Task.WhenAny(reply, Task.Delay(CookieTimeout, cancellation)).ConfigureAwait(false);
            if (finished != reply)
            {
                Complete(id, (false, null));
                cancellation.ThrowIfCancellationRequested();
                Log("PommeBrowser n'a pas donné les cookies à temps : " + url.GetLeftPart(UriPartial.Path));
            }
            (bool ok, object? value) = await reply.ConfigureAwait(false);
            return ok ? value as string : null;
        }

        /// <summary>
        /// Cookie à enregistrer dans la page : reçu d'un serveur (en-tête Set-Cookie,
        /// <paramref name="fromHttp"/>) ou posé par le module (NPN_SetValueForURL).
        /// </summary>
        public static void SetPageCookie(Uri url, string cookie, bool fromHttp)
            => Send("set-cookie", ("url", url.AbsoluteUri), ("cookie", cookie), ("http", fromHttp));

        /// <summary>« result &lt;id&gt; {"ok":…,"value":…} » : réponse de PommeBrowser à une question.</summary>
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
            Complete(id, result);
        }
    }
}
