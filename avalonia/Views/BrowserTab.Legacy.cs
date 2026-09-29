using System;
using System.Diagnostics.CodeAnalysis;
using Avalonia.Controls;
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
        LegacyView? _legacyView;
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

        /// <summary>
        /// Ouvre la page dans Basilisk. Là où c'est possible (Windows, X11), sa fenêtre est logée
        /// dans l'onglet ; sinon elle reste à part et l'onglet affiche son état.
        /// </summary>
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
            bool embedded = LegacyView.IsSupported;
            try
            {
                _basilisk = LegacyBrowser.Start(executable, uri, IsPrivate, embedded);
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
            started.SetBackground(!IsSelected);
            if (!IsPrivate)
                _app.History.Record(uri.AbsoluteUri, uri.Host);
            // Sans module Flash, Basilisk affiche la page mais pas le contenu Flash.
            if (LegacyEngine.InstalledModule == null && executable == LegacyEngine.BundledExecutable)
                Window.ShowToast(Tr("Module Flash absent : ajoutez votre copie de Flash Player dans les paramètres."), Tr("Paramètres"), () => Window.OpenSettings("flash"), warning: true);

            if (embedded)
                ShowEmbeddedBasilisk(started, uri);
            else
                ShowLegacyPage(running: true);
        }

        /// <summary>Page de l'onglet : Basilisk dès que sa fenêtre est logée, un message d'attente avant.</summary>
        void ShowEmbeddedBasilisk(ILegacyBrowser browser, Uri uri)
        {
            var view = new LegacyView { IsVisible = false };
            var waiting = new StatusPage("IconGames",
                Tr("Ouverture de Basilisk…"),
                Tr("{0} s'ouvre avec le lecteur Flash d'origine.", uri.Host),
                new (string, bool, Action)[] { (Tr("Lire avec Ruffle"), false, () => BackToRuffle(uri)) });
            var page = new Grid();
            page.Children.Add(view);
            page.Children.Add(waiting);

            view.Docked += () =>
            {
                waiting.IsVisible = false;
                view.IsVisible = true;
                // Page affichée : Basilisk prend le clavier, sauf si l'utilisateur tape déjà ailleurs.
                // La fenêtre peut sembler inactive à cet instant (le gestionnaire de fenêtres a
                // activé Basilisk avant son accueil) : la vue prend quand même le focus, et le
                // clavier ne quitte pas une autre application (voir X11Dock et Win32Dock).
                if (IsSelected && Window.FocusManager?.GetFocusedElement() is not TextBox)
                    view.Focus();
            };
            view.DockFailed += () =>
            {
                if (_legacyView == view && _basilisk == browser)
                    ShowLegacyPage(running: true);
            };
            view.ShortcutPressed += (key, modifiers) =>
            {
                Window.HandleShortcut(key, modifiers);
                Window.SyncAllKeyboards();
            };
            view.Attach(browser);

            ShowPage(TabPage.Legacy, page);
            _legacyView = view;
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
                    Tr("La page {0} ouverte dans Basilisk a été fermée.", uri.Host),
                    new (string, bool, Action)[]
                    {
                        (Tr("Rouvrir dans Basilisk"), true, () => OpenInBasilisk(target)),
                        (Tr("Lire avec Ruffle"), false, () => BackToRuffle(target))
                    }));
        }

        /// <summary>Clavier de Basilisk logé dans l'onglet (voir MainWindow.SyncAllKeyboards).</summary>
        public void SyncLegacyKeyboard(bool force) => _legacyView?.SyncKeyboard(force);

        void StopBasilisk() => _basilisk?.Close();

        /// <summary>La page de Basilisk est retirée de l'onglet : plus de fenêtre à loger.</summary>
        void ForgetLegacyView()
        {
            _legacyView?.Detach();
            _legacyView = null;
        }

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
