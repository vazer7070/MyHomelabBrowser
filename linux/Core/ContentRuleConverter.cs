using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace PommeBrowser.Linux.Core
{
    /// <summary>Règles WebKit produites à partir des listes de filtres.</summary>
    public sealed record ContentRuleSet(string Json, int RuleCount, int NetworkFilters, int CosmeticFilters, int SkippedFilters);

    /// <summary>
    /// Convertit les listes au format Adblock Plus (EasyList, EasyPrivacy…) en règles de
    /// blocage de contenu WebKit. WebKit compile ces règles une fois, puis les applique
    /// lui-même à chaque requête : aucune requête ne repasse par l'application.
    ///
    /// Ordre des règles (une règle « ignore-previous-rules » n'annule que celles qui la précèdent) :
    /// masquage générique, exceptions $generichide, blocages réseau, masquage propre à un site,
    /// exceptions réseau, puis sites exemptés ($document).
    /// Les filtres que WebKit ne sait pas exprimer sont ignorés plutôt qu'approximés.
    /// </summary>
    public static class ContentRuleConverter
    {
        /// <summary>À incrémenter quand le résultat change : les règles déjà compilées sont alors refaites.</summary>
        public const int FormatVersion = 1;

        // Un sélecteur par règle : WebKit écarte une règle dont le sélecteur est invalide,
        // et avec plusieurs sélecteurs il écarterait aussi tous les autres.

        // Classes de caractères positives : les classes négatives ([^…]) rendent la
        // compilation par WebKit plusieurs fois plus lente et plus volumineuse.
        const string Separator = "[/:?&=;,!'()*+@~]";
        const string HostSeparator = "[/:]";
        const string HostPrefix = "^[a-z]+://([a-z0-9_.-]*\\.)?";

        static readonly string[] UnsupportedSelectorTokens =
        {
            ":-abp-", ":has-text(", ":matches-css", ":matches-path(", ":matches-attr(", ":matches-prop(",
            ":xpath(", ":upward(", ":remove(", ":style(", ":watch-attr(", ":others(", ":min-text-length(",
            ":contains(", ":if(", ":if-not(", ":nth-ancestor(", ":remove-attr(", ":remove-class(", ":shadow("
        };

        [Flags]
        enum ResourceKinds
        {
            None = 0,
            Document = 1,
            Subdocument = 1 << 1,
            Script = 1 << 2,
            Image = 1 << 3,
            Stylesheet = 1 << 4,
            Font = 1 << 5,
            Media = 1 << 6,
            Fetch = 1 << 7,
            WebSocket = 1 << 8,
            Ping = 1 << 9,
            Other = 1 << 10,
            Popup = 1 << 11,

            // Types visés par un filtre sans option de type (comme Adblock Plus : ni page, ni fenêtre surgissante).
            Default = Subdocument | Script | Image | Stylesheet | Font | Media | Fetch | WebSocket | Ping | Other
        }

        sealed class Trigger
        {
            public required string UrlFilter { get; init; }
            public bool CaseSensitive { get; init; }
            public IReadOnlyList<string>? IfDomain { get; init; }
            public IReadOnlyList<string>? UnlessDomain { get; init; }
            public IReadOnlyList<string>? ResourceTypes { get; init; }
            public string? LoadType { get; init; }
            public string? LoadContext { get; init; }
        }

        sealed record Rule(Trigger Trigger, string Action, string? Selector = null);

        sealed class NetworkFilter
        {
            public required string UrlFilter { get; init; }
            public bool CaseSensitive { get; init; }
            public List<string> IncludedDomains { get; } = new();
            public List<string> ExcludedDomains { get; } = new();
            public ResourceKinds Kinds { get; set; }
            public string? LoadType { get; set; }
        }

        public static ContentRuleSet Convert(IEnumerable<(string SourceName, string Content)> sources, bool cosmeticFiltering = true)
        {
            var state = new ConversionState();

            foreach ((string _, string content) in sources)
            {
                using var reader = new StringReader(content ?? string.Empty);
                string? line;
                while ((line = reader.ReadLine()) != null)
                    state.AddLine(line.Trim(), cosmeticFiltering);
            }

            List<Rule> rules = state.BuildRules();
            return new ContentRuleSet(Serialize(rules), rules.Count, state.NetworkFilters, state.CosmeticFilters, state.Skipped);
        }

        /// <summary>
        /// Motif Adblock Plus (sans options) vers l'expression reconnue par WebKit,
        /// ou null s'il ne peut pas être exprimé.
        /// </summary>
        public static string? ConvertPattern(string pattern)
        {
            if (pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/')
                return ConvertRegex(pattern[1..^1]);

            bool hostAnchor = pattern.StartsWith("||", StringComparison.Ordinal);
            bool startAnchor = !hostAnchor && pattern.StartsWith('|');
            string body = hostAnchor ? pattern[2..] : startAnchor ? pattern[1..] : pattern;

            bool endAnchor = body.EndsWith('|');
            if (endAnchor)
                body = body[..^1];

            if (!hostAnchor && !startAnchor)
                body = body.TrimStart('*');
            if (!endAnchor)
                body = body.TrimEnd('*');

            if (body.Length == 0)
                return hostAnchor || startAnchor || endAnchor ? null : ".*";

            var builder = new StringBuilder(body.Length + 48);
            if (hostAnchor)
                builder.Append(HostPrefix);
            else if (startAnchor)
                builder.Append('^');

            // « ||hôte^ » : dans une adresse normalisée, l'hôte est suivi de « / » ou du port.
            bool inHost = hostAnchor;

            for (int i = 0; i < body.Length; i++)
            {
                char c = body[i];
                if (c > 127 || char.IsControl(c))
                    return null;

                if (inHost && !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
                {
                    inHost = false;
                    if (c == '^')
                    {
                        builder.Append(HostSeparator);
                        continue;
                    }
                }

                switch (c)
                {
                    case '*':
                        if (!EndsWith(builder, ".*"))
                            builder.Append(".*");
                        break;
                    case '^':
                        // En fin de motif, le séparateur correspond aussi à la fin de l'adresse.
                        if (i == body.Length - 1 && !endAnchor)
                        {
                            builder.Append('(').Append(Separator).Append(".*)?$");
                            return builder.ToString();
                        }
                        builder.Append(Separator);
                        break;
                    case '.' or '\\' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '$' or '|':
                        builder.Append('\\').Append(c);
                        break;
                    default:
                        builder.Append(c);
                        break;
                }
            }

            if (endAnchor)
                builder.Append('$');

            return builder.ToString();
        }

        /// <summary>
        /// Expression /.../ d'une liste : acceptée seulement si elle reste dans le sous-ensemble
        /// de WebKit (ni alternative « | », ni quantificateur « {n} », ni classes \d \w \s…).
        /// </summary>
        static string? ConvertRegex(string regex)
        {
            var builder = new StringBuilder(regex.Length);
            bool inClass = false;

            for (int i = 0; i < regex.Length; i++)
            {
                char c = regex[i];
                if (c > 127 || char.IsControl(c))
                    return null;

                if (c == '\\')
                {
                    if (i + 1 >= regex.Length)
                        return null;

                    char next = regex[++i];
                    if (char.IsLetterOrDigit(next) || next > 127)
                        return null;

                    // « \/ » n'a pas besoin d'échappement.
                    if (next == '/')
                        builder.Append('/');
                    else
                        builder.Append('\\').Append(next);
                    continue;
                }

                if (inClass)
                {
                    if (c == ']')
                        inClass = false;
                    builder.Append(c);
                    continue;
                }

                switch (c)
                {
                    case '[':
                        inClass = true;
                        break;
                    case '|' or '{' or '}':
                        return null;
                    case '(' when i + 1 < regex.Length && regex[i + 1] == '?':
                        return null;
                    case '^' when i != 0:
                        return null;
                    case '$' when i != regex.Length - 1:
                        return null;
                }

                builder.Append(c);
            }

            return inClass || builder.Length == 0 ? null : builder.ToString();
        }

        static bool EndsWith(StringBuilder builder, string value)
        {
            if (builder.Length < value.Length)
                return false;
            for (int i = 0; i < value.Length; i++)
            {
                if (builder[builder.Length - value.Length + i] != value[i])
                    return false;
            }
            return true;
        }

        /// <summary>
        /// Liste de domaines d'une règle, au format WebKit (« *exemple.fr » : le domaine et ses sous-domaines).
        /// Faux si un domaine ne peut pas être exprimé (joker « exemple.* », caractères invalides).
        /// </summary>
        static bool ParseDomains(string list, char separator, List<string> included, List<string> excluded)
        {
            foreach (string raw in list.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                bool negated = raw.StartsWith('~');
                string? domain = NormalizeDomain(negated ? raw[1..] : raw);
                if (domain == null)
                    return false;

                (negated ? excluded : included).Add("*" + domain);
            }
            return true;
        }

        static readonly IdnMapping Idn = new();

        internal static string? NormalizeDomain(string domain)
        {
            domain = domain.Trim().TrimEnd('.').ToLowerInvariant();
            if (domain.StartsWith("*.", StringComparison.Ordinal))
                domain = domain[2..];

            if (domain.Length == 0 || domain.EndsWith(".*", StringComparison.Ordinal) || domain.Contains('*') || domain.Contains('/'))
                return null;

            try
            {
                if (domain.Any(c => c > 127))
                    domain = Idn.GetAscii(domain);
            }
            catch (ArgumentException)
            {
                return null;
            }

            foreach (char c in domain)
            {
                if (!(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_'))
                    return null;
            }
            return domain;
        }

        /// <summary>Hôte d'un motif « ||hôte^ » ou « ||hôte/… » (exceptions de page).</summary>
        static string? HostOfPattern(string pattern)
        {
            if (!pattern.StartsWith("||", StringComparison.Ordinal))
                return null;

            string body = pattern[2..];
            int stop = body.IndexOfAny(new[] { '^', '/', '*', '|', '?', ':' });
            if (stop >= 0)
                body = body[..stop];
            return NormalizeDomain(body);
        }

        static bool IsSupportedSelector(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector) || selector.Length > 2048 || selector.StartsWith('+') || selector.StartsWith('^'))
                return false;

            foreach (string token in UnsupportedSelectorTokens)
            {
                if (selector.Contains(token, StringComparison.OrdinalIgnoreCase))
                    return false;
            }

            // Accolades : une règle CSS entière et non un sélecteur (injection de styles).
            return selector.IndexOfAny(new[] { '{', '}' }) < 0;
        }

        static bool TryMapKind(string name, out ResourceKinds kind)
        {
            kind = name switch
            {
                "script" => ResourceKinds.Script,
                "image" => ResourceKinds.Image,
                "stylesheet" or "css" => ResourceKinds.Stylesheet,
                "font" => ResourceKinds.Font,
                "media" => ResourceKinds.Media,
                "subdocument" or "frame" => ResourceKinds.Subdocument,
                "xmlhttprequest" or "xhr" => ResourceKinds.Fetch,
                "websocket" => ResourceKinds.WebSocket,
                "ping" or "beacon" => ResourceKinds.Ping,
                "object" or "object-subrequest" or "other" => ResourceKinds.Other,
                "popup" => ResourceKinds.Popup,
                "document" or "doc" => ResourceKinds.Document,
                _ => ResourceKinds.None
            };
            return kind != ResourceKinds.None;
        }

        sealed class ConversionState
        {
            readonly List<NetworkFilter> _blocking = new();
            readonly List<NetworkFilter> _exceptions = new();
            readonly SortedSet<string> _pageExceptions = new(StringComparer.Ordinal);
            readonly SortedSet<string> _genericHideExceptions = new(StringComparer.Ordinal);

            // Masquage : sélecteur générique → domaines où il ne s'applique pas.
            readonly Dictionary<string, SortedSet<string>> _generic = new(StringComparer.Ordinal);
            readonly HashSet<string> _genericDisabled = new(StringComparer.Ordinal);
            readonly Dictionary<string, SortedSet<string>> _specific = new(StringComparer.Ordinal);
            readonly Dictionary<string, HashSet<string>> _selectorExceptions = new(StringComparer.Ordinal);

            public int NetworkFilters { get; private set; }
            public int CosmeticFilters { get; private set; }
            public int Skipped { get; private set; }

            public void AddLine(string line, bool cosmeticFiltering)
            {
                if (line.Length == 0 || line[0] == '!' || line[0] == '[')
                    return;

                int marker = line.IndexOf("#@#", StringComparison.Ordinal);
                bool cosmeticException = marker >= 0;
                if (marker < 0)
                    marker = line.IndexOf("##", StringComparison.Ordinal);

                if (marker >= 0)
                {
                    if (!cosmeticFiltering)
                        return;
                    if (AddCosmetic(line[..marker], line[(marker + (cosmeticException ? 3 : 2))..].Trim(), cosmeticException))
                        CosmeticFilters++;
                    else
                        Skipped++;
                    return;
                }

                // Autres syntaxes cosmétiques ou de filtrage HTML (#?#, #$#, #%#, $$…) : non prises en charge.
                if (line.Contains("#?#", StringComparison.Ordinal) || line.Contains("#$#", StringComparison.Ordinal) ||
                    line.Contains("#%#", StringComparison.Ordinal) || line.Contains("#@", StringComparison.Ordinal) ||
                    line.Contains("$$", StringComparison.Ordinal))
                {
                    Skipped++;
                    return;
                }

                if (AddNetwork(line))
                    NetworkFilters++;
                else
                    Skipped++;
            }

            bool AddCosmetic(string domainPart, string selector, bool exception)
            {
                if (!IsSupportedSelector(selector))
                    return false;

                var included = new List<string>();
                var excluded = new List<string>();
                if (!ParseDomains(domainPart, ',', included, excluded) || (included.Count > 0 && excluded.Count > 0))
                    return false;

                if (exception)
                {
                    if (included.Count == 0 && excluded.Count == 0)
                        _genericDisabled.Add(selector);
                    else if (included.Count > 0)
                        GetOrAdd(_selectorExceptions, selector).UnionWith(included);
                    else
                        return false;
                    return true;
                }

                if (included.Count > 0)
                {
                    string key = string.Join(',', included.Distinct().OrderBy(d => d, StringComparer.Ordinal));
                    GetOrAdd(_specific, key).Add(selector);
                }
                else
                {
                    GetOrAdd(_generic, selector).UnionWith(excluded);
                }
                return true;
            }

            bool AddNetwork(string line)
            {
                bool exception = line.StartsWith("@@", StringComparison.Ordinal);
                string body = exception ? line[2..] : line;

                string pattern = body;
                string options = string.Empty;
                int dollar = FindOptionSeparator(body);
                if (dollar >= 0)
                {
                    pattern = body[..dollar];
                    options = body[(dollar + 1)..];
                }

                pattern = pattern.Trim();
                var included = new List<string>();
                var excluded = new List<string>();
                ResourceKinds kinds = ResourceKinds.None;
                ResourceKinds excludedKinds = ResourceKinds.None;
                bool? thirdParty = null;
                bool caseSensitive = false;
                bool pageException = false;
                bool genericHide = false;

                foreach (string rawOption in options.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    bool negated = rawOption.StartsWith('~');
                    string option = (negated ? rawOption[1..] : rawOption).ToLowerInvariant();

                    if (option.StartsWith("domain=", StringComparison.Ordinal) || option.StartsWith("from=", StringComparison.Ordinal))
                    {
                        if (negated || !ParseDomains(option[(option.IndexOf('=') + 1)..], '|', included, excluded))
                            return false;
                        continue;
                    }

                    switch (option)
                    {
                        case "third-party" or "3p":
                            thirdParty = !negated;
                            continue;
                        case "first-party" or "1p":
                            thirdParty = negated;
                            continue;
                        case "strict3p":
                            thirdParty = true;
                            continue;
                        case "strict1p":
                            thirdParty = false;
                            continue;
                        case "match-case":
                            caseSensitive = !negated;
                            continue;
                        case "important" or "all":
                            continue;
                        case "generichide" or "ghide" or "elemhide" or "ehide":
                            if (!exception || negated)
                                return false;
                            genericHide = true;
                            continue;
                        case "document" or "doc" when exception && !negated:
                            pageException = true;
                            continue;
                    }

                    if (!TryMapKind(option, out ResourceKinds kind))
                        return false; // Option inconnue ou non exprimable (redirect, csp, removeparam…).

                    if (negated)
                        excludedKinds |= kind;
                    else
                        kinds |= kind;
                }

                if (pageException || genericHide)
                {
                    string? host = HostOfPattern(pattern);
                    if (host == null)
                        return false;

                    if (pageException)
                        _pageExceptions.Add("*" + host);
                    else
                        _genericHideExceptions.Add("*" + host);
                    return true;
                }

                if (included.Count > 0 && excluded.Count > 0)
                    return false;

                if (kinds == ResourceKinds.None && excludedKinds != ResourceKinds.None)
                    kinds = ResourceKinds.Default;
                kinds &= ~excludedKinds;
                if (kinds == ResourceKinds.None && excludedKinds != ResourceKinds.None)
                    return false;

                if (pattern.Length == 0 || pattern == "*")
                {
                    // Tout bloquer n'a de sens que restreint à des sites ou à des types.
                    if (included.Count == 0 && kinds == ResourceKinds.None)
                        return false;
                    pattern = "*";
                }

                string? urlFilter = ConvertPattern(pattern);
                if (urlFilter == null)
                    return false;

                var filter = new NetworkFilter
                {
                    UrlFilter = urlFilter,
                    CaseSensitive = caseSensitive,
                    Kinds = kinds,
                    LoadType = thirdParty switch { true => "third-party", false => "first-party", _ => null }
                };
                filter.IncludedDomains.AddRange(included);
                filter.ExcludedDomains.AddRange(excluded);

                (exception ? _exceptions : _blocking).Add(filter);
                return true;
            }

            static int FindOptionSeparator(string line)
            {
                if (line.Length > 2 && line[0] == '/')
                {
                    int closing = line.LastIndexOf('/');
                    if (closing > 0)
                        return line.IndexOf('$', closing + 1);
                }
                return line.LastIndexOf('$');
            }

            public List<Rule> BuildRules()
            {
                var rules = new List<Rule>();

                // 1. Masquage générique : sans condition, puis avec des sites exclus.
                var unconditional = new List<string>();
                var withExclusions = new Dictionary<string, List<string>>(StringComparer.Ordinal);
                foreach ((string selector, SortedSet<string> excludedDomains) in _generic)
                {
                    if (_genericDisabled.Contains(selector))
                        continue;

                    var exclusions = new SortedSet<string>(excludedDomains, StringComparer.Ordinal);
                    if (_selectorExceptions.TryGetValue(selector, out HashSet<string>? exceptions))
                        exclusions.UnionWith(exceptions);

                    if (exclusions.Count == 0)
                        unconditional.Add(selector);
                    else
                        GetOrAdd(withExclusions, string.Join(',', exclusions)).Add(selector);
                }

                foreach (string selector in unconditional)
                    rules.Add(new Rule(new Trigger { UrlFilter = ".*" }, "css-display-none", selector));

                foreach ((string key, List<string> selectors) in withExclusions)
                {
                    string[] domains = key.Split(',');
                    foreach (string selector in selectors)
                        rules.Add(new Rule(new Trigger { UrlFilter = ".*", UnlessDomain = domains }, "css-display-none", selector));
                }

                // 2. Sites où le masquage générique est désactivé ($generichide).
                if (_genericHideExceptions.Count > 0)
                    rules.Add(new Rule(new Trigger { UrlFilter = ".*", IfDomain = _genericHideExceptions.ToArray() }, "ignore-previous-rules"));

                // 3. Blocages réseau.
                foreach (NetworkFilter filter in _blocking)
                    AddNetworkRules(rules, filter, "block", exception: false);

                // 4. Masquage propre à un site.
                foreach ((string key, SortedSet<string> selectors) in _specific)
                {
                    var domains = key.Split(',').ToList();
                    List<string> kept = selectors.Where(s => !_genericDisabled.Contains(s)).ToList();
                    var removed = new HashSet<string>(StringComparer.Ordinal);
                    foreach (string selector in kept)
                    {
                        if (_selectorExceptions.TryGetValue(selector, out HashSet<string>? exceptions) && domains.Any(exceptions.Contains))
                            removed.Add(selector);
                    }

                    foreach (string selector in kept.Where(s => !removed.Contains(s)))
                        rules.Add(new Rule(new Trigger { UrlFilter = ".*", IfDomain = domains }, "css-display-none", selector));
                }

                // 5. Exceptions réseau (@@…).
                foreach (NetworkFilter filter in _exceptions)
                    AddNetworkRules(rules, filter, "ignore-previous-rules", exception: true);

                // 6. Sites entièrement exemptés ($document).
                if (_pageExceptions.Count > 0)
                    rules.Add(new Rule(new Trigger { UrlFilter = ".*", IfDomain = _pageExceptions.ToArray() }, "ignore-previous-rules"));

                return rules;
            }

            static void AddNetworkRules(List<Rule> rules, NetworkFilter filter, string action, bool exception)
            {
                ResourceKinds kinds = filter.Kinds;

                // Une exception sans type ne doit pas s'appliquer au chargement de la page elle-même :
                // elle annulerait aussi le masquage des éléments (comme le fait « $document »).
                if (kinds == ResourceKinds.None && exception)
                    kinds = ResourceKinds.Default;

                if (kinds == ResourceKinds.None)
                {
                    rules.Add(new Rule(MakeTrigger(filter, null, null), action));
                    return;
                }

                // Les cadres (subdocument) sont des pages chargées dans un cadre : règle séparée.
                bool frames = (kinds & ResourceKinds.Subdocument) != 0 && (kinds & ResourceKinds.Document) == 0;
                List<string> types = WebKitTypes(kinds & ~(frames ? ResourceKinds.Subdocument : ResourceKinds.None));

                if (types.Count > 0)
                    rules.Add(new Rule(MakeTrigger(filter, types, null), action));
                if (frames)
                    rules.Add(new Rule(MakeTrigger(filter, new[] { "document" }, "child-frame"), action));
            }

            static Trigger MakeTrigger(NetworkFilter filter, IReadOnlyList<string>? types, string? loadContext) => new()
            {
                UrlFilter = filter.UrlFilter,
                CaseSensitive = filter.CaseSensitive,
                IfDomain = filter.IncludedDomains.Count > 0 ? filter.IncludedDomains : null,
                UnlessDomain = filter.ExcludedDomains.Count > 0 ? filter.ExcludedDomains : null,
                ResourceTypes = types,
                LoadType = filter.LoadType,
                LoadContext = loadContext
            };

            static List<string> WebKitTypes(ResourceKinds kinds)
            {
                var types = new List<string>();
                if ((kinds & (ResourceKinds.Document | ResourceKinds.Subdocument)) != 0) types.Add("document");
                if ((kinds & ResourceKinds.Script) != 0) types.Add("script");
                if ((kinds & ResourceKinds.Image) != 0) types.Add("image");
                if ((kinds & ResourceKinds.Stylesheet) != 0) types.Add("style-sheet");
                if ((kinds & ResourceKinds.Font) != 0) types.Add("font");
                if ((kinds & ResourceKinds.Media) != 0) types.Add("media");
                if ((kinds & ResourceKinds.Fetch) != 0) types.Add("fetch");
                if ((kinds & ResourceKinds.WebSocket) != 0) types.Add("websocket");
                if ((kinds & ResourceKinds.Ping) != 0) types.Add("ping");
                if ((kinds & ResourceKinds.Other) != 0) types.Add("other");
                if ((kinds & ResourceKinds.Popup) != 0) types.Add("popup");
                return types;
            }

            static TValue GetOrAdd<TValue>(Dictionary<string, TValue> map, string key) where TValue : new()
            {
                if (!map.TryGetValue(key, out TValue? value))
                {
                    value = new TValue();
                    map[key] = value;
                }
                return value;
            }
        }

        static string Serialize(List<Rule> rules)
        {
            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartArray();
                foreach (Rule rule in rules)
                {
                    writer.WriteStartObject();
                    writer.WriteStartObject("trigger");
                    writer.WriteString("url-filter", rule.Trigger.UrlFilter);
                    if (rule.Trigger.CaseSensitive)
                        writer.WriteBoolean("url-filter-is-case-sensitive", true);
                    WriteArray(writer, "if-domain", rule.Trigger.IfDomain);
                    WriteArray(writer, "unless-domain", rule.Trigger.UnlessDomain);
                    WriteArray(writer, "resource-type", rule.Trigger.ResourceTypes);
                    if (rule.Trigger.LoadType != null)
                        WriteArray(writer, "load-type", new[] { rule.Trigger.LoadType });
                    if (rule.Trigger.LoadContext != null)
                        WriteArray(writer, "load-context", new[] { rule.Trigger.LoadContext });
                    writer.WriteEndObject();

                    writer.WriteStartObject("action");
                    writer.WriteString("type", rule.Action);
                    if (rule.Selector != null)
                        writer.WriteString("selector", rule.Selector);
                    writer.WriteEndObject();
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            return Encoding.UTF8.GetString(stream.ToArray());
        }

        static void WriteArray(Utf8JsonWriter writer, string name, IReadOnlyList<string>? values)
        {
            if (values == null || values.Count == 0)
                return;

            writer.WriteStartArray(name);
            foreach (string value in values)
                writer.WriteStringValue(value);
            writer.WriteEndArray();
        }
    }
}
