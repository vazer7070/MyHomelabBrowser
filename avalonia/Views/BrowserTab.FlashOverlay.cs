using System;
using System.Runtime.Versioning;
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
    /// Moteur Flash intégré dans la page (Windows) : le contenu garde sa place. Le script de suivi
    /// remplace l'élément par un emplacement vide et en envoie la position ; la fenêtre du lecteur
    /// (PommeFlashHost) est logée par-dessus la page web, à cet endroit, et la suit (défilement,
    /// taille, mise en page). Seule la partie visible dans la zone de la page est affichée.
    /// Les contenus d'un cadre (iframe) restent lus à la place de la page (onglet entier).
    /// </summary>
    public sealed partial class BrowserTab
    {
        Canvas? _overlayLayer;
        LegacyView? _overlayView;
        ILegacyBrowser? _overlayHost;
        FlashContent? _overlayContent;
        FlashRect? _overlayRect;
        bool _overlayDocked;

        /// <summary>Le contenu Flash de la page est lu à sa place par le moteur intégré.</summary>
        public bool HasFlashOverlay => _overlayHost != null;

        /// <summary>Le contenu peut être lu à sa place : page web affichée, contenu du document principal.</summary>
        bool CanPlaceInPage(FlashContent content)
            => Page == TabPage.Web && _engine != null && _web != null && LegacyView.IsSupported && IsTopDocument(content);

        [SupportedOSPlatform("windows")]
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
            host.NavigateRequested += OnFlashNavigate;
            host.ScriptRequested += (id, code) => RunFlashScript(host, content, id, code);
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
                if (Page == TabPage.Web && IsSelected)
                    Window.ShowToast(Tr("Le lecteur Flash s'est arrêté : le contenu ne s'affiche plus."), Tr("Relancer"), () => RelaunchFlashInPage(content), timeout: 10, warning: true);
            };
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
            if (!OperatingSystem.IsWindows() || HasFlashOverlay || !CanPlaceInPage(content))
                return;
            if (NextFlashModule(null) is { } module)
                OpenFlashInPage(content, module);
        }

        /// <summary>Script de suivi dans le document principal (ses messages arrivent par le canal de Ruffle).</summary>
        async void StartFlashTracker()
        {
            if (_engine is not { } engine)
                return;
            try
            {
                await engine.EvaluateAsync(RuffleContent.FlashTrackerScript(EngineHost.ScriptPost(RuffleContent.MessageHandler, "status")), isolated: false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException or TaskCanceledException)
            {
                RuntimeLogBuffer.Append("[Flash] Suivi du contenu dans la page impossible : " + ex.Message);
            }
        }

        /// <summary>Position envoyée par le script de suivi (« null » : contenu introuvable dans la page).</summary>
        void OnFlashRect(string json)
        {
            if (_overlayHost == null || _overlayContent is not { } content)
                return;
            if (json == "null")
            {
                // Élément disparu ou dans un cadre : le contenu est lu à la place de la page, avec le même module.
                if (OperatingSystem.IsWindows())
                {
                    string? module = (_overlayHost as FlashHostProcess)?.Module;
                    CloseFlashOverlay();
                    OpenInIntegratedFlash(content, module);
                }
                else
                {
                    CloseFlashOverlay();
                }
                return;
            }
            if (FlashRect.Parse(json) is { } rect)
            {
                _overlayRect = rect;
                PlaceOverlay();
            }
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
