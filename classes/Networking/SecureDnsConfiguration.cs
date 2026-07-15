using System;
using System.Collections.Generic;

namespace MyHomelabBrowser.classes
{
    public static class SecureDnsConfiguration
    {
        private static readonly IReadOnlyDictionary<BrowserSettings.SecureDnsProvider, string> Presets =
            new Dictionary<BrowserSettings.SecureDnsProvider, string>
            {
                [BrowserSettings.SecureDnsProvider.Cloudflare] = "https://cloudflare-dns.com/dns-query{?dns}",
                [BrowserSettings.SecureDnsProvider.Google] = "https://dns.google/dns-query{?dns}",
                [BrowserSettings.SecureDnsProvider.Quad9] = "https://dns.quad9.net/dns-query{?dns}",
                [BrowserSettings.SecureDnsProvider.AdGuard] = "https://dns.adguard-dns.com/dns-query{?dns}"
            };

        public static string BuildAdditionalBrowserArguments(
            BrowserSettings settings,
            string existingArguments)
        {
            ArgumentNullException.ThrowIfNull(settings);

            var arguments = new List<string>();
            if (!string.IsNullOrWhiteSpace(existingArguments))
                arguments.Add(existingArguments.Trim());

            switch (settings.DnsMode)
            {
                case BrowserSettings.SecureDnsMode.System:
                    arguments.Add("--dns-over-https-mode=off");
                    break;

                case BrowserSettings.SecureDnsMode.Automatic:
                    arguments.Add("--dns-over-https-mode=automatic");
                    break;

                case BrowserSettings.SecureDnsMode.Secure:
                    if (TryGetTemplate(settings, out var template, out _))
                    {
                        arguments.Add("--dns-over-https-mode=secure");
                        arguments.Add($"--dns-over-https-templates={QuoteArgumentValue(template)}");
                    }
                    else
                    {
                        arguments.Add("--dns-over-https-mode=off");
                    }
                    break;
            }

            return string.Join(" ", arguments);
        }

        public static bool TryGetTemplate(
            BrowserSettings settings,
            out string template,
            out string validationMessage)
        {
            ArgumentNullException.ThrowIfNull(settings);

            if (settings.DnsProvider == BrowserSettings.SecureDnsProvider.Custom)
            {
                template = (settings.SecureDnsCustomTemplate ?? string.Empty).Trim();
                if (!IsValidHttpsTemplate(template))
                {
                    validationMessage = "L’adresse DNS personnalisée doit être une URL HTTPS valide.";
                    return false;
                }

                validationMessage = string.Empty;
                return true;
            }

            if (Presets.TryGetValue(settings.DnsProvider, out var preset))
            {
                template = preset;
                validationMessage = string.Empty;
                return true;
            }

            template = string.Empty;
            validationMessage = "Le fournisseur DNS sélectionné n’est pas reconnu.";
            return false;
        }

        public static string GetProviderTemplate(BrowserSettings.SecureDnsProvider provider)
            => Presets.TryGetValue(provider, out var template) ? template : string.Empty;

        public static bool IsValidHttpsTemplate(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return false;

            var normalized = value.Trim()
                .Replace("{?dns}", string.Empty, StringComparison.OrdinalIgnoreCase)
                .Replace("{dns}", string.Empty, StringComparison.OrdinalIgnoreCase);

            return Uri.TryCreate(normalized, UriKind.Absolute, out var uri)
                   && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                   && !string.IsNullOrWhiteSpace(uri.Host);
        }

        private static string QuoteArgumentValue(string value)
            => $"\"{value.Replace("\"", "\\\"")}\"";
    }
}
