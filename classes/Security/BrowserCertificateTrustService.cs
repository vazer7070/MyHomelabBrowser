using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.Security
{
    /// <summary>
    /// Certificat non reconnu présenté par un service local : l'utilisateur décide
    /// s'il lui fait confiance (confiance au premier usage).
    /// </summary>
    public sealed record CertificatePromptRequest(
        Uri Uri,
        string Sha256,
        string Subject,
        string Issuer,
        DateTime NotBefore,
        DateTime NotAfter,
        bool IsSelfSigned,
        bool NameMismatch,
        PinnedCertificate? PreviousPin)
    {
        public string Authority => CertificatePinStore.AuthorityFor(Uri);
        public bool IsExpired => NotAfter < DateTime.Now || NotBefore > DateTime.Now;
        public bool HasChanged => PreviousPin != null;
    }

    public sealed class BrowserCertificateTrustService
    {
        private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
        private const string PinFileName = "pinned-certificates.json";
        private readonly object _sync = new();
        private readonly List<AttachedWebView> _attachedWebViews = new();
        private readonly ConcurrentDictionary<string, CertificatePinStore> _pinStores = new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<string, Task<bool>> _pendingPrompts = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Demande à l'utilisateur s'il fait confiance au certificat (appelé sur le thread UI).
        /// Sans gestionnaire, la page d'avertissement du moteur s'affiche.
        /// </summary>
        public Func<CertificatePromptRequest, Task<bool>>? PromptAsync { get; set; }

        public CertificatePinStore GetPinStore(string profileRoot)
        {
            string root = Path.GetFullPath(profileRoot);
            return _pinStores.GetOrAdd(root, r => new CertificatePinStore(() => Path.Combine(r, PinFileName)));
        }

        public void Attach(CoreWebView2 coreWebView, string profileRoot)
        {
            ArgumentNullException.ThrowIfNull(coreWebView);
            if (string.IsNullOrWhiteSpace(profileRoot))
                throw new ArgumentException("Le dossier du profil est obligatoire.", nameof(profileRoot));

            string normalizedRoot = Path.GetFullPath(profileRoot);

            lock (_sync)
            {
                CleanupDeadEntriesLocked();

                if (_attachedWebViews.Any(entry =>
                        entry.TryGetTarget(out var existing)
                        && ReferenceEquals(existing, coreWebView)))
                {
                    return;
                }

                EventHandler<CoreWebView2ServerCertificateErrorDetectedEventArgs> handler =
                    (_, args) => HandleCertificateErrorAsync(args, normalizedRoot);

                coreWebView.ServerCertificateErrorDetected += handler;
                _attachedWebViews.Add(new AttachedWebView(coreWebView, normalizedRoot, handler));
            }
        }

        public async Task NotifyStoreChangedAsync(string profileRoot)
        {
            if (string.IsNullOrWhiteSpace(profileRoot))
                return;

            string normalizedRoot = Path.GetFullPath(profileRoot);
            List<CoreWebView2> matchingWebViews;

            lock (_sync)
            {
                CleanupDeadEntriesLocked();
                matchingWebViews = _attachedWebViews
                    .Where(entry => string.Equals(
                        entry.ProfileRoot,
                        normalizedRoot,
                        StringComparison.OrdinalIgnoreCase))
                    .Select(entry => entry.TryGetTarget(out var core) ? core : null)
                    .Where(core => core is not null)
                    .Cast<CoreWebView2>()
                    .ToList();
            }

            foreach (CoreWebView2 core in matchingWebViews)
            {
                try
                {
                    await core.ClearServerCertificateErrorActionsAsync();
                }
                catch
                {
                    // Le WebView a pu être fermé entre la collecte et l’appel.
                }
            }
        }

        private async void HandleCertificateErrorAsync(
            CoreWebView2ServerCertificateErrorDetectedEventArgs args,
            string profileRoot)
        {
            var deferral = args.GetDeferral();

            try
            {
                if (!Uri.TryCreate(args.RequestUri, UriKind.Absolute, out var requestUri)
                    || requestUri.Scheme != Uri.UriSchemeHttps)
                {
                    args.Action = CoreWebView2ServerCertificateErrorAction.Default;
                    return;
                }

                bool isLocal = UrlResolver.IsLocalHost(requestUri.Host);

                if (args.ErrorStatus is CoreWebView2WebErrorStatus.CertificateRevoked
                    or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors)
                {
                    args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
                    return;
                }

                // Sur Internet, un certificat expiré ou au mauvais nom n'est jamais accepté.
                // Pour un service local (accès par IP, certificat d'usine), l'utilisateur décide.
                if (!isLocal && args.ErrorStatus is CoreWebView2WebErrorStatus.CertificateExpired
                    or CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect)
                {
                    args.Action = CoreWebView2ServerCertificateErrorAction.Cancel;
                    return;
                }

                byte[] leafRaw;
                string[] issuerChainPem;

                try
                {
                    using X509Certificate2 leaf = args.ServerCertificate.ToX509Certificate2();
                    leafRaw = leaf.Export(X509ContentType.Cert);
                    issuerChainPem = args.ServerCertificate.PemEncodedIssuerCertificateChain?
                        .Where(value => !string.IsNullOrWhiteSpace(value))
                        .ToArray()
                        ?? Array.Empty<string>();
                }
                catch
                {
                    args.Action = CoreWebView2ServerCertificateErrorAction.Default;
                    return;
                }

                bool trusted = await Task.Run(() => ValidateWithBrowserAuthorities(
                    leafRaw,
                    issuerChainPem,
                    requestUri,
                    profileRoot));

                if (trusted)
                {
                    args.Action = CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
                    return;
                }

                args.Action = isLocal
                    ? await DecideLocalCertificateAsync(requestUri, leafRaw, profileRoot)
                    : CoreWebView2ServerCertificateErrorAction.Default;
            }
            catch
            {
                args.Action = CoreWebView2ServerCertificateErrorAction.Default;
            }
            finally
            {
                deferral.Complete();
            }
        }

        /// <summary>
        /// Confiance au premier usage : un certificat accepté est épinglé pour l'hôte et le port.
        /// </summary>
        private async Task<CoreWebView2ServerCertificateErrorAction> DecideLocalCertificateAsync(
            Uri requestUri,
            byte[] leafRaw,
            string profileRoot)
        {
            using var leaf = X509CertificateLoader.LoadCertificate(leafRaw);
            string sha256 = leaf.GetCertHashString(HashAlgorithmName.SHA256);
            CertificatePinStore pins = GetPinStore(profileRoot);

            CertificatePinMatch match = pins.Check(requestUri, sha256);
            if (match == CertificatePinMatch.Matches)
                return CoreWebView2ServerCertificateErrorAction.AlwaysAllow;

            Func<CertificatePromptRequest, Task<bool>>? prompt = PromptAsync;
            if (prompt == null)
                return CoreWebView2ServerCertificateErrorAction.Default;

            var request = new CertificatePromptRequest(
                requestUri,
                sha256,
                leaf.Subject,
                leaf.Issuer,
                leaf.NotBefore,
                leaf.NotAfter,
                IsSelfSigned: string.Equals(leaf.Subject, leaf.Issuer, StringComparison.OrdinalIgnoreCase),
                NameMismatch: !leaf.MatchesHostname(requestUri.IdnHost, allowWildcards: true, allowCommonName: true),
                PreviousPin: match == CertificatePinMatch.Changed ? pins.Get(requestUri) : null);

            // Les ressources d'une même page déclenchent plusieurs erreurs : une seule question.
            string key = profileRoot + "|" + request.Authority + "|" + CertificatePinStore.NormalizeFingerprint(sha256);
            Task<bool> decision = _pendingPrompts.GetOrAdd(key, _ => prompt(request));

            bool accepted;
            try
            {
                accepted = await decision;
            }
            finally
            {
                _pendingPrompts.TryRemove(key, out _);
            }

            if (!accepted)
                return CoreWebView2ServerCertificateErrorAction.Cancel;

            pins.Pin(requestUri, sha256, leaf.Subject, leaf.Issuer, leaf.NotAfter);
            return CoreWebView2ServerCertificateErrorAction.AlwaysAllow;
        }

        private static bool ValidateWithBrowserAuthorities(
            byte[] leafRaw,
            IReadOnlyList<string> issuerChainPem,
            Uri requestUri,
            string profileRoot)
        {
            using var leaf = X509CertificateLoader.LoadCertificate(leafRaw);

            DateTime now = DateTime.Now;
            if (leaf.NotBefore > now || leaf.NotAfter < now)
                return false;

            if (!leaf.MatchesHostname(
                    requestUri.IdnHost,
                    allowWildcards: true,
                    allowCommonName: true))
            {
                return false;
            }

            var store = new CertificateStoreService(profileRoot);
            IReadOnlyList<X509Certificate2> roots = store.LoadBrowserRoots();
            IReadOnlyList<X509Certificate2> storedIntermediates = store.LoadBrowserIntermediates();
            var eventChain = new List<X509Certificate2>();

            try
            {
                if (roots.Count == 0)
                    return false;

                foreach (string pem in issuerChainPem)
                {
                    try
                    {
                        var certificate = X509Certificate2.CreateFromPem(pem);

                        if (certificate.RawData.AsSpan().SequenceEqual(leaf.RawData))
                        {
                            certificate.Dispose();
                            continue;
                        }

                        eventChain.Add(certificate);
                    }
                    catch (CryptographicException)
                    {
                        // Une entrée de chaîne malformée ne rend pas les autres inutilisables.
                    }
                }

                using var chain = new X509Chain();
                chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
                chain.ChainPolicy.VerificationFlags = X509VerificationFlags.NoFlag;
                chain.ChainPolicy.VerificationTime = now;
                chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(4);
                chain.ChainPolicy.ApplicationPolicy.Add(new Oid(ServerAuthenticationOid));

                foreach (X509Certificate2 root in roots)
                    chain.ChainPolicy.CustomTrustStore.Add(root);

                foreach (X509Certificate2 intermediate in storedIntermediates)
                    chain.ChainPolicy.ExtraStore.Add(intermediate);

                foreach (X509Certificate2 certificate in eventChain)
                    chain.ChainPolicy.ExtraStore.Add(certificate);

                if (!chain.Build(leaf))
                    return false;

                X509ChainElement? finalElement = chain.ChainElements.Count > 0
                    ? chain.ChainElements[^1]
                    : null;

                if (finalElement is null)
                    return false;

                return roots.Any(root =>
                    root.RawData.AsSpan().SequenceEqual(finalElement.Certificate.RawData));
            }
            finally
            {
                foreach (X509Certificate2 certificate in roots)
                    certificate.Dispose();

                foreach (X509Certificate2 certificate in storedIntermediates)
                    certificate.Dispose();

                foreach (X509Certificate2 certificate in eventChain)
                    certificate.Dispose();
            }
        }

        private void CleanupDeadEntriesLocked()
            => _attachedWebViews.RemoveAll(entry => !entry.TryGetTarget(out _));

        private sealed class AttachedWebView
        {
            private readonly WeakReference<CoreWebView2> _coreWebView;

            public AttachedWebView(
                CoreWebView2 coreWebView,
                string profileRoot,
                EventHandler<CoreWebView2ServerCertificateErrorDetectedEventArgs> handler)
            {
                _coreWebView = new WeakReference<CoreWebView2>(coreWebView);
                ProfileRoot = profileRoot;
                Handler = handler;
            }

            public string ProfileRoot { get; }
            public EventHandler<CoreWebView2ServerCertificateErrorDetectedEventArgs> Handler { get; }

            public bool TryGetTarget(out CoreWebView2? coreWebView)
                => _coreWebView.TryGetTarget(out coreWebView);
        }
    }

    public static class BrowserCertificateTrustHost
    {
        public static BrowserCertificateTrustService Current { get; } = new();
    }
}
