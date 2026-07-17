using System.Collections.Generic;
using System.Text.Json;

namespace MyHomelabBrowser.classes.Flash
{
    public sealed class RuffleStatus
    {
        public bool Exists { get; init; }
        public bool ScriptLoaded { get; init; }
        public bool InjectionCompleted { get; init; }
        public int PlayerCount { get; init; }
        public int VisiblePlayerCount { get; init; }
        public int LoadedPlayerCount { get; init; }
        public int MaxReadyState { get; init; }
        public bool MetadataLoaded { get; init; }
        public int SuspendedPlayerCount { get; init; }
        public long InjectedAt { get; init; }
        public long LastPlayerMutationAt { get; init; }
        public long LastActivityAt { get; init; }
        public string LastStatus { get; init; } = string.Empty;
        public double MovieWidth { get; init; }
        public double MovieHeight { get; init; }
        public double FrameRate { get; init; }
        public int FrameCount { get; init; }
        public int SwfVersion { get; init; }
        public bool IsActionScript3 { get; init; }
        public long UncompressedLength { get; init; }
        public List<string> Errors { get; init; } = new();

        // ReadyState 1 signifie que le SWF a commencé à s'exécuter et que ses
        // métadonnées sont disponibles. Un simple élément visible ne suffit plus.
        public bool HasUsablePlayer =>
            ScriptLoaded &&
            VisiblePlayerCount > 0 &&
            LoadedPlayerCount > 0 &&
            MetadataLoaded &&
            MaxReadyState >= 1;

        public bool IsFullyLoaded => HasUsablePlayer && MaxReadyState >= 2;

        public static RuffleStatus FromWebViewJson(string webViewResult)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(webViewResult))
                    return new RuffleStatus();

                string raw = webViewResult;
                if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
                    raw = JsonSerializer.Deserialize<string>(raw) ?? raw;

                using JsonDocument doc = JsonDocument.Parse(raw);
                JsonElement root = doc.RootElement;

                var status = new RuffleStatus
                {
                    Exists = GetBool(root, "exists"),
                    ScriptLoaded = GetBool(root, "scriptLoaded"),
                    InjectionCompleted = GetBool(root, "injectionCompleted"),
                    PlayerCount = GetInt32(root, "playerCount"),
                    VisiblePlayerCount = GetInt32(root, "visiblePlayerCount"),
                    LoadedPlayerCount = GetInt32(root, "loadedPlayerCount"),
                    MaxReadyState = GetInt32(root, "maxReadyState"),
                    MetadataLoaded = GetBool(root, "metadataLoaded"),
                    SuspendedPlayerCount = GetInt32(root, "suspendedPlayerCount"),
                    InjectedAt = GetInt64(root, "injectedAt"),
                    LastPlayerMutationAt = GetInt64(root, "lastPlayerMutationAt"),
                    LastActivityAt = GetInt64(root, "lastActivityAt"),
                    LastStatus = GetString(root, "lastStatus")
                };

                if (root.TryGetProperty("metadata", out JsonElement metadata) &&
                    metadata.ValueKind == JsonValueKind.Object)
                {
                    status = new RuffleStatus
                    {
                        Exists = status.Exists,
                        ScriptLoaded = status.ScriptLoaded,
                        InjectionCompleted = status.InjectionCompleted,
                        PlayerCount = status.PlayerCount,
                        VisiblePlayerCount = status.VisiblePlayerCount,
                        LoadedPlayerCount = status.LoadedPlayerCount,
                        MaxReadyState = status.MaxReadyState,
                        MetadataLoaded = status.MetadataLoaded,
                        SuspendedPlayerCount = status.SuspendedPlayerCount,
                        InjectedAt = status.InjectedAt,
                        LastPlayerMutationAt = status.LastPlayerMutationAt,
                        LastActivityAt = status.LastActivityAt,
                        LastStatus = status.LastStatus,
                        MovieWidth = GetDouble(metadata, "width"),
                        MovieHeight = GetDouble(metadata, "height"),
                        FrameRate = GetDouble(metadata, "frameRate"),
                        FrameCount = GetInt32(metadata, "numFrames"),
                        SwfVersion = GetInt32(metadata, "swfVersion"),
                        IsActionScript3 = GetBool(metadata, "isActionScript3"),
                        UncompressedLength = GetInt64(metadata, "uncompressedLength")
                    };
                }

                if (root.TryGetProperty("errors", out JsonElement errorsElement) &&
                    errorsElement.ValueKind == JsonValueKind.Array)
                {
                    foreach (JsonElement error in errorsElement.EnumerateArray())
                    {
                        if (error.ValueKind == JsonValueKind.String)
                            status.Errors.Add(error.GetString() ?? string.Empty);
                    }
                }

                return status;
            }
            catch
            {
                return new RuffleStatus();
            }
        }

        private static bool GetBool(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.True;

        private static int GetInt32(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.TryGetInt32(out int result) ? result : 0;

        private static long GetInt64(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.TryGetInt64(out long result) ? result : 0L;

        private static double GetDouble(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.TryGetDouble(out double result) ? result : 0d;

        private static string GetString(JsonElement root, string name) =>
            root.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
    }
}
