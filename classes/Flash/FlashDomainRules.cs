using MyHomelabBrowser.classes;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MyHomelabBrowser.classes.Flash
{
    public enum FlashRuleMode
    {
        Auto,      // décision automatique
        Ruffle,    // forcer ruffle
        Legacy,    // forcer basilisk
        Disabled   // désactiver flash pour ce domaine
    }

    public static class FlashDomainRules
    {
        static string RulesPath =>
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "MyHomelabBrowser",
                "flash-rules.json");

        static Dictionary<string, FlashRuleMode> _rules = Load();

        static Dictionary<string, FlashRuleMode> Load()
        {
            try
            {
                if (!File.Exists(RulesPath))
                    return new();

                var json = File.ReadAllText(RulesPath);
                return JsonSerializer.Deserialize<Dictionary<string, FlashRuleMode>>(json)
                       ?? new();
            }
            catch
            {
                return new();
            }
        }

        static void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(RulesPath)!);
                File.WriteAllText(
                    RulesPath,
                    JsonSerializer.Serialize(_rules, new JsonSerializerOptions { WriteIndented = true })
                );
            }
            catch { }
        }

        public static FlashRuleMode GetRule(Uri uri)
        {
            var host = uri.Host.ToLowerInvariant();
            return _rules.TryGetValue(host, out var mode)
                ? mode
                : FlashRuleMode.Auto;
        }

        public static void SetRule(Uri uri, FlashRuleMode mode)
        {
            _rules[uri.Host.ToLowerInvariant()] = mode;
            Save();
        }

        public static void RemoveRule(Uri uri)
        {
            if (_rules.Remove(uri.Host.ToLowerInvariant()))
                Save();
        }

        public static IReadOnlyDictionary<string, FlashRuleMode> GetAll()
            => _rules;
    }
}
