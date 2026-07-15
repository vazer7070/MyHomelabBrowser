using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace MyHomelabBrowser.classes.AdBlock.Models
{
    internal sealed class AdBlockNetworkRule
    {
        public int Id { get; init; }
        public required string OriginalText { get; init; }
        public bool IsException { get; init; }
        public string? HostSuffix { get; init; }
        public Regex? Regex { get; init; }
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

            if (ExcludedDomains.Count > 0 && DomainSetMatches(ExcludedDomains, context.DocumentHost))
                return false;

            if (IncludedDomains.Count > 0 && !DomainSetMatches(IncludedDomains, context.DocumentHost))
                return false;

            if (!string.IsNullOrWhiteSpace(HostSuffix))
                return AdBlockDomain.HostMatches(context.RequestUri.Host, HostSuffix);

            try
            {
                return Regex?.IsMatch(context.RequestUri.AbsoluteUri) == true;
            }
            catch (RegexMatchTimeoutException)
            {
                return false;
            }
        }

        private static bool DomainSetMatches(IEnumerable<string> domains, string documentHost)
        {
            foreach (string domain in domains)
            {
                if (AdBlockDomain.HostMatches(documentHost, domain))
                    return true;
            }
            return false;
        }
    }

    internal sealed class AdBlockCosmeticRule
    {
        public required string Selector { get; init; }
        public bool IsException { get; init; }
        public HashSet<string> IncludedDomains { get; init; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> ExcludedDomains { get; init; } = new(StringComparer.OrdinalIgnoreCase);

        public bool AppliesTo(string host)
        {
            if (ExcludedDomains.Count > 0)
            {
                foreach (string domain in ExcludedDomains)
                {
                    if (AdBlockDomain.HostMatches(host, domain))
                        return false;
                }
            }

            if (IncludedDomains.Count == 0)
                return true;

            foreach (string domain in IncludedDomains)
            {
                if (AdBlockDomain.HostMatches(host, domain))
                    return true;
            }
            return false;
        }
    }

    internal sealed class AdBlockRuleSet
    {
        public required AdBlockRuleIndex BlockingRules { get; init; }
        public required AdBlockRuleIndex ExceptionRules { get; init; }
        public required IReadOnlyList<AdBlockCosmeticRule> CosmeticRules { get; init; }
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
                if (!_byHostSuffix.TryGetValue(rule.HostSuffix, out List<AdBlockNetworkRule>? list))
                {
                    list = new List<AdBlockNetworkRule>();
                    _byHostSuffix[rule.HostSuffix] = list;
                }
                list.Add(rule);
                return;
            }

            if (!string.IsNullOrWhiteSpace(rule.IndexToken))
            {
                if (!_byToken.TryGetValue(rule.IndexToken, out List<AdBlockNetworkRule>? list))
                {
                    list = new List<AdBlockNetworkRule>();
                    _byToken[rule.IndexToken] = list;
                }
                list.Add(rule);
                return;
            }

            _generic.Add(rule);
        }

        public bool IsMatch(AdBlockRequestContext context)
        {
            var visited = new HashSet<int>();

            foreach (string hostCandidate in AdBlockDomain.EnumerateHostSuffixes(context.RequestUri.Host))
            {
                if (!_byHostSuffix.TryGetValue(hostCandidate, out List<AdBlockNetworkRule>? rules))
                    continue;

                foreach (AdBlockNetworkRule rule in rules)
                {
                    if (visited.Add(rule.Id) && rule.Matches(context))
                        return true;
                }
            }

            foreach (string token in AdBlockRuleParser.TokenizeUrl(context.RequestUri.AbsoluteUri))
            {
                if (!_byToken.TryGetValue(token, out List<AdBlockNetworkRule>? rules))
                    continue;

                foreach (AdBlockNetworkRule rule in rules)
                {
                    if (visited.Add(rule.Id) && rule.Matches(context))
                        return true;
                }
            }

            foreach (AdBlockNetworkRule rule in _generic)
            {
                if (visited.Add(rule.Id) && rule.Matches(context))
                    return true;
            }

            return false;
        }
    }
}
