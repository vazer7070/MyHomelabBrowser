using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Support;
using MyHomelabBrowser.classes.Support.Models;
using MyHomelabBrowser.classes.Support.Transports;
using PommeBrowser.Core;
using PommeBrowser.Engine;
using PommeBrowser.Linux.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Views.Pages
{
    /// <summary>
    /// « Signaler un problème » : même rapport que les autres éditions, envoyé au serveur de support
    /// s'il est configuré (SupportApiUrl à la compilation, ou POMMEBROWSER_SUPPORT_API_URL), sinon
    /// enregistré dans une archive dans Documents/PommeBrowser/Rapports. Aucun mot de passe n'est joint.
    /// </summary>
    public sealed class ReportPage : PageBase
    {
        static readonly Lazy<SupportSubmissionService> Service = new(() => new SupportSubmissionService(
            localTransport: new LocalSupportExportTransport(Path.Combine(AppPaths.DocumentsDirectory, "PommeBrowser", "Rapports"))));

        static DateTime _lastSend = DateTime.MinValue;

        readonly string? _pageUrl;
        readonly string? _pageTitle;
        readonly ComboBox _module;
        readonly ComboBox _category;
        readonly TextBox _title = new() { PlaceholderText = Tr("En quelques mots") };
        readonly TextBox _description = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 160, MaxHeight = 320, VerticalContentAlignment = VerticalAlignment.Top };
        readonly CheckBox _logs;
        readonly CheckBox _system;
        readonly CheckBox _page;
        readonly TextBlock _error = new() { TextWrapping = TextWrapping.Wrap, IsVisible = false };
        readonly Button _send;

        public ReportPage(MainWindow window, BrowserTab? tab) : base(window, Tr("Signaler un problème"), width: 640)
        {
            // Un onglet privé ne laisse jamais son adresse partir dans un rapport.
            _pageUrl = tab is { IsPrivate: false } && tab.WebUrl.Length > 0 ? tab.WebUrl : null;
            _pageTitle = _pageUrl != null ? tab!.Title : null;

            _module = new ComboBox { ItemsSource = SupportReport.Modules.Select(m => m.Label).ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            _category = new ComboBox { ItemsSource = SupportReport.Categories.Select(c => c.Label).ToList(), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
            _logs = new CheckBox { Content = Tr("Joindre les journaux (session et erreurs)"), IsChecked = App.Settings.ReportIncludeLogs };
            _system = new CheckBox { Content = Tr("Joindre les informations système"), IsChecked = App.Settings.ReportIncludePcInfo };
            _page = new CheckBox { Content = Tr("Joindre la page affichée (adresse sans ses paramètres, et titre)"), IsChecked = false, IsEnabled = _pageUrl != null };
            _error.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("DangerBrush"));
            _send = new Button { Content = Tr("Envoyer"), HorizontalAlignment = HorizontalAlignment.Right };
            _send.Classes.Add("primary");
            _send.Click += async (_, _) => await SendAsync();
        }

        protected override void Build(StackPanel content)
        {
            var intro = Hint(Tr("Ce qui s'est passé, ce que vous attendiez, et comment reproduire le problème. Aucun mot de passe ni cookie n'est envoyé."));
            content.Children.Add(intro);

            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,16,*") };
            grid.Children.Add(Dialogs.FormDialog.Labeled(Tr("Concerne"), _module));
            Control type = Dialogs.FormDialog.Labeled(Tr("Type"), _category);
            Grid.SetColumn(type, 2);
            grid.Children.Add(type);
            content.Children.Add(grid);
            content.Children.Add(Dialogs.FormDialog.Labeled(Tr("Titre"), _title));
            content.Children.Add(Dialogs.FormDialog.Labeled(Tr("Description"), _description));
            content.Children.Add(Heading(Tr("Informations jointes")));
            content.Children.Add(_logs);
            content.Children.Add(_system);
            content.Children.Add(_page);
            content.Children.Add(_error);
            content.Children.Add(_send);
        }

        async Task SendAsync()
        {
            string titleText = (_title.Text ?? string.Empty).Trim();
            string descriptionText = (_description.Text ?? string.Empty).Trim();
            string? problem = SupportReport.Validate(titleText, descriptionText);
            if (problem == null && DateTime.UtcNow - _lastSend < TimeSpan.FromSeconds(10))
                problem = Tr("Merci d’attendre quelques secondes avant un nouvel envoi.");
            _error.Text = problem ?? string.Empty;
            _error.IsVisible = problem != null;
            if (problem != null)
                return;

            (string moduleKey, string moduleLabel) = SupportReport.Modules[Math.Max(0, _module.SelectedIndex)];
            (string categoryKey, string categoryLabel) = SupportReport.Categories[Math.Max(0, _category.SelectedIndex)];
            string version = typeof(ReportPage).Assembly.GetName().Version?.ToString() ?? Tr("inconnue");
            string id = SupportReport.NewId(DateTime.UtcNow);
            bool includeLogs = _logs.IsChecked == true;
            bool includePage = _page.IsChecked == true && _pageUrl != null;

            var report = new SupportReportRequest
            {
                ClientReportId = id,
                ClientVersion = version,
                Module = moduleKey,
                ModuleLabel = moduleLabel,
                Category = categoryKey,
                CategoryLabel = categoryLabel,
                Title = titleText,
                Description = descriptionText,
                TechnicalInformation = TechnicalInformation(moduleKey, includeLogs, _system.IsChecked == true, includePage),
                Context = includePage
                    ? new SupportReportContext
                    {
                        BrowserMode = AppInfo.Platform.ToLowerInvariant() + "-avalonia",
                        FlashMode = App.Settings.EnableFlashSupport ? "ruffle" : "disabled",
                        CurrentUrl = SupportReport.SanitizeUrl(_pageUrl!),
                        PageTitle = SupportReport.Limit(_pageTitle, 180)
                    }
                    : null,
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            SupportAttachment? attachment = includeLogs
                ? new SupportAttachment
                {
                    FileName = $"pommebrowser-log-{id}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                    ContentType = "text/plain; charset=utf-8",
                    Content = Encoding.UTF8.GetBytes(LogsForReport(version))
                }
                : null;

            _lastSend = DateTime.UtcNow;
            _send.IsEnabled = false;
            SupportSubmissionResult result;
            try
            {
                result = await Service.Value.SendAsync(report, attachment);
            }
            catch (SupportApiRejectedException ex)
            {
                ShowError(ex.Message);
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                ShowError(Tr("Impossible d’envoyer le message.\n\n") + ex.Message);
                return;
            }
            finally
            {
                _send.IsEnabled = true;
            }

            await ShowResultAsync(result);
            _title.Text = string.Empty;
            _description.Text = string.Empty;
        }

        /// <summary>
        /// Journal de la session, puis fin du journal des erreurs (errors.log), qui garde aussi les
        /// erreurs des lancements précédents. Le tout reste sous la limite du serveur (1 Mo).
        /// </summary>
        internal static string LogsForReport(string version)
        {
            var text = new StringBuilder();
            text.Append("PommeBrowser ").Append(version).Append(" (").Append(AppInfo.Platform).Append(")\n")
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append("\n\n");
            string errors = ErrorLog.ReadTail(256 * 1024) ?? "(vide)\n";
            text.Append("=== Journal de la session ===\n");
            text.Append(LastBytes(RuntimeLogBuffer.GetSnapshot() ?? string.Empty, 900 * 1024 - Encoding.UTF8.GetByteCount(errors))).Append("\n\n");
            text.Append("=== Journal des erreurs (errors.log, fin) ===\n");
            text.Append(errors);
            return text.ToString();
        }

        /// <summary>Fin du texte tenant en <paramref name="maxBytes"/> octets UTF-8, à partir d'un début de ligne.</summary>
        static string LastBytes(string text, int maxBytes)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            if (bytes.Length <= maxBytes)
                return text;
            string tail = Encoding.UTF8.GetString(bytes, bytes.Length - maxBytes, maxBytes);
            int newline = tail.IndexOf('\n');
            return newline >= 0 ? tail[(newline + 1)..] : tail;
        }

        void ShowError(string message)
        {
            _error.Text = message;
            _error.IsVisible = true;
        }

        async Task ShowResultAsync(SupportSubmissionResult result)
        {
            if (result.Channel == SupportDeliveryChannel.LocalFile && result.FilePath != null)
            {
                string reason = result.UsedFallback
                    ? Tr("Le service de support est injoignable pour le moment.")
                    : Tr("Le service de support n’est pas encore en ligne.");
                int choice = await Dialogs.Dialogs.ChoiceAsync(Window, Tr("Rapport enregistré"),
                    Tr("{0}\n\nLe rapport a été enregistré sur cet ordinateur :\n{1}\n\nVous pouvez transmettre ce fichier au développeur.", reason, result.FilePath),
                    (Tr("Afficher le fichier"), false, false), (Tr("Fermer"), true, false));
                if (choice == 0)
                    await Window.OpenFolderAsync(Path.GetDirectoryName(result.FilePath));
            }
            else
            {
                await Dialogs.Dialogs.AlertAsync(Window, Tr("Message envoyé"), Tr("Merci pour le signalement.\n\nID du rapport : {0}", result.ReportId));
            }
        }

        string TechnicalInformation(string module, bool logs, bool system, bool page)
        {
            var extra = new StringBuilder();
            if (logs)
                extra.AppendLine(Tr("- Journaux (session et erreurs) : joints au message"));

            if (system)
            {
                extra.AppendLine(Tr("- Système : {0}", RuntimeInformation.OSDescription));
                extra.AppendLine($"- Architecture : {RuntimeInformation.OSArchitecture}");
                extra.AppendLine(Tr("- .NET : {0}", Environment.Version));
                extra.AppendLine($"- Interface : Avalonia ({AppInfo.Platform})");
                extra.AppendLine($"- Moteur : {EngineHost.Describe()}");
                if (OperatingSystem.IsLinux())
                    extra.AppendLine(Environment.GetEnvironmentVariable("APPIMAGE") != null ? "- Paquet : AppImage" : "- Paquet : sources");
            }

            if (page && _pageUrl != null)
            {
                extra.AppendLine(Tr("- URL active : {0}", SupportReport.SanitizeUrl(_pageUrl)));
                extra.AppendLine(Tr("- Titre de la page : {0}", SupportReport.Limit(_pageTitle, 180)));
            }

            if (module == "adblock")
            {
                extra.AppendLine(Tr("- Bloqueur activé : {0}", App.AdBlock.Settings.Enabled ? Tr("oui") : Tr("non")));
                extra.AppendLine(Tr("- Moteur prêt : {0}", App.AdBlock.IsReady ? Tr("oui") : Tr("non")));
                extra.AppendLine(Tr("- Règles : {0:N0}", App.AdBlock.RuleCount));
                extra.AppendLine(Tr("- État : {0}", SupportReport.Limit(App.AdBlock.Status, 220)));
            }
            else
            {
                extra.AppendLine(Tr("- Composant : cœur du navigateur"));
            }

            return extra.Length == 0 ? Tr("Aucune information technique jointe.") : extra.ToString().TrimEnd();
        }
    }
}
