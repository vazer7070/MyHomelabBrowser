using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using MyHomelabBrowser.classes.Security;
using PommeBrowser.Linux.Web;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Ui
{
    /// <summary>Informations d'un certificat présenté par un site.</summary>
    sealed record CertificateInfo(string Sha256, string Subject, string Issuer, DateTime NotAfter)
    {
        public static CertificateInfo From(Gio.TlsCertificate certificate)
        {
            try
            {
                using X509Certificate2 x509 = X509Certificate2.CreateFromPem(certificate.CertificatePem);
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

        public static string DescribeErrors(Gio.TlsCertificateFlags errors)
        {
            var reasons = new List<string>();
            if (errors.HasFlag(Gio.TlsCertificateFlags.UnknownCa)) reasons.Add(Tr("émis par une autorité inconnue"));
            if (errors.HasFlag(Gio.TlsCertificateFlags.BadIdentity)) reasons.Add(Tr("établi pour un autre nom"));
            if (errors.HasFlag(Gio.TlsCertificateFlags.NotActivated)) reasons.Add(Tr("pas encore valide"));
            if (errors.HasFlag(Gio.TlsCertificateFlags.Expired)) reasons.Add(Tr("expiré"));
            if (errors.HasFlag(Gio.TlsCertificateFlags.Revoked)) reasons.Add(Tr("révoqué"));
            if (errors.HasFlag(Gio.TlsCertificateFlags.Insecure)) reasons.Add(Tr("algorithme non sûr"));
            return reasons.Count > 0 ? string.Join(", ", reasons) : Tr("certificat invalide");
        }
    }

    /// <summary>
    /// Autorisations demandées par les sites (position, caméra, notifications…). La réponse
    /// peut être mémorisée par site (même fichier que l'édition Windows).
    /// </summary>
    static class PermissionPrompt
    {
        public static bool Handle(BrowserApplication app, BrowserTab tab, WebKit.PermissionRequest request)
        {
            if (!Uri.TryCreate(tab.Web.Url(), UriKind.Absolute, out Uri? page))
            {
                request.Deny();
                return true;
            }

            string site = page.GetLeftPart(UriPartial.Authority);
            (string Kind, string Label)? permission = Describe(request);

            switch (request)
            {
                // Pointeur capturé (jeux) : sans risque, relâché avec Échap.
                case WebKit.PointerLockPermissionRequest:
                    request.Allow();
                    return true;
                // Liste des périphériques : seulement si la caméra ou le micro ont été autorisés.
                case WebKit.DeviceInfoPermissionRequest:
                    if (app.SiteSecurity.Get(site, "camera") == true || app.SiteSecurity.Get(site, "microphone") == true)
                        request.Allow();
                    else
                        request.Deny();
                    return true;
            }

            if (permission == null)
            {
                request.Deny();
                return true;
            }

            (string kind, string label) = permission.Value;
            if (app.SiteSecurity.Get(site, kind) is bool known)
            {
                if (known)
                    request.Allow();
                else
                    request.Deny();
                return true;
            }

            var dialog = Adw.AlertDialog.New(Tr("Autorisation demandée"), Tr("{0} souhaite {1}.", page.Host, label));
            dialog.AddResponse("deny", Tr("Bloquer"));
            dialog.AddResponse("allow", Tr("Autoriser"));
            dialog.SetResponseAppearance("allow", Adw.ResponseAppearance.Suggested);
            dialog.SetDefaultResponse("deny");
            dialog.SetCloseResponse("deny");

            var remember = Gtk.CheckButton.NewWithLabel(Tr("Mémoriser pour ce site"));
            remember.SetActive(!tab.IsPrivate);
            remember.SetSensitive(!tab.IsPrivate);
            dialog.SetExtraChild(remember);

            bool answered = false;
            dialog.OnResponse += (_, args) =>
            {
                answered = true;
                bool allowed = args.Response == "allow";
                if (allowed)
                    request.Allow();
                else
                    request.Deny();

                if (remember.GetActive() && !tab.IsPrivate)
                    app.SiteSecurity.Set(site, kind, allowed);
            };
            // Page quittée ou onglet fermé avant la réponse : refus.
            dialog.OnClosed += (_, _) =>
            {
                if (!answered)
                    request.Deny();
            };
            tab.TrackDialog(dialog);
            dialog.Present(tab.Window.Window);
            return true;
        }

        static (string, string)? Describe(WebKit.PermissionRequest request) => request switch
        {
            WebKit.GeolocationPermissionRequest => ("geolocation", Tr("connaître votre position")),
            WebKit.NotificationPermissionRequest => ("notifications", Tr("afficher des notifications")),
            WebKit.ClipboardPermissionRequest => ("clipboard", Tr("lire le presse-papiers")),
            WebKit.WebsiteDataAccessPermissionRequest => ("storage-access", Tr("utiliser ses cookies sur ce site")),
            WebKit.UserMediaPermissionRequest media when WebKit.Functions.UserMediaPermissionIsForDisplayDevice(media) => ("screen", Tr("partager votre écran")),
            WebKit.UserMediaPermissionRequest media when WebKit.Functions.UserMediaPermissionIsForVideoDevice(media) => ("camera", Tr("utiliser votre caméra")),
            WebKit.UserMediaPermissionRequest => ("microphone", Tr("utiliser votre micro")),
            _ => null
        };

        /// <summary>Libellé d'une autorisation mémorisée (réglages).</summary>
        public static string KindLabel(string kind) => kind switch
        {
            "geolocation" => Tr("Position"),
            "notifications" => Tr("Notifications"),
            "clipboard" => Tr("Presse-papiers"),
            "storage-access" => Tr("Cookies intersites"),
            "screen" => Tr("Partage d'écran"),
            "camera" => Tr("Caméra"),
            "microphone" => Tr("Micro"),
            SiteSecurityStore.InsecureHttp => Tr("Ouverture en HTTP"),
            _ => kind
        };
    }
}
