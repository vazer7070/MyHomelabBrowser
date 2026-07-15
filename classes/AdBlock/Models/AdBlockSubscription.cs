using System;

namespace MyHomelabBrowser.classes.AdBlock.Models
{
    public sealed class AdBlockSubscription
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");
        public string Name { get; set; } = string.Empty;
        public string Url { get; set; } = string.Empty;
        public bool Enabled { get; set; } = true;
        public bool IsPrivacyList { get; set; }

        public AdBlockSubscription Clone() => new()
        {
            Id = Id,
            Name = Name,
            Url = Url,
            Enabled = Enabled,
            IsPrivacyList = IsPrivacyList
        };
    }
}
