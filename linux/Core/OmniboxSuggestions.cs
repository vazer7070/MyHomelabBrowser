using System;
using System.Collections.Generic;
using System.Linq;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Homelab;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Core
{
    public enum SuggestionKind
    {
        Search,
        Address,
        Service,
        Favorite,
        History
    }

    public sealed record Suggestion(SuggestionKind Kind, string Title, string Url);

    /// <summary>
    /// Propositions de la barre d'adresse : d'abord l'adresse ou la recherche saisie, puis les
    /// services du homelab, les favoris et l'historique (les pages les plus visitées en tête).
    /// </summary>
    public static class OmniboxSuggestions
    {
        public static IReadOnlyList<Suggestion> Build(
            string input,
            BrowserSettings.SearchEngine engine,
            IEnumerable<HomelabService> services,
            IEnumerable<FavoriteItem> favorites,
            IReadOnlyList<HistoryEntry> history,
            int max = 8)
        {
            string text = (input ?? string.Empty).Trim();
            if (text.Length == 0)
                return Array.Empty<Suggestion>();

            var results = new List<Suggestion>();
            var seen = new HashSet<string>(StringComparer.Ordinal);

            string? url = UrlResolver.TryResolveUrl(text);
            results.Add(url != null
                ? new Suggestion(SuggestionKind.Address, url, url)
                : new Suggestion(SuggestionKind.Search,
                    Tr("Rechercher « {0} » avec {1}", text, UrlResolver.GetSearchEngineName(engine)),
                    UrlResolver.BuildSearchUrl(text, engine)));
            seen.Add(FavoritesStore.Key(results[0].Url));

            string[] tokens = text.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            void Add(SuggestionKind kind, string title, string address)
            {
                if (results.Count < max && seen.Add(FavoritesStore.Key(address)))
                    results.Add(new Suggestion(kind, string.IsNullOrWhiteSpace(title) ? address : title, address));
            }

            foreach (HomelabService service in services.Where(s => Matches(tokens, s.Name, s.Url)))
                Add(SuggestionKind.Service, service.Name, service.Url);

            foreach (FavoriteItem favorite in favorites.Where(f => Matches(tokens, f.Title, f.Url)))
                Add(SuggestionKind.Favorite, favorite.Title, favorite.Url);

            // Historique : les adresses les plus visitées d'abord, à égalité la plus récente.
            var visits = new Dictionary<string, (HistoryEntry Latest, int Count)>(StringComparer.Ordinal);
            for (int i = history.Count - 1; i >= 0; i--)
            {
                HistoryEntry entry = history[i];
                if (!Matches(tokens, entry.Title, entry.Url))
                    continue;

                visits[entry.Url] = visits.TryGetValue(entry.Url, out var known)
                    ? (known.Latest, known.Count + 1)
                    : (entry, 1);
            }

            foreach (var (latest, _) in visits.Values
                         .OrderByDescending(v => v.Count)
                         .ThenByDescending(v => v.Latest.VisitedAt))
            {
                Add(SuggestionKind.History, latest.Title, latest.Url);
            }

            return results;
        }

        static bool Matches(string[] tokens, string? title, string? url)
        {
            string haystack = ((title ?? string.Empty) + " " + (url ?? string.Empty)).ToLowerInvariant();
            foreach (string token in tokens)
            {
                if (!haystack.Contains(token, StringComparison.Ordinal))
                    return false;
            }
            return true;
        }
    }
}
