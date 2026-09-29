using System;
using System.Collections.Generic;
using MyHomelabBrowser.classes.Security;
using PommeBrowser.Engine;
using PommeBrowser.Views.Dialogs;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>
    /// Autorisations demandées par les sites (position, caméra, notifications…). La réponse peut
    /// être mémorisée par site, dans le même fichier et sous les mêmes noms que l'édition Windows ;
    /// en navigation privée, elle n'est gardée que jusqu'à la fermeture du navigateur.
    /// </summary>
    public static class PermissionPrompt
    {
        static readonly Dictionary<(string Site, string Kind), bool> PrivateDecisions = new();

        public static async void Handle(BrowserTab tab, PermissionRequest request)
        {
            try
            {
                string site = request.Origin.ToLowerInvariant();
                BrowserApp app = tab.Window.App;

                switch (request.Kind)
                {
                    // Pointeur capturé (jeux) : sans risque, relâché avec Échap.
                    case PermissionKind.PointerLock:
                        request.Allow();
                        return;
                    // Liste des périphériques : seulement si la caméra ou le micro ont été autorisés.
                    case PermissionKind.MediaDevices:
                        if (Get(app, tab.IsPrivate, site, "Camera") == true || Get(app, tab.IsPrivate, site, "Microphone") == true)
                            request.Allow();
                        else
                            request.Deny();
                        return;
                }

                if (site.Length == 0 || Describe(request.Kind) is not { } permission)
                {
                    request.Deny();
                    return;
                }

                // Caméra et micro ensemble : les deux doivent avoir été accordés.
                var decisions = new List<bool?>();
                foreach (string kind in permission.Kinds)
                    decisions.Add(Get(app, tab.IsPrivate, site, kind));
                if (decisions.Contains(false))
                {
                    request.Deny();
                    return;
                }
                if (!decisions.Contains(null))
                {
                    request.Allow();
                    return;
                }

                string host = Uri.TryCreate(request.Origin, UriKind.Absolute, out Uri? uri) ? uri.Host : request.Origin;
                var dialog = new FormDialog(Tr("Autorisation demandée"), Tr("Autoriser"), cancelLabel: Tr("Bloquer"));
                dialog.AddText(Tr("{0} souhaite {1}.", host, permission.Label));
                var remember = dialog.AddCheck(tab.IsPrivate ? Tr("Mémoriser jusqu'à la fermeture du navigateur") : Tr("Mémoriser pour ce site"), true);

                bool allowed = await dialog.ShowAsync(tab.Window);
                if (allowed)
                    request.Allow();
                else
                    request.Deny();

                if (remember.IsChecked == true)
                {
                    foreach (string kind in permission.Kinds)
                    {
                        if (tab.IsPrivate)
                            PrivateDecisions[(site, kind)] = allowed;
                        else
                            app.SiteSecurity.Set(site, kind, allowed);
                    }
                }
            }
            catch (Exception)
            {
                request.Deny();
            }
        }

        static bool? Get(BrowserApp app, bool isPrivate, string site, string kind)
        {
            if (isPrivate)
                return PrivateDecisions.TryGetValue((site, kind), out bool decision) ? decision : null;
            // Édition GTK : mêmes décisions, noms en minuscules.
            return app.SiteSecurity.Get(site, kind) ?? app.SiteSecurity.Get(site, kind.ToLowerInvariant());
        }

        static (string[] Kinds, string Label)? Describe(PermissionKind kind) => kind switch
        {
            PermissionKind.Geolocation => (new[] { "Geolocation" }, Tr("connaître votre position")),
            PermissionKind.Notifications => (new[] { "Notifications" }, Tr("afficher des notifications")),
            PermissionKind.Clipboard => (new[] { "ClipboardRead" }, Tr("lire le contenu du presse-papiers")),
            PermissionKind.StorageAccess => (new[] { "StorageAccess" }, Tr("utiliser ses cookies sur ce site")),
            PermissionKind.ScreenCapture => (new[] { "ScreenCapture" }, Tr("partager votre écran")),
            PermissionKind.Camera => (new[] { "Camera" }, Tr("utiliser votre caméra")),
            PermissionKind.Microphone => (new[] { "Microphone" }, Tr("utiliser votre micro")),
            PermissionKind.CameraAndMicrophone => (new[] { "Camera", "Microphone" }, Tr("utiliser votre caméra et votre micro")),
            _ => null
        };

        /// <summary>Libellé d'une autorisation mémorisée (réglages), noms Windows ou GTK.</summary>
        public static string KindLabel(string kind) => kind.ToLowerInvariant() switch
        {
            "geolocation" => Tr("Position"),
            "notifications" => Tr("Notifications"),
            "clipboard" or "clipboardread" => Tr("Presse-papiers"),
            "storage-access" or "storageaccess" => Tr("Cookies intersites"),
            "screen" or "screencapture" => Tr("Partage d'écran"),
            "camera" => Tr("Caméra"),
            "microphone" => Tr("Micro"),
            _ when kind == SiteSecurityStore.InsecureHttp => Tr("Ouverture en HTTP"),
            _ => kind
        };
    }
}
