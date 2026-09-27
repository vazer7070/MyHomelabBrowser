using MyHomelabBrowser.classes.AdBlock.Models;
using System;
using System.Collections.Generic;
using System.Threading;

namespace MyHomelabBrowser.classes.AdBlock.Services
{
    public sealed class AdBlockEngine
    {
        private readonly ReaderWriterLockSlim _lock = new();
        private AdBlockRuleSet _rules = AdBlockRuleParser.Parse(Array.Empty<(string, string)>());
        private Dictionary<string, string[]> _cosmeticCache = new(StringComparer.OrdinalIgnoreCase);

        public int NetworkRuleCount
        {
            get
            {
                _lock.EnterReadLock();
                try { return _rules.NetworkRuleCount; }
                finally { _lock.ExitReadLock(); }
            }
        }

        public int CosmeticRuleCount
        {
            get
            {
                _lock.EnterReadLock();
                try { return _rules.CosmeticRuleCount; }
                finally { _lock.ExitReadLock(); }
            }
        }

        public void ReplaceRules(IEnumerable<(string SourceName, string Content)> sources)
        {
            AdBlockRuleSet parsed = AdBlockRuleParser.Parse(sources);
            _lock.EnterWriteLock();
            try
            {
                _rules = parsed;
                _cosmeticCache = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
            }
            finally { _lock.ExitWriteLock(); }
        }

        public bool ShouldBlock(AdBlockRequestContext context)
        {
            _lock.EnterReadLock();
            try
            {
                if (AdBlockDomain.ContainsHostOrParent(_rules.PageExceptionDomains, context.DocumentHost))
                    return false;

                if (_rules.ExceptionRules.IsMatch(context))
                    return false;

                return _rules.BlockingRules.IsMatch(context);
            }
            finally
            {
                _lock.ExitReadLock();
            }
        }

        public IReadOnlyList<string> GetCosmeticSelectors(string host)
        {
            host = AdBlockDomain.NormalizeHost(host);
            if (host.Length == 0)
                return Array.Empty<string>();

            _lock.EnterUpgradeableReadLock();
            try
            {
                if (_cosmeticCache.TryGetValue(host, out string[]? cached))
                    return cached;

                bool skipGeneric = AdBlockDomain.ContainsHostOrParent(_rules.GenericHideExceptionDomains, host);
                string[] result = _rules.CosmeticRules.GetSelectors(host, skipGeneric);

                _lock.EnterWriteLock();
                try
                {
                    if (_cosmeticCache.Count >= 512)
                        _cosmeticCache.Clear();
                    _cosmeticCache[host] = result;
                }
                finally { _lock.ExitWriteLock(); }

                return result;
            }
            finally
            {
                _lock.ExitUpgradeableReadLock();
            }
        }
    }
}
