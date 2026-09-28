using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.Support;
using MyHomelabBrowser.classes.Support.Models;
using MyHomelabBrowser.classes.Support.Transports;
using PommeBrowser.Linux.Core;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace PommeBrowser.Linux.Ui
{
    /// <summary>
    /// « Signaler un problème » : même rapport que l'édition Windows, envoyé au serveur de support
    /// s'il est configuré (SupportApiUrl à la compilation, ou POMMEBROWSER_SUPPORT_API_URL), sinon
    /// enregistré dans une archive dans Documents/PommeBrowser/Rapports. Aucun mot de passe n'est joint.
    /// </summary>
    static class ReportDialog
    {
        static readonly Lazy<SupportSubmissionService> Service = new(() => new SupportSubmissionService(
            localTransport: new LocalSupportExportTransport(Path.Combine(LinuxPaths.DocumentsDirectory(), "PommeBrowser", "Rapports"))));

        static DateTime _lastSend = DateTime.MinValue;

        public static void Show(BrowserApplication app, BrowserWindow window)
        {
            BrowserTab? tab = window.Current;
            var form = new FormDialog(Tr("Signaler un problème"), Tr("Envoyer"), width: 540);

            var modules = SupportReport.Modules;
            var categories = SupportReport.Categories;
            Adw.ComboRow module = Combo(Tr("Concerne"), modules.Select(m => m.Label).ToArray());
            Adw.ComboRow category = Combo(Tr("Type"), categories.Select(c => c.Label).ToArray());
            Adw.EntryRow title = FormDialog.EntryRow(Tr("Titre"));
            form.AddGroup(null, module, category, title);

            var description = Gtk.TextView.New();
            description.SetWrapMode(Gtk.WrapMode.WordChar);
            description.SetTopMargin(10);
            description.SetBottomMargin(10);
            description.SetLeftMargin(12);
            description.SetRightMargin(12);
            description.AddCssClass("card");
            var scroller = Gtk.ScrolledWindow.New();
            scroller.SetChild(description);
            scroller.SetMinContentHeight(140);
            scroller.SetMaxContentHeight(260);
            scroller.SetPropagateNaturalHeight(true);
            var descriptionGroup = Adw.PreferencesGroup.New();
            descriptionGroup.SetTitle(Tr("Description"));
            descriptionGroup.SetDescription(Tr("Ce qui s'est passé, ce que vous attendiez, et comment reproduire le problème."));
            descriptionGroup.Add(scroller);
            form.Add(descriptionGroup);

            Adw.SwitchRow logs = Switch(Tr("Joindre le journal de la session"), true);
            Adw.SwitchRow system = Switch(Tr("Joindre les informations système"), true);
            Adw.SwitchRow page = Switch(Tr("Joindre la page affichée"), false);
            page.SetSubtitle(Tr("Adresse sans ses paramètres, et titre de la page."));
            page.SetSensitive(tab?.Uri.Length > 0);
            form.AddGroup(Tr("Informations jointes"), logs, system, page);
            form.AddText(Tr("Aucun mot de passe ni cookie n'est envoyé."));

            form.Submit = async () =>
            {
                string titleText = title.GetText().Trim();
                string descriptionText = BufferText(description).Trim();
                if (SupportReport.Validate(titleText, descriptionText) is { } invalid)
                    return invalid;
                if (DateTime.UtcNow - _lastSend < TimeSpan.FromSeconds(10))
                    return Tr("Merci d’attendre quelques secondes avant un nouvel envoi.");

                (string moduleKey, string moduleLabel) = modules[(int)module.GetSelected()];
                (string categoryKey, string categoryLabel) = categories[(int)category.GetSelected()];
                string version = typeof(ReportDialog).Assembly.GetName().Version?.ToString() ?? Tr("inconnue");
                string id = SupportReport.NewId(DateTime.UtcNow);
                bool includePage = page.GetActive() && tab != null && tab.Uri.Length > 0;

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
                    TechnicalInformation = TechnicalInformation(app, window, tab, moduleKey, logs.GetActive(), system.GetActive(), includePage),
                    Context = includePage
                        ? new SupportReportContext
                        {
                            BrowserMode = window.IsPrivate ? "linux-private" : "linux-normal",
                            FlashMode = app.Settings.EnableRuffle ? "ruffle" : "disabled",
                            CurrentUrl = SupportReport.SanitizeUrl(tab!.Uri),
                            PageTitle = SupportReport.Limit(tab.Title, 180)
                        }
                        : null,
                    CreatedAtUtc = DateTimeOffset.UtcNow
                };

                SupportAttachment? attachment = logs.GetActive()
                    ? new SupportAttachment
                    {
                        FileName = $"pommebrowser-log-{id}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                        ContentType = "text/plain; charset=utf-8",
                        Content = Encoding.UTF8.GetBytes(RuntimeLogBuffer.GetSnapshot() is { Length: > 0 } snapshot
                            ? snapshot
                            : $"PommeBrowser {version} (Linux)\n{DateTime.Now:yyyy-MM-dd HH:mm:ss}\n")
                    }
                    : null;

                _lastSend = DateTime.UtcNow;
                SupportSubmissionResult result;
                try
                {
                    result = await Service.Value.SendAsync(report, attachment);
                }
                catch (SupportApiRejectedException ex)
                {
                    return ex.Message;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    return Tr("Impossible d’envoyer le message.\n\n") + ex.Message;
                }

                MainThread.Post(() => ShowResult(window, result));
                return null;
            };

            form.Present(window.Window);
            title.GrabFocus();
        }

        static void ShowResult(BrowserWindow window, SupportSubmissionResult result)
        {
            Adw.AlertDialog dialog;
            if (result.Channel == SupportDeliveryChannel.LocalFile && result.FilePath != null)
            {
                string reason = result.UsedFallback
                    ? Tr("Le service de support est injoignable pour le moment.")
                    : Tr("Le service de support n’est pas encore en ligne.");
                dialog = Adw.AlertDialog.New(Tr("Rapport enregistré"),
                    Tr("{0}\n\nLe rapport a été enregistré sur cet ordinateur :\n{1}\n\nVous pouvez transmettre ce fichier au développeur.", reason, result.FilePath));
                dialog.AddResponse("show", Tr("Afficher le fichier"));
                dialog.AddResponse("close", Tr("Fermer"));
                dialog.SetDefaultResponse("close");
                dialog.OnResponse += async (_, args) =>
                {
                    if (args.Response != "show")
                        return;
                    try
                    {
                        await Gtk.FileLauncher.New(Gio.FileHelper.NewForPath(result.FilePath)).OpenContainingFolderAsync(window.Window);
                    }
                    catch (Exception ex)
                    {
                        RuntimeLogBuffer.Append("[Support] " + ex.Message);
                    }
                };
            }
            else
            {
                dialog = Adw.AlertDialog.New(Tr("Message envoyé"), Tr("Merci pour le signalement.\n\nID du rapport : {0}", result.ReportId));
                dialog.AddResponse("close", Tr("Fermer"));
            }
            dialog.SetCloseResponse("close");
            dialog.Present(window.Window);
        }

        static string TechnicalInformation(BrowserApplication app, BrowserWindow window, BrowserTab? tab, string module, bool logs, bool system, bool page)
        {
            var extra = new StringBuilder();
            if (logs)
                extra.AppendLine(Tr("- Logs récents : joints au message"));

            if (system)
            {
                extra.AppendLine(Tr("- Système : {0}", DistributionName()));
                extra.AppendLine($"- Architecture : {RuntimeInformation.OSArchitecture}");
                extra.AppendLine(Tr("- .NET : {0}", Environment.Version));
                extra.AppendLine($"- WebKitGTK : {WebKit.Functions.GetMajorVersion()}.{WebKit.Functions.GetMinorVersion()}.{WebKit.Functions.GetMicroVersion()}");
                extra.AppendLine($"- GTK : {Gtk.Functions.GetMajorVersion()}.{Gtk.Functions.GetMinorVersion()}.{Gtk.Functions.GetMicroVersion()}");
                extra.AppendLine($"- libadwaita : {Adw.Functions.GetMajorVersion()}.{Adw.Functions.GetMinorVersion()}.{Adw.Functions.GetMicroVersion()}");
                extra.AppendLine(Environment.GetEnvironmentVariable("APPIMAGE") != null ? "- Paquet : AppImage" : "- Paquet : sources");
            }

            if (page && tab != null)
            {
                extra.AppendLine(Tr("- Mode navigateur : {0}", window.IsPrivate ? Tr("privé") : "normal"));
                extra.AppendLine(Tr("- URL active : {0}", SupportReport.SanitizeUrl(tab.Uri)));
                extra.AppendLine(Tr("- Titre de la page : {0}", SupportReport.Limit(tab.Title, 180)));
            }

            if (module == "adblock")
            {
                extra.AppendLine(Tr("- Bloqueur activé : {0}", app.Engine.AdBlocker.Settings.Enabled ? Tr("oui") : Tr("non")));
                extra.AppendLine(Tr("- Moteur prêt : {0}", app.Engine.AdBlocker.IsReady ? Tr("oui") : Tr("non")));
                extra.AppendLine(Tr("- Règles : {0:N0}", app.Engine.AdBlocker.RuleCount));
                extra.AppendLine(Tr("- État : {0}", SupportReport.Limit(app.Engine.AdBlocker.Status, 220)));
            }
            else
            {
                extra.AppendLine(Tr("- Composant : cœur du navigateur"));
            }

            return extra.Length == 0 ? Tr("Aucune information technique jointe.") : extra.ToString().TrimEnd();
        }

        /// <summary>Distribution (lue par .NET dans /etc/os-release) et version du noyau.</summary>
        static string DistributionName()
            => $"{RuntimeInformation.OSDescription} · Linux {Environment.OSVersion.Version}";

        static string BufferText(Gtk.TextView view)
        {
            Gtk.TextBuffer buffer = view.GetBuffer();
            buffer.GetBounds(out Gtk.TextIter start, out Gtk.TextIter end);
            return buffer.GetText(start, end, false);
        }

        static Adw.ComboRow Combo(string title, string[] choices)
        {
            var row = Adw.ComboRow.New();
            row.SetTitle(title);
            row.SetModel(Gtk.StringList.New(choices));
            return row;
        }

        static Adw.SwitchRow Switch(string title, bool active)
        {
            var row = Adw.SwitchRow.New();
            row.SetTitle(title);
            row.SetActive(active);
            return row;
        }
    }
}
