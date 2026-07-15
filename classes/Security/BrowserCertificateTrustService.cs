using Microsoft.Web.WebView2.Core;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading.Tasks;

namespace MyHomelabBrowser.classes.Security
{
    public sealed class BrowserCertificateTrustService
    {
        private const string ServerAuthenticationOid = "1.3.6.1.5.5.7.3.1";
        private readonly object _sync = new();
        private readonly List<AttachedWebView> _attachedWebViews = new();

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

                if (args.ErrorStatus is CoreWebView2WebErrorStatus.CertificateExpired
                    or CoreWebView2WebErrorStatus.CertificateCommonNameIsIncorrect
                    or CoreWebView2WebErrorStatus.CertificateRevoked
                    or CoreWebView2WebErrorStatus.ClientCertificateContainsErrors)
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

                args.Action = trusted
                    ? CoreWebView2ServerCertificateErrorAction.AlwaysAllow
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
