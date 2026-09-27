using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;

namespace MyHomelabBrowser.classes.AdBlock.Models
{
    internal sealed class AdBlockNetworkRule
    {
        private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(40);

        private Regex? _regex;
        private bool _regexFailed;

        public int Id { get; init; }
        public bool IsException { get; init; }
        public string? HostSuffix { get; init; }

        /// <summary>
        /// Motif sans joker ni ancre : une simple recherche de sous-chaîne suffit.
        /// </summary>
        public string? Literal { get; init; }

        /// <summary>
        /// Expression convertie depuis le motif. La Regex n'est construite qu'à la première
        /// évaluation : la grande majorité des règles ne sont jamais testées.
        /// </summary>
        public string? RegexSource { get; init; }

        /// <summary>
        /// Regex déjà construite (règles /.../ validées au chargement).
        /// </summary>
        public Regex? Regex
        {
            get => GetRegex();
            init => _regex = value;
        }

        public bool MatchCase { get; init; }
        public bool? ThirdPartyOnly { get; init; }
        public AdBlockResourceType IncludedTypes { get; init; } = AdBlockResourceType.Any;
        public AdBlockResourceType ExcludedTypes { get; init; } = AdBlockResourceType.None;
        public HashSet<string> IncludedDomains { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExcludedDomains { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public string? IndexToken { get; init; }

        public bool Matches(AdBlockRequestContext context)
        {
            if ((IncludedTypes & context.ResourceType) == 0)
                return false;

            if ((ExcludedTypes & context.ResourceType) != 0)
                return false;

            if (ThirdPartyOnly.HasValue && ThirdPartyOnly.Value != context.IsThirdParty)
                return false;

            if (ExcludedDomains.Count > 0 && AdBlockDomain.ContainsHostOrParent(ExcludedDomains, context.DocumentHost))
                return false;

            if (IncludedDomains.Count > 0 && !AdBlockDomain.ContainsHostOrParent(IncludedDomains, context.DocumentHost))
                return false;

            if (HostSuffix != null)
                return AdBlockDomain.IsSameOrSubdomain(context.RequestHost, HostSuffix);

            if (Literal != null)
            {
                return context.RequestUrl.Contains(
                    Literal,
                    MatchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);
            }

            Regex? regex = GetRegex();
            if (regex == null)
                return false;

            try
            {
                return regex.IsMatch(context.RequestUrl);
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        /// <summary>
        /// Construit dès maintenant une Regex compilée : réservé aux règles sans jeton
        /// d'index, évaluées pour chaque requête (appelé hors du thread UI, au chargement).
        /// </summary>
        public void PrepareForFrequentUse()
        {
            if (_regex != null || RegexSource == null)
                return;

            try
            {
                var options = RegexOptions.CultureInvariant | RegexOptions.Compiled;
                if (!MatchCase)
                    options |= RegexOptions.IgnoreCase;
                _regex = new Regex(RegexSource, options, RegexTimeout);
            }
            catch (ArgumentException)
            {
                _regexFailed = true;
            }
        }

        private Regex? GetRegex()
        {
            Regex? regex = Volatile.Read(ref _regex);
            if (regex != null || _regexFailed || RegexSource == null)
                return regex;

            try
            {
                var options = RegexOptions.CultureInvariant;
                if (!MatchCase)
                    options |= RegexOptions.IgnoreCase;

                // Deux threads peuvent la construire en même temps : sans conséquence.
                regex = new Regex(RegexSource, options, RegexTimeout);
                Volatile.Write(ref _regex, regex);
                return regex;
            }
            catch (ArgumentException)
            {
                _regexFailed = true;
                return null;
            }
        }
    }

    internal sealed class AdBlockCosmeticRule
    {
        public required string Selector { get; init; }
        public bool IsException { get; init; }
        public HashSet<string> IncludedDomains { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExcludedDomains { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        /// <param name="host">Hôte déjà normalisé.</param>
        public bool AppliesTo(string host)
        {
            if (ExcludedDomains.Count > 0 && AdBlockDomain.ContainsHostOrParent(ExcludedDomains, host))
                return false;

            return IncludedDomains.Count == 0 || AdBlockDomain.ContainsHostOrParent(IncludedDomains, host);
        }
    }

    internal sealed class AdBlockRuleSet
    {
        public required AdBlockRuleIndex BlockingRules { get; init; }
        public required AdBlockRuleIndex ExceptionRules { get; init; }
        public required AdBlockCosmeticIndex CosmeticRules { get; init; }
        public required HashSet<string> PageExceptionDomains { get; init; }
        public required HashSet<string> GenericHideExceptionDomains { get; init; }
        public int NetworkRuleCount { get; init; }
        public int CosmeticRuleCount { get; init; }
    }

    internal sealed class AdBlockRuleIndex
    {
        private readonly Dictionary<string, List<AdBlockNetworkRule>> _byToken = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, List<AdBlockNetworkRule>> _byHostSuffix = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<AdBlockNetworkRule> _generic = new();

        public void Add(AdBlockNetworkRule rule)
        {
            if (!string.IsNullOrWhiteSpace(rule.HostSuffix))
            {
                AddTo(_byHostSuffix, rule.HostSuffix, rule);
                return;
            }

            if (!string.IsNullOrWhiteSpace(rule.IndexToken))
            {
                AddTo(_byToken, rule.IndexToken, rule);
                return;
            }

            _generic.Add(rule);
        }

        private static void AddTo(Dictionary<string, List<AdBlockNetworkRule>> index, string key, AdBlockNetworkRule rule)
        {
            if (!index.TryGetValue(key, out List<AdBlockNetworkRule>? list))
            {
                list = new List<AdBlockNetworkRule>();
                index[key] = list;
            }
            list.Add(rule);
        }

        // Chaque règle n'est rangée que dans un seul compartiment, et les jetons de l'URL
        // sont distincts : aucune règle ne peut être évaluée deux fois.
        public bool IsMatch(AdBlockRequestContext context)
        {
            if (_byHostSuffix.Count > 0 && context.RequestHost.Length > 0)
            {
                var lookup = _byHostSuffix.GetAlternateLookup<ReadOnlySpan<char>>();
                ReadOnlySpan<char> current = context.RequestHost;
                while (true)
                {
                    if (lookup.TryGetValue(current, out List<AdBlockNetworkRule>? rules) && AnyMatch(rules, context))
                        return true;

                    int dot = current.IndexOf('.');
                    if (dot < 0 || dot == current.Length - 1)
                        break;

                    current = current[(dot + 1)..];
                }
            }

            if (_byToken.Count > 0)
            {
                foreach (string token in context.UrlTokens)
                {
                    if (_byToken.TryGetValue(token, out List<AdBlockNetworkRule>? rules) && AnyMatch(rules, context))
                        return true;
                }
            }

            return AnyMatch(_generic, context);
        }

        /// <summary>
        /// Prépare les règles génériques, évaluées pour chaque requête.
        /// </summary>
        public void Prepare()
        {
            foreach (AdBlockNetworkRule rule in _generic)
                rule.PrepareForFrequentUse();
        }

        private static bool AnyMatch(List<AdBlockNetworkRule> rules, AdBlockRequestContext context)
        {
            foreach (AdBlockNetworkRule rule in rules)
            {
                if (rule.Matches(context))
                    return true;
            }
            return false;
        }
    }

    /// <summary>
    /// Règles cosmétiques indexées par domaine : seules les règles du site visité
    /// sont parcourues, au lieu de toute la liste à chaque nouvel hôte.
    /// </summary>
    internal sealed class AdBlockCosmeticIndex
    {
        private readonly List<string> _genericSelectors = new();
        private readonly HashSet<string> _genericExceptions = new(StringComparer.Ordinal);
        private readonly List<AdBlockCosmeticRule> _genericWithExclusions = new();
        private readonly Dictionary<string, List<AdBlockCosmeticRule>> _byDomain = new(StringComparer.OrdinalIgnoreCase);
        private string[]? _genericResult;

        public int Count { get; private set; }

        public void Add(AdBlockCosmeticRule rule)
        {
            Count++;
            _genericResult = null;

            if (rule.IncludedDomains.Count > 0)
            {
                foreach (string domain in rule.IncludedDomains)
                {
                    if (!_byDomain.TryGetValue(domain, out List<AdBlockCosmeticRule>? list))
                    {
                        list = new List<AdBlockCosmeticRule>();
                        _byDomain[domain] = list;
                    }
                    list.Add(rule);
                }
                return;
            }

            if (rule.ExcludedDomains.Count > 0)
            {
                _genericWithExclusions.Add(rule);
                return;
            }

            if (rule.IsException)
                _genericExceptions.Add(rule.Selector);
            else
                _genericSelectors.Add(rule.Selector);
        }

        /// <param name="host">Hôte déjà normalisé.</param>
        /// <param name="skipGeneric">Vrai si le site est exempté des règles génériques ($generichide).</param>
        public string[] GetSelectors(string host, bool skipGeneric)
        {
            List<AdBlockCosmeticRule>? matched = null;

            if (!skipGeneric)
            {
                foreach (AdBlockCosmeticRule rule in _genericWithExclusions)
                {
                    if (rule.AppliesTo(host))
                        (matched ??= new List<AdBlockCosmeticRule>()).Add(rule);
                }
            }

            if (_byDomain.Count > 0 && host.Length > 0)
            {
                var lookup = _byDomain.GetAlternateLookup<ReadOnlySpan<char>>();
                ReadOnlySpan<char> current = host;
                while (true)
                {
                    if (lookup.TryGetValue(current, out List<AdBlockCosmeticRule>? rules))
                    {
                        foreach (AdBlockCosmeticRule rule in rules)
                        {
                            if (rule.AppliesTo(host))
                                (matched ??= new List<AdBlockCosmeticRule>()).Add(rule);
                        }
                    }

                    int dot = current.IndexOf('.');
                    if (dot < 0 || dot == current.Length - 1)
                        break;

                    current = current[(dot + 1)..];
                }
            }

            // Cas le plus courant : aucune règle propre au site, le résultat générique est partagé.
            if (matched == null)
                return skipGeneric ? Array.Empty<string>() : GetGenericResult();

            var blocked = new HashSet<string>(StringComparer.Ordinal);
            var exceptions = new HashSet<string>(StringComparer.Ordinal);

            if (!skipGeneric)
            {
                blocked.UnionWith(_genericSelectors);
                exceptions.UnionWith(_genericExceptions);
            }

            foreach (AdBlockCosmeticRule rule in matched)
                (rule.IsException ? exceptions : blocked).Add(rule.Selector);

            blocked.ExceptWith(exceptions);
            return blocked.ToArray();
        }

        private string[] GetGenericResult()
        {
            string[]? result = Volatile.Read(ref _genericResult);
            if (result != null)
                return result;

            var blocked = new HashSet<string>(_genericSelectors, StringComparer.Ordinal);
            blocked.ExceptWith(_genericExceptions);
            result = blocked.ToArray();
            Volatile.Write(ref _genericResult, result);
            return result;
        }
    }
}
