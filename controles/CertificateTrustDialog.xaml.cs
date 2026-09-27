using MyHomelabBrowser.classes.Security;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles
{
    public partial class CertificateTrustDialog : DialogWindow
    {
        public CertificateTrustDialog(CertificatePromptRequest request)
        {
            InitializeComponent();

            AuthorityText.Text = request.Authority;
            SubjectText.Text = request.Subject;
            IssuerText.Text = request.IsSelfSigned ? Tr("Lui-même (certificat auto-signé)") : request.Issuer;
            ValidityText.Text = Tr("du {0:dd/MM/yyyy} au {1:dd/MM/yyyy}", request.NotBefore, request.NotAfter);
            FingerprintText.Text = CertificatePinStore.FormatFingerprint(request.Sha256);

            if (request.HasChanged)
            {
                Title = Tr("Le certificat a changé");
                Banner.SetResourceReference(Border.BackgroundProperty, "DangerSoftBrush");
                BannerIcon.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                BannerText.Text =
                    Tr("{0} présente un certificat différent de celui que vous aviez accepté le {1:dd/MM/yyyy}. C’est normal après un renouvellement ou une réinstallation, mais cela peut aussi signaler une interception.",
                        request.Authority, request.PreviousPin!.PinnedAt);
                TrustButton.Content = Tr("Faire confiance au nouveau certificat");
            }
            else
            {
                BannerText.Text =
                    Tr("{0} est un service de votre réseau local, mais son certificat n’est pas reconnu. C’est courant pour un NAS, un hyperviseur ou un routeur.", request.Authority);
            }

            var problems = new List<string>();
            if (request.IsSelfSigned)
                problems.Add(Tr("certificat auto-signé"));
            else
                problems.Add(Tr("autorité de certification inconnue"));
            if (request.NameMismatch)
                problems.Add(Tr("le nom ne correspond pas à l’adresse"));
            if (request.IsExpired)
                problems.Add(Tr("certificat expiré ou pas encore valide"));

            ProblemsText.Text = Tr("Problèmes détectés : ") + string.Join(", ", problems) + ".";
        }

        private void Trust_Click(object sender, RoutedEventArgs e) => DialogResult = true;
    }
}
