using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace MyHomelabBrowser.classes.AdBlock.Models
{
    internal static class AdBlockRuleParser
    {
        private static readonly Regex SimpleHostRuleRegex = new(
            "^\\|\\|(?<host>[a-z0-9._-]+)\\^(?:\\|)?$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly Regex TokenRegex = new(
            "[a-z0-9%_-]{5,}",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static readonly HashSet<string> WeakTokens = new(StringComparer.OrdinalIgnoreCase)
        {
            "https", "http", "www", "image", "images", "script", "content", "static",
            "assets", "media", "common", "source", "index", "pixel", "request"
        };

        public static AdBlockRuleSet Parse(IEnumerable<(string SourceName, string Content)> sources)
        {
            var blocking = new AdBlockRuleIndex();
            var exceptions = new AdBlockRuleIndex();
            var cosmetics = new List<AdBlockCosmeticRule>();
            var pageExceptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var genericHideExceptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int nextRuleId = 1;
            int networkCount = 0;

            foreach ((string _, string content) in sources)
            {
                using var reader = new System.IO.StringReader(content ?? string.Empty);
                string? rawLine;
                while ((rawLine = reader.ReadLine()) != null)
                {
                    string line = rawLine.Trim();
                    if (line.Length == 0 || line[0] == '!' || line[0] == '[')
                        continue;

                    if (TryParseCosmeticRule(line, out AdBlockCosmeticRule? cosmetic))
                    {
                        cosmetics.Add(cosmetic);
                        continue;
                    }

                    if (LooksLikeUnsupportedCosmeticRule(line))
                        continue;

                    if (!TryParseNetworkRule(line, nextRuleId, pageExceptions, genericHideExceptions, out AdBlockNetworkRule? rule))
                        continue;

                    nextRuleId++;
                    networkCount++;
                    (rule.IsException ? exceptions : blocking).Add(rule);
                }
            }

            return new AdBlockRuleSet
            {
                BlockingRules = blocking,
                ExceptionRules = exceptions,
                CosmeticRules = cosmetics,
                PageExceptionDomains = pageExceptions,
                GenericHideExceptionDomains = genericHideExceptions,
                NetworkRuleCount = networkCount,
                CosmeticRuleCount = cosmetics.Count
            };
        }


        private static bool LooksLikeUnsupportedCosmeticRule(string line)
        {
            return line.Contains("##", StringComparison.Ordinal)
                || line.Contains("#@#", StringComparison.Ordinal)
                || line.Contains("#?#", StringComparison.Ordinal)
                || line.Contains("#@?#", StringComparison.Ordinal)
                || line.Contains("#$#", StringComparison.Ordinal)
                || line.Contains("#%#", StringComparison.Ordinal);
        }

        private static bool TryParseCosmeticRule(string line, out AdBlockCosmeticRule? result)
        {
            result = null;

            if (line.Contains("#?#", StringComparison.Ordinal)
                || line.Contains("#$#", StringComparison.Ordinal)
                || line.Contains("#%#", StringComparison.Ordinal)
                || line.Contains("##+js", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            int markerIndex = line.IndexOf("#@#", StringComparison.Ordinal);
            bool exception = markerIndex >= 0;
            int markerLength = 3;

            if (markerIndex < 0)
            {
                markerIndex = line.IndexOf("##", StringComparison.Ordinal);
                markerLength = 2;
            }

            if (markerIndex < 0)
                return false;

            string domainPart = line[..markerIndex].Trim();
            string selector = line[(markerIndex + markerLength)..].Trim();
            if (!IsSupportedSelector(selector))
                return false;

            var included = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ParseDomainList(domainPart, ',', included, excluded);

            result = new AdBlockCosmeticRule
            {
                Selector = selector,
                IsException = exception,
                IncludedDomains = included,
                ExcludedDomains = excluded
            };
            return true;
        }

        private static bool IsSupportedSelector(string selector)
        {
            if (string.IsNullOrWhiteSpace(selector) || selector.Length > 2048)
                return false;

            string[] unsupported =
            {
                ":-abp-", ":has-text(", ":matches-css(", ":matches-path(", ":xpath(",
                ":upward(", ":remove(", ":style(", ":watch-attr(", ":others()", ":min-text-length("
            };

            return !unsupported.Any(token => selector.Contains(token, StringComparison.OrdinalIgnoreCase));
        }

        private static bool TryParseNetworkRule(
            string line,
            int id,
            HashSet<string> pageExceptions,
            HashSet<string> genericHideExceptions,
            out AdBlockNetworkRule? result)
        {
            result = null;

            bool exception = line.StartsWith("@@", StringComparison.Ordinal);
            if (exception)
                line = line[2..];

            string pattern = line;
            string optionText = string.Empty;
            int dollar = FindOptionSeparator(line);
            if (dollar >= 0)
            {
                pattern = line[..dollar];
                optionText = line[(dollar + 1)..];
            }

            pattern = pattern.Trim();
            if (pattern.Length == 0 || pattern.Equals("*", StringComparison.Ordinal))
                return false;

            var includedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var excludedDomains = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AdBlockResourceType includedTypes = AdBlockResourceType.None;
            AdBlockResourceType excludedTypes = AdBlockResourceType.None;
            bool hasPositiveResourceType = false;
            bool? thirdPartyOnly = null;
            bool matchCase = false;
            bool documentOption = false;
            bool genericHideOption = false;

            foreach (string rawOption in optionText.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string option = rawOption.Trim();
                bool negated = option.StartsWith('~');
                string name = negated ? option[1..] : option;

                if (name.StartsWith("domain=", StringComparison.OrdinalIgnoreCase))
                {
                    ParseDomainList(name[7..], '|', includedDomains, excludedDomains);
                    continue;
                }

                if (name.Equals("third-party", StringComparison.OrdinalIgnoreCase))
                {
                    thirdPartyOnly = !negated;
                    continue;
                }

                if (name.Equals("match-case", StringComparison.OrdinalIgnoreCase))
                {
                    matchCase = !negated;
                    continue;
                }

                if (name.Equals("badfilter", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("removeparam", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("replace", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("redirect", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("csp", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("permissions", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("urltransform", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("header", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("removeheader", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("cookie", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                if (name.Equals("generichide", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("elemhide", StringComparison.OrdinalIgnoreCase))
                {
                    if (!negated)
                        genericHideOption = true;
                    continue;
                }

                if (!TryMapResourceType(name, out AdBlockResourceType resourceType))
                    continue;

                if (resourceType == AdBlockResourceType.Document && !negated)
                    documentOption = true;

                if (negated)
                    excludedTypes |= resourceType;
                else
                {
                    includedTypes |= resourceType;
                    hasPositiveResourceType = true;
                }
            }

            if (!hasPositiveResourceType)
                includedTypes = AdBlockResourceType.Any;

            if (exception && TryExtractHostSuffix(pattern, out string? exceptionHost))
            {
                if (documentOption)
                    pageExceptions.Add(exceptionHost);
                if (genericHideOption)
                    genericHideExceptions.Add(exceptionHost);
            }

            if (genericHideOption && !documentOption && optionText.Length > 0)
            {
                // Une règle purement cosmétique ne doit pas devenir une règle réseau.
                bool hasNetworkType = hasPositiveResourceType || excludedTypes != AdBlockResourceType.None || thirdPartyOnly.HasValue;
                if (!hasNetworkType)
                    return false;
            }

            string? hostSuffix = null;
            Regex? regex = null;

            if (TryExtractSimpleHostRule(pattern, out string? simpleHost))
            {
                hostSuffix = simpleHost;
            }
            else
            {
                try
                {
                    string regexText = ConvertPatternToRegex(pattern);
                    var options = RegexOptions.CultureInvariant;
                    if (!matchCase)
                        options |= RegexOptions.IgnoreCase;
                    regex = new Regex(regexText, options, TimeSpan.FromMilliseconds(40));
                }
                catch
                {
                    return false;
                }
            }

            result = new AdBlockNetworkRule
            {
                Id = id,
                OriginalText = line,
                IsException = exception,
                HostSuffix = hostSuffix,
                Regex = regex,
                MatchCase = matchCase,
                ThirdPartyOnly = thirdPartyOnly,
                IncludedTypes = includedTypes,
                ExcludedTypes = excludedTypes,
                IncludedDomains = includedDomains,
                ExcludedDomains = excludedDomains,
                IndexToken = hostSuffix == null ? ExtractIndexToken(pattern) : null
            };
            return true;
        }

        private static int FindOptionSeparator(string line)
        {
            if (line.Length > 2 && line[0] == '/' && line.LastIndexOf('/') > 0)
            {
                int closingSlash = line.LastIndexOf('/');
                int dollarAfterRegex = line.IndexOf('$', closingSlash + 1);
                return dollarAfterRegex;
            }

            return line.LastIndexOf('$');
        }

        private static bool TryExtractSimpleHostRule(string pattern, out string? host)
        {
            host = null;
            Match match = SimpleHostRuleRegex.Match(pattern);
            if (!match.Success)
                return false;

            host = AdBlockDomain.NormalizeHost(match.Groups["host"].Value);
            return host.Length > 0;
        }

        private static bool TryExtractHostSuffix(string pattern, out string host)
        {
            host = string.Empty;
            if (TryExtractSimpleHostRule(pattern, out string? simpleHost) && simpleHost != null)
            {
                host = simpleHost;
                return true;
            }

            if (!pattern.StartsWith("||", StringComparison.Ordinal))
                return false;

            string body = pattern[2..];
            int stop = body.IndexOfAny(new[] { '^', '/', '*', '|', '?' });
            if (stop >= 0)
                body = body[..stop];

            host = AdBlockDomain.NormalizeHost(body);
            return host.Length > 0;
        }

        private static string ConvertPatternToRegex(string pattern)
        {
            if (pattern.Length > 2 && pattern[0] == '/' && pattern[^1] == '/')
                return pattern[1..^1];

            bool domainAnchor = pattern.StartsWith("||", StringComparison.Ordinal);
            bool startAnchor = !domainAnchor && pattern.StartsWith('|');
            bool endAnchor = pattern.EndsWith('|') && !pattern.EndsWith("||", StringComparison.Ordinal);

            int start = domainAnchor ? 2 : startAnchor ? 1 : 0;
            int end = endAnchor ? pattern.Length - 1 : pattern.Length;
            string body = pattern[start..end];

            var builder = new StringBuilder();
            if (domainAnchor)
                builder.Append(@"^(?:[^:/?#]+:)?//(?:[^/?#]*\.)?");
            else if (startAnchor)
                builder.Append('^');

            foreach (char character in body)
            {
                switch (character)
                {
                    case '*':
                        builder.Append(".*");
                        break;
                    case '^':
                        builder.Append(@"(?:[^a-zA-Z0-9_.%-]|$)");
                        break;
                    default:
                        builder.Append(Regex.Escape(character.ToString()));
                        break;
                }
            }

            if (endAnchor)
                builder.Append('$');

            return builder.ToString();
        }

        private static string? ExtractIndexToken(string pattern)
        {
            string? best = null;
            foreach (Match match in TokenRegex.Matches(pattern))
            {
                string candidate = match.Value.ToLowerInvariant();
                if (WeakTokens.Contains(candidate))
                    continue;

                if (best == null || candidate.Length > best.Length)
                    best = candidate;
            }
            return best;
        }

        internal static IEnumerable<string> TokenizeUrl(string url)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Match match in TokenRegex.Matches(url))
            {
                string token = match.Value.ToLowerInvariant();
                if (seen.Add(token))
                    yield return token;
            }
        }

        private static void ParseDomainList(
            string domainText,
            char separator,
            HashSet<string> included,
            HashSet<string> excluded)
        {
            foreach (string raw in domainText.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                bool negated = raw.StartsWith('~');
                string domain = AdBlockDomain.NormalizeHost(negated ? raw[1..] : raw);
                if (domain.Length == 0)
                    continue;

                (negated ? excluded : included).Add(domain);
            }
        }

        private static bool TryMapResourceType(string option, out AdBlockResourceType type)
        {
            type = option.ToLowerInvariant() switch
            {
                "document" => AdBlockResourceType.Document,
                "subdocument" => AdBlockResourceType.SubDocument,
                "script" => AdBlockResourceType.Script,
                "image" => AdBlockResourceType.Image,
                "stylesheet" => AdBlockResourceType.Stylesheet,
                "media" => AdBlockResourceType.Media,
                "font" => AdBlockResourceType.Font,
                "xmlhttprequest" or "xhr" => AdBlockResourceType.XmlHttpRequest,
                "fetch" => AdBlockResourceType.Fetch,
                "ping" or "beacon" => AdBlockResourceType.Ping,
                "websocket" => AdBlockResourceType.WebSocket,
                "other" or "object" => AdBlockResourceType.Other,
                _ => AdBlockResourceType.None
            };
            return type != AdBlockResourceType.None;
        }
    }
}
