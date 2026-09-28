using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MyHomelabBrowser.classes;

namespace PommeBrowser.Linux.Core
{
    public sealed class SessionTab
    {
        public string Url { get; set; } = string.Empty;
        public string? Title { get; set; }
    }

    public sealed class SessionState
    {
        public List<SessionTab> Tabs { get; set; } = new();
        public int Selected { get; set; }
    }

    /// <summary>Onglets ouverts à la fermeture, rouverts au démarrage suivant.</summary>
    public static class SessionStore
    {
        public const int MaxTabs = 100;

        static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

        /// <summary>Seules les pages web sont reprises (pas les pages d'erreur ni about:blank).</summary>
        public static bool IsRestorable(string? url)
            => Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeFile);

        public static SessionState Load(string path)
        {
            try
            {
                if (File.Exists(path))
                {
                    SessionState? state = JsonSerializer.Deserialize<SessionState>(File.ReadAllText(path), JsonOptions);
                    if (state != null)
                        return Clean(state);
                }
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
            }
            return new SessionState();
        }

        public static void Save(string path, SessionState state)
        {
            try
            {
                AtomicFile.WriteAllText(path, JsonSerializer.Serialize(Clean(state), JsonOptions));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        static SessionState Clean(SessionState state)
        {
            var tabs = (state.Tabs ?? new List<SessionTab>())
                .Where(t => t != null && IsRestorable(t.Url))
                .Take(MaxTabs)
                .ToList();

            return new SessionState
            {
                Tabs = tabs,
                Selected = tabs.Count == 0 ? 0 : Math.Clamp(state.Selected, 0, tabs.Count - 1)
            };
        }
    }
}
