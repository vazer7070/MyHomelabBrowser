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

                bool exists = r.TryGetProperty("exists", out var ex) && ex.ValueKind == JsonValueKind.True;
                if (!exists) return new RuffleStatus { Exists = false };

                var status = new RuffleStatus
                {
                    Exists = true,
                    Started = r.TryGetProperty("started", out var st) && st.ValueKind == JsonValueKind.True,
                    InjectedAt = GetInt64Safe(r, "injectedAt"),
                    LastFrameAt = GetInt64Safe(r, "lastFrameAt")
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

        private static long GetInt64Safe(JsonElement root, string propertyName)
        {
            if (!root.TryGetProperty(propertyName, out var el))
                return 0;

            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out var n))
                return n;

            if (el.ValueKind == JsonValueKind.String)
            {
                var s = el.GetString();
                if (long.TryParse(s, out var n2))
                    return n2;
            }

            // null / undefined / true/false / object => 0
            return 0;
        }
    }
}
