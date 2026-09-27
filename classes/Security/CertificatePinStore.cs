using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace MyHomelabBrowser.classes.Security
{
    /// <summary>
    /// Certificat auto-signé accepté par l'utilisateur pour un service local.
    /// </summary>
    public sealed class PinnedCertificate
    {
        public string Authority { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public string Subject { get; set; } = string.Empty;
        public string Issuer { get; set; } = string.Empty;
        public DateTime NotAfter { get; set; }
        public DateTime PinnedAt { get; set; } = DateTime.Now;
    }

    public enum CertificatePinMatch
    {
        NotPinned,
        Matches,
        Changed
    }

    /// <summary>
    /// Confiance au premier usage : un certificat accepté est lié à un hôte et un port.
    /// S'il change ensuite, la page est bloquée et l'utilisateur est prévenu.
    /// </summary>
    public sealed class CertificatePinStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

        private readonly Func<string> _pathProvider;
        private readonly object _gate = new();
        private List<PinnedCertificate> _pins = new();
        private string? _loadedPath;

        public CertificatePinStore(Func<string> pathProvider)
        {
            _pathProvider = pathProvider;
        }

        public event Action? Changed;

        public static string AuthorityFor(Uri uri) => uri.Authority.ToLowerInvariant();

        public static string NormalizeFingerprint(string fingerprint)
            => new string((fingerprint ?? string.Empty).Where(Uri.IsHexDigit).ToArray()).ToUpperInvariant();

        public static string FormatFingerprint(string fingerprint)
        {
            string hex = NormalizeFingerprint(fingerprint);
            return string.Join(":", Enumerable.Range(0, hex.Length / 2).Select(i => hex.Substring(i * 2, 2)));
        }

        public CertificatePinMatch Check(Uri uri, string sha256)
        {
            lock (_gate)
            {
                EnsureLoaded();
                PinnedCertificate? pin = Find(AuthorityFor(uri));
                if (pin == null)
                    return CertificatePinMatch.NotPinned;

                return pin.Sha256 == NormalizeFingerprint(sha256)
                    ? CertificatePinMatch.Matches
                    : CertificatePinMatch.Changed;
            }
        }

        public PinnedCertificate? Get(Uri uri)
        {
            lock (_gate)
            {
                EnsureLoaded();
                return Find(AuthorityFor(uri));
            }
        }

        public IReadOnlyList<PinnedCertificate> GetAll()
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _pins.OrderBy(p => p.Authority, StringComparer.OrdinalIgnoreCase).ToList();
            }
        }

        public void Pin(Uri uri, string sha256, string subject, string issuer, DateTime notAfter)
        {
            lock (_gate)
            {
                EnsureLoaded();
                string authority = AuthorityFor(uri);
                _pins.RemoveAll(p => p.Authority.Equals(authority, StringComparison.OrdinalIgnoreCase));
                _pins.Add(new PinnedCertificate
                {
                    Authority = authority,
                    Sha256 = NormalizeFingerprint(sha256),
                    Subject = subject,
                    Issuer = issuer,
                    NotAfter = notAfter,
                    PinnedAt = DateTime.Now
                });
                Save();
            }

            Changed?.Invoke();
        }

        public bool Remove(string authority)
        {
            bool removed;
            lock (_gate)
            {
                EnsureLoaded();
                removed = _pins.RemoveAll(p => p.Authority.Equals(authority, StringComparison.OrdinalIgnoreCase)) > 0;
                if (removed)
                    Save();
            }

            if (removed)
                Changed?.Invoke();
            return removed;
        }

        private PinnedCertificate? Find(string authority)
            => _pins.FirstOrDefault(p => p.Authority.Equals(authority, StringComparison.OrdinalIgnoreCase));

        private void EnsureLoaded()
        {
            string path = _pathProvider();
            if (string.Equals(path, _loadedPath, StringComparison.OrdinalIgnoreCase))
                return;

            _loadedPath = path;
            _pins = new List<PinnedCertificate>();

            try
            {
                if (File.Exists(path))
                    _pins = JsonSerializer.Deserialize<List<PinnedCertificate>>(File.ReadAllText(path)) ?? new List<PinnedCertificate>();
            }
            catch
            {
                _pins = new List<PinnedCertificate>();
            }
        }

        private void Save()
        {
            if (_loadedPath == null)
                return;

            try
            {
                AtomicFile.WriteAllText(_loadedPath, JsonSerializer.Serialize(_pins, JsonOptions));
            }
            catch
            {
                // La confiance restera valable pour la session en cours.
            }
        }
    }
}
