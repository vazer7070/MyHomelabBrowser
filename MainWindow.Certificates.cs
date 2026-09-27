using MyHomelabBrowser.classes.Security;
using MyHomelabBrowser.controles;
using System.Windows;

namespace MyHomelabBrowser
{
    public partial class MainWindow
    {
        // ---------------------------
        // Certificats des services locaux (confiance au premier usage)
        // ---------------------------
        void InitializeCertificatePrompts()
        {
            BrowserCertificateTrustHost.Current.PromptAsync = request =>
                Dispatcher.InvokeAsync(() => ShowCertificatePrompt(request)).Task;
        }

        bool ShowCertificatePrompt(CertificatePromptRequest request)
        {
            // Une page qui charge une ressource en arrière-plan ne doit pas surgir si la fenêtre est réduite.
            if (WindowState == WindowState.Minimized)
                WindowState = WindowState.Normal;

            return new CertificateTrustDialog(request).ShowFor(this);
        }
    }
}
