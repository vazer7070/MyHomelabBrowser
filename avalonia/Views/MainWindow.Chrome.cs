using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.classes.Profiles.Credentials;
using MyHomelabBrowser.classes.Security;
using PommeBrowser.Core;
using PommeBrowser.Linux.Core;
using PommeBrowser.Views.Dialogs;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views
{
    /// <summary>Indicateurs de la barre d'adresse : sécurité, zoom, identifiants, favori, Basilisk.</summary>
    public sealed partial class MainWindow
    {
        // ---------------------------------------------------------------
        // Sécurité de la connexion
        // ---------------------------------------------------------------

        void UpdateSecurityIcon(BrowserTab? tab)
        {
            (string icon, string brush, string tip) = (tab?.Security ?? SecurityLevel.None) switch
            {
                SecurityLevel.Secure => ("IconLock", "TextSecondaryBrush", Tr("Connexion sécurisée")),
                SecurityLevel.Trusted => ("IconLock", "AccentBrush", Tr("Connexion chiffrée avec un certificat que vous avez approuvé")),
                SecurityLevel.Mixed => ("IconWarning", "WarningBrush", Tr("Connexion sécurisée, mais la page charge des éléments non sécurisés")),
                SecurityLevel.Insecure => ("IconWarning", "DangerBrush", Tr("Connexion non sécurisée (HTTP)")),
                SecurityLevel.Local => ("IconServer", "WarningBrush", Tr("Réseau local : connexion non chiffrée")),
                _ => ("IconSearch", "TextTertiaryBrush", string.Empty)
            };
            SecurityIcon.Data = this.FindResource(icon) as Geometry;
            SecurityIcon.Foreground = FindThemeBrush(brush);
            ToolTip.SetTip(SecurityButton, tip.Length == 0 ? null : tip);
        }

        /// <summary>Informations sur la connexion, le certificat approuvé et les autorisations du site.</summary>
        void Security_Click(object? sender, RoutedEventArgs e)
        {
            if (_selected is not { Page: TabPage.Web } tab || !Uri.TryCreate(tab.WebUrl, UriKind.Absolute, out Uri? uri) || uri.Host.Length == 0)
            {
                FocusAddressBar();
                return;
            }

            var panel = new StackPanel { Spacing = 8, Width = 320, Margin = new Thickness(6) };
            panel.Children.Add(new TextBlock { Text = uri.Host, FontWeight = FontWeight.SemiBold, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis });
            panel.Children.Add(Hint(ToolTip.GetTip(SecurityButton) as string ?? string.Empty));

            if (App.CertificatePins.Get(uri) is { } pin)
            {
                panel.Children.Add(Hint(Tr("Certificat approuvé le {0} : {1}", pin.PinnedAt.ToString("d", Culture), CertificatePinStore.FormatFingerprint(pin.Sha256))));
                var forget = new Button { Content = Tr("Ne plus faire confiance à ce certificat") };
                forget.Classes.Add("link");
                forget.Click += (_, _) =>
                {
                    App.CertificatePins.Remove(pin.Authority);
                    App.SessionTrustedHosts.Remove(uri.Authority);
                    ShowToast(Tr("Le certificat de {0} n'est plus approuvé.", uri.Authority));
                };
                panel.Children.Add(forget);
            }

            string site = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
            var decisions = App.SiteSecurity.All.Where(d => string.Equals(d.Site, site, StringComparison.OrdinalIgnoreCase) || string.Equals(d.Site, uri.Host, StringComparison.OrdinalIgnoreCase)).ToList();
            if (decisions.Count > 0)
            {
                panel.Children.Add(new TextBlock { Text = Tr("Autorisations"), FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 6, 0, 0) });
                foreach (SiteDecision decision in decisions)
                {
                    var row = new DockPanel();
                    var remove = new Button { Content = Tr("Oublier") };
                    remove.Classes.Add("link");
                    DockPanel.SetDock(remove, Dock.Right);
                    SiteDecision current = decision;
                    remove.Click += (_, _) =>
                    {
                        App.SiteSecurity.Remove(current);
                        row.IsVisible = false;
                    };
                    row.Children.Add(remove);
                    row.Children.Add(new TextBlock
                    {
                        Text = PermissionPrompt.KindLabel(decision.Kind) + " : " + (decision.Allowed ? Tr("autorisé") : Tr("bloqué")),
                        VerticalAlignment = VerticalAlignment.Center
                    });
                    panel.Children.Add(row);
                }
            }

            new Flyout { Content = panel, Placement = PlacementMode.BottomEdgeAlignedLeft }.ShowAt(SecurityButton);
        }

        TextBlock Hint(string text)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            block.Classes.Add("hint");
            return block;
        }

        // ---------------------------------------------------------------
        // Favori
        // ---------------------------------------------------------------

        void UpdateFavoriteButton(BrowserTab? tab)
        {
            bool web = tab?.Page == TabPage.Web && tab.WebUrl.Length > 0;
            bool isFavorite = web && App.Favorites.Find(tab!.WebUrl) != null;
            FavoriteButton.IsVisible = web;
            FavoriteIconEmpty.IsVisible = !isFavorite;
            FavoriteIconFilled.IsVisible = isFavorite;
            ToolTip.SetTip(FavoriteButton, isFavorite ? Tr("Modifier le favori") : Tr("Ajouter aux favoris (Ctrl+D)"));
        }

        void Favorite_Click(object? sender, RoutedEventArgs e) => ToggleFavorite();

        /// <summary>Ajoute la page aux favoris, ou ouvre la modification du favori existant.</summary>
        public void ToggleFavorite()
        {
            if (_selected is not { Page: TabPage.Web } tab || tab.WebUrl.Length == 0)
                return;

            if (App.Favorites.Find(tab.WebUrl) is { } existing)
            {
                _ = FavoriteDialog.EditAsync(this, existing);
                return;
            }

            App.Favorites.Add(tab.Title, tab.WebUrl);
            ShowToast(Tr("Ajouté aux favoris"), Tr("Modifier"), () =>
            {
                if (App.Favorites.Find(tab.WebUrl) is { } added)
                    _ = FavoriteDialog.EditAsync(this, added);
            });
            UpdateChrome();
        }

        // ---------------------------------------------------------------
        // Zoom
        // ---------------------------------------------------------------

        void UpdateZoomButton(BrowserTab? tab)
        {
            double zoom = tab?.Page == TabPage.Web ? tab.Zoom : 1;
            ZoomButton.IsVisible = Math.Abs(zoom - 1) > 0.001;
            ZoomButtonText.Text = SiteZoomStore.Format(zoom);
            ToolTip.SetTip(ZoomButton, Tr("Zoom : {0} — cliquer pour revenir à 100 %", SiteZoomStore.Format(zoom)));
        }

        void ZoomReset_Click(object? sender, RoutedEventArgs e) => SetZoom(1);

        public void ZoomStep(int direction)
        {
            if (_selected is { Page: TabPage.Web } tab)
                SetZoom(SiteZoomStore.Step(tab.Zoom, direction));
        }

        public void SetZoom(double zoom)
        {
            if (_selected is not { Page: TabPage.Web } tab)
                return;
            tab.SetZoom(Math.Clamp(zoom, SiteZoomStore.MinimumZoom, SiteZoomStore.MaximumZoom));
            UpdateZoomButton(tab);
        }

        // ---------------------------------------------------------------
        // Identifiants
        // ---------------------------------------------------------------

        void UpdateCredentialButton(BrowserTab? tab)
        {
            bool visible = tab is { IsPrivate: false, Page: TabPage.Web } &&
                           CredentialOrigin.TryCreateTrusted(tab.WebUrl, out string origin) &&
                           (!App.Vault.IsUnlocked || App.Vault.ForOrigin(origin).Count > 0) &&
                           App.Vault.Service.VaultExists;
            CredentialButton.IsVisible = visible;
        }

        void Credential_Click(object? sender, RoutedEventArgs e)
        {
            if (_selected is { } tab)
                _ = App.Vault.FillAsync(this, tab);
        }

        // ---------------------------------------------------------------
        // Basilisk
        // ---------------------------------------------------------------

        void UpdateLegacyButton(BrowserTab? tab)
            => LegacyButton.IsVisible = App.Settings.EnableFlashSupport && App.BasiliskExecutable != null &&
                                        tab?.Page == TabPage.Web && BasiliskInstall.IsOpenable(tab.WebUrl, out _);

        void Legacy_Click(object? sender, RoutedEventArgs e)
        {
            if (_selected is not { } tab || !BasiliskInstall.IsOpenable(tab.WebUrl, out Uri uri))
                return;

            tab.OpenInBasilisk(uri);
            // Basilisk lancé : le site peut s'y ouvrir d'office ensuite (« Lire avec Ruffle » annule).
            if (tab.Page == TabPage.Legacy && FlashDomainRules.GetRule(uri) != FlashRuleMode.Legacy)
            {
                ShowToast(Tr("Toujours ouvrir {0} dans Basilisk ?", uri.Host), Tr("Toujours"),
                    () => FlashDomainRules.SetRule(uri, FlashRuleMode.Legacy), timeout: 8);
            }
        }

        /// <summary>Basilisk introuvable : où le trouver et comment l'installer.</summary>
        public async void ShowBasiliskMissing()
        {
            string body = OperatingSystem.IsLinux()
                ? Tr("Basilisk lit les contenus Flash avec le lecteur d'origine, dans sa propre fenêtre.\n\n1. Téléchargez Basilisk pour Linux sur basilisk-browser.org.\n2. Décompressez l'archive dans ~/.local/share/basilisk, ou indiquez l'emplacement de basilisk dans les préférences.\n3. Copiez le module Flash libflashplayer.so dans {0}.", AppPaths.SharedData("plugins"))
                : OperatingSystem.IsWindows()
                    ? Tr("Basilisk lit les contenus Flash avec le lecteur d'origine, dans sa propre fenêtre. Téléchargez la version portable de Basilisk sur basilisk-browser.org, puis indiquez l'emplacement de basilisk.exe dans les paramètres.")
                    : Tr("Basilisk n'est pas disponible sur ce système : les contenus Flash sont lus avec Ruffle.");

            int choice = await Dialogs.Dialogs.ChoiceAsync(this, Tr("Basilisk n'est pas installé"), body,
                (Tr("Site de Basilisk"), false, false), (Tr("Paramètres"), false, false), (Tr("Fermer"), true, false));
            if (choice == 0)
                NewTab("https://www.basilisk-browser.org/download.shtml", select: true);
            else if (choice == 1)
                OpenSettings("flash");
        }
    }
}
