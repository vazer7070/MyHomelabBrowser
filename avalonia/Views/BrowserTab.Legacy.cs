using System;
using System.Diagnostics.CodeAnalysis;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using PommeBrowser.Legacy;
using PommeBrowser.Linux.Core;
using PommeBrowser.Views.Pages;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>Contenus Flash que Ruffle ne lit pas : la page s'ouvre dans Basilisk (lecteur Flash d'origine).</summary>
    public sealed partial class BrowserTab
    {
        ILegacyBrowser? _basilisk;
        Uri? _legacyUri;

        /// <summary>Site réglé sur « toujours dans Basilisk », et Basilisk installé.</summary>
        bool WantsBasilisk(string? url, [NotNullWhen(true)] out Uri? uri)
        {
            uri = null;
            if (!BasiliskInstall.IsOpenable(url, out Uri parsed) || FlashDomainRules.GetRule(parsed) != FlashRuleMode.Legacy)
                return false;
            if (_app.BasiliskExecutable == null)
                return false;
            uri = parsed;
            return true;
        }

        /// <summary>Ouvre la page dans Basilisk ; l'onglet affiche son état.</summary>
        public void OpenInBasilisk(Uri uri)
        {
            if (_app.BasiliskExecutable is not { } executable)
            {
                Window.ShowBasiliskMissing();
                return;
            }

            StopBasilisk();
            _engine?.Stop();
            _legacyUri = uri;
            try
            {
                _basilisk = LegacyBrowser.Start(executable, uri, IsPrivate);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                RuntimeLogBuffer.Append("[Basilisk] " + ex.Message);
                ShowError(uri.AbsoluteUri, Tr("Impossible de lancer Basilisk"), ex.Message,
                    (Tr("Réessayer"), true, () => OpenInBasilisk(uri)),
                    (Tr("Lire avec Ruffle"), false, () => BackToRuffle(uri)));
                return;
            }

            ILegacyBrowser started = _basilisk;
            started.Exited += () =>
            {
                if (_basilisk != started)
                    return;
                _basilisk = null;
                if (Page == TabPage.Legacy)
                    ShowLegacyPage(running: false);
            };
            if (!IsPrivate)
                _app.History.Record(uri.AbsoluteUri, uri.Host);
            ShowLegacyPage(running: true);
        }

        void ShowLegacyPage(bool running)
        {
            if (_legacyUri is not { } uri)
                return;

            Uri target = uri;
            _errorUrl = null;
            ShowPage(TabPage.Legacy, running
                ? new StatusPage("IconGames",
                    Tr("Ouvert dans Basilisk"),
                    Tr("{0} s'affiche dans une fenêtre Basilisk, avec le lecteur Flash d'origine. Fermer cet onglet ferme aussi Basilisk.", uri.Host),
                    new (string, bool, Action)[]
                    {
                        (Tr("Fermer Basilisk"), false, StopBasilisk),
                        (Tr("Lire avec Ruffle"), false, () => BackToRuffle(target))
                    })
                : new StatusPage("IconGames",
                    Tr("Basilisk est fermé"),
                    Tr("La fenêtre Basilisk de {0} a été fermée.", uri.Host),
                    new (string, bool, Action)[]
                    {
                        (Tr("Rouvrir dans Basilisk"), true, () => OpenInBasilisk(target)),
                        (Tr("Lire avec Ruffle"), false, () => BackToRuffle(target))
                    }));
        }

        void StopBasilisk() => _basilisk?.Close();

        /// <summary>Retour à la page dans PommeBrowser (Ruffle) ; le site n'est plus ouvert d'office dans Basilisk.</summary>
        void BackToRuffle(Uri uri)
        {
            if (FlashDomainRules.GetRule(uri) == FlashRuleMode.Legacy)
                FlashDomainRules.RemoveRule(uri);
            StopBasilisk();
            _basilisk = null;
            _legacyUri = null;
            Navigate(uri.AbsoluteUri);
        }
    }
}
