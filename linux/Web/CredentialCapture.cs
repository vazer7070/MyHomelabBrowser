using MyHomelabBrowser.classes.Profiles.Credentials;

namespace PommeBrowser.Linux.Web
{
    /// <summary>
    /// Détection des identifiants envoyés par un formulaire de connexion (script commun avec
    /// l'édition Windows). Le script tourne dans le monde isolé de PommeBrowser et dans le cadre
    /// principal seulement : la page ne voit pas le gestionnaire et ne peut pas envoyer de faux
    /// identifiants, et un cadre tiers ne peut pas se faire passer pour le site.
    /// </summary>
    sealed class CredentialCapture
    {
        public const string MessageHandler = "pommeCredentials";
        public const string World = RuffleSupport.World;

        WebKit.UserScript? _script;

        public void Attach(WebKit.UserContentManager manager)
        {
            _script ??= WebKit.UserScript.NewForWorld(
                CredentialScripts.Capture(CredentialScripts.WebKitPost),
                WebKit.UserContentInjectedFrames.TopFrame,
                WebKit.UserScriptInjectionTime.Start,
                World,
                null,
                null);

            manager.AddScript(_script);
            manager.RegisterScriptMessageHandler(MessageHandler, World);
        }

        /// <summary>Remplit le formulaire de la page (même monde isolé : la page ne peut pas intercepter les valeurs).</summary>
        public static System.Threading.Tasks.Task<string?> FillAsync(WebKit.WebView web, CredentialEntry credential, string origin, string? otp)
            => Native.EvaluateAsync(web, CredentialScripts.Fill(credential.Username, credential.Password, otp, origin), World);
    }
}
