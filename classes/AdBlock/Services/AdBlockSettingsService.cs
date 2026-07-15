using MyHomelabBrowser.classes.AdBlock.Models;
using MyHomelabBrowser.classes.Profiles;
using System;
using System.IO;
using System.Text.Json;

namespace MyHomelabBrowser.classes.AdBlock.Services
{
    public sealed class AdBlockSettingsService
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly object _gate = new();
        private string _loadedRoot = string.Empty;
        private AdBlockSettings _settings = new();

        public event Action<AdBlockSettings>? SettingsChanged;

        public AdBlockSettings Current
        {
            get
            {
                EnsureCurrentProfile();
                lock (_gate)
                    return _settings.Clone();
            }
        }

        public AdBlockSettingsService()
        {
            ReloadForCurrentProfile();
        }

        public void ReloadForCurrentProfile()
        {
            string root = AppDataContext.Root;
            AdBlockSettings loaded = LoadFromDisk(root);

            lock (_gate)
            {
                _loadedRoot = root;
                _settings = loaded;
            }

            SettingsChanged?.Invoke(loaded.Clone());
        }

        public void Save(AdBlockSettings settings)
        {
            settings.Normalize();
            string root = AppDataContext.Root;
            string path = GetSettingsPath(root);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            string temporaryPath = path + ".tmp";
            string json = JsonSerializer.Serialize(settings, JsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, path, overwrite: true);

            lock (_gate)
            {
                _loadedRoot = root;
                _settings = settings.Clone();
            }

            SettingsChanged?.Invoke(settings.Clone());
        }

        public void Update(Action<AdBlockSettings> update)
        {
            AdBlockSettings working = Current;
            update(working);
            Save(working);
        }

        public string GetProfileDirectory()
        {
            EnsureCurrentProfile();
            return Path.Combine(AppDataContext.Root, "AdBlock");
        }

        private void EnsureCurrentProfile()
        {
            if (!_loadedRoot.Equals(AppDataContext.Root, StringComparison.OrdinalIgnoreCase))
                ReloadForCurrentProfile();
        }

        private static AdBlockSettings LoadFromDisk(string root)
        {
            string path = GetSettingsPath(root);
            if (!File.Exists(path))
            {
                var defaults = new AdBlockSettings();
                defaults.Normalize();
                return defaults;
            }

            try
            {
                string json = File.ReadAllText(path);
                AdBlockSettings settings = JsonSerializer.Deserialize<AdBlockSettings>(json, JsonOptions) ?? new AdBlockSettings();
                settings.Normalize();
                return settings;
            }
            catch
            {
                try
                {
                    string corruptPath = path + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                    File.Move(path, corruptPath, overwrite: true);
                }
                catch { }

                var defaults = new AdBlockSettings();
                defaults.Normalize();
                return defaults;
            }
        }

        private static string GetSettingsPath(string root)
            => Path.Combine(root, "AdBlock", "adblock.json");
    }
}
