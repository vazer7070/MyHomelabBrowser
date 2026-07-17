using MyHomelabBrowser.classes.Profiles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyHomelabBrowser.classes.Flash
{
    /// <summary>
    /// Mémoire de compatibilité par contenu SWF. Elle ne contient aucune règle de site :
    /// une préférence Legacy n'est apprise qu'après plusieurs échecs Ruffle sur le même
    /// contenu et au moins un lancement Legacy réussi.
    /// </summary>
    public static class FlashCompatibilityMemory
    {
        private sealed class Entry
        {
            public Entry() { }

            public string Key { get; set; } = string.Empty;
            public string DisplaySource { get; set; } = string.Empty;
            public int RuffleSuccessCount { get; set; }
            public int RuffleFailureCount { get; set; }
            public int LegacySuccessCount { get; set; }
            public DateTime? LastRuffleSuccessUtc { get; set; }
            public DateTime? LastRuffleFailureUtc { get; set; }
            public DateTime? LastLegacySuccessUtc { get; set; }
            public string? LastFailureReason { get; set; }
            public DateTime UpdatedUtc { get; set; }
        }

        private static readonly object Sync = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private static string? _loadedPath;
        private static Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

        private static string CachePath =>
            Path.Combine(AppDataContext.Root, "flash", "compatibility-cache.json");

        public static FlashMode GetRecommendedMode(
            Uri pageUri,
            FlashDetectionResult detection,
            bool legacyAvailable)
        {
            if (!legacyAvailable || !detection.Detected)
                return FlashMode.Ruffle;

            lock (Sync)
            {
                EnsureLoaded();
                string key = BuildKey(pageUri, detection);
                if (!_entries.TryGetValue(key, out Entry? entry))
                    return FlashMode.Ruffle;

                bool recent = entry.UpdatedUtc >= DateTime.UtcNow.AddDays(-90);
                bool legacyProven = entry.LegacySuccessCount > 0 && entry.LastLegacySuccessUtc.HasValue;
                bool repeatedRuffleFailure = entry.RuffleFailureCount >= 2;
                bool newerThanRuffleSuccess = !entry.LastRuffleSuccessUtc.HasValue ||
                    entry.LastLegacySuccessUtc > entry.LastRuffleSuccessUtc;

                return recent && legacyProven && repeatedRuffleFailure && newerThanRuffleSuccess
                    ? FlashMode.Legacy
                    : FlashMode.Ruffle;
            }
        }

        public static void RecordRuffleSuccess(Uri pageUri, FlashDetectionResult detection)
        {
            Update(pageUri, detection, entry =>
            {
                entry.RuffleSuccessCount++;
                entry.RuffleFailureCount = 0;
                entry.LastRuffleSuccessUtc = DateTime.UtcNow;
                entry.LastFailureReason = null;
            });
        }

        public static void RecordRuffleFailure(
            Uri pageUri,
            FlashDetectionResult detection,
            string? reason)
        {
            Update(pageUri, detection, entry =>
            {
                entry.RuffleFailureCount++;
                entry.LastRuffleFailureUtc = DateTime.UtcNow;
                entry.LastFailureReason = Trim(reason, 500);
            });
        }

        public static void RecordLegacySuccess(Uri pageUri, FlashDetectionResult detection)
        {
            Update(pageUri, detection, entry =>
            {
                entry.LegacySuccessCount++;
                entry.LastLegacySuccessUtc = DateTime.UtcNow;
            });
        }

        public static void ReloadForCurrentProfile()
        {
            lock (Sync)
            {
                _loadedPath = null;
                _entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
                EnsureLoaded();
            }
        }

        public static void ClearForCurrentProfile()
        {
            lock (Sync)
            {
                string path = CachePath;
                _entries.Clear();
                _loadedPath = path;
                try
                {
                    if (File.Exists(path))
                        File.Delete(path);
                    if (File.Exists(path + ".bak"))
                        File.Delete(path + ".bak");
                    if (File.Exists(path + ".tmp"))
                        File.Delete(path + ".tmp");
                }
                catch (Exception ex)
                {
                    FlashDebugConsole.Log("FlashCompatibilityMemory clear failed: " + ex.Message);
                }
            }
        }

        private static void Update(
            Uri pageUri,
            FlashDetectionResult detection,
            Action<Entry> mutation)
        {
            if (!detection.Detected)
                return;

            lock (Sync)
            {
                EnsureLoaded();
                string key = BuildKey(pageUri, detection);
                if (!_entries.TryGetValue(key, out Entry? entry))
                {
                    entry = new Entry
                    {
                        Key = key,
                        DisplaySource = BuildDisplaySource(pageUri, detection)
                    };
                    _entries[key] = entry;
                }

                mutation(entry);
                entry.UpdatedUtc = DateTime.UtcNow;
                Prune();
                SaveAtomic();
            }
        }

        private static string BuildKey(Uri pageUri, FlashDetectionResult detection)
        {
            string identity = !string.IsNullOrWhiteSpace(detection.SourceUrl)
                ? NormalizeUrl(detection.SourceUrl!)
                : $"{pageUri.Scheme}://{pageUri.Authority}{pageUri.AbsolutePath}|{detection.Evidence}|{detection.TargetElement}";

            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(identity));
            return Convert.ToHexString(hash);
        }

        private static string BuildDisplaySource(Uri pageUri, FlashDetectionResult detection) =>
            !string.IsNullOrWhiteSpace(detection.SourceUrl)
                ? Trim(detection.SourceUrl, 500) ?? string.Empty
                : $"{pageUri.GetLeftPart(UriPartial.Path)} [{detection.Evidence}]";

        private static string NormalizeUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                return value.Trim();

            var builder = new UriBuilder(uri)
            {
                Fragment = string.Empty
            };
            return builder.Uri.AbsoluteUri;
        }

        private static void EnsureLoaded()
        {
            string path = CachePath;
            if (string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase))
                return;

            _loadedPath = path;
            _entries = Load(path);
        }

        private static Dictionary<string, Entry> Load(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return new Dictionary<string, Entry>(StringComparer.Ordinal);

                List<Entry>? list = JsonSerializer.Deserialize<List<Entry>>(
                    File.ReadAllText(path), JsonOptions);

                return (list ?? new List<Entry>())
                    .Where(entry => !string.IsNullOrWhiteSpace(entry.Key))
                    .GroupBy(entry => entry.Key, StringComparer.Ordinal)
                    .ToDictionary(group => group.Key, group => group.OrderByDescending(x => x.UpdatedUtc).First(), StringComparer.Ordinal);
            }
            catch (Exception ex)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        string corrupt = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                        File.Move(path, corrupt, overwrite: false);
                    }
                }
                catch { }

                FlashDebugConsole.Log("FlashCompatibilityMemory load failed: " + ex.Message);
                return new Dictionary<string, Entry>(StringComparer.Ordinal);
            }
        }

        private static void Prune()
        {
            DateTime limit = DateTime.UtcNow.AddDays(-180);
            foreach (string key in _entries.Values
                         .Where(entry => entry.UpdatedUtc < limit)
                         .Select(entry => entry.Key)
                         .ToList())
            {
                _entries.Remove(key);
            }

            if (_entries.Count <= 500)
                return;

            foreach (string key in _entries.Values
                         .OrderBy(entry => entry.UpdatedUtc)
                         .Take(_entries.Count - 500)
                         .Select(entry => entry.Key)
                         .ToList())
            {
                _entries.Remove(key);
            }
        }

        private static void SaveAtomic()
        {
            string path = CachePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            string temp = path + ".tmp";
            string backup = path + ".bak";
            string json = JsonSerializer.Serialize(
                _entries.Values.OrderByDescending(entry => entry.UpdatedUtc).ToList(),
                JsonOptions);

            File.WriteAllText(temp, json);
            if (File.Exists(path))
                File.Replace(temp, path, backup, ignoreMetadataErrors: true);
            else
                File.Move(temp, path);
        }

        private static string? Trim(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return value;

            string normalized = value.Trim();
            return normalized.Length <= maxLength
                ? normalized
                : normalized[..maxLength];
        }
    }
}
