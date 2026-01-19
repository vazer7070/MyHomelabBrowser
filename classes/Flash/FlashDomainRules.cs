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
            if (uri == null)
                return FlashRuleMode.Auto;

            var host = uri.Host?.Trim().ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(host))
                return FlashRuleMode.Auto;

            // ✅ 1) match exact
            if (_rules.TryGetValue(host, out var direct))
                return direct;

            // ✅ 2) match parent domains (sous-domaines)
            // ex: play13.ministryofwar.com -> ministryofwar.com
            var parts = host.Split('.');
            if (parts.Length < 2)
                return FlashRuleMode.Auto;

            // On enlève les labels de gauche 1 par 1
            for (int i = 1; i <= parts.Length - 2; i++)
            {
                var parent = string.Join(".", parts, i, parts.Length - i);

                if (_rules.TryGetValue(parent, out var inherited))
                    return inherited;
            }

            return FlashRuleMode.Auto;
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
