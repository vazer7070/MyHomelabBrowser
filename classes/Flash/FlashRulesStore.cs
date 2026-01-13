using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace MyHomelabBrowser.classes.Flash
{
    public sealed class FlashRulesStore
    {
        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        public string RulesPath { get; }

        public List<FlashRule> Rules { get; private set; } = new();

        public FlashRulesStore(string appDataDir)
        {
            Directory.CreateDirectory(appDataDir);
            RulesPath = Path.Combine(appDataDir, "flash-rules.json");
        }

        public void LoadOrCreateDefaults()
        {
            try
            {
                if (!File.Exists(RulesPath))
                {
                    Rules = new List<FlashRule>
                    {
                        new() { Domain = "ministryofwar.com", Mode = FlashMode.Legacy, Enabled = true },
                        new() { Domain = "kabam.com",        Mode = FlashMode.Legacy, Enabled = true },
                        new() { Domain = "armor-games.com",  Mode = FlashMode.Ruffle, Enabled = true },
                        new() { Domain = "newgrounds.com",   Mode = FlashMode.Ruffle, Enabled = true }
                    };
                    Save();
                    return;
                }

                var json = File.ReadAllText(RulesPath);
                Rules = JsonSerializer.Deserialize<List<FlashRule>>(json, JsonOpts) ?? new List<FlashRule>();
            }
            catch
            {
                Rules = new List<FlashRule>();
            }
        }

        public void Save()
        {
            try
            {
                var json = JsonSerializer.Serialize(Rules, JsonOpts);
                File.WriteAllText(RulesPath, json);
            }
            catch { }
        }
    }
}
