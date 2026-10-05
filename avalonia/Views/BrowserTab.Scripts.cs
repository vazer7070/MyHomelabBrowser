using System;
using System.Collections.Generic;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
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
        // Un contenu Flash de la page est lu par Ruffle (remis à zéro à chaque page).
        bool _rufflePlaying;
        // Contenu Flash principal de la page, décrit par le script de détection (le plus grand).
        FlashContent? _flashContent;

        /// <summary>Contenu Flash principal de la page affichée, s'il est connu.</summary>
        public FlashContent? FlashContent => _flashContent;

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
                AddRuffleProbe(engine);
            }
            else
            {
                engine.RemoveUserScript(RuffleContent.PluginScriptId);
                engine.RemoveUserScript(RuffleContent.ScriptId);
            }
            _ruffleAttached = wanted;
        }

        void AddRuffleProbe(IEngineTab engine)
            => engine.AddUserScript(RuffleContent.ScriptId,
                RuffleContent.ProbeScript(EngineHost.RuffleBaseUrl, EngineHost.ScriptPost(RuffleContent.MessageHandler, "status"), _app.SessionIntegratedHosts),
                allFrames: true, atDocumentStart: false);

        /// <summary>Sites lus par le moteur intégré modifiés : script de détection mis à jour (pages suivantes).</summary>
        public void RefreshRuffleProbe()
        {
            if (_engine is { } engine && _ruffleAttached)
                AddRuffleProbe(engine);
        }

        // Moteur intégré déjà lancé d'office pour la page affichée (remis à zéro à chaque page).
        bool _integratedStartPlanned;

        /// <summary>Hôte de la page de l'onglet (site retenu pour le moteur intégré).</summary>
        string? PageHost => System.Uri.TryCreate(WebUrl, UriKind.Absolute, out Uri? page) ? page.Host : null;

        /// <summary>
        /// Contenu Flash décrit sur un site retenu pour le moteur intégré (Ruffle ne l'a pas lancé) :
        /// le moteur intégré le lit d'office, une fois la liste des contenus de la page arrivée.
        /// </summary>
        void StartIntegratedIfPreferred()
        {
            if (_integratedStartPlanned || PageHost is not { } host || !_app.SessionIntegratedHosts.Contains(host))
                return;
            _integratedStartPlanned = true;
            Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                if (Page != TabPage.Web || HasFlashOverlay || _inPageModule != null || _flashContent == null ||
                    !UsesIntegratedFlash || PageHost != host || !System.Uri.TryCreate(WebUrl, UriKind.Absolute, out Uri? uri))
                    return;
                RuntimeLogBuffer.Append($"[Flash] {host} : lu d'office par le moteur intégré (retenu pour la session).");
                OpenFlashFallback(uri, automatic: true);
            }, TimeSpan.FromMilliseconds(400));
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
            if (status.StartsWith(PommeBrowser.Engine.FlashContent.MessagePrefix, StringComparison.Ordinal))
            {
                if (PommeBrowser.Engine.FlashContent.Parse(status[PommeBrowser.Engine.FlashContent.MessagePrefix.Length..]) is not { } content)
                    return;
                if (content.IsPreferredOver(_flashContent))
                {
                    if (_flashContent?.Swf != content.Swf)
                        RuntimeLogBuffer.Append($"[Flash] Contenu de la page : {content.Swf.GetLeftPart(UriPartial.Path)} ({content.Width}×{content.Height}){(IsTopDocument(content) ? string.Empty : ", dans un cadre")}.");
                    _flashContent = content;
                    RaiseChanged();
                }
                StartIntegratedIfPreferred();
                return;
            }
            if (status.StartsWith(PommeBrowser.Engine.FlashContent.ListPrefix, StringComparison.Ordinal))
            {
                OnFlashContents(status[PommeBrowser.Engine.FlashContent.ListPrefix.Length..]);
                return;
            }
            if (status.StartsWith(RuffleContent.RectPrefix, StringComparison.Ordinal))
            {
                OnFlashRect(status[RuffleContent.RectPrefix.Length..]);
                return;
            }

            switch (status)
            {
                case "playing":
                    _rufflePlaying = true;
                    break;
                case "blocked":
                    OfferFlashFallback(Tr("Ce site empêche Ruffle de démarrer : le contenu Flash ne peut pas être lu."));
                    break;
                case "failed":
                    OnRuffleFailed();
                    break;
            }
        }

        /// <summary>
        /// Ruffle s'est arrêté sur une erreur : la page passe d'elle-même au moteur de secours
        /// (Basilisk), sauf si la bascule est désactivée, si un autre contenu Flash de la page est
        /// déjà lu (une publicité qui échoue ne doit pas emporter le jeu), ou si l'utilisateur est
        /// revenu à Ruffle pour ce site pendant la session. Sinon, la bascule est proposée.
        /// </summary>
        void OnRuffleFailed()
        {
            // Page déjà passée au moteur intégré (ses contenus, même ajoutés ensuite, sont lus par
            // lui) : l'erreur d'un lecteur Ruffle n'y change rien.
            if (Page != TabPage.Web || HasFlashOverlay || _inPageModule != null || !BasiliskInstall.IsOpenable(WebUrl, out Uri uri))
                return;

            RuntimeLogBuffer.Append("[Ruffle] Contenu Flash illisible sur " + uri.Host);
            bool automatic = _app.Settings.FlashAutoFallback && !_rufflePlaying &&
                             HasFlashFallback && !_app.SessionRuffleHosts.Contains(uri.Host);
            if (!automatic)
            {
                RuntimeLogBuffer.Append("[Ruffle] Pas de bascule d'office : " + FallbackDiagnosis(uri) + ".");
                OfferFlashFallback(Tr("Ruffle n'a pas pu lire le contenu Flash de cette page."));
                return;
            }

            OpenFlashFallback(uri, automatic: true);
            if (!IsSelected)
                return;
            if (HasFlashOverlay)
            {
                Window.ShowToast(Tr("Ruffle n'a pas pu lire ce contenu : il est lu avec votre module Flash."), Tr("Lire avec Ruffle"), StopFlashOverlay, timeout: 8);
            }
            else if (IsIntegratedFlash)
            {
                Window.ShowToast(Tr("Ruffle n'a pas pu lire ce contenu : il est lu avec votre module Flash."));
            }
            else if (Page == TabPage.Legacy && FlashDomainRules.GetRule(uri) != FlashRuleMode.Legacy)
            {
                Window.ShowToast(Tr("Ruffle n'a pas pu lire ce contenu : {0} s'ouvre dans Basilisk. L'ouvrir toujours ainsi ?", uri.Host),
                    Tr("Toujours"), () => FlashDomainRules.SetRule(uri, FlashRuleMode.Legacy), timeout: 10);
            }
        }

        /// <summary>Pourquoi la bascule d'office n'a pas lieu (journal).</summary>
        string FallbackDiagnosis(Uri uri)
        {
            var reasons = new List<string>();
            if (!_app.Settings.FlashAutoFallback)
                reasons.Add("bascule d'office désactivée");
            if (_rufflePlaying)
                reasons.Add("un autre contenu est déjà lu par Ruffle");
            if (_app.SessionRuffleHosts.Contains(uri.Host))
                reasons.Add("retour à Ruffle choisi pour ce site");
            if (!HasFlashFallback)
            {
                string integrated = !_app.Settings.FlashIntegratedEngine ? "désactivé"
                    : _flashContent == null ? "aucun contenu décrit par la page"
                    : NextFlashModule(null) == null ? "aucun module Flash" : "indisponible sur ce système";
                string basilisk = !_app.BasiliskAllowed ? "désactivé" : "introuvable";
                reasons.Add($"aucun moteur de secours (moteur intégré : {integrated} ; Basilisk : {basilisk})");
            }
            return string.Join(", ", reasons);
        }

        /// <summary>
        /// Contenu Flash illisible : le moteur de secours (moteur intégré ou Basilisk), qui lit le
        /// Flash sans dépendre de la page, est proposé s'il est prêt.
        /// </summary>
        void OfferFlashFallback(string message)
        {
            if (!IsSelected)
                return;
            if (HasFlashFallback && BasiliskInstall.IsOpenable(WebUrl, out Uri uri))
                Window.ShowToast(message, FallbackIsIntegrated ? Tr("Lire avec le module Flash") : Tr("Ouvrir dans Basilisk"), () => OpenFlashFallback(uri));
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
