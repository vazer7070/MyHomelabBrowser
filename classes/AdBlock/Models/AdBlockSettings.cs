using System;
using System.Collections.Generic;
using System.Linq;

namespace MyHomelabBrowser.classes.AdBlock.Models
{
    public sealed class AdBlockSettings
    {
        public bool Enabled { get; set; } = true;
        public bool CosmeticFiltering { get; set; } = true;
        public bool AutoUpdate { get; set; } = true;
        public bool BypassPrivateNetworks { get; set; } = true;
        public int UpdateIntervalHours { get; set; } = 72;
        public DateTimeOffset? LastSuccessfulUpdateUtc { get; set; }
        public List<string> AllowlistedDomains { get; set; } = new();
        public List<AdBlockSubscription> Subscriptions { get; set; } = CreateDefaultSubscriptions();

        public static List<AdBlockSubscription> CreateDefaultSubscriptions() => new()
        {
            new AdBlockSubscription
            {
                Id = "easylist",
                Name = "EasyList",
                Url = "https://easylist.to/easylist/easylist.txt",
                Enabled = true,
                IsPrivacyList = false
            },
            new AdBlockSubscription
            {
                Id = "easyprivacy",
                Name = "EasyPrivacy",
                Url = "https://easylist.to/easylist/easyprivacy.txt",
                Enabled = true,
                IsPrivacyList = true
            }
        };

        public void Normalize()
        {
            UpdateIntervalHours = Math.Clamp(UpdateIntervalHours, 12, 720);

            AllowlistedDomains = (AllowlistedDomains ?? new List<string>())
                .Select(AdBlockDomain.NormalizeHost)
                .Where(static host => !string.IsNullOrWhiteSpace(host))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static host => host, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Subscriptions ??= new List<AdBlockSubscription>();
            foreach (AdBlockSubscription defaultItem in CreateDefaultSubscriptions())
            {
                if (!Subscriptions.Any(item => item.Id.Equals(defaultItem.Id, StringComparison.OrdinalIgnoreCase)))
                    Subscriptions.Add(defaultItem);
            }

            Subscriptions = Subscriptions
                .Where(item => !string.IsNullOrWhiteSpace(item.Id) && Uri.TryCreate(item.Url, UriKind.Absolute, out _))
                .GroupBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        public AdBlockSettings Clone()
        {
            var copy = new AdBlockSettings
            {
                Enabled = Enabled,
                CosmeticFiltering = CosmeticFiltering,
                AutoUpdate = AutoUpdate,
                BypassPrivateNetworks = BypassPrivateNetworks,
                UpdateIntervalHours = UpdateIntervalHours,
                LastSuccessfulUpdateUtc = LastSuccessfulUpdateUtc,
                AllowlistedDomains = new List<string>(AllowlistedDomains ?? new List<string>()),
                Subscriptions = (Subscriptions ?? new List<AdBlockSubscription>())
                    .Select(item => item.Clone())
                    .ToList()
            };
            // Pas de Normalize : l'original l'est déjà (chargement et enregistrement normalisent).
            return copy;
        }
    }
}
