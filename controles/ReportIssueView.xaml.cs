using MyHomelabBrowser.classes;
using MyHomelabBrowser.classes.AdBlock.Services;
using MyHomelabBrowser.classes.CloudTorrent.Models;
using MyHomelabBrowser.classes.CloudTorrent.Services;
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;

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
                    "Trop rapide",
                    "Merci d’attendre quelques secondes avant un nouvel envoi.");
                return;
            }

            string title = TitleBox.Text.Trim();
            string description = DescriptionBox.Text.Trim();
            IssueType issueType = GetSelectedIssueType();
            ReportModule module = GetSelectedModule();

            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(description))
            {
                ShowDialog(
                    "Champs manquants",
                    "Merci de remplir le titre et la description avant l’envoi.");
                return;
            }

            if (description.Length > 3500)
            {
                ShowDialog(
                    "Message trop long",
                    "La description est trop longue pour être envoyée.");
                return;
            }

            MessageBoxResult confirm = MessageBox.Show(
                $"Envoyer ce rapport concernant {GetModuleLabel(module)} sur le support Discord ?",
                "Confirmation",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);

            if (confirm != MessageBoxResult.Yes)
                return;

            string webhook = DiscordWebhooks.GetWebhook(issueType);
            if (string.IsNullOrWhiteSpace(webhook))
            {
                ShowDialog(
                    "Erreur interne",
                    "Aucun webhook n’est configuré pour ce type de demande.");
                return;
            }

            string appVersion = System.Reflection.Assembly
                .GetExecutingAssembly()
                .GetName()
                .Version?
                .ToString()
                ?? "inconnue";

            string reportId = GenerateReportId();
            string technicalInformation = BuildExtraInformation(module);
            string moduleLabel = GetModuleLabel(module);
            string issueLabel = GetIssueTypeLabel(issueType);

            var payload = new
            {
                username = "PommeBrowser Support",
                embeds = new[]
                {
                    new
                    {
                        title = $"{GetModuleIcon(module)} {moduleLabel} — {issueLabel}",
                        description =
$"""
🆔 **ID rapport :** `{reportId}`
🧭 **Version navigateur :** `{appVersion}`
🧩 **Module :** {moduleLabel}
🏷️ **Type :** {issueLabel}

**Titre :** {title}

**Description :**
{description}
""",
                        color = GetModuleColor(module),
                        fields = new[]
                        {
                            new
                            {
                                name = "Informations techniques",
                                value = LimitForDiscordField(technicalInformation),
                                inline = false
                            }
                        },
                        footer = new
                        {
                            text = "PommeBrowser – Support utilisateur"
                        },
                        timestamp = DateTimeOffset.UtcNow
                    }
                }
            };

            SetSendingState(true);
            _sending = true;
            _lastSend = DateTime.UtcNow;

            try
            {
                using var http = new HttpClient
                {
                    Timeout = TimeSpan.FromSeconds(30)
                };

                HttpResponseMessage response;

                if (_options.IncludeLogs)
                {
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

                    string tempFile = Path.Combine(
                        Path.GetTempPath(),
                        $"pommebrowser-log-{reportId}-{DateTime.Now:yyyyMMdd-HHmmss}.txt");

                    try
                    {
                        File.WriteAllText(tempFile, logs, Encoding.UTF8);

                        using var form = new MultipartFormDataContent();
                        form.Add(
                            new StringContent(
                                System.Text.Json.JsonSerializer.Serialize(payload),
                                Encoding.UTF8,
                                "application/json"),
                            "payload_json");

                        form.Add(
                            new ByteArrayContent(await File.ReadAllBytesAsync(tempFile)),
                            "file",
                            Path.GetFileName(tempFile));

                        response = await http.PostAsync(webhook, form);
                    }
                    finally
                    {
                        try
                        {
                            if (File.Exists(tempFile))
                                File.Delete(tempFile);
                        }
                        catch
                        {
                            // Le rapport a déjà été envoyé : l'échec de nettoyage n'est pas bloquant.
                        }
                    }
                }
                else
                {
                    string json = System.Text.Json.JsonSerializer.Serialize(payload);
                    response = await http.PostAsync(
                        webhook,
                        new StringContent(json, Encoding.UTF8, "application/json"));
                }

                if (!response.IsSuccessStatusCode)
                {
                    string responseBody = await response.Content.ReadAsStringAsync();
                    string details = string.IsNullOrWhiteSpace(responseBody)
                        ? response.ReasonPhrase ?? "Réponse Discord inconnue"
                        : responseBody;

                    if (details.Length > 300)
                        details = details[..300];

                    throw new Exception($"Erreur HTTP {(int)response.StatusCode} : {details}");
                }

                ShowDialog(
                    "Message envoyé",
                    $"Merci pour le signalement.\n\nID du rapport : {reportId}");

                CloseRequested?.Invoke();
            }
            catch (Exception ex)
            {
                ShowDialog(
                    "Erreur d’envoi",
                    "Impossible d’envoyer le message.\n\n" + ex.Message);
            }
            finally
            {
                _sending = false;
                SetSendingState(false);
            }
        }

        private string BuildExtraInformation(ReportModule module)
        {
            var extra = new StringBuilder();

            if (_options.IncludeLogs)
                extra.AppendLine("- Logs récents : joints au message");

            if (_options.IncludePcInfo)
            {
                extra.AppendLine($"- Système : {Environment.OSVersion}");
                extra.AppendLine($"- Architecture : {System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}");
                extra.AppendLine($"- .NET : {Environment.Version}");
            }

            if (_options.IncludeMode)
                AppendBrowserContext(extra);

            AppendModuleDiagnostics(extra, module);

            return extra.Length == 0
                ? "Aucune information technique jointe."
                : extra.ToString().TrimEnd();
        }

        private void AppendBrowserContext(StringBuilder extra)
        {
            BrowserContext? context = _options.Context;
            if (context == null)
            {
                extra.AppendLine("- Mode navigateur : inconnu");
                return;
            }

            string mode = context.IsLegacy
                ? "legacy"
                : context.IsPrivate
                    ? "privé"
                    : "normal";

            extra.AppendLine($"- Mode navigateur : {mode}");
            extra.AppendLine($"- Mode Flash : {context.FlashMode}");

            if (!string.IsNullOrWhiteSpace(context.CurrentUrl))
                extra.AppendLine($"- URL active : {SanitizeUrl(context.CurrentUrl)}");

            if (!string.IsNullOrWhiteSpace(context.PageTitle))
                extra.AppendLine($"- Titre de la page : {Limit(context.PageTitle, 180)}");

            if (context.TabId != null)
                extra.AppendLine($"- Onglet : {context.TabId}");
        }

        private static void AppendModuleDiagnostics(StringBuilder extra, ReportModule module)
        {
            switch (module)
            {
                case ReportModule.AdBlock:
                {
                    var snapshot = AdBlockModuleHost.Current.GetSnapshot();
                    extra.AppendLine($"- Bloqueur activé : {YesNo(snapshot.Enabled)}");
                    extra.AppendLine($"- Moteur prêt : {YesNo(snapshot.IsReady)}");
                    extra.AppendLine($"- Règles réseau : {snapshot.NetworkRuleCount:N0}");
                    extra.AppendLine($"- Règles visuelles : {snapshot.CosmeticRuleCount:N0}");
                    extra.AppendLine($"- Blocages pendant la session : {snapshot.SessionBlockedCount:N0}");
                    extra.AppendLine($"- État : {Limit(snapshot.StatusMessage, 220)}");
                    extra.AppendLine($"- Dernière mise à jour : {FormatDate(snapshot.LastSuccessfulUpdateUtc)}");
                    break;
                }

                case ReportModule.CloudTorrent:
                {
                    CloudTorrentModuleSnapshot snapshot = CloudTorrentModuleHost.Current.Snapshot;
                    extra.AppendLine($"- État CloudTorrent : {GetCloudTorrentStatusLabel(snapshot.Status)}");
                    extra.AppendLine($"- Module actif : {YesNo(snapshot.IsActive)}");
                    extra.AppendLine($"- Serveur : {GetServerHost(snapshot.ServerUrl)}");
                    extra.AppendLine($"- Utilisateur : {SafeValue(snapshot.Username)}");
                    extra.AppendLine($"- Version API : {SafeValue(snapshot.ApiVersion)}");
                    extra.AppendLine($"- Analyse automatique : {YesNo(snapshot.AutoAnalyzePages)}");
                    extra.AppendLine($"- Clé enregistrée : {YesNo(snapshot.HasStoredApiKey)}");
                    extra.AppendLine($"- Dernière validation : {FormatDate(snapshot.LastValidatedAt)}");

                    string permissions = snapshot.Permissions.Count == 0
                        ? "aucune"
                        : string.Join(", ", snapshot.Permissions.OrderBy(item => item, StringComparer.Ordinal));
                    extra.AppendLine($"- Permissions : {Limit(permissions, 500)}");
                    break;
                }

                default:
                    extra.AppendLine("- Composant : cœur du navigateur");
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
                    ModuleHint.Text = "Pour une publicité non bloquée, un site cassé, une liste qui ne se met pas à jour ou un problème de filtrage.";
                    DiagnosticNotice.Text = "Le rapport ajoutera l’état du bloqueur, le nombre de règles, les statistiques de session et la date de mise à jour des listes.";
                    break;

                case ReportModule.CloudTorrent:
                    ModuleHint.Text = "Pour la détection des pages, les téléchargements, les liens 1fichier, l’analyse des médias ou la connexion à l’API.";
                    DiagnosticNotice.Text = "Le rapport ajoutera l’état de CloudTorrent, la version API et les permissions. La clé API n’est jamais envoyée.";
                    break;

                default:
                    ModuleHint.Text = "Pour un problème général du navigateur, des onglets, des téléchargements, des profils ou de l’interface.";
                    DiagnosticNotice.Text = "Le rapport indiquera le contexte de navigation autorisé dans les paramètres. Aucun mot de passe ni secret n’est envoyé.";
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
            ReportModule.AdBlock => "Bloqueur de publicités",
            ReportModule.CloudTorrent => "CloudTorrent",
            _ => "PommeBrowser"
        };

        private static string GetModuleIcon(ReportModule module) => module switch
        {
            ReportModule.AdBlock => "🛡️",
            ReportModule.CloudTorrent => "☁️",
            _ => "🌐"
        };

        private static int GetModuleColor(ReportModule module) => module switch
        {
            ReportModule.AdBlock => 0x3973C6,
            ReportModule.CloudTorrent => 0x2F80ED,
            _ => 0xB03030
        };

        private static string GetIssueTypeLabel(IssueType type) => type switch
        {
            IssueType.Bug => "Bug ou dysfonctionnement",
            IssueType.MissingFeature => "Fonctionnalité absente",
            IssueType.FeatureRequest => "Demande d’ajout",
            IssueType.UiUx => "Interface ou ergonomie",
            IssueType.Performance => "Performance",
            _ => "Autre"
        };

        private static string GetCloudTorrentStatusLabel(CloudTorrentConnectionStatus status) => status switch
        {
            CloudTorrentConnectionStatus.Active => "actif",
            CloudTorrentConnectionStatus.Validating => "vérification en cours",
            CloudTorrentConnectionStatus.InvalidApiKey => "clé API refusée",
            CloudTorrentConnectionStatus.Forbidden => "permissions insuffisantes",
            CloudTorrentConnectionStatus.ServerUnavailable => "serveur inaccessible",
            CloudTorrentConnectionStatus.InvalidConfiguration => "configuration invalide",
            CloudTorrentConnectionStatus.Error => "erreur",
            _ => "non configuré"
        };

        private static string GetServerHost(string serverUrl)
        {
            if (Uri.TryCreate(serverUrl, UriKind.Absolute, out Uri? uri))
                return uri.Host;

            return string.IsNullOrWhiteSpace(serverUrl) ? "non configuré" : "adresse invalide";
        }

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
            => value?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "inconnue";

        private static string SafeValue(string? value)
            => string.IsNullOrWhiteSpace(value) ? "inconnu" : Limit(value.Trim(), 180);

        private static string Limit(string? value, int maxLength)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "inconnu";

            string trimmed = value.Trim();
            return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + "…";
        }


        private static string LimitForDiscordField(string? value)
        {
            const int maxLength = 1024;
            string text = string.IsNullOrWhiteSpace(value)
                ? "Aucune information technique jointe."
                : value.Trim();

            return text.Length <= maxLength
                ? text
                : text[..(maxLength - 1)] + "…";
        }

        private static string YesNo(bool value) => value ? "oui" : "non";

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

    static class DiscordWebhooks
    {
        public static string GetWebhook(ReportIssueView.IssueType type)
        {
            return type switch
            {
                ReportIssueView.IssueType.Bug =>
                    "https://discord.com/api/webhooks/1466856163688845413/SNN4ZMroiwUejaUgIHDLtbvb8ovVebpT1tigZjuS_zGIc2CE0SLkT78cIJH3NS_gDxC4",

                ReportIssueView.IssueType.MissingFeature =>
                    "https://discord.com/api/webhooks/1466856296002228235/HguCY5QSmLSzbVaomvsWuJGusjmg8dK_axxkBGWYCmIMXIszVPO6QKKNBrtvc2x-iiTo",

                ReportIssueView.IssueType.FeatureRequest =>
                    "https://discord.com/api/webhooks/1466856426881421364/Q9tepiS8kF1jV0wetkr4TKtN0vZNOP_5FRlEwzeycfbzu_1_kvZbciYiR1toLjCkL19L",

                ReportIssueView.IssueType.UiUx =>
                    "https://discord.com/api/webhooks/1466856539980566764/Zo5Q8BRkNH2XxrZmQ2jneF3GSDsdvpVco-JxggullC8XiCxrKGHBayvY5y6lC4S_mywg",

                ReportIssueView.IssueType.Performance =>
                    "https://discord.com/api/webhooks/1466856656121106614/2xq3neD_NLWcWbHmX37HyMHq90utWOtY-wlVhBoSUXmJakBL01o6BC6pVxEAdFbWAIrX",

                _ =>
                    "https://discord.com/api/webhooks/1466856775859834880/pfgE0hXTaEzaRXKjl1_P-IOvuFlcNK7ES3-fKv0FvwCKVepmWJ3Ac3eY2PEQCsSexF8O",
            };
        }
    }
}