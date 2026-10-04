using System;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using MyHomelabBrowser.classes;
using PommeBrowser.Engine;
using PommeBrowser.Legacy;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>
    /// Moteur Flash intégré dans la page (Windows, Linux sous X11) : le contenu garde sa place. Le script de suivi
    /// remplace l'élément par un emplacement vide et en envoie la position ; la fenêtre du lecteur
    /// (PommeFlashHost) est logée par-dessus la page web, à cet endroit, et la suit (défilement,
    /// taille, mise en page). Seule la partie visible dans la zone de la page est affichée.
    /// Un contenu d'un cadre (iframe) de même origine que la page y est lu de même, la page restant
    /// active (ses scripts peuvent remplacer le contenu : logo, puis jeu) ; celui d'un cadre d'un
    /// autre site est lu à la place de la page (onglet entier).
    /// </summary>
    public sealed partial class BrowserTab
    {
        Canvas? _overlayLayer;
        LegacyView? _overlayView;
        ILegacyBrowser? _overlayHost;
        FlashContent? _overlayContent;
        FlashRect? _overlayRect;
        bool _overlayDocked;
        // Contenu décrit par la page pendant la lecture : il prend la place de celui que la page retire.
        FlashContent? _overlaySuccessor;
        // Module du contenu retiré par la page : le contenu qu'elle met à sa place est lu avec lui.
        string? _successorModule;

        /// <summary>Le contenu Flash de la page est lu à sa place par le moteur intégré.</summary>
        public bool HasFlashOverlay => _overlayHost != null;

        /// <summary>
        /// Le contenu peut être lu à sa place : page web affichée, contenu du document principal ou
        /// d'un cadre de même origine.
        /// </summary>
        bool CanPlaceInPage(FlashContent content)
            => Page == TabPage.Web && _engine != null && _web != null && LegacyView.IsSupported && IsReachable(content);

        void OpenFlashInPage(FlashContent content, string module)
        {
            CloseFlashOverlay();
            FlashHostProcess host;
            try
            {
                host = FlashHostProcess.Start(content, module, IsPrivate);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or System.IO.IOException or UnauthorizedAccessException)
            {
                RuntimeLogBuffer.Append("[Flash] " + ex.Message);
                Window.ShowToast(Tr("Impossible de lancer le moteur Flash intégré : {0}", ex.Message), warning: true);
                return;
            }

            var view = new LegacyView { IsVisible = false };
            var layer = new Canvas { ClipToBounds = true };
            layer.Children.Add(view);
            // Au-dessus de la vue web (premier enfant), sous les pages de PommeBrowser.
            _host.Children.Insert(_web != null ? _host.Children.IndexOf(_web) + 1 : 0, layer);
            _overlayLayer = layer;
            _overlayView = view;
            _overlayHost = host;
            _overlayContent = content;
            _overlayRect = null;
            _overlayDocked = false;

            host.SetBackground(!IsSelected);
            ConnectFlashHost(host, content);
            host.Exited += () =>
            {
                if (_overlayHost != host)
                    return;
                CloseFlashOverlay();
                RaiseChanged();
                // Module qui ne lit pas le contenu : l'autre (32 ou 64 bits) prend sa place.
                if (host.FailedToStart && CanPlaceInPage(content) && NextFlashModule(host.Module) is { } next)
                {
                    AnnounceFlashRetry(host.Module, next);
                    OpenFlashInPage(content, next);
                    return;
                }
                if (CanPlaceInPage(content) && TryAutoRelaunch(host, m => OpenFlashInPage(content, m)))
                    return;
                if (Page == TabPage.Web && IsSelected)
                    Window.ShowToast(Tr("Le lecteur Flash s'est arrêté : le contenu ne s'affiche plus."), Tr("Relancer"), () => RelaunchFlashInPage(content), timeout: 10, warning: true);
            };
            WatchFlashResponsiveness(host, () => _overlayHost == host, m => OpenFlashInPage(content, m));
            view.Docked += () =>
            {
                if (_overlayView != view)
                    return;
                _overlayDocked = true;
                view.BringToFront();
                PlaceOverlay();
            };
            view.DockFailed += () =>
            {
                if (_overlayView != view)
                    return;
                CloseFlashOverlay();
                RaiseChanged();
                Window.ShowToast(Tr("Le lecteur Flash n'a pas pu s'afficher dans la page."), Tr("Lire avec Ruffle"), () => BackToRuffle(content.Page), warning: true);
            };
            view.ShortcutPressed += (key, modifiers) =>
            {
                Window.HandleShortcut(key, modifiers);
                Window.SyncAllKeyboards();
            };
            layer.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.BoundsProperty)
                    PlaceOverlay();
            };
            view.Attach(host);
            StartFlashTracker();
            RaiseChanged();
        }

        /// <summary>Relance après un arrêt du lecteur : le script de suivi reprend l'emplacement laissé dans la page.</summary>
        void RelaunchFlashInPage(FlashContent content)
        {
            if (HasFlashOverlay || !CanPlaceInPage(content))
                return;
            if (NextFlashModule(null) is { } module)
                OpenFlashInPage(content, module);
        }

        /// <summary>
        /// Script de suivi dans le document principal (ses messages arrivent par le canal de Ruffle),
        /// qui trouve le contenu dans le cadre de même origine qui le contient.
        /// </summary>
        async void StartFlashTracker()
        {
            if (_engine is not { } engine || _overlayContent is not { } content)
                return;
            try
            {
                string post = EngineHost.ScriptPost(RuffleContent.MessageHandler, "status");
                await engine.EvaluateAsync(RuffleContent.FlashTrackerScript(post, IsTopDocument(content) ? null : content.Page), isolated: false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException or TaskCanceledException)
            {
                RuntimeLogBuffer.Append("[Flash] Suivi du contenu dans la page impossible : " + ex.Message);
            }
        }

        /// <summary>
        /// Position envoyée par le script de suivi (« null » : contenu introuvable dans la page,
        /// « gone » : contenu retiré par la page).
        /// </summary>
        void OnFlashRect(string json)
        {
            if (_overlayHost == null || _overlayContent is not { } content)
                return;
            if (json == "null")
            {
                // Élément introuvable (cadre d'un autre site) : le contenu est lu à la place de la page, avec le même module.
                string? module = (_overlayHost as FlashHostProcess)?.Module;
                CloseFlashOverlay();
                OpenInIntegratedFlash(content, module);
                return;
            }
            if (json == "gone")
            {
                OnFlashContentRemoved(content);
                return;
            }
            if (FlashRect.Parse(json) is { } rect)
            {
                _overlayRect = rect;
                PlaceOverlay();
            }
        }

        /// <summary>
        /// La page a retiré le contenu lu (remplacé par un autre : logo puis jeu ; cadre rechargé) :
        /// le lecteur s'arrête, et le contenu qu'elle met à sa place est lu avec le même module,
        /// dans la page, dès qu'il est décrit (ou aussitôt s'il l'est déjà).
        /// </summary>
        void OnFlashContentRemoved(FlashContent content)
        {
            string? module = (_overlayHost as FlashHostProcess)?.Module;
            FlashContent? successor = _overlaySuccessor;
            RuntimeLogBuffer.Append($"[Flash] Contenu retiré par la page : {content.Swf.GetLeftPart(UriPartial.Path)}.");
            CloseFlashOverlay();
            if (_flashContent is { } current && current.IsSameAs(content))
                _flashContent = successor;
            if (module != null && successor != null && CanPlaceInPage(successor))
                PlaySuccessor(successor, module);
            else
                _successorModule = module;
            RaiseChanged();
        }

        /// <summary>
        /// Contenu décrit par la page pendant que le moteur intégré y lit le sien : gardé pour le
        /// remplacer si la page le retire, ou lu aussitôt s'il remplace un contenu déjà retiré. Un
        /// format publicitaire n'est jamais pris pour le contenu principal.
        /// </summary>
        /// <returns>Le contenu est lu par le moteur intégré.</returns>
        bool NoteFlashContent(FlashContent content)
        {
            if (content.HasAdSize || !CanPlaceInPage(content))
                return false;
            if (_overlayContent is { } current)
            {
                if (!content.IsSameAs(current))
                    _overlaySuccessor = content;
                return false;
            }
            if (_successorModule is not { } module)
                return false;
            _flashContent = content;
            PlaySuccessor(content, module);
            return true;
        }

        void PlaySuccessor(FlashContent content, string module)
        {
            RuntimeLogBuffer.Append($"[Flash] Contenu mis à sa place par la page : {content.Swf.GetLeftPart(UriPartial.Path)} ({content.Width}×{content.Height}), lu avec le même module.");
            OpenFlashInPage(content, module);
        }

        /// <summary>
        /// La vue couvre la partie visible du contenu ; la fenêtre du lecteur y est placée à sa
        /// taille entière, décalée si le contenu dépasse de la zone de la page.
        /// </summary>
        void PlaceOverlay()
        {
            if (_overlayView is not { } view || _overlayLayer is not { } layer)
                return;
            double scaling = TopLevel.GetTopLevel(layer)?.RenderScaling ?? 1;
            FlashPlacement? placement = _overlayRect?.Place(layer.Bounds.Width, layer.Bounds.Height, scaling);
            if (placement is { } p)
            {
                Canvas.SetLeft(view, p.Left);
                Canvas.SetTop(view, p.Top);
                view.Width = p.Width;
                view.Height = p.Height;
                view.PlaceClient((p.ClientX, p.ClientY, p.ClientWidth, p.ClientHeight));
            }
            view.IsVisible = _overlayDocked && placement != null;
        }

        /// <summary>Lecteur fermé et retiré de la page (page quittée, onglet fermé, retour à Ruffle).</summary>
        void CloseFlashOverlay()
        {
            ILegacyBrowser? host = _overlayHost;
            _overlayHost = null;
            _overlayContent = null;
            _overlayRect = null;
            _overlayDocked = false;
            _overlaySuccessor = null;
            _successorModule = null;
            if (_overlayView is { } view)
            {
                view.Detach();
                _overlayView = null;
            }
            if (_overlayLayer is { } layer)
            {
                _host.Children.Remove(layer);
                _overlayLayer = null;
            }
            host?.Close();
        }

        /// <summary>Retour à Ruffle pour le contenu lu dans la page (bouton ⚡, notification).</summary>
        public void StopFlashOverlay()
        {
            if (_overlayContent is { } content)
                BackToRuffle(content.Page);
        }
    }
}
