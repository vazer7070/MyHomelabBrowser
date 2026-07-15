using MyHomelabBrowser.classes.CloudTorrent.Models;
using MyHomelabBrowser.classes.Profiles;
using System;
using System.IO;
using System.Text.Json;

namespace MyHomelabBrowser.classes.CloudTorrent.Services
{
    public sealed class CloudTorrentConfigurationService
    {
        private const string ConfigurationFileName = "cloudtorrent.json";

        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = true,
            PropertyNameCaseInsensitive = true
        };

        private readonly CloudTorrentSecureStorage _secureStorage;
        private string _loadedProfileRoot = string.Empty;

        public CloudTorrentConfigurationService(CloudTorrentSecureStorage? secureStorage = null)
        {
            _secureStorage = secureStorage ?? new CloudTorrentSecureStorage();
            Current = LoadFromCurrentProfile();
            _loadedProfileRoot = AppDataContext.Root;
        }

        private CloudTorrentConfiguration _current = new();

        public CloudTorrentConfiguration Current
        {
            get
            {
                EnsureCurrentProfile();
                return _current;
            }
            private set => _current = value;
        }

        public bool HasStoredApiKey
        {
            get
            {
                EnsureCurrentProfile();
                return _secureStorage.HasSecret;
            }
        }

        private string ConfigurationPath => AppDataContext.GetPath(ConfigurationFileName);

        public string? ReadApiKey()
        {
            EnsureCurrentProfile();
            return _secureStorage.ReadApiKey();
        }

        public void ReloadForCurrentProfile()
        {
            Current = LoadFromCurrentProfile();
            _loadedProfileRoot = AppDataContext.Root;
        }

        public void Save(CloudTorrentConfiguration configuration, string apiKey)
        {
            EnsureCurrentProfile();
            ArgumentNullException.ThrowIfNull(configuration);

            var normalized = configuration.Clone();
            normalized.ServerUrl = CloudTorrentUrl.NormalizeServerUrl(normalized.ServerUrl);

            string path = ConfigurationPath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            string temporaryPath = path + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(normalized, JsonOptions));
            File.Move(temporaryPath, path, overwrite: true);

            _secureStorage.WriteApiKey(apiKey);
            Current = normalized;
        }

        public void Clear()
        {
            EnsureCurrentProfile();
            string path = ConfigurationPath;
            if (File.Exists(path))
                File.Delete(path);

            _secureStorage.DeleteApiKey();
            Current = new CloudTorrentConfiguration();
        }

        private void EnsureCurrentProfile()
        {
            string currentRoot = AppDataContext.Root;
            if (string.Equals(_loadedProfileRoot, currentRoot, StringComparison.OrdinalIgnoreCase))
                return;

            Current = LoadFromCurrentProfile();
            _loadedProfileRoot = currentRoot;
        }

        private CloudTorrentConfiguration LoadFromCurrentProfile()
        {
            string path = ConfigurationPath;
            if (!File.Exists(path))
                return new CloudTorrentConfiguration();

            try
            {
                string json = File.ReadAllText(path);
                var configuration = JsonSerializer.Deserialize<CloudTorrentConfiguration>(json, JsonOptions)
                    ?? new CloudTorrentConfiguration();

                if (!string.IsNullOrWhiteSpace(configuration.ServerUrl))
                    configuration.ServerUrl = CloudTorrentUrl.NormalizeServerUrl(configuration.ServerUrl);

                return configuration;
            }
            catch
            {
                TryBackupBrokenFile(path);
                return new CloudTorrentConfiguration();
            }
        }

        private static void TryBackupBrokenFile(string path)
        {
            try
            {
                string backup = path + ".invalid-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
                File.Move(path, backup, overwrite: false);
            }
            catch
            {
            }
        }
    }
}
