using System;
using System.Collections.Generic;

namespace MyHomelabBrowser.classes.AdBlock.Models
{
    [Flags]
    public enum AdBlockResourceType
    {
        None = 0,
        Document = 1 << 0,
        SubDocument = 1 << 1,
        Script = 1 << 2,
        Image = 1 << 3,
        Stylesheet = 1 << 4,
        Media = 1 << 5,
        Font = 1 << 6,
        XmlHttpRequest = 1 << 7,
        Fetch = 1 << 8,
        Ping = 1 << 9,
        WebSocket = 1 << 10,
        Other = 1 << 11,
        Any = int.MaxValue
    }

    public sealed class AdBlockRequestContext
    {
        private string? _requestHost;
        private string? _requestUrl;
        private List<string>? _urlTokens;

        public required Uri RequestUri { get; init; }

        /// <summary>
        /// Hôte du document, déjà normalisé.
        /// </summary>
        public required string DocumentHost { get; init; }

        public required AdBlockResourceType ResourceType { get; init; }
        public bool IsThirdParty { get; init; }

        // Valeurs calculées une seule fois par requête, partagées entre règles d'exception et de blocage.
        public string RequestHost => _requestHost ??= AdBlockDomain.NormalizeHost(RequestUri.Host);
        public string RequestUrl => _requestUrl ??= RequestUri.AbsoluteUri;
        internal IReadOnlyList<string> UrlTokens => _urlTokens ??= AdBlockRuleParser.TokenizeUrl(RequestUrl);
    }
}
