using System;

namespace MyHomelabBrowser.classes.AdBlock.Models
{
    public sealed class AdBlockModuleSnapshot
    {
        public bool Enabled { get; init; }
        public bool IsReady { get; init; }
        public int NetworkRuleCount { get; init; }
        public int CosmeticRuleCount { get; init; }
        public long SessionBlockedCount { get; init; }
        public DateTimeOffset? LastSuccessfulUpdateUtc { get; init; }
        public string StatusMessage { get; init; } = string.Empty;
    }
}
