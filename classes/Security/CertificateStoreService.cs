using MyHomelabBrowser.classes.Profiles;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography.X509Certificates;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.classes.Security
{
    public sealed class CertificateStoreService
    {
        private const string BrowserCertificatesFolder = "certificates";
        private const string BrowserRootsFolder = "roots";
        private const string BrowserIntermediatesFolder = "intermediates";

        public CertificateStoreService()
            : this(AppDataContext.Root)
        {
        }

        public CertificateStoreService(string profileRoot)
        {
            if (string.IsNullOrWhiteSpace(profileRoot))
                throw new ArgumentException("Le dossier du profil est obligatoire.", nameof(profileRoot));

            ProfileRoot = Path.GetFullPath(profileRoot);
            BrowserStoreRoot = Path.Combine(ProfileRoot, BrowserCertificatesFolder);
            BrowserRootsPath = Path.Combine(BrowserStoreRoot, BrowserRootsFolder);
            BrowserIntermediatesPath = Path.Combine(BrowserStoreRoot, BrowserIntermediatesFolder);

            Directory.CreateDirectory(BrowserRootsPath);
            Directory.CreateDirectory(BrowserIntermediatesPath);
        }

        public string ProfileRoot { get; }
        public string BrowserStoreRoot { get; }
        public string BrowserRootsPath { get; }
        public string BrowserIntermediatesPath { get; }

        public IReadOnlyList<CertificateEntry> GetAuthorities(bool includeSystem = true)
        {
            var entries = new List<CertificateEntry>();

            ReadBrowserDirectory(entries, BrowserRootsPath, CertificateKind.Root);
            ReadBrowserDirectory(entries, BrowserIntermediatesPath, CertificateKind.Intermediate);

            if (includeSystem)
            {
                ReadWindowsStore(entries, StoreName.Root, StoreLocation.CurrentUser,
                    CertificateSource.WindowsUser, Tr("Windows · Utilisateur"), Tr("Racines de confiance"));
                ReadWindowsStore(entries, StoreName.CertificateAuthority, StoreLocation.CurrentUser,
                    CertificateSource.WindowsUser, Tr("Windows · Utilisateur"), Tr("Autorités intermédiaires"));
                ReadWindowsStore(entries, StoreName.Root, StoreLocation.LocalMachine,
                    CertificateSource.WindowsMachine, Tr("Windows · Ordinateur"), Tr("Racines de confiance"));
                ReadWindowsStore(entries, StoreName.CertificateAuthority, StoreLocation.LocalMachine,
                    CertificateSource.WindowsMachine, Tr("Windows · Ordinateur"), Tr("Autorités intermédiaires"));
            }

            return entries
                .OrderBy(entry => entry.SourceSortOrder)
                .ThenBy(entry => entry.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(entry => entry.StoreDisplay, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }

        public CertificateImportResult ImportAuthorityForBrowser(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return CertificateImportResult.Failed(Tr("Aucun fichier n’a été sélectionné."));

            try
            {
                using var certificate = X509CertificateLoader.LoadCertificateFromFile(filePath);
                var validation = ValidateAuthority(certificate);
                if (!validation.Success)
                    return validation;

                bool selfSigned = IsSelfSigned(certificate);
                string destinationFolder = selfSigned ? BrowserRootsPath : BrowserIntermediatesPath;
                string thumbprint = NormalizeThumbprint(certificate.Thumbprint);
                string destination = Path.Combine(destinationFolder, $"{thumbprint}.cer");

                string? existingPath = FindBrowserCertificatePath(thumbprint);
                if (existingPath is not null)
                {
                    return CertificateImportResult.Succeeded(
                        Tr("Cette autorité est déjà enregistrée pour ce profil PommeBrowser."),
                        selfSigned ? Tr("PommeBrowser · Racines") : Tr("PommeBrowser · Intermédiaires"));
                }

                Directory.CreateDirectory(destinationFolder);
                string temporary = destination + ".tmp";
                File.WriteAllBytes(temporary, certificate.Export(X509ContentType.Cert));
                File.Move(temporary, destination, overwrite: true);

                return CertificateImportResult.Succeeded(
                    selfSigned
                        ? Tr("L’autorité racine a été ajoutée uniquement à ce profil PommeBrowser.")
                        : Tr("L’autorité intermédiaire a été ajoutée uniquement à ce profil PommeBrowser."),
                    selfSigned ? Tr("PommeBrowser · Racines") : Tr("PommeBrowser · Intermédiaires"));
            }
            catch (Exception ex)
            {
                return CertificateImportResult.Failed(
                    Tr("Impossible d’importer ce certificat : {0}", ex.Message));
            }
        }

        public CertificateImportResult ImportAuthorityToWindowsCurrentUser(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                return CertificateImportResult.Failed(Tr("Aucun fichier n’a été sélectionné."));

            try
            {
                using var certificate = X509CertificateLoader.LoadCertificateFromFile(filePath);
                var validation = ValidateAuthority(certificate);
                if (!validation.Success)
                    return validation;

                bool selfSigned = IsSelfSigned(certificate);
                StoreName targetStore = selfSigned ? StoreName.Root : StoreName.CertificateAuthority;

                using var store = new X509Store(targetStore, StoreLocation.CurrentUser);
                store.Open(OpenFlags.ReadWrite);

                bool alreadyPresent = store.Certificates
                    .Find(X509FindType.FindByThumbprint, certificate.Thumbprint, validOnly: false)
                    .Count > 0;

                if (alreadyPresent)
                {
                    return CertificateImportResult.Succeeded(
                        Tr("Cette autorité est déjà installée dans le magasin Windows de l’utilisateur."),
                        selfSigned ? Tr("Windows · Racines utilisateur") : Tr("Windows · Intermédiaires utilisateur"));
                }

                store.Add(certificate);

                return CertificateImportResult.Succeeded(
                    Tr("L’autorité a été installée dans Windows pour l’utilisateur courant."),
                    selfSigned ? Tr("Windows · Racines utilisateur") : Tr("Windows · Intermédiaires utilisateur"));
            }
            catch (Exception ex)
            {
                return CertificateImportResult.Failed(
                    Tr("Impossible d’installer ce certificat dans Windows : {0}", ex.Message));
            }
        }

        public CertificateOperationResult RemoveBrowserAuthority(CertificateEntry entry)
        {
            if (entry.Source != CertificateSource.Browser || string.IsNullOrWhiteSpace(entry.StoragePath))
                return CertificateOperationResult.Failed(Tr("Ce certificat n’est pas géré par PommeBrowser."));

            try
            {
                string fullPath = Path.GetFullPath(entry.StoragePath);
                string allowedRoot = Path.GetFullPath(BrowserStoreRoot)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;

                if (!fullPath.StartsWith(allowedRoot, StringComparison.OrdinalIgnoreCase))
                    return CertificateOperationResult.Failed(Tr("Le chemin du certificat est invalide."));

                if (!File.Exists(fullPath))
                    return CertificateOperationResult.Succeeded(Tr("Le certificat n’était déjà plus présent."));

                File.Delete(fullPath);
                return CertificateOperationResult.Succeeded(
                    Tr("L’autorité a été retirée de ce profil PommeBrowser."));
            }
            catch (Exception ex)
            {
                return CertificateOperationResult.Failed(
                    Tr("Impossible de retirer cette autorité : {0}", ex.Message));
            }
        }

        internal IReadOnlyList<X509Certificate2> LoadBrowserRoots()
            => LoadBrowserCertificates(BrowserRootsPath);

        internal IReadOnlyList<X509Certificate2> LoadBrowserIntermediates()
            => LoadBrowserCertificates(BrowserIntermediatesPath);

        private IReadOnlyList<X509Certificate2> LoadBrowserCertificates(string directory)
        {
            var certificates = new List<X509Certificate2>();
            if (!Directory.Exists(directory))
                return certificates;

            foreach (string path in Directory.EnumerateFiles(directory, "*.cer", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    certificates.Add(X509CertificateLoader.LoadCertificateFromFile(path));
                }
                catch
                {
                    // Un fichier illisible ne doit pas désactiver toutes les autres CA du profil.
                }
            }

            return certificates;
        }

        private void ReadBrowserDirectory(
            ICollection<CertificateEntry> target,
            string directory,
            CertificateKind kind)
        {
            if (!Directory.Exists(directory))
                return;

            foreach (string path in Directory.EnumerateFiles(directory, "*.cer", SearchOption.TopDirectoryOnly))
            {
                try
                {
                    using var certificate = X509CertificateLoader.LoadCertificateFromFile(path);
                    target.Add(CertificateEntry.FromBrowserCertificate(certificate, path, kind));
                }
                catch
                {
                    // Le gestionnaire continue d’afficher les autres certificats.
                }
            }
        }

        private static void ReadWindowsStore(
            ICollection<CertificateEntry> target,
            StoreName storeName,
            StoreLocation location,
            CertificateSource source,
            string scope,
            string storeLabel)
        {
            try
            {
                using var store = new X509Store(storeName, location);
                store.Open(OpenFlags.ReadOnly | OpenFlags.OpenExistingOnly);

                foreach (var certificate in store.Certificates)
                {
                    target.Add(CertificateEntry.FromWindowsCertificate(
                        certificate,
                        source,
                        location,
                        storeName,
                        scope,
                        storeLabel));
                }
            }
            catch
            {
                // Certains magasins système peuvent être indisponibles selon les droits.
            }
        }

        private static CertificateImportResult ValidateAuthority(X509Certificate2 certificate)
        {
            if (certificate.HasPrivateKey)
            {
                return CertificateImportResult.Failed(
                    Tr("Le fichier contient une clé privée. Seuls les certificats publics d’autorités sont acceptés."));
            }

            var basicConstraints = certificate.Extensions
                .OfType<X509BasicConstraintsExtension>()
                .FirstOrDefault();

            if (basicConstraints is null || !basicConstraints.CertificateAuthority)
            {
                return CertificateImportResult.Failed(
                    Tr("Ce certificat n’est pas déclaré comme autorité de certification."));
            }

            var keyUsage = certificate.Extensions
                .OfType<X509KeyUsageExtension>()
                .FirstOrDefault();

            if (keyUsage is not null
                && !keyUsage.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign))
            {
                return CertificateImportResult.Failed(
                    Tr("Ce certificat ne possède pas le droit de signer d’autres certificats."));
            }

            DateTime now = DateTime.Now;
            if (certificate.NotAfter < now)
                return CertificateImportResult.Failed(Tr("Cette autorité de certification est expirée."));

            if (certificate.NotBefore > now)
                return CertificateImportResult.Failed(Tr("Cette autorité de certification n’est pas encore valide."));

            return CertificateImportResult.Succeeded(Tr("Certificat valide."), "Validation");
        }

        private string? FindBrowserCertificatePath(string normalizedThumbprint)
        {
            foreach (string directory in new[] { BrowserRootsPath, BrowserIntermediatesPath })
            {
                string candidate = Path.Combine(directory, $"{normalizedThumbprint}.cer");
                if (File.Exists(candidate))
                    return candidate;
            }

            return null;
        }

        private static bool IsSelfSigned(X509Certificate2 certificate)
            => certificate.SubjectName.RawData.SequenceEqual(certificate.IssuerName.RawData);

        private static string NormalizeThumbprint(string? thumbprint)
            => (thumbprint ?? Guid.NewGuid().ToString("N"))
                .Replace(" ", string.Empty, StringComparison.Ordinal)
                .ToUpperInvariant();
    }

    public enum CertificateSource
    {
        Browser,
        WindowsUser,
        WindowsMachine
    }

    public enum CertificateKind
    {
        Root,
        Intermediate
    }

    public sealed class CertificateEntry
    {
        public required string DisplayName { get; init; }
        public required string Subject { get; init; }
        public required string Issuer { get; init; }
        public required string Thumbprint { get; init; }
        public required string SerialNumber { get; init; }
        public required string SignatureAlgorithm { get; init; }
        public required string PublicKeyAlgorithm { get; init; }
        public required string Scope { get; init; }
        public required string StoreLabel { get; init; }
        public required CertificateSource Source { get; init; }
        public required CertificateKind Kind { get; init; }
        public StoreLocation? StoreLocation { get; init; }
        public StoreName? StoreName { get; init; }
        public string? StoragePath { get; init; }
        public required DateTime NotBefore { get; init; }
        public required DateTime NotAfter { get; init; }
        public required bool IsCertificateAuthority { get; init; }
        public required bool IsSelfSigned { get; init; }

        public bool CanRemove => Source == CertificateSource.Browser;
        public bool IsBrowserManaged => Source == CertificateSource.Browser;
        public bool IsExpired => NotAfter < DateTime.Now;
        public bool IsNotYetValid => NotBefore > DateTime.Now;
        public bool IsValidNow => !IsExpired && !IsNotYetValid;
        public int SourceSortOrder => Source switch
        {
            CertificateSource.Browser => 0,
            CertificateSource.WindowsUser => 1,
            _ => 2
        };

        public string ValidityLabel => IsExpired
            ? Tr("Expiré")
            : IsNotYetValid
                ? Tr("Pas encore valide")
                : Tr("Valide");

        public string StoreDisplay => $"{Scope} · {StoreLabel}";
        public string ExpirationDisplay => NotAfter.ToString("dd/MM/yyyy");
        public string ExpirationLabel => Tr("Expire le {0}", ExpirationDisplay);
        public string IssuerDisplay => Tr("Émis par : {0}", Issuer);
        public string ValidityPeriodDisplay => $"{NotBefore:dd/MM/yyyy HH:mm} → {NotAfter:dd/MM/yyyy HH:mm}";
        public string SourceBadge => Source switch
        {
            CertificateSource.Browser => "PommeBrowser",
            CertificateSource.WindowsUser => Tr("Windows utilisateur"),
            _ => Tr("Windows ordinateur")
        };

        public static CertificateEntry FromBrowserCertificate(
            X509Certificate2 certificate,
            string storagePath,
            CertificateKind kind)
            => FromCertificate(
                certificate,
                CertificateSource.Browser,
                null,
                null,
                "PommeBrowser",
                kind == CertificateKind.Root ? Tr("Racines privées") : Tr("Intermédiaires privés"),
                storagePath,
                kind);

        public static CertificateEntry FromWindowsCertificate(
            X509Certificate2 certificate,
            CertificateSource source,
            StoreLocation location,
            StoreName storeName,
            string scope,
            string storeLabel)
            => FromCertificate(
                certificate,
                source,
                location,
                storeName,
                scope,
                storeLabel,
                null,
                storeName == System.Security.Cryptography.X509Certificates.StoreName.Root
                    ? CertificateKind.Root
                    : CertificateKind.Intermediate);

        private static CertificateEntry FromCertificate(
            X509Certificate2 certificate,
            CertificateSource source,
            StoreLocation? location,
            StoreName? storeName,
            string scope,
            string storeLabel,
            string? storagePath,
            CertificateKind kind)
        {
            var basicConstraints = certificate.Extensions
                .OfType<X509BasicConstraintsExtension>()
                .FirstOrDefault();

            string displayName = certificate.GetNameInfo(X509NameType.SimpleName, forIssuer: false);
            if (string.IsNullOrWhiteSpace(displayName))
                displayName = certificate.Subject;

            return new CertificateEntry
            {
                DisplayName = displayName,
                Subject = certificate.Subject,
                Issuer = certificate.Issuer,
                Thumbprint = certificate.Thumbprint ?? string.Empty,
                SerialNumber = certificate.SerialNumber ?? string.Empty,
                SignatureAlgorithm = certificate.SignatureAlgorithm?.FriendlyName
                                     ?? certificate.SignatureAlgorithm?.Value
                                     ?? string.Empty,
                PublicKeyAlgorithm = certificate.PublicKey?.Oid?.FriendlyName
                                     ?? certificate.PublicKey?.Oid?.Value
                                     ?? string.Empty,
                Scope = scope,
                StoreLabel = storeLabel,
                Source = source,
                Kind = kind,
                StoreLocation = location,
                StoreName = storeName,
                StoragePath = storagePath,
                NotBefore = certificate.NotBefore,
                NotAfter = certificate.NotAfter,
                IsCertificateAuthority = basicConstraints?.CertificateAuthority ?? false,
                IsSelfSigned = ComputeIsSelfSigned(certificate)
            };
        }

        private static bool ComputeIsSelfSigned(X509Certificate2 certificate)
            => certificate.SubjectName.RawData.SequenceEqual(certificate.IssuerName.RawData);
    }

    public sealed record CertificateImportResult(
        bool Success,
        string Message,
        string? Target)
    {
        public static CertificateImportResult Succeeded(string message, string target)
            => new(true, message, target);

        public static CertificateImportResult Failed(string message)
            => new(false, message, null);
    }

    public sealed record CertificateOperationResult(bool Success, string Message)
    {
        public static CertificateOperationResult Succeeded(string message)
            => new(true, message);

        public static CertificateOperationResult Failed(string message)
            => new(false, message);
    }
}
