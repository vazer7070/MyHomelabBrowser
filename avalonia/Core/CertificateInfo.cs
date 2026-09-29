using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PommeBrowser.Engine;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Core
{
    /// <summary>Informations d'un certificat présenté par un site.</summary>
    public sealed record CertificateInfo(string Sha256, string Subject, string Issuer, DateTime NotAfter)
    {
        public static CertificateInfo From(byte[] der)
        {
            if (der.Length == 0)
                return new CertificateInfo(string.Empty, "?", "?", DateTime.MinValue);
            try
            {
                using X509Certificate2 x509 = X509CertificateLoader.LoadCertificate(der);
                return new CertificateInfo(
                    x509.GetCertHashString(HashAlgorithmName.SHA256),
                    x509.GetNameInfo(X509NameType.SimpleName, forIssuer: false),
                    x509.GetNameInfo(X509NameType.SimpleName, forIssuer: true),
                    x509.NotAfter);
            }
            catch (CryptographicException)
            {
                return new CertificateInfo(string.Empty, "?", "?", DateTime.MinValue);
            }
        }

        public static string DescribeErrors(CertificateErrors errors)
        {
            var reasons = new List<string>();
            if (errors.HasFlag(CertificateErrors.UnknownAuthority)) reasons.Add(Tr("émis par une autorité inconnue"));
            if (errors.HasFlag(CertificateErrors.WrongName)) reasons.Add(Tr("établi pour un autre nom"));
            if (errors.HasFlag(CertificateErrors.NotYetValid)) reasons.Add(Tr("pas encore valide"));
            if (errors.HasFlag(CertificateErrors.Expired)) reasons.Add(Tr("expiré"));
            if (errors.HasFlag(CertificateErrors.Revoked)) reasons.Add(Tr("révoqué"));
            if (errors.HasFlag(CertificateErrors.Insecure)) reasons.Add(Tr("algorithme non sûr"));
            return reasons.Count > 0 ? string.Join(", ", reasons) : Tr("certificat invalide");
        }
    }
}
