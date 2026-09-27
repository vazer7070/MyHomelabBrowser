using System;
using System.Security.Cryptography;
using System.Text;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.classes.Security
{
    /// <summary>
    /// Paramètres d'un code à usage unique (TOTP, RFC 6238).
    /// </summary>
    public sealed record TotpParameters(byte[] Secret, int Digits = 6, int PeriodSeconds = 30, string Algorithm = "SHA1", string? Issuer = null, string? Account = null);

    /// <summary>
    /// Codes TOTP compatibles avec les applications d'authentification (Google Authenticator,
    /// Aegis, 2FAS…). Accepte une clé en base32 ou une adresse otpauth://totp/… (QR code).
    /// </summary>
    public static class Totp
    {
        private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

        public static bool TryParse(string? input, out TotpParameters parameters, out string error)
        {
            parameters = new TotpParameters(Array.Empty<byte>());
            error = string.Empty;
            string value = (input ?? string.Empty).Trim();

            if (value.Length == 0)
            {
                error = Tr("La clé est vide.");
                return false;
            }

            if (value.StartsWith("otpauth://", StringComparison.OrdinalIgnoreCase))
                return TryParseUri(value, out parameters, out error);

            if (!TryDecodeBase32(value, out byte[] secret) || secret.Length < 10)
            {
                error = Tr("Clé invalide : attendu une clé en base32 (lettres A à Z et chiffres 2 à 7) ou une adresse otpauth://.");
                return false;
            }

            parameters = new TotpParameters(secret);
            return true;
        }

        private static bool TryParseUri(string value, out TotpParameters parameters, out string error)
        {
            parameters = new TotpParameters(Array.Empty<byte>());
            error = string.Empty;

            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri) ||
                !uri.Host.Equals("totp", StringComparison.OrdinalIgnoreCase))
            {
                error = Tr("Seules les adresses otpauth://totp/… sont prises en charge.");
                return false;
            }

            string? secretText = null;
            string? issuer = null;
            int digits = 6;
            int period = 30;
            string algorithm = "SHA1";

            foreach (string pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = pair.IndexOf('=');
                if (equals <= 0)
                    continue;

                string key = pair[..equals].ToLowerInvariant();
                string raw = Uri.UnescapeDataString(pair[(equals + 1)..].Replace('+', ' '));

                switch (key)
                {
                    case "secret":
                        secretText = raw;
                        break;
                    case "issuer":
                        issuer = raw;
                        break;
                    case "digits" when int.TryParse(raw, out int d) && d is >= 6 and <= 8:
                        digits = d;
                        break;
                    case "period" when int.TryParse(raw, out int p) && p is >= 10 and <= 120:
                        period = p;
                        break;
                    case "algorithm":
                        algorithm = raw.ToUpperInvariant();
                        break;
                }
            }

            if (algorithm is not ("SHA1" or "SHA256" or "SHA512"))
            {
                error = Tr("Algorithme non pris en charge : ") + algorithm;
                return false;
            }

            if (secretText == null || !TryDecodeBase32(secretText, out byte[] secret) || secret.Length < 10)
            {
                error = Tr("L’adresse otpauth ne contient pas de clé valide.");
                return false;
            }

            string label = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            string? account = label;
            int colon = label.IndexOf(':');
            if (colon >= 0)
            {
                issuer ??= label[..colon].Trim();
                account = label[(colon + 1)..].Trim();
            }

            parameters = new TotpParameters(secret, digits, period, algorithm, issuer, string.IsNullOrWhiteSpace(account) ? null : account);
            return true;
        }

        public static string Generate(TotpParameters parameters, DateTimeOffset now)
        {
            long counter = now.ToUnixTimeSeconds() / parameters.PeriodSeconds;
            return GenerateForCounter(parameters, counter);
        }

        public static int SecondsRemaining(TotpParameters parameters, DateTimeOffset now)
            => parameters.PeriodSeconds - (int)(now.ToUnixTimeSeconds() % parameters.PeriodSeconds);

        internal static string GenerateForCounter(TotpParameters parameters, long counter)
        {
            Span<byte> message = stackalloc byte[8];
            for (int i = 7; i >= 0; i--)
            {
                message[i] = (byte)(counter & 0xFF);
                counter >>= 8;
            }

            byte[] hash = parameters.Algorithm switch
            {
                "SHA256" => HMACSHA256.HashData(parameters.Secret, message),
                "SHA512" => HMACSHA512.HashData(parameters.Secret, message),
                _ => HMACSHA1.HashData(parameters.Secret, message)
            };

            int offset = hash[^1] & 0x0F;
            int binary = ((hash[offset] & 0x7F) << 24)
                         | (hash[offset + 1] << 16)
                         | (hash[offset + 2] << 8)
                         | hash[offset + 3];

            int modulo = 1;
            for (int i = 0; i < parameters.Digits; i++)
                modulo *= 10;

            return (binary % modulo).ToString().PadLeft(parameters.Digits, '0');
        }

        public static bool TryDecodeBase32(string input, out byte[] bytes)
        {
            var output = new System.Collections.Generic.List<byte>();
            int buffer = 0;
            int bitsLeft = 0;

            foreach (char raw in input)
            {
                if (raw is ' ' or '-' or '=')
                    continue;

                int value = Base32Alphabet.IndexOf(char.ToUpperInvariant(raw));
                if (value < 0)
                {
                    bytes = Array.Empty<byte>();
                    return false;
                }

                buffer = (buffer << 5) | value;
                bitsLeft += 5;
                if (bitsLeft >= 8)
                {
                    output.Add((byte)(buffer >> (bitsLeft - 8)));
                    bitsLeft -= 8;
                }
            }

            bytes = output.ToArray();
            return bytes.Length > 0;
        }

        public static string EncodeBase32(byte[] data)
        {
            var builder = new StringBuilder();
            int buffer = 0;
            int bitsLeft = 0;

            foreach (byte b in data)
            {
                buffer = (buffer << 8) | b;
                bitsLeft += 8;
                while (bitsLeft >= 5)
                {
                    builder.Append(Base32Alphabet[(buffer >> (bitsLeft - 5)) & 31]);
                    bitsLeft -= 5;
                }
            }

            if (bitsLeft > 0)
                builder.Append(Base32Alphabet[(buffer << (5 - bitsLeft)) & 31]);

            return builder.ToString();
        }

        /// <summary>
        /// « 123456 » devient « 123 456 », plus lisible à recopier.
        /// </summary>
        public static string FormatForDisplay(string code)
            => code.Length is 6 or 8 ? code[..(code.Length / 2)] + " " + code[(code.Length / 2)..] : code;
    }
}
