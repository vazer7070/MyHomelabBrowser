using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.Versioning;
using Avalonia.Controls;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using PommeBrowser.Engine;
using PommeBrowser.Legacy;
using PommeBrowser.Linux.Core;
using PommeBrowser.Views.Pages;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>
    /// Contenus Flash que Ruffle ne lit pas : moteur Flash intégré (module Flash de l'utilisateur,
    /// Windows, expérimental) ou Basilisk (lecteur Flash d'origine), logés dans l'onglet.
    /// </summary>
    public sealed partial class BrowserTab
    {
        ILegacyBrowser? _basilisk;
        LegacyView? _legacyView;
        Uri? _legacyUri;
        // Contenu lu par le moteur intégré (null : Basilisk).
        FlashContent? _integrated;
        // Lecteur que la page appelle (ExternalInterface.addCallback), le plus récent de l'onglet.
        ILegacyBrowser? _flashBridgeHost;

        /// <summary>Attente d'un appel de la page vers le contenu : l'interface reste figée pendant ce temps.</summary>
        static readonly TimeSpan FlashCallTimeout = TimeSpan.FromSeconds(8);

        /// <summary>
        /// Moteur intégré prêt pour le contenu de la page : Windows, réglage activé, et un module
        /// Flash (32 ou 64 bits, voir LegacyEngine.IntegratedModules) dont l'hôte est livré.
        /// </summary>
        bool UsesIntegratedFlash => OperatingSystem.IsWindows() && _app.Settings.FlashIntegratedEngine && _flashContent != null &&
                                    NextFlashModule(null) != null;

        /// <summary>
        /// Module du moteur intégré à essayer : le premier, ou celui qui suit <paramref name="failed"/>
        /// (null s'il n'y en a plus). Seuls comptent les modules dont l'hôte de l'architecture est livré.
        /// </summary>
        [SupportedOSPlatform("windows")]
        static string? NextFlashModule(string? failed)
        {
            List<string> modules = LegacyEngine.IntegratedModules.Where(FlashHostProcess.IsAvailableFor).ToList();
            if (failed == null)
                return modules.FirstOrDefault();
            int index = modules.FindIndex(m => string.Equals(m, failed, StringComparison.OrdinalIgnoreCase));
            return index >= 0 && index + 1 < modules.Count ? modules[index + 1] : null;
        }

        /// <summary>Le module essayé n'a pas lu le contenu : l'autre prend le relais (journal, et message si l'onglet est affiché).</summary>
        [SupportedOSPlatform("windows")]
        void AnnounceFlashRetry(string failed, string next)
        {
            RuntimeLogBuffer.Append($"[Flash] {System.IO.Path.GetFileName(failed)} n'a pas lu le contenu : essai avec {System.IO.Path.GetFileName(next)}.");
            if (IsSelected)
                Window.ShowToast(Tr("Le module Flash {0} bits n'a pas pu lire ce contenu : le module {1} bits prend le relais.",
                    FlashModuleSearch.Is32Bit(failed) ? 32 : 64, FlashModuleSearch.Is32Bit(next) ? 32 : 64));
        }

        /// <summary>Un moteur de secours peut lire le Flash de cette page.</summary>
        public bool HasFlashFallback => UsesIntegratedFlash || _app.BasiliskExecutable != null;

        /// <summary>Le moteur de secours de cette page est le moteur intégré.</summary>
        public bool FallbackIsIntegrated => UsesIntegratedFlash;

        /// <summary>La page affichée est lue par le moteur intégré.</summary>
        public bool IsIntegratedFlash => Page == TabPage.Legacy && _integrated != null;

        /// <summary>
        /// Contenu que Ruffle ne lit pas : moteur intégré s'il est prêt (à sa place dans la page si
        /// possible, sinon à la place de la page), sinon Basilisk.
        /// </summary>
        public void OpenFlashFallback(Uri uri)
        {
            if (OperatingSystem.IsWindows() && UsesIntegratedFlash)
            {
                FlashContent content = _flashContent!;
                if (CanPlaceInPage(content) && NextFlashModule(null) is { } module)
                    OpenFlashInPage(content, module);
                else
                    OpenInIntegratedFlash(content);
            }
            else
            {
                OpenInBasilisk(uri);
            }
        }

        /// <summary>
        /// Le contenu principal de la page lu par le moteur intégré : PommeFlashHost charge le module
        /// Flash de l'utilisateur (<paramref name="module"/>, sinon le premier à essayer), et sa
        /// fenêtre est logée dans l'onglet. Si le module ne lit pas le contenu, le suivant est essayé.
        /// </summary>
        [SupportedOSPlatform("windows")]
        void OpenInIntegratedFlash(FlashContent content, string? module = null)
        {
            module ??= NextFlashModule(null);
            if (module == null)
            {
                Window.ShowToast(Tr("Module Flash absent : ajoutez votre copie de Flash Player dans les paramètres."), Tr("Paramètres"), () => Window.OpenSettings("flash"), warning: true);
                return;
            }

            StopBasilisk();
            _engine?.Stop();
            _legacyUri = content.Page;
            _integrated = content;
            FlashHostProcess host;
            try
            {
                host = FlashHostProcess.Start(content, module, IsPrivate);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException)
            {
                RuntimeLogBuffer.Append("[Flash] " + ex.Message);
                ShowError(content.Page.AbsoluteUri, Tr("Impossible de lancer le moteur Flash intégré"), ex.Message,
                    (Tr("Réessayer"), true, () => OpenInIntegratedFlash(content)),
                    (Tr("Lire avec Ruffle"), false, () => BackToRuffle(content.Page)));
                return;
            }

            _basilisk = host;
            host.Exited += () =>
            {
                if (_basilisk != host)
                    return;
                _basilisk = null;
                if (host.FailedToStart && Page == TabPage.Legacy && _integrated == content && NextFlashModule(host.Module) is { } next)
                {
                    AnnounceFlashRetry(host.Module, next);
                    OpenInIntegratedFlash(content, next);
                    return;
                }
                if (Page == TabPage.Legacy)
                    ShowLegacyPage(running: false);
            };
            ConnectFlashHost(host, content);
            host.SetBackground(!IsSelected);
            ShowEmbeddedLegacy(host, Tr("Ouverture du lecteur Flash…"), Tr("{0} s'ouvre avec votre module Flash.", content.Swf.Host), content.Page);
        }

        /// <summary>Le lecteur agit sur la page comme un greffon de navigateur : pages demandées, scripts, cookies.</summary>
        [SupportedOSPlatform("windows")]
        void ConnectFlashHost(FlashHostProcess host, FlashContent content)
        {
            host.NavigateRequested += OnFlashNavigate;
            host.ScriptRequested += (id, code) => RunFlashScript(host, content, id, code);
            host.CookiesRequested += (id, url, httpOnly) => GiveFlashCookies(host, content, id, url, httpOnly);
            host.CookieReceived += (url, cookie, fromHttp) => KeepFlashCookie(content, url, cookie, fromHttp);

            // Appels de la page vers le contenu : l'élément du contenu reçoit CallFunction.
            if (_engine is { } engine)
            {
                _flashBridgeHost = host;
                engine.SetFlashBridge(request => CallFlash(host, content, request));
                InstallFlashBridge(engine, content);
                host.Exited += () =>
                {
                    if (_flashBridgeHost != host)
                        return;
                    _flashBridgeHost = null;
                    _engine?.SetFlashBridge(null);
                };
            }
        }

        /// <summary>Appel de la page vers le contenu, sur le fil de l'interface (la page attend la réponse).</summary>
        [SupportedOSPlatform("windows")]
        string? CallFlash(FlashHostProcess host, FlashContent content, string request)
            => _flashBridgeHost == host && !host.HasExited && IsTopDocument(content) ? host.CallFunction(request, FlashCallTimeout) : null;

        async void InstallFlashBridge(IEngineTab engine, FlashContent content)
        {
            try
            {
                await engine.EvaluateAsync(RuffleContent.FlashBridgeScript(content.Id), isolated: false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or
                                           System.Runtime.InteropServices.COMException or System.Threading.Tasks.TaskCanceledException)
            {
                RuntimeLogBuffer.Append("[Flash] Appels de la page vers le contenu indisponibles : " + ex.Message);
            }
        }

        /// <summary>
        /// Cookies de la page pour une adresse que le lecteur charge, comme un navigateur les joint
        /// aux requêtes d'un greffon. Seulement pour le site de la page : le lecteur exécute un
        /// module tiers, il n'a pas accès aux cookies des autres sites.
        /// </summary>
        [SupportedOSPlatform("windows")]
        async void GiveFlashCookies(FlashHostProcess host, FlashContent content, int id, Uri url, bool httpOnly)
        {
            string? header = null;
            if (_engine is { } engine && FlashCookies.IsShared(content.Page, url))
            {
                try
                {
                    header = await engine.GetCookieHeaderAsync(url, httpOnly);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or
                                               System.Runtime.InteropServices.COMException or ArgumentException)
                {
                    RuntimeLogBuffer.Append("[Flash] Cookies de la page indisponibles : " + ex.Message);
                }
            }
            host.Reply(id, header != null, header);
        }

        /// <summary>Cookie reçu par le lecteur (ou posé par lui) : gardé dans la page, s'il est du site de la page et valide.</summary>
        async void KeepFlashCookie(FlashContent content, Uri url, string cookie, bool fromHttp)
        {
            if (_engine is not { } engine || !FlashCookies.IsShared(content.Page, url))
                return;
            if (FlashCookies.Parse(url, cookie, fromHttp, DateTimeOffset.UtcNow) is not { } parsed)
            {
                RuntimeLogBuffer.Append($"[Flash] Cookie refusé ({url.Host}) : {cookie.Split(';', 2)[0].Split('=', 2)[0].Trim()}");
                return;
            }
            try
            {
                await engine.SetCookieAsync(parsed);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or
                                           System.Runtime.InteropServices.COMException or ArgumentException)
            {
                RuntimeLogBuffer.Append($"[Flash] Cookie « {parsed.Name} » non enregistré : {ex.Message}");
            }
        }

        /// <summary>Page demandée par le contenu : dans l'onglet (_self, _top) ou dans un nouvel onglet.</summary>
        void OnFlashNavigate(Uri url, string target)
        {
            if (target is "_self" or "_top" or "_parent")
            {
                StopBasilisk();
                _basilisk = null;
                _legacyUri = null;
                _integrated = null;
                Navigate(url.AbsoluteUri);
            }
            else
            {
                Window.OpenTab(url.AbsoluteUri, background: false, opener: this);
            }
        }

        /// <summary>Le contenu vient du document principal de la page chargée dans l'onglet.</summary>
        bool IsTopDocument(FlashContent content)
            => System.Uri.TryCreate(WebUrl, UriKind.Absolute, out Uri? page) &&
               System.Uri.Compare(page, content.Page, UriComponents.HttpRequestUrl, UriFormat.UriEscaped, StringComparison.Ordinal) == 0;

        /// <summary>
        /// Script demandé par le contenu (ExternalInterface.call, adresse javascript:) : exécuté
        /// dans la page, comme dans un navigateur, et son résultat renvoyé au lecteur. Le lecteur
        /// applique lui-même allowScriptAccess. Seulement pour un contenu du document principal :
        /// celui d'un cadre n'agit pas sur la page qui le contient (il reçoit un refus).
        /// </summary>
        [SupportedOSPlatform("windows")]
        async void RunFlashScript(FlashHostProcess host, FlashContent content, int? id, string code)
        {
            string? value = null;
            bool ok = false;
            if (_engine is { } engine && IsTopDocument(content))
            {
                try
                {
                    value = await engine.EvaluateAsync(code, isolated: false);
                    ok = true;
                }
                catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or
                                               System.Runtime.InteropServices.COMException or System.Threading.Tasks.TaskCanceledException)
                {
                    RuntimeLogBuffer.Append("[Flash] Script de la page impossible : " + ex.Message);
                }
            }
            if (id is { } request)
                host.Reply(request, ok, value);
        }

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
            _integrated = null;
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
                ShowEmbeddedLegacy(started, Tr("Ouverture de Basilisk…"), Tr("{0} s'ouvre avec le lecteur Flash d'origine.", uri.Host), uri);
            else
                ShowLegacyPage(running: true);
        }

        /// <summary>Page de l'onglet : la fenêtre du lecteur dès qu'elle est logée, un message d'attente avant.</summary>
        void ShowEmbeddedLegacy(ILegacyBrowser browser, string title, string text, Uri uri)
        {
            var view = new LegacyView { IsVisible = false };
            var waiting = new StatusPage("IconGames", title, text,
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
            if (!running && _integrated is { } content)
            {
                ShowPage(TabPage.Legacy, new StatusPage("IconGames",
                    Tr("Le lecteur Flash s'est arrêté"),
                    Tr("Le contenu de {0} ne s'affiche plus : le module Flash s'est fermé ou a planté.", content.Swf.Host),
                    new (string, bool, Action)[]
                    {
                        (Tr("Relancer"), true, () =>
                        {
                            if (OperatingSystem.IsWindows())
                                OpenInIntegratedFlash(content);
                        }),
                        (Tr("Lire avec Ruffle"), false, () => BackToRuffle(target))
                    }));
                return;
            }
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
        public void SyncLegacyKeyboard(bool force)
        {
            _legacyView?.SyncKeyboard(force);
            _overlayView?.SyncKeyboard(force);
        }

        void StopBasilisk() => _basilisk?.Close();

        /// <summary>La page de Basilisk est retirée de l'onglet : plus de fenêtre à loger.</summary>
        void ForgetLegacyView()
        {
            _legacyView?.Detach();
            _legacyView = null;
        }

        /// <summary>
        /// Retour à la page dans PommeBrowser (Ruffle) : le site n'est plus ouvert d'office dans
        /// Basilisk, et n'y bascule plus de lui-même pendant la session.
        /// </summary>
        void BackToRuffle(Uri uri)
        {
            if (FlashDomainRules.GetRule(uri) == FlashRuleMode.Legacy)
                FlashDomainRules.RemoveRule(uri);
            _app.SessionRuffleHosts.Add(uri.Host);
            StopBasilisk();
            CloseFlashOverlay();
            _basilisk = null;
            _legacyUri = null;
            _integrated = null;
            Navigate(uri.AbsoluteUri);
        }
    }
}
