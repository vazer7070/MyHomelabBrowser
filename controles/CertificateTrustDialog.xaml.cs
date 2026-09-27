using MyHomelabBrowser.classes.Security;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace MyHomelabBrowser.controles
{
    public partial class CertificateTrustDialog : DialogWindow
    {
        public CertificateTrustDialog(CertificatePromptRequest request)
        {
            InitializeComponent();

            AuthorityText.Text = request.Authority;
            SubjectText.Text = request.Subject;
            IssuerText.Text = request.IsSelfSigned ? "Lui-même (certificat auto-signé)" : request.Issuer;
            ValidityText.Text = $"du {request.NotBefore:dd/MM/yyyy} au {request.NotAfter:dd/MM/yyyy}";
            FingerprintText.Text = CertificatePinStore.FormatFingerprint(request.Sha256);

            if (request.HasChanged)
            {
                Title = "Le certificat a changé";
                Banner.SetResourceReference(Border.BackgroundProperty, "DangerSoftBrush");
                BannerIcon.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                BannerText.Text =
                    $"{request.Authority} présente un certificat différent de celui que vous aviez accepté le " +
                    $"{request.PreviousPin!.PinnedAt:dd/MM/yyyy}. C’est normal après un renouvellement ou une réinstallation, " +
                    "mais cela peut aussi signaler une interception.";
                TrustButton.Content = "Faire confiance au nouveau certificat";
            }
            else
            {
                BannerText.Text =
                    $"{request.Authority} est un service de votre réseau local, mais son certificat n’est pas reconnu. " +
                    "C’est courant pour un NAS, un hyperviseur ou un routeur.";
            }

            var problems = new List<string>();
            if (request.IsSelfSigned)
                problems.Add("certificat auto-signé");
            else
                problems.Add("autorité de certification inconnue");
            if (request.NameMismatch)
                problems.Add("le nom ne correspond pas à l’adresse");
            if (request.IsExpired)
                problems.Add("certificat expiré ou pas encore valide");

            ProblemsText.Text = "Problèmes détectés : " + string.Join(", ", problems) + ".";
        }

        private void Trust_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
