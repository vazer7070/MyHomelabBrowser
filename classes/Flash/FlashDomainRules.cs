using MyHomelabBrowser.classes.Profiles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MyHomelabBrowser.classes.Flash
{
    public enum FlashRuleMode
    {
        Auto,
        Ruffle,
        Legacy,
        Disabled
    }

    public static class FlashDomainRules
    {
        private static readonly object Sync = new();
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private static string? _loadedPath;
        private static Dictionary<string, FlashRuleMode> _rules = new(StringComparer.OrdinalIgnoreCase);

        private static string RulesPath =>
            Path.Combine(AppDataContext.Root, "flash", "flash-rules.json");

        public static FlashRuleMode GetRule(Uri? uri)
        {
            if (uri == null || string.IsNullOrWhiteSpace(uri.Host))
                return FlashRuleMode.Auto;

            lock (Sync)
            {
                EnsureLoaded();
                string host = NormalizeHost(uri.Host);

                if (_rules.TryGetValue(host, out FlashRuleMode direct))
                    return direct;

                string[] parts = host.Split('.', StringSplitOptions.RemoveEmptyEntries);
                for (int i = 1; i <= parts.Length - 2; i++)
                {
                    string parent = string.Join(".", parts, i, parts.Length - i);
                    if (_rules.TryGetValue(parent, out FlashRuleMode inherited))
                        return inherited;
                }

                return FlashRuleMode.Auto;
            }
        }

        public static void SetRule(Uri uri, FlashRuleMode mode)
        {
            ArgumentNullException.ThrowIfNull(uri);

            lock (Sync)
            {
                EnsureLoaded();
                string host = NormalizeHost(uri.Host);

                if (mode == FlashRuleMode.Auto)
                    _rules.Remove(host);
                else
                    _rules[host] = mode;

                SaveAtomic();
            }
        }

        public static void RemoveRule(Uri uri)
        {
            ArgumentNullException.ThrowIfNull(uri);

            lock (Sync)
            {
                EnsureLoaded();
                if (_rules.Remove(NormalizeHost(uri.Host)))
                    SaveAtomic();
            }
        }

        public static IReadOnlyDictionary<string, FlashRuleMode> GetAll()
        {
            lock (Sync)
            {
                EnsureLoaded();
                return new Dictionary<string, FlashRuleMode>(_rules, StringComparer.OrdinalIgnoreCase);
            }
        }

        public static void ReloadForCurrentProfile()
        {
            lock (Sync)
            {
                _loadedPath = null;
                _rules = new Dictionary<string, FlashRuleMode>(StringComparer.OrdinalIgnoreCase);
                EnsureLoaded();
            }
        }

        private static void EnsureLoaded()
        {
            string path = RulesPath;
            if (string.Equals(_loadedPath, path, StringComparison.OrdinalIgnoreCase))
                return;

            _loadedPath = path;
            _rules = Load(path);
        }

        private static Dictionary<string, FlashRuleMode> Load(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return new Dictionary<string, FlashRuleMode>(StringComparer.OrdinalIgnoreCase);

                string json = File.ReadAllText(path);
                Dictionary<string, FlashRuleMode>? loaded =
                    JsonSerializer.Deserialize<Dictionary<string, FlashRuleMode>>(json, JsonOptions);

                return loaded == null
                    ? new Dictionary<string, FlashRuleMode>(StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, FlashRuleMode>(loaded, StringComparer.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                try
                {
                    string corruptPath = path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");
                    if (File.Exists(path))
                        File.Move(path, corruptPath, overwrite: false);
                }
                catch { }

                FlashDebugConsole.Log("FlashDomainRules load failed: " + ex.Message);
                return new Dictionary<string, FlashRuleMode>(StringComparer.OrdinalIgnoreCase);
            }
        }

        private static void SaveAtomic()
        {
            string path = RulesPath;
            string directory = Path.GetDirectoryName(path)!;
            Directory.CreateDirectory(directory);

            string tempPath = path + ".tmp";
            string backupPath = path + ".bak";
            string json = JsonSerializer.Serialize(_rules, JsonOptions);

            File.WriteAllText(tempPath, json);

            if (File.Exists(path))
                File.Replace(tempPath, path, backupPath, ignoreMetadataErrors: true);
            else
                File.Move(tempPath, path);
        }

        private static string NormalizeHost(string host) =>
            host.Trim().TrimEnd('.').ToLowerInvariant();
    }
}
