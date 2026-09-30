using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using MyHomelabBrowser.classes.Profiles.Credentials;
using MyHomelabBrowser.classes.Security;
using PommeBrowser.Core;
using PommeBrowser.Views.Dialogs;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Pages
{
    /// <summary>
    /// Mots de passe du coffre : recherche, affichage, copie (effacée du presse-papiers après 30 s),
    /// codes de double authentification en direct, suppression.
    /// </summary>
    public sealed class PasswordsPage : PageBase
    {
        const string Hidden = "••••••••••";

        readonly List<(TextBlock Label, TotpParameters Parameters)> _codes = new();
        readonly HashSet<string> _expanded = new(StringComparer.OrdinalIgnoreCase);
        readonly DispatcherTimer _timer;
        string _query = string.Empty;

        public PasswordsPage(MainWindow window) : base(window, Tr("Mots de passe"))
        {
            App.Vault.Changed += ScheduleRefresh;
            // Codes 2FA : mis à jour chaque seconde tant que la page est affichée.
            _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => UpdateCodes());
            _timer.Start();
        }

        CredentialVaultService Service => App.Vault.Service;

        protected override void OnDisposed()
        {
            App.Vault.Changed -= ScheduleRefresh;
            _timer.Stop();
        }

        protected override void Build(StackPanel content)
        {
            _codes.Clear();

            if (!Service.VaultExists)
            {
                content.Children.Add(EmptyState("IconVault", Tr("Aucun coffre"),
                    Tr("Le coffre enregistre vos mots de passe et vos codes de double authentification, chiffrés avec un mot de passe que vous choisissez.")));
                content.Children.Add(Centered(TextButton(Tr("Créer le coffre…"), () => _ = UnlockAsync(), primary: true)));
                return;
            }

            if (!App.Vault.IsUnlocked)
            {
                content.Children.Add(EmptyState("IconLock", Tr("Coffre verrouillé"),
                    Tr("Déverrouillez le coffre pour voir et utiliser vos identifiants.")));
                content.Children.Add(Centered(TextButton(Tr("Déverrouiller…"), () => _ = UnlockAsync(), primary: true)));
                return;
            }

            var search = new TextBox { PlaceholderText = Tr("Rechercher un site ou un identifiant"), Text = _query };
            search.TextChanged += (_, _) =>
            {
                _query = search.Text ?? string.Empty;
                ScheduleRefresh();
            };
            var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(TextButton(Tr("Changer le mot de passe…"), async () =>
            {
                if (await App.Vault.ChangePasswordAsync(Window) is { } message)
                    Window.ShowToast(message);
            }));
            buttons.Children.Add(TextButton(Tr("Verrouiller"), App.Vault.Lock));
            DockPanel.SetDock(buttons, Dock.Right);
            bar.Children.Add(buttons);
            bar.Children.Add(search);
            content.Children.Add(bar);

            string[] tokens = _query.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            List<CredentialEntry> entries = Service.GetAll()
                .Where(e => tokens.All((e.DisplayHost + " " + e.Username).ToLowerInvariant().Contains))
                .OrderBy(e => e.DisplayHost, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(e => e.Username, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            if (entries.Count == 0)
            {
                content.Children.Add(EmptyState("IconKey",
                    tokens.Length > 0 ? Tr("Aucun résultat") : Tr("Aucun identifiant enregistré"),
                    tokens.Length > 0
                        ? Tr("Aucun identifiant ne correspond à cette recherche.")
                        : Tr("Connectez-vous à un site : PommeBrowser vous proposera d'enregistrer le mot de passe.")));
                return;
            }

            content.Children.Add(Heading(entries.Count == 1 ? Tr("1 identifiant") : Tr("{0} identifiants", entries.Count)));
            foreach (CredentialEntry entry in entries)
                content.Children.Add(EntryCard(entry));
            UpdateCodes();
        }

        static Control Centered(Control control)
        {
            control.HorizontalAlignment = HorizontalAlignment.Center;
            control.Margin = new Thickness(0, 12, 0, 0);
            return control;
        }

        async Task UnlockAsync()
        {
            if (await App.Vault.EnsureUnlockedAsync(Window))
                ScheduleRefresh();
        }

        Control EntryCard(CredentialEntry entry)
        {
            string key = entry.Host + "\n" + entry.Username;

            var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
            Control icon = SiteIcon(entry.Host + "/");
            icon.Margin = new Thickness(0, 0, 12, 0);
            icon.VerticalAlignment = VerticalAlignment.Center;
            header.Children.Add(icon);
            var names = new StackPanel();
            names.Children.Add(new TextBlock { Text = entry.DisplayHost, FontSize = 13.5, TextTrimming = TextTrimming.CharacterEllipsis });
            names.Children.Add(Hint(entry.Username.Length > 0 ? entry.Username : Tr("(sans identifiant)")));
            Grid.SetColumn(names, 1);
            header.Children.Add(names);
            if (entry.HasTotp)
            {
                var badge = new Border { Padding = new Thickness(6, 1), CornerRadius = new CornerRadius(6), VerticalAlignment = VerticalAlignment.Center };
                badge.Bind(Border.BackgroundProperty, this.GetResourceObservable("AccentSoftBrush"));
                badge.Child = new TextBlock { Text = "2FA", FontSize = 11, FontWeight = FontWeight.SemiBold };
                ToolTip.SetTip(badge, Tr("Code de double authentification enregistré"));
                Grid.SetColumn(badge, 2);
                header.Children.Add(badge);
            }

            // Mot de passe, masqué par défaut.
            var password = new SelectableTextBlock { Text = Hidden, VerticalAlignment = VerticalAlignment.Center };
            bool shown = false;
            Button reveal = IconButton("IconEye", Tr("Afficher le mot de passe"), () => { });
            reveal.Click += (_, _) =>
            {
                shown = !shown;
                password.Text = shown ? entry.Password : Hidden;
                ToolTip.SetTip(reveal, shown ? Tr("Masquer le mot de passe") : Tr("Afficher le mot de passe"));
            };

            // Code de double authentification.
            Control codeRow;
            if (entry.HasTotp && Totp.TryParse(entry.TotpSecret, out TotpParameters parameters, out _))
            {
                var label = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("monospace") };
                _codes.Add((label, parameters));
                codeRow = Field(Tr("Code 2FA"), label,
                    CopyButton(Tr("Copier le code"), () => Totp.Generate(parameters, DateTimeOffset.UtcNow), secret: true),
                    IconButton("IconEdit", Tr("Modifier la clé 2FA"), () => _ = EditTotpAsync(entry)));
            }
            else
            {
                codeRow = Field(Tr("Code 2FA"), Hint(Tr("Aucune clé enregistrée")), TextButton(Tr("Ajouter…"), () => _ = EditTotpAsync(entry)));
            }

            var details = new StackPanel { Spacing = 6, Margin = new Thickness(0, 8, 0, 0) };
            details.Children.Add(Field(Tr("Identifiant"), new SelectableTextBlock { Text = entry.Username, VerticalAlignment = VerticalAlignment.Center },
                CopyButton(Tr("Copier l'identifiant"), () => entry.Username, secret: false)));
            details.Children.Add(Field(Tr("Mot de passe"), password, reveal, CopyButton(Tr("Copier le mot de passe"), () => entry.Password, secret: true)));
            details.Children.Add(codeRow);
            details.Children.Add(Field(Tr("Site"), new TextBlock { Text = entry.Host, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center },
                TextButton(Tr("Ouvrir le site"), () => Window.NewTab(entry.Host + "/", select: true)),
                TextButton(Tr("Supprimer"), () => _ = DeleteAsync(entry), danger: true)));

            var expander = new Expander { Header = header, Content = details, HorizontalAlignment = HorizontalAlignment.Stretch, IsExpanded = _expanded.Contains(key) };
            // Fiche dépliée gardée ouverte quand la page se met à jour (clé 2FA ajoutée…).
            expander.Expanded += (_, _) => _expanded.Add(key);
            expander.Collapsed += (_, _) => _expanded.Remove(key);
            return expander;
        }

        Control Field(string label, Control value, params Control[] actions)
        {
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("140,*,Auto") };
            var title = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            title.Classes.Add("subtitle");
            grid.Children.Add(title);
            Grid.SetColumn(value, 1);
            grid.Children.Add(value);
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (Control action in actions)
                row.Children.Add(action);
            Grid.SetColumn(row, 2);
            grid.Children.Add(row);
            return grid;
        }

        Button CopyButton(string tip, Func<string> value, bool secret)
            => IconButton("IconCopy", tip, async () =>
            {
                if (!await SecureClipboard.CopyAsync(Window, value(), secret))
                    return;
                Window.ShowToast(secret ? Tr("Copié : effacé du presse-papiers dans 30 secondes.") : Tr("Copié dans le presse-papiers."));
            });

        void UpdateCodes()
        {
            DateTimeOffset now = DateTimeOffset.UtcNow;
            foreach ((TextBlock label, TotpParameters parameters) in _codes)
            {
                string code = Totp.FormatForDisplay(Totp.Generate(parameters, now));
                label.Text = Tr("{0} — encore {1} s", code, Totp.SecondsRemaining(parameters, now));
            }
        }

        async Task DeleteAsync(CredentialEntry entry)
        {
            if (!await Dialogs.Dialogs.ConfirmAsync(Window, Tr("Supprimer cet identifiant ?"),
                    Tr("{0} sur {1} sera retiré du coffre.", entry.Username, entry.DisplayHost), Tr("Supprimer"), destructive: true))
                return;
            Service.Delete(entry.Host, entry.Username);
            App.Vault.NotifyChanged();
            Window.ShowToast(Tr("Identifiant supprimé du coffre."));
        }

        async Task EditTotpAsync(CredentialEntry entry)
        {
            var form = new FormDialog(Tr("Clé de double authentification"), Tr("Enregistrer"));
            form.AddText(Tr("Collez la clé fournie par {0} lors de l'activation de la double authentification : le texte en base32, ou l'adresse otpauth:// du QR code.", entry.DisplayHost));
            TextBox key = form.AddEntry(Tr("Clé"), password: true);
            if (entry.HasTotp)
            {
                form.AddExtraButton(Tr("Retirer la clé"), destructive: true, () =>
                {
                    Service.SetTotpSecret(entry.Host, entry.Username, null);
                    form.Close();
                    App.Vault.NotifyChanged();
                    Window.ShowToast(Tr("Code de double authentification retiré."));
                });
            }

            form.Submit = () =>
            {
                string secret = (key.Text ?? string.Empty).Trim();
                if (!Totp.TryParse(secret, out _, out string error))
                    return Task.FromResult<string?>(error);
                try
                {
                    Service.SetTotpSecret(entry.Host, entry.Username, secret);
                }
                catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or System.IO.IOException or UnauthorizedAccessException)
                {
                    return Task.FromResult<string?>(ex.Message);
                }
                App.Vault.NotifyChanged();
                Window.ShowToast(Tr("Code de double authentification enregistré."));
                return Task.FromResult<string?>(null);
            };
            await form.ShowAsync(Window);
        }
    }
}
