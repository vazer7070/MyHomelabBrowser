using System;
using MyHomelabBrowser.classes.Profiles.Credentials;
using PommeBrowser.Engine;
using PommeBrowser.Linux.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>
    /// Scripts de PommeBrowser dans les pages : détection des contenus Flash (Ruffle) et des
    /// identifiants envoyés par un formulaire (coffre). Ils tournent dans le monde isolé du
    /// moteur quand il en a un : la page ne les voit pas.
    /// </summary>
    public sealed partial class BrowserTab
    {
        public const string CredentialChannel = "pommeCredentials";
        const string CredentialScriptId = "credentials";

        bool _ruffleAttached;

        void AttachScripts(IEngineTab engine)
        {
            engine.RegisterMessageHandler(RuffleContent.MessageHandler);
            _ruffleAttached = false;
            ApplyRuffle(engine);

            // Coffre : pas de capture en navigation privée (rien n'y est conservé).
            if (!IsPrivate)
            {
                engine.RegisterMessageHandler(CredentialChannel);
                engine.AddUserScript(CredentialScriptId,
                    CredentialScripts.Capture(EngineHost.ScriptPost(CredentialChannel, "JSON.stringify(message)")),
                    allFrames: false, atDocumentStart: true);
            }
        }

        /// <summary>Réglages modifiés : Ruffle ajouté ou retiré, filtre anti-pub réappliqué.</summary>
        public void OnSettingsChanged()
        {
            if (_engine == null)
                return;
            ApplyRuffle(_engine);
            _engine.RefreshContentFilter();
        }

        void ApplyRuffle(IEngineTab engine)
        {
            bool wanted = _app.Settings.EnableFlashSupport && RuffleContent.IsAvailable;
            if (wanted == _ruffleAttached)
                return;

            if (wanted)
            {
                // Flash annoncé avant les scripts de la page, qui n'ajoutent souvent leur contenu qu'à cette condition.
                engine.AddUserScript(RuffleContent.PluginScriptId, RuffleContent.PluginScript,
                    allFrames: true, atDocumentStart: true, pageWorld: true);
                engine.AddUserScript(RuffleContent.ScriptId,
                    RuffleContent.ProbeScript(EngineHost.RuffleBaseUrl, EngineHost.ScriptPost(RuffleContent.MessageHandler, "status")),
                    allFrames: true, atDocumentStart: false);
            }
            else
            {
                engine.RemoveUserScript(RuffleContent.PluginScriptId);
                engine.RemoveUserScript(RuffleContent.ScriptId);
            }
            _ruffleAttached = wanted;
        }

        void OnScriptMessage(string channel, string body)
        {
            switch (channel)
            {
                case RuffleContent.MessageHandler:
                    OnRuffleMessage(body);
                    break;
                case CredentialChannel:
                    OnCredentialMessage(body);
                    break;
            }
        }

        void OnRuffleMessage(string status)
        {
            if (status != "blocked")
                return;

            // Basilisk lit le Flash sans dépendre de la page : proposé quand il est installé.
            string message = Tr("Ce site empêche Ruffle de démarrer : le contenu Flash ne peut pas être lu.");
            if (_app.BasiliskExecutable != null && BasiliskInstall.IsOpenable(WebUrl, out Uri uri))
                Window.ShowToast(message, Tr("Ouvrir dans Basilisk"), () => OpenInBasilisk(uri));
            else
                Window.ShowToast(message);
        }

        /// <summary>
        /// Identifiants envoyés par le formulaire de la page. L'origine retenue est celle de la page
        /// affichée : le script ne peut que la confirmer (un message arrivé après un changement de page est écarté).
        /// </summary>
        void OnCredentialMessage(string json)
        {
            if (IsPrivate || Page != TabPage.Web || !CredentialOrigin.TryCreateTrusted(WebUrl, out string origin))
                return;

            if (CredentialScripts.TryParseSubmission(json, origin, out CredentialCandidate? candidate))
                _app.Vault.OnSubmitted(Window, candidate!);
        }

        /// <summary>Page chargée : remplissage automatique des identifiants enregistrés pour le site.</summary>
        void OnPageFinished() => _app.Vault.AutoFill(this);

        /// <summary>Remplit le formulaire de la page (monde isolé : la page ne peut pas intercepter les valeurs).</summary>
        public System.Threading.Tasks.Task<string?> FillCredentialAsync(CredentialEntry credential, string origin, string? otp)
            => _engine?.EvaluateAsync(CredentialScripts.Fill(credential.Username, credential.Password, otp, origin), isolated: true)
               ?? System.Threading.Tasks.Task.FromResult<string?>(null);
    }
}
