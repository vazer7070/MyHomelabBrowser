using System.Collections.Generic;
using System.Text.Json;

namespace MyHomelabBrowser.classes.Flash
{
    public sealed class RuffleStatus
    {
        public bool Exists { get; init; }
        public bool Started { get; init; }
        public long InjectedAt { get; init; }
        public long LastFrameAt { get; init; }
        public List<string> Errors { get; init; } = new();

        public static RuffleStatus FromWebViewJson(string webViewResult)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(webViewResult))
                    return new RuffleStatus { Exists = false };

                string raw = webViewResult;

                // ExecuteScriptAsync renvoie souvent une string JSON encodée (entre guillemets)
                if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
                    raw = JsonSerializer.Deserialize<string>(raw) ?? raw;

                using var doc = JsonDocument.Parse(raw);
                var r = doc.RootElement;

                bool exists = r.TryGetProperty("exists", out var ex) && ex.GetBoolean();
                if (!exists) return new RuffleStatus { Exists = false };

                var status = new RuffleStatus
                {
                    Exists = true,
                    Started = r.TryGetProperty("started", out var st) && st.GetBoolean(),
                    InjectedAt = r.TryGetProperty("injectedAt", out var ia) ? ia.GetInt64() : 0,
                    LastFrameAt = r.TryGetProperty("lastFrameAt", out var lf) ? lf.GetInt64() : 0
                };

                if (r.TryGetProperty("errors", out var errs) && errs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in errs.EnumerateArray())
                        if (e.ValueKind == JsonValueKind.String)
                            status.Errors.Add(e.GetString() ?? "");
                }

                return status;
            }
            catch
            {
                return new RuffleStatus { Exists = false };
            }
        }
    }
}
