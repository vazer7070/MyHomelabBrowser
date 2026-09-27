using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.AdBlock.Services;
using MyHomelabBrowser.classes.Support;
using MyHomelabBrowser.classes.Support.Models;
using System;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using static MyHomelabBrowser.classes.Localization.Loc;

namespace MyHomelabBrowser.controles
{
    public partial class ReportIssueView : UserControl
    {
        public event Action? CloseRequested;

        private static DateTime _lastSend = DateTime.MinValue;
        private static bool _sending;

        private readonly ReportIssueOptions _options;

        public ReportIssueView(ReportIssueOptions options)
        {
            InitializeComponent();

            _options = new ReportIssueOptions
            {
                IncludeLogs = options.IncludeLogs,
                IncludePcInfo = options.IncludePcInfo,
                IncludeMode = options.IncludeMode,
                Module = options.Module,
                Context = options.Context
            };

            SelectModule(_options.Module);
            UpdateModuleHelp(_options.Module);
        }

        public void Cancel_Click(object sender, RoutedEventArgs e)
        {
            CloseRequested?.Invoke();
        }

        private void ModuleBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!IsLoaded && ModuleHint == null)
                return;

            UpdateModuleHelp(GetSelectedModule());
        }

        private async void Send_Click(object sender, RoutedEventArgs e)
        {
            if (_sending)
                return;

            if (DateTime.UtcNow - _lastSend < TimeSpan.FromSeconds(10))
            {
                ShowDialog(
                    Tr("Trop rapide"),
                    Tr("Merci d’attendre quelques secondes avant un nouvel envoi."));
                return;
            }

            string title = TitleBox.Text.Trim();
            string description = DescriptionBox.Text.Trim();
            IssueType issueType = GetSelectedIssueType();
            ReportModule module = GetSelectedModule();

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description))
            {
                ShowDialog(
                    Tr("Champs manquants"),
                    Tr("Merci de remplir le titre et la description avant l’envoi."));
                return;
            }

            if (description.Length > 3500)
            {
                ShowDialog(
                    Tr("Message trop long"),
                    Tr("La description est trop longue pour être envoyée."));
                return;
            }

            MessageBoxResult confirm = MessageBox.Show(
                Tr("Envoyer ce rapport concernant {0} ?", GetModuleLabel(module)),
                Tr("Confirmation"),
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
                return;

            string appVersion = System.Reflection.Assembly
                .GetExecutingAssembly()
                .GetName()
                .Version?
                .ToString()
                ?? Tr("inconnue");

            string reportId = GenerateReportId();
            string moduleLabel = GetModuleLabel(module);
            string issueLabel = GetIssueTypeLabel(issueType);

            var report = new SupportReportRequest
            {
                ClientReportId = reportId,
                ClientVersion = appVersion,
                Module = GetModuleKey(module),
                ModuleLabel = moduleLabel,
                Category = GetIssueTypeKey(issueType),
                CategoryLabel = issueLabel,
                Title = title,
                Description = description,
                TechnicalInformation = BuildExtraInformation(module),
                Context = BuildStructuredContext(),
                CreatedAtUtc = DateTimeOffset.UtcNow
            };

            SupportAttachment? attachment = BuildLogAttachment(
                reportId,
                appVersion,
                moduleLabel);

            SetSendingState(true);
            _sending = true;
            _lastSend = DateTime.UtcNow;

            try
            {
                SupportSubmissionResult result = await SupportSubmissionHost.Current
                    .SendAsync(report, attachment);

                if (result.Channel == SupportDeliveryChannel.LocalFile && result.FilePath != null)
                {
                    string reason = result.UsedFallback
                        ? Tr("Le service de support est injoignable pour le moment.")
                        : Tr("Le service de support n’est pas encore en ligne.");

                    ShowDialog(
                        Tr("Rapport enregistré"),
                        Tr("{0}\n\nLe rapport a été enregistré sur cet ordinateur :\n{1}\n\nVous pouvez transmettre ce fichier au développeur.", reason, result.FilePath));

                    RevealInExplorer(result.FilePath);
                }
                else
                {
                    ShowDialog(
                        Tr("Message envoyé"),
                        Tr("Merci pour le signalement.\n\nID du rapport : {0}", result.ReportId));
                }

                CloseRequested?.Invoke();
            }
            catch (SupportApiRejectedException ex)
            {
                ShowDialog(
                    Tr("Rapport refusé"),
                    ex.Message);
            }
            catch (Exception ex)
            {
                ShowDialog(
                    Tr("Erreur d’envoi"),
                    Tr("Impossible d’envoyer le message.\n\n") + ex.Message);
            }
            finally
            {
                _sending = false;
                SetSendingState(false);
            }
        }

        private SupportAttachment? BuildLogAttachment(
            string reportId,
            string appVersion,
            string moduleLabel)
        {
            if (!_options.IncludeLogs)
                return null;

            string logs = RuntimeLogBuffer.GetSnapshot();
            if (string.IsNullOrWhiteSpace(logs))
            {
                logs =
$"""
PommeBrowser – Runtime snapshot
Version : {appVersion}
Date    : {DateTime.Now:yyyy-MM-dd HH:mm:ss}
Module  : {moduleLabel}

Aucun log console n’a été généré pour cette session.
(Application WPF – Console non utilisée)
""";
            }

            return new SupportAttachment
            {
                FileName = $"pommebrowser-log-{reportId}-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
                ContentType = "text/plain; charset=utf-8",
                Content = Encoding.UTF8.GetBytes(logs)
            };
        }

        private SupportReportContext? BuildStructuredContext()
        {
            if (!_options.IncludeMode || _options.Context == null)
                return null;

            BrowserContext context = _options.Context;
            string browserMode = context.IsLegacy
                ? "legacy"
                : context.IsPrivate
                    ? "private"
                    : "normal";

            return new SupportReportContext
            {
                BrowserMode = browserMode,
                FlashMode = context.FlashMode,
                CurrentUrl = string.IsNullOrWhiteSpace(context.CurrentUrl)
                    ? null
                    : SanitizeUrl(context.CurrentUrl),
                PageTitle = string.IsNullOrWhiteSpace(context.PageTitle)
                    ? null
                    : Limit(context.PageTitle, 180),
                TabId = context.TabId?.ToString()
            };
        }

        private string BuildExtraInformation(ReportModule module)
        {
            var extra = new StringBuilder();

            if (_options.IncludeLogs)
                extra.AppendLine(Tr("- Logs récents : joints au message"));

            if (_options.IncludePcInfo)
            {
                extra.AppendLine(Tr("- Système : {0}", Environment.OSVersion));
                extra.AppendLine($"- Architecture : {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
                extra.AppendLine(Tr("- .NET : {0}", Environment.Version));
            }

            if (_options.IncludeMode)
                AppendBrowserContext(extra);

            AppendModuleDiagnostics(extra, module);

            return extra.Length == 0
                ? Tr("Aucune information technique jointe.")
                : extra.ToString().TrimEnd();
        }

        private void AppendBrowserContext(StringBuilder extra)
        {
            BrowserContext? context = _options.Context;
            if (context == null)
            {
                extra.AppendLine(Tr("- Mode navigateur : inconnu"));
                return;
            }

            string mode = context.IsLegacy
                ? "legacy"
                : context.IsPrivate
                    ? Tr("privé")
                    : "normal";

            extra.AppendLine(Tr("- Mode navigateur : {0}", mode));
            extra.AppendLine(Tr("- Mode Flash : {0}", context.FlashMode));

            if (!string.IsNullOrWhiteSpace(context.CurrentUrl))
                extra.AppendLine(Tr("- URL active : {0}", SanitizeUrl(context.CurrentUrl)));

            if (!string.IsNullOrWhiteSpace(context.PageTitle))
                extra.AppendLine(Tr("- Titre de la page : {0}", Limit(context.PageTitle, 180)));

            if (context.TabId != null)
                extra.AppendLine(Tr("- Onglet : {0}", context.TabId));
        }

        private static void AppendModuleDiagnostics(StringBuilder extra, ReportModule module)
        {
            switch (module)
            {
                case ReportModule.AdBlock:
                {
                    var snapshot = AdBlockModuleHost.Current.GetSnapshot();
                    extra.AppendLine(Tr("- Bloqueur activé : {0}", YesNo(snapshot.Enabled)));
                    extra.AppendLine(Tr("- Moteur prêt : {0}", YesNo(snapshot.IsReady)));
                    extra.AppendLine(Tr("- Règles réseau : {0:N0}", snapshot.NetworkRuleCount));
                    extra.AppendLine(Tr("- Règles visuelles : {0:N0}", snapshot.CosmeticRuleCount));
                    extra.AppendLine(Tr("- Blocages pendant la session : {0:N0}", snapshot.SessionBlockedCount));
                    extra.AppendLine(Tr("- État : {0}", Limit(snapshot.StatusMessage, 220)));
                    extra.AppendLine(Tr("- Dernière mise à jour : {0}", FormatDate(snapshot.LastSuccessfulUpdateUtc)));
                    break;
                }

                default:
                    extra.AppendLine(Tr("- Composant : cœur du navigateur"));
                    break;
            }
        }

        private void SelectModule(ReportModule module)
        {
            foreach (object entry in ModuleBox.Items)
            {
                if (entry is ComboBoxItem item &&
                    Enum.TryParse(item.Tag?.ToString(), out ReportModule itemModule) &&
                    itemModule == module)
                {
                    ModuleBox.SelectedItem = item;
                    return;
                }
            }

            ModuleBox.SelectedIndex = 0;
        }

        private void UpdateModuleHelp(ReportModule module)
        {
            if (ModuleHint == null || DiagnosticNotice == null)
                return;

            switch (module)
            {
                case ReportModule.AdBlock:
                    ModuleHint.Text = Tr("Pour une publicité non bloquée, un site cassé, une liste qui ne se met pas à jour ou un problème de filtrage.");
                    DiagnosticNotice.Text = Tr("Le rapport ajoutera l’état du bloqueur, le nombre de règles, les statistiques de session et la date de mise à jour des listes.");
                    break;

                default:
                    ModuleHint.Text = Tr("Pour un problème général du navigateur, des onglets, des téléchargements, des profils ou de l’interface.");
                    DiagnosticNotice.Text = Tr("Le rapport indiquera le contexte de navigation autorisé dans les paramètres. Aucun mot de passe ni secret n’est envoyé.");
                    break;
            }
        }

        private ReportModule GetSelectedModule()
        {
            if (ModuleBox.SelectedItem is ComboBoxItem item &&
                Enum.TryParse(item.Tag?.ToString(), out ReportModule module))
            {
                return module;
            }

            return ReportModule.Browser;
        }

        private IssueType GetSelectedIssueType()
        {
            if (TypeBox.SelectedItem is ComboBoxItem item &&
                Enum.TryParse(item.Tag?.ToString(), out IssueType type))
            {
                return type;
            }

            return IssueType.Other;
        }

        private void SetSendingState(bool sending)
        {
            SendButton.IsEnabled = !sending;
            ModuleBox.IsEnabled = !sending;
            TypeBox.IsEnabled = !sending;
            TitleBox.IsEnabled = !sending;
            DescriptionBox.IsEnabled = !sending;
            SendingPanel.Visibility = sending ? Visibility.Visible : Visibility.Collapsed;
        }

        private static void RevealInExplorer(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                // Le chemin reste affiché dans la boîte de dialogue.
            }
        }

        private void ShowDialog(string title, string message)
        {
            ThemedDialogWindow.Show(
                Window.GetWindow(this),
                title,
                message);
        }

        private static string GenerateReportId()
        {
            string random = Guid.NewGuid().ToString("N")[..4].ToUpperInvariant();
            return $"PB-{DateTime.UtcNow:yyyyMMdd}-{random}";
        }

        private static string GetModuleLabel(ReportModule module) => module switch
        {
            ReportModule.AdBlock => Tr("Bloqueur de publicités"),
            _ => "PommeBrowser"
        };

        private static string GetModuleKey(ReportModule module) => module switch
        {
            ReportModule.AdBlock => "adblock",
            _ => "browser"
        };

        private static string GetIssueTypeLabel(IssueType type) => type switch
        {
            IssueType.Bug => Tr("Bug ou dysfonctionnement"),
            IssueType.MissingFeature => Tr("Fonctionnalité absente"),
            IssueType.FeatureRequest => Tr("Demande d’ajout"),
            IssueType.UiUx => Tr("Interface ou ergonomie"),
            IssueType.Performance => "Performance",
            _ => Tr("Autre")
        };

        private static string GetIssueTypeKey(IssueType type) => type switch
        {
            IssueType.Bug => "bug",
            IssueType.MissingFeature => "missing_feature",
            IssueType.FeatureRequest => "feature_request",
            IssueType.UiUx => "ui_ux",
            IssueType.Performance => "performance",
            _ => "other"
        };

        private static string SanitizeUrl(string value)
        {
            if (!Uri.TryCreate(value, UriKind.Absolute, out Uri? uri))
                return Limit(value, 300);

            var builder = new UriBuilder(uri)
            {
                UserName = string.Empty,
                Password = string.Empty,
                Query = string.Empty,
                Fragment = string.Empty
            };

            return Limit(builder.Uri.ToString(), 300);
        }

        private static string FormatDate(DateTimeOffset? value)
            => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? Tr("inconnue");

        private static string Limit(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return Tr("inconnu");

            string trimmed = value.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "…";
        }


        private static string YesNo(bool value) => value ? Tr("oui") : Tr("non");

        public enum IssueType
        {
            Bug,
            MissingFeature,
            FeatureRequest,
            UiUx,
            Performance,
            Other
        }
    }

}
