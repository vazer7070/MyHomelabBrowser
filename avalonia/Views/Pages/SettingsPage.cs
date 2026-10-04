using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.AdBlock.Models;
using MyHomelabBrowser.classes.Flash;
using MyHomelabBrowser.classes.Security;
using PommeBrowser.Core;
using PommeBrowser.Engine;
using PommeBrowser.Legacy;
using PommeBrowser.Linux.Core;
using PommeBrowser.Views.Dialogs;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Pages
{
    /// <summary>
    /// Paramètres, par section comme l'édition Windows : Général, Téléchargements, Historique,
    /// Mises à jour, Bloqueur de publicités, Avancé. Chaque changement est enregistré aussitôt.
    /// </summary>
    public sealed class SettingsPage : UserControl, IDisposable
    {
        sealed record Section(string Key, string Title, string Icon, Func<Control> Build);

        readonly MainWindow _window;
        readonly BrowserApp _app;
        readonly List<Section> _sections;
        readonly ListBox _nav = new();
        readonly ContentControl _content = new();
        readonly ScrollViewer _scroller;
        bool _building;
        // Modules utilisés par le moteur Flash intégré (mis à jour après une installation).
        TextBlock? _integratedModules;

        public SettingsPage(MainWindow window, string? section)
        {
            _window = window;
            _app = window.App;
            this.Bind(BackgroundProperty, this.GetResourceObservable("WindowBackgroundBrush"));

            _sections = new List<Section>
            {
                new("general", Tr("Général"), "IconSettings", BuildGeneral),
                new("downloads", Tr("Téléchargements"), "IconDownload", BuildDownloads),
                new("history", Tr("Historique"), "IconHistory", BuildHistory),
                new("updates", Tr("Mises à jour"), "IconSync", BuildUpdates),
                new("adblock", Tr("Bloqueur de publicités"), "IconShield", BuildAdBlock),
                new("flash", Tr("Avancé"), "IconFlash", BuildAdvanced)
            };

            _nav.ItemsSource = _sections.Select(s => NavItem(s)).ToList();
            _nav.Width = 230;
            _nav.Background = Brushes.Transparent;
            _nav.SelectionChanged += (_, _) =>
            {
                if (_nav.SelectedIndex >= 0)
                    ShowSection(_sections[_nav.SelectedIndex]);
            };

            var navColumn = new StackPanel { Margin = new Thickness(16, 32, 8, 16), Spacing = 12 };
            navColumn.Children.Add(new TextBlock { Text = Tr("Paramètres"), FontSize = 24, FontWeight = FontWeight.SemiBold, Margin = new Thickness(8, 0, 0, 0) });
            var subtitle = new TextBlock { Text = Tr("Personnalisez le navigateur et ses modules"), Margin = new Thickness(8, 0, 0, 8), TextWrapping = TextWrapping.Wrap };
            subtitle.Classes.Add("hint");
            navColumn.Children.Add(subtitle);
            navColumn.Children.Add(_nav);

            _scroller = new ScrollViewer { Content = new Border { Child = _content, MaxWidth = 720, Margin = new Thickness(24, 32, 24, 48), HorizontalAlignment = HorizontalAlignment.Left }, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            grid.Children.Add(navColumn);
            Grid.SetColumn(_scroller, 1);
            grid.Children.Add(_scroller);
            Content = grid;

            _app.AdBlock.Changed += OnAdBlockChanged;
            ScrollTo(section);
        }

        public void Dispose() => _app.AdBlock.Changed -= OnAdBlockChanged;

        void OnAdBlockChanged()
        {
            if (!_building && _nav.SelectedIndex >= 0 && _sections[_nav.SelectedIndex].Key == "adblock")
                ShowSection(_sections[_nav.SelectedIndex]);
        }

        /// <summary>Affiche une section (« general », « adblock », « flash »…).</summary>
        public void ScrollTo(string? section)
        {
            int index = Math.Max(0, _sections.FindIndex(s => s.Key == section));
            if (_nav.SelectedIndex == index)
                ShowSection(_sections[index]);
            else
                _nav.SelectedIndex = index;
        }

        Control NavItem(Section section)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Margin = new Thickness(2, 4) };
            var icon = new PathIcon { Width = 15, Height = 15 };
            icon.Bind(PathIcon.DataProperty, this.GetResourceObservable(section.Icon));
            row.Children.Add(icon);
            row.Children.Add(new TextBlock { Text = section.Title, VerticalAlignment = VerticalAlignment.Center });
            return row;
        }

        void ShowSection(Section section)
        {
            _building = true;
            try
            {
                var panel = new StackPanel { Spacing = 10 };
                panel.Children.Add(new TextBlock { Text = section.Title, FontSize = 22, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
                panel.Children.Add(section.Build());
                _content.Content = panel;
                _scroller.Offset = default;
            }
            finally
            {
                _building = false;
            }
        }

        BrowserSettings Settings => _app.Settings;

        void Save() => _app.SettingsService.Save();

        // ---------------------------------------------------------------
        // Éléments
        // ---------------------------------------------------------------

        Border Card(string title, string? description, params Control[] children)
        {
            var box = new StackPanel { Spacing = 10 };
            box.Children.Add(new TextBlock { Text = title, FontSize = 15, FontWeight = FontWeight.SemiBold });
            if (description != null)
                box.Children.Add(Hint(description));
            foreach (Control child in children)
                box.Children.Add(child);
            var card = new Border { Child = box };
            card.Classes.Add("card");
            return card;
        }

        static TextBlock Hint(string text)
        {
            var block = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap };
            block.Classes.Add("hint");
            return block;
        }

        static CheckBox Check(string label, bool value, Action<bool> changed)
        {
            var check = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, IsChecked = value };
            check.IsCheckedChanged += (_, _) => changed(check.IsChecked == true);
            return check;
        }

        static Control Choice<T>(string label, IReadOnlyList<(T Value, string Label)> items, T selected, Action<T> changed)
        {
            var combo = new ComboBox
            {
                ItemsSource = items.Select(i => i.Label).ToList(),
                SelectedIndex = Math.Max(0, items.ToList().FindIndex(i => EqualityComparer<T>.Default.Equals(i.Value, selected))),
                MinWidth = 240
            };
            combo.SelectionChanged += (_, _) =>
            {
                if (combo.SelectedIndex >= 0)
                    changed(items[combo.SelectedIndex].Value);
            };
            var row = new DockPanel();
            DockPanel.SetDock(combo, Dock.Right);
            row.Children.Add(combo);
            row.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
            return row;
        }

        static TextBox Entry(string? text, string? placeholder, Action<string> committed)
        {
            var box = new TextBox { Text = text ?? string.Empty, PlaceholderText = placeholder };
            box.LostFocus += (_, _) => committed((box.Text ?? string.Empty).Trim());
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Avalonia.Input.Key.Enter)
                    committed((box.Text ?? string.Empty).Trim());
            };
            return box;
        }

        Button Action(string label, Action action, bool primary = false, bool danger = false)
        {
            var button = new Button { Content = label };
            if (primary)
                button.Classes.Add("primary");
            if (danger)
                button.Classes.Add("danger");
            button.Click += (_, _) => action();
            return button;
        }

        static StackPanel Buttons(params Control[] buttons)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            foreach (Control button in buttons)
                row.Children.Add(button);
            return row;
        }

        // ---------------------------------------------------------------
        // Général
        // ---------------------------------------------------------------

        /// <summary>Navigateur par défaut : état, et bouton pour que les liens des autres applications s'ouvrent ici.</summary>
        Control DefaultBrowserCard()
        {
            var status = Hint(Tr("Vérification…"));
            Button make = Action(Tr("Définir comme navigateur par défaut"), () => { }, primary: true);
            make.IsVisible = false;

            async void Refresh()
            {
                bool? isDefault = await DefaultBrowser.IsDefaultAsync();
                status.Text = isDefault == true
                    ? Tr("PommeBrowser est votre navigateur par défaut : les liens des autres applications s'ouvrent ici.")
                    : Tr("Les liens des autres applications (courriels, documents, messageries) s'ouvrent dans un autre navigateur.");
                make.IsVisible = isDefault != true;
            }

            make.Click += async (_, _) =>
            {
                make.IsEnabled = false;
                string? error = await DefaultBrowser.MakeDefaultAsync();
                make.IsEnabled = true;
                if (error != null)
                    _window.ShowToast(error, warning: true);
                else if (OperatingSystem.IsWindows())
                    status.Text = Tr("Dans la fenêtre de Windows qui s'est ouverte, choisissez PommeBrowser pour les liens (HTTP et HTTPS).");
                else
                    Refresh();
            };
            Refresh();
            return Card(Tr("Navigateur par défaut"), null, status, Buttons(make));
        }

        Control BuildGeneral()
        {
            var panel = new StackPanel { Spacing = 14 };
            panel.Children.Add(Hint(Tr("Démarrage, nouveaux onglets, réseau et certificats du navigateur.")));
            panel.Children.Add(DefaultBrowserCard());
            panel.Children.Add(Card(Tr("Venir d'un autre navigateur"),
                Tr("Reprenez les favoris, l'historique et les mots de passe de Chrome, Edge, Brave, Firefox…"),
                Buttons(
                    Action(Tr("Favoris et historique…"), () => _ = ImportFavoritesDialog.ShowAsync(_window)),
                    Action(Tr("Mots de passe…"), () => _ = ImportPasswordsDialog.ShowAsync(_window)))));

            // Apparence
            var restart = Action(Tr("Redémarrer maintenant"), () => _app.Restart());
            restart.IsVisible = false;
            panel.Children.Add(Card(Tr("Apparence"), null,
                Choice(Tr("Thème"), new[] { (AppTheme.System, Tr("Système")), (AppTheme.Dark, Tr("Sombre")), (AppTheme.Light, Tr("Clair")) }, _app.Appearance.Theme, theme =>
                {
                    _app.Appearance.Theme = theme;
                    _app.Appearance.Save();
                    _app.ApplyTheme();
                }),
                Choice(Tr("Langue"), new[] { ("fr", "Français"), ("en", "English") }, _app.Appearance.Language, language =>
                {
                    _app.Appearance.Language = language;
                    _app.Appearance.Save();
                    restart.IsVisible = language != Language;
                }),
                Hint(Tr("La langue est appliquée au prochain démarrage.")),
                restart));

            // Démarrage
            var custom = Entry(Settings.CustomStartupPage, "https://…", text =>
            {
                Settings.CustomStartupPage = text;
                Save();
            });
            RadioButton Mode(string label, BrowserSettings.StartupMode mode)
            {
                var radio = new RadioButton { Content = label, GroupName = "startup", IsChecked = Settings.Startup == mode };
                radio.IsCheckedChanged += (_, _) =>
                {
                    if (radio.IsChecked != true)
                        return;
                    Settings.Startup = mode;
                    custom.IsEnabled = mode == BrowserSettings.StartupMode.CustomPage;
                    Save();
                };
                return radio;
            }
            custom.IsEnabled = Settings.Startup == BrowserSettings.StartupMode.CustomPage;
            panel.Children.Add(Card(Tr("Démarrage du navigateur"), null,
                Mode(Tr("Ouvrir un onglet vide PommeBrowser"), BrowserSettings.StartupMode.EmptyTab),
                Mode(Tr("Ouvrir une page personnalisée"), BrowserSettings.StartupMode.CustomPage),
                custom,
                Mode(Tr("Restaurer les onglets de la session précédente"), BrowserSettings.StartupMode.RestoreSession)));

            // Recherche et nouveaux onglets
            var engines = Enum.GetValues<BrowserSettings.SearchEngine>().Select(e => (e, UrlResolver.GetSearchEngineName(e))).ToList();
            panel.Children.Add(Card(Tr("Recherche et nouveaux onglets"), Tr("Moteur utilisé par la barre d’adresse et la page d’accueil."),
                Choice(Tr("Moteur de recherche"), engines, Settings.Search, engine =>
                {
                    Settings.Search = engine;
                    Save();
                }),
                Hint(Tr("Page ouverte lors de la création d’un nouvel onglet classique (laisser vide pour la page d’accueil PommeBrowser).")),
                Entry(Settings.NewTabPage, Tr("Page d'accueil de PommeBrowser"), text =>
                {
                    Settings.NewTabPage = text;
                    Save();
                })));

            // Onglets
            var delays = new[] { 5, 10, 15, 30, 60, 120 }.Select(m => (m, Tr("{0} min", m))).ToList();
            panel.Children.Add(Card(Tr("Onglets"), Tr("Les onglets en veille libèrent la mémoire de leur page ; elle est rechargée quand vous y revenez."),
                Check(Tr("Mettre en veille les onglets inactifs"), Settings.EnableSuspension, value =>
                {
                    Settings.EnableSuspension = value;
                    Save();
                }),
                Choice(Tr("Après"), delays, Settings.SuspendDelayMinutes, minutes =>
                {
                    Settings.SuspendDelayMinutes = minutes;
                    Save();
                }),
                Check(Tr("Commandes dans la barre d'adresse (:new, :history, @onglet…)"), Settings.EnableCommands, value =>
                {
                    Settings.EnableCommands = value;
                    Save();
                })));

            // Services du homelab
            var intervals = new[] { 30, 60, 120, 300, 600 }.Select(s => (s, s < 60 ? Tr("{0} s", s) : Tr("{0} min", s / 60))).ToList();
            panel.Children.Add(Card(Tr("Services du homelab"), Tr("Les services s’ajoutent depuis la page d’accueil ou le menu « Ajouter la page aux services »."),
                Check(Tr("Vérifier régulièrement que les services répondent"), Settings.ServiceMonitoring, value =>
                {
                    Settings.ServiceMonitoring = value;
                    Save();
                }),
                Choice(Tr("Fréquence"), intervals, Settings.ServiceCheckIntervalSeconds, seconds =>
                {
                    Settings.ServiceCheckIntervalSeconds = seconds;
                    Save();
                }),
                Check(Tr("Me prévenir quand un service tombe ou revient"), Settings.ServiceAlerts, value =>
                {
                    Settings.ServiceAlerts = value;
                    Save();
                })));

            // Sécurité
            var tracking = new[]
            {
                (BrowserSettings.TrackingProtection.Off, Tr("Désactivée")),
                (BrowserSettings.TrackingProtection.Basic, Tr("Essentielle")),
                (BrowserSettings.TrackingProtection.Balanced, Tr("Équilibrée")),
                (BrowserSettings.TrackingProtection.Strict, Tr("Stricte"))
            };
            panel.Children.Add(Card(Tr("Sécurité de la navigation"), null,
                Check(Tr("Passer automatiquement les sites en HTTPS"), Settings.HttpsUpgrade, value =>
                {
                    Settings.HttpsUpgrade = value;
                    Save();
                }),
                Hint(Tr("Une adresse http:// est d’abord essayée en https://, et un avertissement s’affiche si le site ne propose pas HTTPS. Le réseau local et les ports explicites (…:8080) ne sont pas concernés.")),
                Choice(Tr("Protection contre le pistage"), tracking, Settings.TrackingPrevention, level =>
                {
                    Settings.TrackingPrevention = level;
                    Save();
                }),
                Hint(EngineHost.Kind == EngineKind.WebView2
                    ? Tr("« Stricte » bloque davantage de traqueurs, mais certains sites peuvent mal s’afficher.")
                    : Tr("« Essentielle » active la protection intelligente contre le pistage ; « Équilibrée » et « Stricte » bloquent en plus les cookies tiers.")),
                Buttons(Action(Tr("Autorisations des sites…"), () => _ = SitePermissionsDialog.ShowAsync(_window)))));

            // Coffre des mots de passe
            var lockDelays = new[] { 5, 15, 30, 60 }.Select(m => (m, Tr("{0} min", m))).Append((0, Tr("Jamais"))).ToList();
            panel.Children.Add(Card(Tr("Coffre des mots de passe"), Tr("Le coffre se verrouille tout seul quand il n’a pas servi depuis ce délai ; son mot de passe est alors redemandé."),
                Choice(Tr("Verrouiller automatiquement après"), lockDelays, Settings.VaultAutoLockMinutes, minutes =>
                {
                    Settings.VaultAutoLockMinutes = minutes;
                    Save();
                })));

            // DNS sécurisé (WebView2 seulement)
            if (EngineHost.Kind == EngineKind.WebView2)
                panel.Children.Add(BuildSecureDns());

            // Certificats
            panel.Children.Add(Card(Tr("Certificats"),
                Tr("Ajoutez des autorités propres au profil PommeBrowser (autorité de votre homelab), sans modifier la confiance du système, et gérez les certificats que vous avez approuvés."),
                Buttons(
                    Action(Tr("Autorités du profil…"), () => _ = CertificatesDialog.ShowAuthoritiesAsync(_window)),
                    Action(Tr("Certificats acceptés pour les services locaux…"), () => _ = CertificatesDialog.ShowPinnedAsync(_window)))));
            return panel;
        }

        Control BuildSecureDns()
        {
            var modes = new[]
            {
                (BrowserSettings.SecureDnsMode.System, Tr("Système")),
                (BrowserSettings.SecureDnsMode.Automatic, Tr("Automatique")),
                (BrowserSettings.SecureDnsMode.Secure, Tr("Sécurisé uniquement"))
            };
            var providers = Enum.GetValues<BrowserSettings.SecureDnsProvider>().Select(p => (p, p == BrowserSettings.SecureDnsProvider.Custom ? Tr("Personnalisé") : p.ToString())).ToList();
            return Card(Tr("DNS sécurisé"), Tr("Choisissez la résolution DNS utilisée par les onglets. Le changement sera appliqué après le redémarrage complet de PommeBrowser."),
                Choice(Tr("Mode"), modes, Settings.DnsMode, mode =>
                {
                    Settings.DnsMode = mode;
                    Save();
                }),
                Choice(Tr("Fournisseur"), providers, Settings.DnsProvider, provider =>
                {
                    Settings.DnsProvider = provider;
                    Save();
                }),
                Entry(Settings.SecureDnsCustomTemplate, "https://dns.exemple.net/dns-query{?dns}", text =>
                {
                    Settings.SecureDnsCustomTemplate = text;
                    Save();
                }));
        }

        // ---------------------------------------------------------------
        // Téléchargements, historique, mises à jour
        // ---------------------------------------------------------------

        Control BuildDownloads()
        {
            var path = new TextBlock { Text = _app.DownloadDirectory, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
            return Card(Tr("Dossier de téléchargement"), Tr("Les fichiers téléchargés seront enregistrés automatiquement dans ce dossier."),
                path,
                Buttons(
                    Action(Tr("Parcourir…"), async () =>
                    {
                        IReadOnlyList<IStorageFolder> folders = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                        {
                            Title = Tr("Choisir le dossier de téléchargement"),
                            AllowMultiple = false
                        });
                        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } chosen)
                        {
                            Settings.DownloadFolder = chosen;
                            Save();
                            path.Text = _app.DownloadDirectory;
                        }
                    }),
                    Action(Tr("Dossier du système"), () =>
                    {
                        Settings.DownloadFolder = string.Empty;
                        Save();
                        path.Text = _app.DownloadDirectory;
                    }),
                    Action(Tr("Ouvrir le dossier"), () => _ = _window.OpenFolderAsync(_app.DownloadDirectory))));
        }

        Control BuildHistory()
            => Card(Tr("Historique de navigation"), Tr("Accéder à la liste complète des pages visitées, ou effacer les données de navigation (cookies, cache, historique)."),
                Buttons(
                    Action(Tr("Ouvrir l'historique"), _window.OpenHistory),
                    Action(Tr("Effacer les données de navigation…"), () => _ = ClearDataDialog.ShowAsync(_window), danger: true)));

        Control BuildUpdates() => new UpdatesPanel(_window);

        // ---------------------------------------------------------------
        // Bloqueur de publicités
        // ---------------------------------------------------------------

        Control BuildAdBlock()
        {
            AdBlockService blocker = _app.AdBlock;
            AdBlockSettings settings = blocker.Settings;
            var panel = new StackPanel { Spacing = 14 };
            panel.Children.Add(Hint(Tr("Protection intégrée au navigateur. Les requêtes publicitaires et de suivi sont bloquées avant leur chargement, puis les emplacements résiduels sont masqués dans la page.")));

            if (!AdBlockService.IsSupported)
                panel.Children.Add(Hint(Tr("Le moteur web de ce système ne permet pas encore d'appliquer les listes.")));

            panel.Children.Add(Card(Tr("Protection globale"), blocker.Status,
                Check(Tr("Activée"), settings.Enabled, value => blocker.UpdateSettings(s => s.Enabled = value, rebuild: false))));

            var lists = new List<Control>();
            foreach (AdBlockSubscription subscription in settings.Subscriptions)
            {
                string id = subscription.Id;
                lists.Add(Check(subscription.IsPrivacyList ? Tr("Bloquer les traqueurs avec {0}", subscription.Name) : Tr("Bloquer les publicités avec {0}", subscription.Name),
                    subscription.Enabled, value => blocker.UpdateSettings(s =>
                    {
                        if (s.Subscriptions.FirstOrDefault(x => x.Id == id) is { } target)
                            target.Enabled = value;
                    }, rebuild: true)));
            }
            lists.Add(Check(Tr("Masquer les emplacements publicitaires restants"), settings.CosmeticFiltering,
                value => blocker.UpdateSettings(s => s.CosmeticFiltering = value, rebuild: true)));
            lists.Add(Check(Tr("Ne pas filtrer les adresses locales et privées"), settings.BypassPrivateNetworks,
                value => blocker.UpdateSettings(s => s.BypassPrivateNetworks = value, rebuild: false)));
            lists.Add(Hint(Tr("Évite de casser les services du homelab : localhost, domaines .lan/.local et réseaux privés.")));
            panel.Children.Add(Card(Tr("Protection"), null, lists.ToArray()));

            panel.Children.Add(Card(Tr("Listes de filtres"),
                settings.LastSuccessfulUpdateUtc is { } updated ? Tr("Dernière mise à jour : {0}", updated.ToLocalTime().ToString("g", Culture)) : Tr("Listes jamais mises à jour."),
                Check(Tr("Mettre à jour automatiquement les listes"), settings.AutoUpdate, value => blocker.UpdateSettings(s => s.AutoUpdate = value, rebuild: false)),
                Buttons(Action(Tr("Mettre à jour maintenant"), async () =>
                {
                    var result = await blocker.UpdateListsAsync(force: true);
                    _window.ShowToast(result.Message, warning: !result.Success);
                }))));

            var allowed = new StackPanel { Spacing = 4 };
            foreach (string domain in settings.AllowlistedDomains.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            {
                var row = new DockPanel();
                string target = domain;
                var remove = new Button { Content = Tr("Retirer") };
                remove.Classes.Add("link");
                remove.Click += (_, _) => blocker.SetSiteAllowed(target, false);
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                row.Children.Add(new TextBlock { Text = domain, VerticalAlignment = VerticalAlignment.Center });
                allowed.Children.Add(row);
            }
            if (settings.AllowlistedDomains.Count == 0)
                allowed.Children.Add(Hint(Tr("Aucun site autorisé.")));
            var newDomain = new TextBox { PlaceholderText = "exemple.com", MinWidth = 260 };
            var add = Action(Tr("Ajouter"), () =>
            {
                string domain = AdBlockDomain.NormalizeHost(newDomain.Text);
                if (domain.Length > 0)
                    blocker.SetSiteAllowed(domain, true);
            });
            panel.Children.Add(Card(Tr("Sites autorisés"), Tr("La protection est désactivée uniquement sur les domaines ci-dessous. Les sous-domaines sont également autorisés."),
                allowed, Buttons(newDomain, add)));
            return panel;
        }

        // ---------------------------------------------------------------
        // Avancé : Flash
        // ---------------------------------------------------------------

        Control BuildAdvanced()
        {
            var panel = new StackPanel { Spacing = 14 };
            panel.Children.Add(Hint(Tr("Options techniques réservées aux utilisateurs avancés.")));

            panel.Children.Add(Card(Tr("Compatibilité Flash"),
                RuffleContent.InstalledVersion is { } version
                    ? Tr("Ruffle {0} est intégré : les contenus Flash sont lus directement dans la page.", version)
                    : Tr("Ruffle n'est pas présent dans cette compilation."),
                Check(Tr("Activer le moteur Flash intégré (Ruffle)"), Settings.EnableFlashSupport, value =>
                {
                    Settings.EnableFlashSupport = value;
                    Save();
                }),
                Check(Tr("Ouvrir dans Basilisk les contenus que Ruffle ne sait pas lire"), Settings.FlashAutoFallback, value =>
                {
                    Settings.FlashAutoFallback = value;
                    Save();
                }),
                IntegratedEngineOption(),
                Check(Tr("Mode debug Flash (journal détaillé)"), Settings.FlashDebugEnabled, value =>
                {
                    Settings.FlashDebugEnabled = value;
                    Save();
                })));

            var status = Hint(BasiliskStatus());
            var module = Hint(FlashModuleStatus());
            var path = new TextBlock { Text = string.IsNullOrWhiteSpace(Settings.BasiliskPath) ? Tr("Recherche automatique") : Settings.BasiliskPath, TextWrapping = TextWrapping.Wrap };
            Button search = null!;
            Button remove32 = null!;
            // Module installé : statut à jour, et le bouton de recherche n'est plus mis en avant.
            void Installed()
            {
                module.Text = FlashModuleStatus();
                remove32.IsVisible = LegacyEngine.InstalledModule32 != null;
                if (LegacyEngine.InstalledModule != null || LegacyEngine.InstalledModule32 != null)
                    search.Classes.Remove("primary");
                if (_integratedModules != null)
                    _integratedModules.Text = IntegratedModulesStatus();
            }
            remove32 = Action(Tr("Retirer le module 32 bits"), async () =>
            {
                if (LegacyEngine.RemoveModule32() is { } error)
                {
                    await Dialogs.Dialogs.AlertAsync(_window, Tr("Module Flash"), error);
                    return;
                }
                Installed();
            });
            remove32.IsVisible = LegacyEngine.InstalledModule32 != null;
            search = Action(Tr("Rechercher le module Flash"), async () => await SearchFlashModuleAsync(search, Installed), primary: LegacyEngine.InstalledModule == null);
            panel.Children.Add(Card(Tr("Moteur de secours Legacy (Basilisk)"),
                LegacyView.IsSupported
                    ? Tr("Basilisk lit dans l'onglet, avec le lecteur Flash d'origine, les contenus que Ruffle ne sait pas lire.")
                    : Tr("Basilisk lit les contenus Flash que Ruffle ne sait pas lire, avec le lecteur d'origine, dans sa propre fenêtre."),
                path, status,
                Buttons(
                    Action(Tr("Parcourir…"), async () =>
                    {
                        IReadOnlyList<IStorageFile> files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                        {
                            Title = Tr("Choisir Basilisk"),
                            AllowMultiple = false
                        });
                        if (files.Count > 0 && files[0].TryGetLocalPath() is { } chosen)
                        {
                            Settings.BasiliskPath = chosen;
                            Save();
                            path.Text = chosen;
                            status.Text = BasiliskStatus();
                        }
                    }),
                    Action(Tr("Recherche automatique"), () =>
                    {
                        Settings.BasiliskPath = string.Empty;
                        Save();
                        path.Text = Tr("Recherche automatique");
                        status.Text = BasiliskStatus();
                    })),
                module,
                Hint(Tr("Adobe ne distribue plus Flash Player : PommeBrowser ne peut pas le fournir. « Rechercher le module Flash » trouve votre copie sur l'ordinateur, par exemple dans un Basilisk portable ; sinon, choisissez le fichier ({0}). Les dernières versions bloquent les contenus depuis le 12 janvier 2021 : prenez une version plus ancienne.", LegacyEngine.ExpectedModuleName)),
                OperatingSystem.IsWindows()
                    ? Hint(Tr("Le moteur intégré lit les modules 32 et 64 bits et choisit seul : « Rechercher le module Flash » installe le meilleur de chaque, et le Flash Player installé dans Windows sert aussi tel quel. Si un module ne lit pas un contenu, l'autre prend le relais. Basilisk n'utilise que le module 64 bits."))
                    : OperatingSystem.IsLinux()
                        ? Hint(Tr("Sous Linux, le moteur intégré utilise libflashplayer.so (64 bits) : la copie installée ici, sinon celle d'un dossier de modules du système (/usr/lib/mozilla/plugins…). Il lui faut GTK 2 (paquet libgtk2.0-0) et X11 ou XWayland."))
                        : new Panel { IsVisible = false },
                Buttons(
                    search,
                    remove32,
                    Action(Tr("Choisir le module Flash…"), async () =>
                    {
                        IReadOnlyList<IStorageFile> files = await _window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                        {
                            Title = Tr("Choisir le module Flash"),
                            AllowMultiple = false
                        });
                        if (files.Count > 0 && files[0].TryGetLocalPath() is { } chosen)
                            await InstallFlashModuleAsync(chosen, Installed);
                    }))));

            // Sites ouverts d'office dans Basilisk.
            var rules = new StackPanel { Spacing = 4 };
            IReadOnlyDictionary<string, FlashRuleMode> all = FlashDomainRules.GetAll();
            foreach ((string domain, FlashRuleMode mode) in all.Where(r => r.Value == FlashRuleMode.Legacy).OrderBy(r => r.Key))
            {
                var row = new DockPanel();
                string target = domain;
                var remove = new Button { Content = Tr("Supprimer") };
                remove.Classes.Add("link");
                remove.Click += (_, _) =>
                {
                    if (Uri.TryCreate("https://" + target + "/", UriKind.Absolute, out Uri? uri))
                        FlashDomainRules.RemoveRule(uri);
                    ScrollTo("flash");
                };
                DockPanel.SetDock(remove, Dock.Right);
                row.Children.Add(remove);
                row.Children.Add(new TextBlock { Text = domain, VerticalAlignment = VerticalAlignment.Center });
                rules.Children.Add(row);
            }
            if (rules.Children.Count == 0)
                rules.Children.Add(Hint(Tr("Aucune exception.")));
            var newDomain = new TextBox { PlaceholderText = "jeu.exemple.com", MinWidth = 260 };
            panel.Children.Add(Card(Tr("Exceptions forcées en mode Legacy"),
                Tr("Ces sites s'ouvrent directement dans Basilisk. Sans règle, les contenus Flash sont lus avec Ruffle."),
                rules,
                Buttons(newDomain, Action(Tr("Ajouter"), async () =>
                {
                    string text = (newDomain.Text ?? string.Empty).Trim();
                    if (!Uri.TryCreate(text.Contains("://") ? text : "https://" + text, UriKind.Absolute, out Uri? uri) || uri.Host.Length == 0)
                    {
                        await Dialogs.Dialogs.AlertAsync(_window, Tr("Erreur"), Tr("Domaine invalide.\nExemple : jeu.exemple.com"));
                        return;
                    }
                    FlashDomainRules.SetRule(uri, FlashRuleMode.Legacy);
                    ScrollTo("flash");
                }),
                Action(Tr("Réinitialiser l’apprentissage"), async () =>
                {
                    if (!await Dialogs.Dialogs.ConfirmAsync(_window, Tr("Compatibilité Flash"), Tr("Effacer les choix Ruffle/Legacy appris pour le profil actuel ?"), Tr("Effacer")))
                        return;
                    FlashCompatibilityMemory.ClearForCurrentProfile();
                    _window.ShowToast(Tr("La compatibilité apprise a été réinitialisée."));
                }))));
            return panel;
        }

        /// <summary>Moteur Flash intégré (Windows, Linux sous X11) : réglage expérimental, ou raison de son absence.</summary>
        Control IntegratedEngineOption()
        {
            if (!OperatingSystem.IsWindows() && !(OperatingSystem.IsLinux() && LegacyView.IsSupported))
                return new Panel { IsVisible = false };

            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(Check(Tr("Moteur Flash intégré (expérimental) : lire ces contenus avec votre module Flash, sans Basilisk"), Settings.FlashIntegratedEngine, value =>
            {
                Settings.FlashIntegratedEngine = value;
                Save();
            }));
            _integratedModules = Hint(IntegratedModulesStatus());
            panel.Children.Add(_integratedModules);
            return panel;
        }

        /// <summary>Modules que le moteur intégré essaiera, dans l'ordre, ou ce qui l'empêche de fonctionner.</summary>
        static string IntegratedModulesStatus()
        {
            if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
                return string.Empty;
            if (!FlashHostProcess.IsAvailable)
                return Tr("Le moteur intégré n'est pas présent dans cette compilation.");
            IReadOnlyList<string> modules = LegacyEngine.IntegratedModules;
            if (modules.Count == 0)
                return Tr("Il utilise votre module Flash : ajoutez-le ci-dessous.");
            List<string> usable = modules.Where(FlashHostProcess.IsAvailableFor).ToList();
            if (usable.Count == 0)
            {
                return OperatingSystem.IsWindows()
                    ? Tr("Le moteur intégré 32 bits n'est pas présent dans cette compilation : ajoutez un module Flash 64 bits.")
                    : Tr("Le moteur intégré n'est pas présent dans cette compilation.");
            }
            return usable.Count == 1
                ? Tr("Module utilisé : {0}", DescribeIntegratedModule(usable[0]))
                : Tr("Modules utilisés : {0}, puis {1} si le premier ne lit pas un contenu.", DescribeIntegratedModule(usable[0]), DescribeIntegratedModule(usable[1]));
        }

        static string DescribeIntegratedModule(string path)
        {
            int bits = FlashModuleSearch.Is32Bit(path) ? 32 : 64;
            bool installed = path.StartsWith(LegacyEngine.PluginDirectory, StringComparison.OrdinalIgnoreCase);
            if (installed)
                return Tr("{0} ({1} bits)", System.IO.Path.GetFileName(path), bits);
            return OperatingSystem.IsWindows()
                ? Tr("{0} ({1} bits, Flash Player de Windows)", System.IO.Path.GetFileName(path), bits)
                : Tr("{0} (module du système, {1})", System.IO.Path.GetFileName(path), System.IO.Path.GetDirectoryName(path));
        }

        string BasiliskStatus()
        {
            if (_app.BasiliskExecutable is not { } executable)
                return OperatingSystem.IsMacOS()
                    ? Tr("Basilisk n'est pas disponible sur macOS : les contenus Flash passent uniquement par Ruffle.")
                    : Tr("Aucun Basilisk trouvé : les contenus Flash passent uniquement par Ruffle.");

            if (executable == LegacyEngine.BundledExecutable)
                return Tr("Pomme Legacy {0}, livré avec PommeBrowser.", LegacyEngine.BundledVersion ?? string.Empty).Replace(" ,", ",");
            (string? name, string? version) = OperatingSystem.IsLinux()
                ? BasiliskInstall.Describe(executable)
                : OperatingSystem.IsWindows() ? MyHomelabBrowser.BasiliskExecutable.Describe(executable) : (null, null);
            return name != null ? Tr("{0} {1} détecté : {2}", name, version ?? string.Empty, executable) : Tr("Basilisk détecté : {0}", executable);
        }

        /// <summary>Recherche du module Flash sur l'ordinateur, puis installation du meilleur de chaque architecture.</summary>
        async Task SearchFlashModuleAsync(Button button, Action installed)
        {
            object? label = button.Content;
            button.IsEnabled = false;
            button.Content = Tr("Recherche…");
            IReadOnlyList<FlashModuleSearch.Module> found;
            try
            {
                found = await Task.Run(() => LegacyEngine.FindModules());
            }
            finally
            {
                button.Content = label;
                button.IsEnabled = true;
            }
            await OfferFlashModulesAsync(found, installed, folder: null);
        }

        /// <summary>Recherche dans un dossier choisi (Basilisk ou Pale Moon portable…), sous-dossiers compris.</summary>
        async Task SearchFlashFolderAsync(Action installed)
        {
            IReadOnlyList<IStorageFolder> folders = await _window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = Tr("Dossier du module Flash"),
                AllowMultiple = false
            });
            if (folders.Count == 0 || folders[0].TryGetLocalPath() is not { } folder)
                return;
            IReadOnlyList<FlashModuleSearch.Module> found = await Task.Run(() => LegacyEngine.FindModules(folder));
            await OfferFlashModulesAsync(found, installed, folder);
        }

        /// <summary>
        /// Modules trouvés : le meilleur de chaque architecture (64 bits, et 32 bits sous Windows)
        /// est installé d'office ; l'utilisateur peut en choisir un autre. Rien trouvé : un dossier où chercher.
        /// </summary>
        async Task OfferFlashModulesAsync(IReadOnlyList<FlashModuleSearch.Module> found, Action installed, string? folder)
        {
            if (found.Count == 0)
            {
                int choice = await Dialogs.Dialogs.ChoiceAsync(_window, Tr("Module Flash introuvable"),
                    folder == null
                        ? Tr("Aucun module Flash ({0}) n'a été trouvé dans les dossiers habituels. Indiquez le dossier où il se trouve, par exemple celui d'un Basilisk ou d'un Pale Moon portable : il y sera cherché, sous-dossiers compris.", LegacyEngine.ExpectedModuleName)
                        : Tr("Aucun module Flash ({0}) dans {1}, sous-dossiers compris.", LegacyEngine.ExpectedModuleName, folder),
                    (Tr("Annuler"), false, false), (Tr("Choisir un dossier…"), true, false));
                if (choice == 1)
                    await SearchFlashFolderAsync(installed);
                return;
            }

            (IReadOnlyList<FlashModuleSearch.Module> added, IReadOnlyList<string> errors) = LegacyEngine.InstallBest(found);
            installed();
            if (errors.Count > 0)
                await Dialogs.Dialogs.AlertAsync(_window, Tr("Module Flash"), string.Join("\n", errors));
            string names = string.Join(", ", added.Select(m => Tr("{0} ({1} bits)", System.IO.Path.GetFileName(m.Path), m.Is32Bit ? 32 : 64)));
            if (added.Count > 0 || errors.Count == 0)
            {
                _window.ShowToast(added.Count switch
                {
                    0 => Tr("Les modules Flash installés restent les meilleurs trouvés."),
                    1 => Tr("Module Flash installé : {0}.", names),
                    _ => Tr("Modules Flash installés : {0}.", names)
                }, Tr("Voir les modules trouvés"), () => _ = ChooseFlashModuleAsync(found, installed), timeout: 10);
            }
        }

        /// <summary>Liste des modules trouvés : l'utilisateur choisit celui à installer, ou un dossier où chercher.</summary>
        async Task ChooseFlashModuleAsync(IReadOnlyList<FlashModuleSearch.Module> found, Action installed)
        {
            string? chosen = null;
            bool browse = false;
            var dialog = new ListDialog(Tr("Module Flash trouvé"),
                Tr("Choisissez le module à installer. Il est copié dans les données de PommeBrowser : son dossier d'origine n'est plus nécessaire ensuite. Un module 32 bits ne sert qu'au moteur intégré."),
                string.Empty,
                list => found.Select(m => list.Row(System.IO.Path.GetFileName(m.Path), FlashModuleDetail(m), Tr("Installer"), () =>
                {
                    chosen = m.Path;
                    list.Close();
                })));
            dialog.AddExtraButton(Tr("Chercher dans un dossier…"), false, () =>
            {
                browse = true;
                dialog.Close();
            });
            await dialog.ShowDialog(_window);
            if (browse)
                await SearchFlashFolderAsync(installed);
            else if (chosen != null)
                await InstallFlashModuleAsync(chosen, installed);
        }

        async Task InstallFlashModuleAsync(string path, Action installed)
        {
            if (LegacyEngine.InstallModule(path) is { } error)
            {
                await Dialogs.Dialogs.AlertAsync(_window, Tr("Module Flash"), error);
                return;
            }
            installed();
            _window.ShowToast(OperatingSystem.IsWindows() && FlashModuleSearch.Is32Bit(path)
                ? Tr("Module Flash 32 bits installé : le moteur intégré l'utilisera.")
                : Tr("Module Flash installé : il sera utilisé à la prochaine ouverture dans Basilisk."));
        }

        /// <summary>Version et dossier du module ; avertissement pour les versions qui bloquent les contenus.</summary>
        static string FlashModuleDetail(FlashModuleSearch.Module module)
        {
            string folder = System.IO.Path.GetDirectoryName(module.Path) ?? module.Path;
            string detail = module.Version is not { } version
                ? folder
                : FlashModuleSearch.MayBlockContent(module)
                    ? Tr("Version {0}, dans {1}. Cette version peut refuser les contenus Flash (blocage du 12 janvier 2021), sauf si elle a été modifiée pour l'éviter.", version, folder)
                    : Tr("Version {0}, dans {1}", version, folder);
            return module.Is32Bit ? Tr("32 bits, pour le moteur intégré seulement. {0}", detail) : detail;
        }

        static string FlashModuleStatus()
        {
            string status = LegacyEngine.InstalledModule is { } module
                ? Tr("Module Flash : {0}", System.IO.Path.GetFileName(module))
                : Tr("Module Flash absent : Basilisk ne pourra pas lire les contenus Flash.");
            return LegacyEngine.InstalledModule32 is { } module32
                ? status + "\n" + Tr("Module 32 bits (moteur intégré) : {0}", System.IO.Path.GetFileName(module32))
                : status;
        }
    }
}
