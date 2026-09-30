using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Session;
using PommeBrowser.Core;
using PommeBrowser.Linux.Core;
using PommeBrowser.Views;

namespace PommeBrowser
{
    public sealed partial class BrowserApp
    {
        static readonly JsonSerializerOptions SessionJson = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

        /// <summary>
        /// Onglets de toutes les fenêtres normales (jamais ceux de la navigation privée), dans
        /// le format de l'édition Windows ; l'onglet actif de la dernière fenêtre est retenu.
        /// </summary>
        public void SaveSession(bool isUpdateRestart = false)
        {
            var state = new BrowserSessionState { IsUpdateRestart = isUpdateRestart };
            MainWindow? active = ActiveWindow;
            foreach (MainWindow window in _windows)
            {
                foreach (BrowserTab tab in window.Tabs)
                {
                    if (tab.GetSessionState() is not { } saved)
                        continue;
                    if (window == active && tab == window.SelectedTab)
                        state.SelectedIndex = state.Tabs.Count;
                    state.Tabs.Add(saved);
                }
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(AppPaths.SessionFile)!);
                AtomicFile.WriteAllText(AppPaths.SessionFile, JsonSerializer.Serialize(state, SessionJson));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                RuntimeLogBuffer.Append("[Session] " + ex.Message);
            }
        }

        /// <summary>Session précédente (format Windows, ou celui de l'édition GTK).</summary>
        public static BrowserSessionState LoadSession(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return new BrowserSessionState();

                using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
                BrowserSessionState state = document.Deserialize<BrowserSessionState>(SessionJson) ?? new BrowserSessionState();
                // Édition GTK : l'onglet actif s'appelait « Selected ».
                if (document.RootElement.TryGetProperty("Selected", out JsonElement selected) && selected.TryGetInt32(out int index))
                    state.SelectedIndex = index;

                state.Tabs = state.Tabs.Where(t => SessionStore.IsRestorable(t.IsLegacy && !string.IsNullOrWhiteSpace(t.LegacyUrl) ? t.LegacyUrl : t.Url))
                    .Take(SessionStore.MaxTabs)
                    .ToList();
                state.SelectedIndex = Math.Clamp(state.SelectedIndex, 0, Math.Max(0, state.Tabs.Count - 1));
                return state;
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                RuntimeLogBuffer.Append("[Session] " + ex.Message);
                return new BrowserSessionState();
            }
        }

        /// <summary>
        /// Démarrage : onglets de la dernière session, page personnalisée ou onglet vide selon
        /// les réglages ; les adresses passées en ligne de commande s'ouvrent en plus.
        /// </summary>
        void RestoreSession(MainWindow window, IReadOnlyList<string> urls)
        {
            BrowserSessionState state = LoadSession(AppPaths.SessionFile);
            bool restore = Settings.Startup == BrowserSettings.StartupMode.RestoreSession || state.IsUpdateRestart;

            if (restore && state.Tabs.Count > 0)
            {
                for (int i = 0; i < state.Tabs.Count; i++)
                    window.RestoreTab(state.Tabs[i], select: i == state.SelectedIndex);
            }
            else if (Settings.Startup == BrowserSettings.StartupMode.CustomPage && UrlResolver.TryResolveUrl(Settings.CustomStartupPage) is { } custom)
            {
                window.NewTab(custom, select: true);
            }

            foreach (string url in urls)
            {
                if (UrlResolver.TryResolveUrl(url) is { } resolved)
                    window.NewTab(resolved, select: true);
            }

            if (window.Tabs.Count == 0)
                window.NewTab(null, select: true);
        }
    }
}
