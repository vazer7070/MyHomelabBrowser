using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
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
    /// Moteur Flash intégré dans la page (Windows, Linux sous X11) : chaque contenu garde sa place.
    /// Le script de suivi remplace l'élément par un emplacement vide et en envoie la position ; la
    /// fenêtre du lecteur (PommeFlashHost) est logée par-dessus la page web, à cet endroit, et la
    /// suit (défilement, taille, mise en page). Seule la partie visible dans la zone de la page est
    /// affichée. Comme dans un navigateur avec Flash, une page passée au moteur intégré y lit tous
    /// ses contenus, un lecteur chacun : un jeu peut charger son client à part (caché, minuscule)
    /// pendant qu'il montre un logo. Un contenu d'un cadre (iframe) de même origine que la page y
    /// est lu de même, la page restant active ; celui d'un cadre d'un autre site est lu à la place
    /// de la page (onglet entier).
    /// </summary>
    public sealed partial class BrowserTab
    {
        /// <summary>Contenu lu à sa place dans la page : son lecteur et la vue qui loge sa fenêtre.</summary>
        sealed class FlashSlot
        {
            public FlashSlot(string key, FlashContent content, FlashHostProcess host, LegacyView view)
            {
                Key = key;
                Content = content;
                Host = host;
                View = view;
            }

            /// <summary>Clé de l'emplacement dans la page (« f1 »…), gardée d'une relance à l'autre.</summary>
            public string Key { get; }

            public FlashContent Content { get; }

            public FlashHostProcess Host { get; }

            public LegacyView View { get; }

            public FlashRect? Rect { get; set; }

            public bool Docked { get; set; }
        }

        /// <summary>Lecteurs d'une page au plus (un processus chacun).</summary>
        const int MaxFlashSlots = 8;

        /// <summary>Documents d'une page dont les contenus sont retenus au plus.</summary>
        const int MaxFlashDocuments = 16;

        Canvas? _overlayLayer;
        readonly List<FlashSlot> _slots = new();
        // Clé de l'emplacement de chaque contenu de la page, par identité.
        readonly Dictionary<string, string> _slotKeys = new(StringComparer.Ordinal);
        // Module du moteur intégré, une fois la page passée à lui : les contenus qu'elle ajoute (ou
        // met à la place d'un contenu retiré) sont lus avec lui, sans repasser par Ruffle.
        string? _inPageModule;
        // Contenus décrits par chaque document de la page (sa dernière liste).
        readonly Dictionary<Uri, IReadOnlyList<FlashContent>> _pageContents = new();

        /// <summary>Des contenus Flash de la page sont lus à leur place par le moteur intégré.</summary>
        public bool HasFlashOverlay => _slots.Count > 0;

        /// <summary>
        /// Le contenu peut être lu à sa place : page web affichée, contenu du document principal ou
        /// d'un cadre de même origine.
        /// </summary>
        bool CanPlaceInPage(FlashContent content)
            => Page == TabPage.Web && _engine != null && _web != null && LegacyView.IsSupported && IsReachable(content);

        FlashSlot? FindSlot(FlashContent content) => _slots.Find(slot => slot.Content.IsSameAs(content));

        FlashSlot? FindSlot(string key) => _slots.Find(slot => slot.Key == key);

        string SlotKey(FlashContent content)
        {
            string identity = content.Identity;
            if (!_slotKeys.TryGetValue(identity, out string? key))
                _slotKeys[identity] = key = "f" + (_slotKeys.Count + 1).ToString(CultureInfo.InvariantCulture);
            return key;
        }

        /// <summary>
        /// La page passe au moteur intégré : son contenu principal, puis ses autres contenus déjà
        /// décrits (dans les documents qu'elle atteint), chacun à sa place.
        /// </summary>
        void PlayPageInIntegratedFlash(FlashContent main, string module)
        {
            _inPageModule = module;
            OpenFlashInPage(main, module);
            foreach (FlashContent content in _pageContents.Values.SelectMany(list => list).ToList())
                PlayAddedContent(content);
        }

        /// <summary>Contenu de la page pas encore lu : lu à sa place si la page est passée au moteur intégré.</summary>
        void PlayAddedContent(FlashContent content)
        {
            if (_inPageModule is not { } module || !CanPlaceInPage(content) || FindSlot(content) != null)
                return;
            if (_slots.Count >= MaxFlashSlots)
            {
                RuntimeLogBuffer.Append($"[Flash] {MaxFlashSlots} lecteurs déjà ouverts dans la page : {content} reste à Ruffle.");
                return;
            }
            RuntimeLogBuffer.Append($"[Flash] Autre contenu de la page lu par le moteur intégré : {content}.");
            OpenFlashInPage(content, module);
        }

        /// <summary>
        /// Liste des contenus d'un document (« contents: ») : gardée, notée au journal quand elle
        /// change, et ses nouveaux contenus lus par le moteur intégré si la page y est passée.
        /// </summary>
        void OnFlashContents(string json)
        {
            IReadOnlyList<FlashContent> list = FlashContent.ParseList(json);
            if (list.Count == 0)
                return;
            Uri document = list[0].Page;
            bool known = _pageContents.TryGetValue(document, out IReadOnlyList<FlashContent>? previous);
            if (!known && _pageContents.Count >= MaxFlashDocuments)
                return;
            _pageContents[document] = list;
            if (!known || previous!.Count != list.Count || previous.Where((item, i) => !item.IsSameAs(list[i])).Any())
            {
                RuntimeLogBuffer.Append($"[Flash] Contenus Flash de {document.GetLeftPart(UriPartial.Path)} : " +
                                        string.Join(" ; ", list.Select(content => content.ToString())) + ".");
            }
            foreach (FlashContent content in list)
                PlayAddedContent(content);
        }

        /// <summary>Lit un contenu à sa place dans la page (relancé s'il l'est déjà).</summary>
        void OpenFlashInPage(FlashContent content, string module)
        {
            if (FindSlot(content) is { } running)
                CloseSlot(running);
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

            string key = SlotKey(content);
            var view = new LegacyView { IsVisible = false, KeyboardName = $"lecteur {key} : {FlashContent.ShortName(content.Swf)}" };
            OverlayLayer().Children.Add(view);
            var slot = new FlashSlot(key, content, host, view);
            _slots.Add(slot);

            host.SetBackground(!IsSelected);
            ConnectFlashHost(host, content, slot.Key);
            host.Exited += () =>
            {
                if (!_slots.Contains(slot))
                    return;
                CloseSlot(slot);
                RaiseChanged();
                // Module qui ne lit pas le contenu : l'autre (32 ou 64 bits) prend sa place.
                if (host.FailedToStart && CanPlaceInPage(content) && NextFlashModule(host.Module) is { } next)
                {
                    AnnounceFlashRetry(host.Module, next);
                    if (_inPageModule == host.Module)
                        _inPageModule = next;
                    OpenFlashInPage(content, next);
                    return;
                }
                if (CanPlaceInPage(content) && TryAutoRelaunch(host, m => OpenFlashInPage(content, m)))
                    return;
                if (Page == TabPage.Web && IsSelected)
                    Window.ShowToast(Tr("Le lecteur Flash s'est arrêté : le contenu ne s'affiche plus."), Tr("Relancer"), () => RelaunchFlashInPage(content), timeout: 10, warning: true);
            };
            WatchFlashResponsiveness(host, () => _slots.Contains(slot), m => OpenFlashInPage(content, m));
            view.Docked += () =>
            {
                if (!_slots.Contains(slot))
                    return;
                slot.Docked = true;
                view.BringToFront();
                PlaceSlot(slot);
            };
            view.DockFailed += () =>
            {
                if (!_slots.Contains(slot))
                    return;
                CloseSlot(slot);
                RaiseChanged();
                Window.ShowToast(Tr("Le lecteur Flash n'a pas pu s'afficher dans la page."), Tr("Lire avec Ruffle"), () => BackToRuffle(TopPage(content)), warning: true);
            };
            view.ShortcutPressed += (key, modifiers) =>
            {
                Window.HandleShortcut(key, modifiers);
                Window.SyncAllKeyboards();
            };
            view.Attach(host);
            StartFlashTracker(slot);
            RaiseChanged();
        }

        /// <summary>Calque des lecteurs, au-dessus de la vue web (premier enfant), sous les pages de PommeBrowser.</summary>
        Canvas OverlayLayer()
        {
            if (_overlayLayer is { } existing)
                return existing;
            var layer = new Canvas { ClipToBounds = true };
            _host.Children.Insert(_web != null ? _host.Children.IndexOf(_web) + 1 : 0, layer);
            layer.PropertyChanged += (_, e) =>
            {
                if (e.Property == Visual.BoundsProperty)
                {
                    foreach (FlashSlot slot in _slots)
                        PlaceSlot(slot);
                }
            };
            _overlayLayer = layer;
            return layer;
        }

        /// <summary>Relance après un arrêt du lecteur : le script de suivi reprend l'emplacement laissé dans la page.</summary>
        void RelaunchFlashInPage(FlashContent content)
        {
            if (FindSlot(content) != null || !CanPlaceInPage(content))
                return;
            if ((_inPageModule ?? NextFlashModule(null)) is { } module)
                OpenFlashInPage(content, module);
        }

        /// <summary>
        /// Script de suivi du contenu, dans le document principal (ses messages arrivent par le
        /// canal de Ruffle), qui le trouve dans le cadre de même origine qui le contient.
        /// </summary>
        async void StartFlashTracker(FlashSlot slot)
        {
            if (_engine is not { } engine)
                return;
            try
            {
                string post = EngineHost.ScriptPost(RuffleContent.MessageHandler, "status");
                FlashContent content = slot.Content;
                await engine.EvaluateAsync(RuffleContent.FlashTrackerScript(post, slot.Key, content, IsTopDocument(content) ? null : content.Page), isolated: false);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ObjectDisposedException or System.Runtime.InteropServices.COMException or TaskCanceledException)
            {
                RuntimeLogBuffer.Append("[Flash] Suivi du contenu dans la page impossible : " + ex.Message);
            }
        }

        /// <summary>
        /// Message du script de suivi : « clé:position », « clé:null » (contenu introuvable dans la
        /// page) ou « clé:gone » (contenu retiré par la page).
        /// </summary>
        void OnFlashRect(string message)
        {
            int separator = message.IndexOf(':', StringComparison.Ordinal);
            if (separator <= 0 || FindSlot(message[..separator]) is not { } slot)
                return;
            string payload = message[(separator + 1)..];
            FlashContent content = slot.Content;
            if (payload == "null")
            {
                string module = slot.Host.Module;
                RuntimeLogBuffer.Append($"[Flash] Contenu introuvable dans la page : {content}.");
                CloseSlot(slot);
                RaiseChanged();
                // Contenu principal, seul lu (élément introuvable) : à la place de la page, avec le même module.
                if (_slots.Count == 0 && _flashContent is { } main && main.IsSameAs(content))
                    OpenInIntegratedFlash(content, module);
                return;
            }
            if (payload == "gone")
            {
                // Retiré par la page (logo remplacé par le jeu, cadre rechargé) : ce qu'elle met à sa
                // place est décrit par le script de détection, puis lu avec le même module.
                RuntimeLogBuffer.Append($"[Flash] Contenu retiré par la page : {content}.");
                CloseSlot(slot);
                if (_flashContent is { } current && current.IsSameAs(content))
                    _flashContent = _slots.FirstOrDefault()?.Content;
                RaiseChanged();
                return;
            }
            if (FlashRect.Parse(payload) is { } rect)
            {
                slot.Rect = rect;
                PlaceSlot(slot);
            }
        }

        /// <summary>
        /// La vue couvre la partie visible du contenu ; la fenêtre du lecteur y est placée à sa
        /// taille entière, décalée si le contenu dépasse de la zone de la page.
        /// </summary>
        void PlaceSlot(FlashSlot slot)
        {
            if (_overlayLayer is not { } layer)
                return;
            LegacyView view = slot.View;
            double scaling = TopLevel.GetTopLevel(layer)?.RenderScaling ?? 1;
            FlashPlacement? placement = slot.Rect?.Place(layer.Bounds.Width, layer.Bounds.Height, scaling);
            if (placement is { } p)
            {
                Canvas.SetLeft(view, p.Left);
                Canvas.SetTop(view, p.Top);
                view.Width = p.Width;
                view.Height = p.Height;
                view.PlaceClient((p.ClientX, p.ClientY, p.ClientWidth, p.ClientHeight));
            }
            view.IsVisible = slot.Docked && placement != null;
        }

        /// <summary>Lecteur d'un contenu fermé et retiré de la page.</summary>
        void CloseSlot(FlashSlot slot)
        {
            if (!_slots.Remove(slot))
                return;
            slot.View.Detach();
            _overlayLayer?.Children.Remove(slot.View);
            ForgetFlashCallee(slot.Key, slot.Host);
            slot.Host.Close();
        }

        /// <summary>
        /// Lecteurs fermés et retirés, et la page oubliée (page quittée ou remplacée, onglet fermé,
        /// retour à Ruffle) : la suivante repart de Ruffle.
        /// </summary>
        void CloseFlashOverlay()
        {
            foreach (FlashSlot slot in _slots.ToList())
                CloseSlot(slot);
            if (_overlayLayer is { } layer)
            {
                _host.Children.Remove(layer);
                _overlayLayer = null;
            }
            _inPageModule = null;
            _slotKeys.Clear();
            _pageContents.Clear();
        }

        /// <summary>Page de l'onglet (retour à Ruffle) : celle affichée, pas celle d'un cadre.</summary>
        Uri TopPage(FlashContent content)
            => System.Uri.TryCreate(WebUrl, UriKind.Absolute, out Uri? page) ? page : content.Page;

        /// <summary>Retour à Ruffle pour les contenus lus dans la page (bouton ⚡, notification).</summary>
        public void StopFlashOverlay()
        {
            if (_slots.FirstOrDefault()?.Content is { } content)
                BackToRuffle(TopPage(content));
        }

        /// <summary>Lecteurs de la page mis en arrière-plan (onglet caché) ou au premier plan.</summary>
        void SetFlashSlotsBackground(bool background)
        {
            foreach (FlashSlot slot in _slots)
                slot.Host.SetBackground(background);
        }

        /// <summary>La page a donné le focus à l'élément d'un contenu : le clavier va à son lecteur (onglet affiché).</summary>
        void FocusFlashSlot(string key)
        {
            if (IsSelected && FindSlot(key) is { } slot)
                slot.View.TakeKeyboard();
        }

        /// <summary>Clavier des lecteurs logés dans la page (voir MainWindow.SyncAllKeyboards).</summary>
        void SyncFlashSlotsKeyboard(bool force)
        {
            foreach (FlashSlot slot in _slots)
                slot.View.SyncKeyboard(force);
        }
    }
}
