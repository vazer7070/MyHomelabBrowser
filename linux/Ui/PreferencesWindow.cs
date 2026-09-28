using System;
using System.Linq;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.AdBlock.Models;
using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.classes.Security;
using PommeBrowser.Linux.Core;
using PommeBrowser.Linux.Web;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Ui
{
    /// <summary>Préférences : appliquées immédiatement, comme dans les applications GNOME.</summary>
    static class PreferencesWindow
    {
        static readonly BrowserSettings.SearchEngine[] Engines = Enum.GetValues<BrowserSettings.SearchEngine>();

        public static void Show(BrowserApplication app, BrowserWindow? window)
        {
            var dialog = Adw.PreferencesDialog.New();
            dialog.SetSearchEnabled(true);
            dialog.Add(GeneralPage(app, dialog));
            dialog.Add(PrivacyPage(app, dialog));
            dialog.Add(FlashAndServicesPage(app, dialog));
            dialog.Present(window?.Window);
        }

        // ---------------------------------------------------------------
        // Général
        // ---------------------------------------------------------------

        static Adw.PreferencesPage GeneralPage(BrowserApplication app, Adw.PreferencesDialog dialog)
        {
            LinuxSettings settings = app.Settings;
            var page = Page(Tr("Général"), "preferences-system-symbolic");

            var browsing = Group(Tr("Navigation"));
            var search = Combo(Tr("Moteur de recherche"), Engines.Select(UrlResolver.GetSearchEngineName).ToArray(), Array.IndexOf(Engines, settings.Search));
            search.OnNotify += (_, args) =>
            {
                if (args.Pspec.GetName() != "selected")
                    return;
                settings.Search = Engines[(int)search.GetSelected()];
                app.SaveSettings();
            };
            browsing.Add(search);
            browsing.Add(Switch(Tr("Rouvrir les onglets au démarrage"), null, settings.RestoreSession, value =>
            {
                settings.RestoreSession = value;
                app.SaveSettings();
            }));

            var downloads = Adw.ActionRow.New();
            downloads.SetTitle(Tr("Dossier des téléchargements"));
            downloads.SetSubtitle(GLib.Functions.MarkupEscapeText(app.Engine.Downloads.Directory, -1));
            var choose = Gtk.Button.NewWithLabel(Tr("Choisir…"));
            choose.SetValign(Gtk.Align.Center);
            choose.OnClicked += async (_, _) =>
            {
                try
                {
                    var picker = Gtk.FileDialog.New();
                    picker.SetTitle(Tr("Dossier des téléchargements"));
                    Gio.File? folder = await picker.SelectFolderAsync(dialog.GetRoot() as Gtk.Window);
                    if (folder?.GetPath() is { } path)
                    {
                        settings.DownloadDirectory = path;
                        app.SaveSettings();
                        downloads.SetSubtitle(GLib.Functions.MarkupEscapeText(path, -1));
                    }
                }
                catch (GLib.GException)
                {
                    // Sélection annulée.
                }
            };
            downloads.AddSuffix(choose);
            browsing.Add(downloads);
            page.Add(browsing);

            var appearance = Group(Tr("Apparence"));
            AppTheme[] themes = { AppTheme.System, AppTheme.Light, AppTheme.Dark };
            var theme = Combo(Tr("Thème"), new[] { Tr("Système"), Tr("Clair"), Tr("Sombre") }, Array.IndexOf(themes, app.Appearance.Theme));
            theme.OnNotify += (_, args) =>
            {
                if (args.Pspec.GetName() != "selected")
                    return;
                app.Appearance.Theme = themes[(int)theme.GetSelected()];
                app.SaveAppearance();
            };
            appearance.Add(theme);

            string[] languages = { "fr", "en" };
            var language = Combo(Tr("Langue"), new[] { "Français", "English" }, Array.IndexOf(languages, app.Appearance.Language));
            language.SetSubtitle(Tr("Prend effet au prochain démarrage"));
            language.OnNotify += (_, args) =>
            {
                if (args.Pspec.GetName() != "selected")
                    return;
                app.Appearance.Language = languages[(int)language.GetSelected()];
                app.SaveAppearance();
                dialog.AddToast(Adw.Toast.New(Tr("La langue changera au prochain démarrage de PommeBrowser.")));
            };
            appearance.Add(language);
            page.Add(appearance);
            page.Add(UpdatesGroup(app, dialog));
            return page;
        }

        /// <summary>Mises à jour : automatiques pour l'AppImage (téléchargée, vérifiée, appliquée au redémarrage).</summary>
        static Adw.PreferencesGroup UpdatesGroup(BrowserApplication app, Adw.PreferencesDialog dialog)
        {
            LinuxSettings settings = app.Settings;
            Updater updater = app.Updater;
            var group = Group(Tr("Mises à jour"));
            group.Add(Switch(Tr("Rechercher les mises à jour au démarrage"),
                Updater.AppImagePath != null ? Tr("La nouvelle version est installée automatiquement, puis appliquée au redémarrage.") : Tr("Pour l'AppImage uniquement."),
                settings.AutoUpdate, value =>
                {
                    settings.AutoUpdate = value;
                    app.SaveSettings();
                }));

            var status = Adw.ActionRow.New();
            status.SetTitle(Tr("PommeBrowser {0}", Updater.Current.ToString(3)));
            var check = Gtk.Button.NewWithLabel(Tr("Rechercher"));
            check.SetValign(Gtk.Align.Center);
            check.OnClicked += (_, _) =>
            {
                if (updater.Installed != null)
                    app.Restart();
                else
                    _ = app.CheckForUpdatesAsync(manual: true);
            };
            status.AddSuffix(check);
            group.Add(status);

            void Refresh()
            {
                status.SetSubtitle(GLib.Functions.MarkupEscapeText(updater.Status, -1));
                check.SetSensitive(!updater.IsBusy);
                check.SetLabel(updater.Installed != null ? Tr("Redémarrer") : Tr("Rechercher"));
                if (updater.Installed != null)
                    check.AddCssClass("suggested-action");
            }
            Action changed = Refresh;
            updater.Changed += changed;
            dialog.OnClosed += (_, _) => updater.Changed -= changed;
            Refresh();
            return group;
        }

        // ---------------------------------------------------------------
        // Confidentialité et sécurité
        // ---------------------------------------------------------------

        static Adw.PreferencesPage PrivacyPage(BrowserApplication app, Adw.PreferencesDialog dialog)
        {
            LinuxSettings settings = app.Settings;
            AdBlocker blocker = app.Engine.AdBlocker;
            var page = Page(Tr("Confidentialité"), "security-high-symbolic");

            var security = Group(Tr("Sécurité"));
            security.Add(Switch(Tr("HTTPS automatique"), Tr("Essayer https:// avant http:// (hors réseau local)"), settings.HttpsUpgrade, value =>
            {
                settings.HttpsUpgrade = value;
                app.SaveSettings();
            }));
            security.Add(Switch(Tr("Protection contre le pistage"), Tr("Limite le suivi d'un site à l'autre (ITP de WebKit)"), settings.TrackingPrevention, value =>
            {
                settings.TrackingPrevention = value;
                app.SaveSettings();
            }));
            security.Add(Switch(Tr("Bloquer les cookies tiers"), null, settings.BlockThirdPartyCookies, value =>
            {
                settings.BlockThirdPartyCookies = value;
                app.SaveSettings();
            }));
            page.Add(security);

            AdBlockSettings adblock = blocker.Settings;
            var ads = Group(Tr("Bloqueur de publicités"));
            ads.SetDescription(blocker.Status);
            ads.Add(Switch(Tr("Bloquer les publicités et les pisteurs"), null, adblock.Enabled, value =>
                blocker.UpdateSettings(s => s.Enabled = value, rebuild: false)));
            ads.Add(Switch(Tr("Masquer les emplacements publicitaires"), Tr("Règles de masquage des listes (recompilation d'environ 10 secondes)"), adblock.CosmeticFiltering, value =>
                blocker.UpdateSettings(s => s.CosmeticFiltering = value, rebuild: true)));
            ads.Add(Switch(Tr("Ne pas filtrer le réseau local"), Tr("Les interfaces du homelab restent intactes"), adblock.BypassPrivateNetworks, value =>
                blocker.UpdateSettings(s => s.BypassPrivateNetworks = value, rebuild: false)));
            foreach (AdBlockSubscription subscription in adblock.Subscriptions)
            {
                string id = subscription.Id;
                ads.Add(Switch(subscription.Name, subscription.Url, subscription.Enabled, value =>
                {
                    blocker.UpdateSettings(s =>
                    {
                        if (s.Subscriptions.FirstOrDefault(x => x.Id == id) is { } target)
                            target.Enabled = value;
                    }, rebuild: false);
                    _ = blocker.UpdateListsAsync(force: false).ContinueWith(_ => MainThread.Post(() => _ = blocker.RebuildAsync()));
                }));
            }
            var update = Gtk.Button.NewWithLabel(Tr("Mettre à jour"));
            update.SetValign(Gtk.Align.Center);
            update.OnClicked += async (_, _) =>
            {
                update.SetSensitive(false);
                var result = await blocker.UpdateListsAsync(force: true);
                dialog.AddToast(Adw.Toast.New(result.Message));
                ads.SetDescription(blocker.Status);
                update.SetSensitive(true);
            };
            ads.SetHeaderSuffix(update);
            page.Add(ads);

            var allowed = Group(Tr("Sites sans bloqueur"));
            foreach (string domain in adblock.AllowlistedDomains)
            {
                var row = RemovableRow(domain, null, row => { blocker.SetSiteAllowed(domain, false); allowed.Remove(row); });
                allowed.Add(row);
            }
            if (adblock.AllowlistedDomains.Count == 0)
                allowed.SetDescription(Tr("Aucun. Le bouton du bloqueur, dans la barre d'en-tête, désactive le blocage sur un site."));
            page.Add(allowed);

            var permissions = Group(Tr("Autorisations des sites"));
            foreach (SiteDecision decision in app.SiteSecurity.All.OrderBy(d => d.Site, StringComparer.OrdinalIgnoreCase))
            {
                string state = decision.Allowed ? Tr("autorisé") : Tr("bloqué");
                permissions.Add(RemovableRow(decision.Site, PermissionPrompt.KindLabel(decision.Kind) + " · " + state, row =>
                {
                    app.SiteSecurity.Remove(decision);
                    permissions.Remove(row);
                }));
            }
            if (app.SiteSecurity.All.Count == 0)
                permissions.SetDescription(Tr("Aucune autorisation mémorisée."));
            page.Add(permissions);

            var certificates = Group(Tr("Certificats approuvés"));
            certificates.SetDescription(Tr("Certificats non reconnus que vous avez acceptés (serveurs du homelab…). Si l'un d'eux change, PommeBrowser vous prévient."));
            foreach (PinnedCertificate pin in app.CertificatePins.GetAll())
            {
                string detail = Tr("Expire le {0}", pin.NotAfter.ToString("d", Culture)) + " · " + CertificatePinStore.FormatFingerprint(pin.Sha256)[..23] + "…";
                certificates.Add(RemovableRow(pin.Authority, detail, row =>
                {
                    app.CertificatePins.Remove(pin.Authority);
                    certificates.Remove(row);
                }));
            }
            page.Add(certificates);

            var data = Group(Tr("Données de navigation"));
            var clear = Adw.ActionRow.New();
            clear.SetTitle(Tr("Effacer l'historique et les données des sites"));
            clear.SetSubtitle(Tr("Cookies, cache, stockage des sites"));
            var clearButton = Gtk.Button.NewWithLabel(Tr("Effacer…"));
            clearButton.AddCssClass("destructive-action");
            clearButton.SetValign(Gtk.Align.Center);
            clearButton.OnClicked += (_, _) => AskClearData(app, dialog);
            clear.AddSuffix(clearButton);
            data.Add(clear);
            page.Add(data);
            return page;
        }

        static void AskClearData(BrowserApplication app, Adw.PreferencesDialog preferences)
        {
            var dialog = Adw.AlertDialog.New(Tr("Effacer les données de navigation"),
                Tr("L'historique, les cookies, le cache et les données enregistrées par les sites seront effacés. Vous serez déconnecté des sites concernés."));
            dialog.AddResponse("cancel", Tr("Annuler"));
            dialog.AddResponse("hour", Tr("Dernière heure"));
            dialog.AddResponse("all", Tr("Tout effacer"));
            dialog.SetResponseAppearance("all", Adw.ResponseAppearance.Destructive);
            dialog.SetCloseResponse("cancel");
            dialog.OnResponse += async (_, args) =>
            {
                if (args.Response is not ("hour" or "all"))
                    return;

                bool all = args.Response == "all";
                app.History.RemoveSince(all ? DateTime.MinValue : DateTime.Now.AddHours(-1));
                try
                {
                    await app.Engine.ClearBrowsingDataAsync(all ? null : TimeSpan.FromHours(1));
                    preferences.AddToast(Adw.Toast.New(Tr("Données de navigation effacées.")));
                }
                catch (Exception ex)
                {
                    preferences.AddToast(Adw.Toast.New(Tr("Effacement incomplet : {0}", ex.Message)));
                }
            };
            dialog.Present(preferences);
        }

        // ---------------------------------------------------------------
        // Flash et services
        // ---------------------------------------------------------------

        static Adw.PreferencesPage FlashAndServicesPage(BrowserApplication app, Adw.PreferencesDialog dialog)
        {
            LinuxSettings settings = app.Settings;
            RuffleSupport ruffle = app.Engine.Ruffle;
            var page = Page(Tr("Flash et services"), "network-server-symbolic");

            var flash = Group(Tr("Flash"));
            string ruffleState = ruffle.IsAvailable
                ? Tr("Ruffle {0}, intégré à PommeBrowser", ruffle.InstalledVersion ?? "?")
                : Tr("Ruffle est absent de cette installation");
            var ruffleRow = Switch(Tr("Lire les contenus Flash avec Ruffle"), ruffleState, settings.EnableRuffle && ruffle.IsAvailable, value =>
            {
                settings.EnableRuffle = value;
                app.SaveSettings();
            });
            ruffleRow.SetSensitive(ruffle.IsAvailable);
            flash.Add(ruffleRow);
            page.Add(flash);
            page.Add(BasiliskGroup(app, dialog));

            var services = Group(Tr("Services du homelab"));
            services.Add(Switch(Tr("Surveiller les services"), Tr("Vérifie régulièrement qu'ils répondent"), settings.ServiceMonitoring, value =>
            {
                settings.ServiceMonitoring = value;
                app.SaveSettings();
            }));
            var interval = Adw.SpinRow.NewWithRange(15, 3600, 15);
            interval.SetTitle(Tr("Intervalle de vérification (secondes)"));
            interval.SetValue(settings.ServiceCheckIntervalSeconds);
            interval.OnNotify += (_, args) =>
            {
                if (args.Pspec.GetName() != "value")
                    return;
                settings.ServiceCheckIntervalSeconds = (int)interval.GetValue();
                app.SaveSettings();
            };
            services.Add(interval);
            services.Add(Switch(Tr("Prévenir quand un service tombe ou revient"), null, settings.ServiceAlerts, value =>
            {
                settings.ServiceAlerts = value;
                app.SaveSettings();
            }));
            page.Add(services);
            return page;
        }

        /// <summary>
        /// Basilisk (Flash d'origine, fenêtre séparée) : emplacement, module Flash et sites
        /// ouverts d'office dans Basilisk.
        /// </summary>
        static Adw.PreferencesGroup BasiliskGroup(BrowserApplication app, Adw.PreferencesDialog dialog)
        {
            LinuxSettings settings = app.Settings;
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string plugins = LinuxPaths.SharedData("plugins");

            var group = Group(Tr("Basilisk (Flash d'origine)"));
            group.SetDescription(Tr("Pour les contenus que Ruffle ne lit pas : la page s'ouvre dans une fenêtre Basilisk, avec le lecteur Flash d'origine. Menu principal → Ouvrir dans Basilisk."));

            var location = Adw.ActionRow.New();
            location.SetTitle(Tr("Basilisk"));
            void ShowLocation()
            {
                string? executable = app.BasiliskExecutable;
                (string? name, string? version) = BasiliskInstall.Describe(executable);
                location.SetSubtitle(GLib.Functions.MarkupEscapeText(executable == null
                    ? Tr("Introuvable : téléchargez Basilisk pour Linux sur basilisk-browser.org.")
                    : $"{name ?? "Basilisk"} {version} — {executable}".Replace("  ", " "), -1));
            }
            var choose = Gtk.Button.NewWithLabel(Tr("Choisir…"));
            choose.SetValign(Gtk.Align.Center);
            choose.OnClicked += async (_, _) =>
            {
                try
                {
                    var picker = Gtk.FileDialog.New();
                    picker.SetTitle(Tr("Exécutable de Basilisk"));
                    Gio.File? file = await picker.OpenAsync(dialog.GetRoot() as Gtk.Window);
                    if (file?.GetPath() is not { } path)
                        return;
                    if (!BasiliskInstall.IsLaunchable(path))
                    {
                        dialog.AddToast(Adw.Toast.New(Tr("Ce fichier n'est pas un programme exécutable.")));
                        return;
                    }
                    settings.BasiliskPath = path;
                    app.SaveSettings();
                    ShowLocation();
                }
                catch (GLib.GException)
                {
                    // Sélection annulée.
                }
            };
            location.AddSuffix(choose);
            ShowLocation();
            group.Add(location);

            var plugin = Adw.ActionRow.New();
            plugin.SetTitle(Tr("Module Flash"));
            string? found = BasiliskInstall.FindFlashPlugin(home, plugins);
            plugin.SetSubtitle(GLib.Functions.MarkupEscapeText(found ?? Tr("Absent : copiez libflashplayer.so dans {0}", plugins), -1));
            var open = Gtk.Button.NewWithLabel(Tr("Ouvrir le dossier"));
            open.SetValign(Gtk.Align.Center);
            open.OnClicked += async (_, _) =>
            {
                try
                {
                    System.IO.Directory.CreateDirectory(plugins);
                    await Gtk.FileLauncher.New(Gio.FileHelper.NewForPath(plugins)).LaunchAsync(dialog.GetRoot() as Gtk.Window);
                }
                catch (Exception ex) when (ex is GLib.GException or System.IO.IOException or UnauthorizedAccessException)
                {
                    dialog.AddToast(Adw.Toast.New(plugins));
                }
            };
            plugin.AddSuffix(open);
            group.Add(plugin);

            // Sites ouverts d'office dans Basilisk (réglage mémorisé depuis le menu).
            foreach ((string host, FlashRuleMode mode) in FlashDomainRules.GetAll().Where(r => r.Value == FlashRuleMode.Legacy).OrderBy(r => r.Key))
            {
                var row = Adw.ActionRow.New();
                row.SetTitle(GLib.Functions.MarkupEscapeText(host, -1));
                row.SetSubtitle(Tr("Toujours ouvert dans Basilisk"));
                var remove = Gtk.Button.NewFromIconName("user-trash-symbolic");
                remove.AddCssClass("flat");
                remove.SetValign(Gtk.Align.Center);
                remove.SetTooltipText(Tr("Ne plus ouvrir ce site dans Basilisk"));
                remove.OnClicked += (_, _) =>
                {
                    if (Uri.TryCreate("https://" + host + "/", UriKind.Absolute, out Uri? uri))
                        FlashDomainRules.RemoveRule(uri);
                    group.Remove(row);
                };
                row.AddSuffix(remove);
                group.Add(row);
            }
            return group;
        }

        // ---------------------------------------------------------------
        // Éléments
        // ---------------------------------------------------------------

        static Adw.PreferencesPage Page(string title, string icon)
        {
            var page = Adw.PreferencesPage.New();
            page.SetTitle(title);
            page.SetIconName(icon);
            return page;
        }

        static Adw.PreferencesGroup Group(string title)
        {
            var group = Adw.PreferencesGroup.New();
            group.SetTitle(title);
            return group;
        }

        static Adw.SwitchRow Switch(string title, string? subtitle, bool value, Action<bool> changed)
        {
            var row = Adw.SwitchRow.New();
            row.SetTitle(GLib.Functions.MarkupEscapeText(title, -1));
            if (subtitle != null)
                row.SetSubtitle(GLib.Functions.MarkupEscapeText(subtitle, -1));
            row.SetActive(value);
            row.OnNotify += (_, args) =>
            {
                if (args.Pspec.GetName() == "active")
                    changed(row.GetActive());
            };
            return row;
        }

        static Adw.ComboRow Combo(string title, string[] choices, int selected)
        {
            var row = Adw.ComboRow.New();
            row.SetTitle(title);
            row.SetModel(Gtk.StringList.New(choices));
            row.SetSelected((uint)Math.Max(0, selected));
            return row;
        }

        static Adw.ActionRow RemovableRow(string title, string? subtitle, Action<Adw.ActionRow> remove)
        {
            var row = Adw.ActionRow.New();
            row.SetTitle(GLib.Functions.MarkupEscapeText(title, -1));
            if (subtitle != null)
                row.SetSubtitle(GLib.Functions.MarkupEscapeText(subtitle, -1));
            var button = Panels.SuffixButton("user-trash-symbolic", Tr("Retirer"));
            button.OnClicked += (_, _) => remove(row);
            row.AddSuffix(button);
            return row;
        }
    }
}
